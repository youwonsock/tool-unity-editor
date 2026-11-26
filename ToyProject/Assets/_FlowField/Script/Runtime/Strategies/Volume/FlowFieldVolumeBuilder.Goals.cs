using System.Collections.Generic;
using BuildContext = Common.FlowField.FlowFieldVolumeBuildContext;
using VolumeFieldData = Common.FlowField.FlowFieldField<Common.FlowField.FlowFieldVolumeRequest>;

namespace Common.FlowField
{
    /// <summary>
    /// The additional Goals of a Volume3D session: the spare arrays each one
    /// keeps and the batch build that publishes their Goal layers. The default
    /// Goal's builds go first; a batch begins only on a published field with
    /// nothing owed to them.
    /// </summary>
    internal sealed partial class FlowFieldVolumeBuilder
    {
        private readonly Dictionary<int, FlowFieldVolumeGoalScratch> _goalScratch
            = new Dictionary<int, FlowFieldVolumeGoalScratch>();
        private readonly List<int> _removedGoals = new List<int>(4);
        // The batch the session steps, or the last one.
        private FlowFieldVolumeGoalBatch _goalBatch;
        // A default Goal build began since the last batch.
        private bool _goalTurn;
        // The kind the last batch built.
        private FlowFieldGoalKind? _lastBatchKind;

        private bool AdditionalGoalsDue => _fields.Committed != null && _host.Goals.HasDue;

        // A batch builds each Goal not yet built on the published field. With
        // nothing owed to the default Goal, that field stands for every input.
        private bool TryBeginGoalBatch(bool primaryOwed, out IFlowFieldBuild build)
        {
            build = null;
            if (!AdditionalGoalsDue)
                return false;
            FlowFieldGoalSet goals = _host.Goals;
            if (!primaryOwed)
                goals.MarkPublished(goals.BaseStamp);
            if (!goals.HasBuildable)
                return false;
            PruneGoalScratch(goals);
            FlowFieldModifierWorkItem[] snapshots;
            using (_host.EnterUserCode())
                snapshots = _host.Modifiers.CaptureSnapshots();
            var batch = new FlowFieldVolumeGoalBatch(this, _fields.Committed, goals.PublishedStamp, goals.FieldEpoch);
            // One kind a batch, so a moving Group Goal never waits for every
            // Shared Goal.
            FlowFieldGoalKind kind = goals.NextBatchKind(_lastBatchKind);
            _lastBatchKind = kind;
            AddDueGoals(batch, goals, kind, snapshots);
            build = _goalBatch = batch;
            return true;
        }

        private void AddDueGoals(
            FlowFieldVolumeGoalBatch batch,
            FlowFieldGoalSet goals,
            FlowFieldGoalKind kind,
            FlowFieldModifierWorkItem[] snapshots)
        {
            VolumeFieldData published = batch.Base;
            FlowFieldVolumeRequest baseRequest = published.Request;
            IReadOnlyList<FlowFieldGoalSet.Entry> entries = goals.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                FlowFieldGoalSet.Entry entry = entries[i];
                if (entry.Kind != kind || !goals.IsBuildable(entry))
                    continue;
                if (!_goalScratch.TryGetValue(entry.Id, out FlowFieldVolumeGoalScratch scratch))
                {
                    scratch = new FlowFieldVolumeGoalScratch();
                    _goalScratch.Add(entry.Id, scratch);
                }
                var request = new FlowFieldVolumeRequest(
                    baseRequest.Grid,
                    baseRequest.WorldBounds,
                    baseRequest.BakeMode,
                    baseRequest.ObstacleLayer,
                    baseRequest.ObstacleClearance,
                    baseRequest.SweepUnregistered,
                    hasGoal: true,
                    entry.Request.WorldPosition,
                    entry.Request.InfluenceRadius,
                    baseRequest.DefaultDirection,
                    baseRequest.BakeData);
                // The published obstacle results stand: the build allocates
                // the Goal arrays from the Goal's own spares and solves.
                var build = new BuildContext(this, request, snapshots)
                {
                    Spares = scratch.Spares,
                    ReusesObstacleQueries = true,
                    ReusedAgent = published.Agent,
                    Blocked = published.Agent.Blocked,
                    Topology = published.Goal.Topology,
                    EscapeDirections = published.Agent.EscapeDirections,
                };
                batch.Items.Add(new FlowFieldVolumeGoalBatch.Item
                {
                    Entry = entry,
                    RequestRevision = entry.RequestRevision,
                    Scratch = scratch,
                    Build = build,
                });
            }
        }

        // Frees the spares of Goals the session removed.
        private void PruneGoalScratch(FlowFieldGoalSet goals)
        {
            _removedGoals.Clear();
            foreach (int id in _goalScratch.Keys)
            {
                if (goals.Find(id) == null)
                    _removedGoals.Add(id);
            }
            for (int i = 0; i < _removedGoals.Count; i++)
                _goalScratch.Remove(_removedGoals[i]);
        }

        private void CancelGoalBatch()
        {
            _goalBatch?.End();
            _goalBatch = null;
        }

        private void ReleaseGoalScratch()
        {
            CancelGoalBatch();
            _goalScratch.Clear();
        }

        // One step of the batch the session runs; the batch forwards here.
        // Every Goal moves on as far as it can, so each solve starts before
        // any is waited for.
        internal FlowFieldBuildStep StepGoalBatch(FlowFieldVolumeGoalBatch batch, in FlowFieldBuildBudget budget, bool waitForWorker)
        {
            // A new grid, a stop or a restart dropped the fields: the batch
            // would publish nothing.
            if (_host.Goals.FieldEpoch != batch.FieldEpoch)
            {
                batch.End();
                return FlowFieldBuildStep.Rejected;
            }
            bool waiting = false;
            bool remaining = false;
            for (int i = 0; i < batch.Items.Count; i++)
            {
                FlowFieldVolumeGoalBatch.Item item = batch.Items[i];
                if (item.Done)
                    continue;
                if (budget.IsExpired)
                    return FlowFieldBuildStep.Yield;
                if (!waitForWorker && item.Build.IsWaitingForWorker)
                {
                    waiting = true;
                    remaining = true;
                    continue;
                }
                if (StepBuild(item.Build, budget, waitForWorker))
                {
                    item.Done = true;
                    continue;
                }
                remaining = true;
                waiting |= item.Build.SolveRunning;
            }
            if (!remaining)
                return FlowFieldBuildStep.Completed;
            return waiting && !budget.IsExpired ? FlowFieldBuildStep.Waiting : FlowFieldBuildStep.Yield;
        }

        // Publishes each Goal still in the set as a Goal layer over the agent
        // layer the batch began with; true when any field reads differently.
        // A field dropped meanwhile (a new grid, a stop) publishes nothing.
        internal bool PublishGoalBatch(FlowFieldVolumeGoalBatch batch)
        {
            batch.End();
            FlowFieldGoalSet goals = _host.Goals;
            if (goals.FieldEpoch != batch.FieldEpoch)
                return false;
            bool changed = false;
            for (int i = 0; i < batch.Items.Count; i++)
            {
                FlowFieldVolumeGoalBatch.Item item = batch.Items[i];
                if (!ReferenceEquals(goals.Find(item.Entry.Id), item.Entry))
                    continue;
                BuildContext build = item.Build;
                // The solve has resolved the Goal; an anchor flag never
                // changes its identity, as for the default Goal.
                int goalIndex = build.Request.HasGoal ? build.GoalIndex : -1;
                var goal = new FlowFieldGoalLayer(
                    build.Topology,
                    build.GoalFlags,
                    build.Next,
                    build.Directions,
                    build.Speeds,
                    build.FinalDirections,
                    build.FinalSpeeds,
                    modifierInfluence: null,
                    isActive: goalIndex >= 0,
                    goalIndex,
                    distances: build.Costs);
                var field = new VolumeFieldData(build.Request, build.Request.Grid, scene: null, batch.Base.Agent, goal);
                changed |= !FlowFieldField.HasSameContent(item.Entry.Field, field);
                FlowFieldSpareArrays spares = item.Scratch.Spares;
                spares.RetireGoal(item.Entry.Field?.Goal, goal);
                spares.Queue = build.Queue ?? spares.Queue;
                spares.Influence = build.Influence ?? spares.Influence;
                item.Entry.Field = field;
                item.Entry.BuiltRequestRevision = item.RequestRevision;
                item.Entry.BuiltBaseStamp = batch.BaseStamp;
            }
            return changed;
        }
    }
}
