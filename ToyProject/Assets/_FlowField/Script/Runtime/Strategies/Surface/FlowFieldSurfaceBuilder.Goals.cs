using System;
using System.Collections.Generic;
using UnityEngine;
using BatchPhase = Common.FlowField.FlowFieldSurfaceGoalBatchPhase;

namespace Common.FlowField
{
    /// <summary>
    /// The additional Goals of a Surface2D session: the scratch each one keeps
    /// and the batch build that publishes their Goal layers. The default
    /// Goal's pipeline goes first; a batch begins only on a published field
    /// with nothing owed to that pipeline.
    /// </summary>
    internal sealed partial class FlowFieldSurfaceBuilder
    {
        private readonly Dictionary<int, FlowFieldSurfaceGoalScratch> _goalScratch
            = new Dictionary<int, FlowFieldSurfaceGoalScratch>();
        private readonly List<int> _removedGoals = new List<int>(4);
        // The batch the session steps, or the last one.
        private FlowFieldSurfaceGoalBatch _goalBatch;
        // A default Goal build began since the last batch.
        private bool _goalTurn;
        // The kind the last batch built.
        private FlowFieldGoalKind? _lastBatchKind;

        private bool AdditionalGoalsDue => PublishedField != null && _host.Goals.HasDue;

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
            var batch = new FlowFieldSurfaceGoalBatch(this, PublishedField, goals.PublishedStamp, goals.FieldEpoch);
            // One kind a batch, so a moving Group Goal never waits for every
            // Shared Goal.
            FlowFieldGoalKind kind = goals.NextBatchKind(_lastBatchKind);
            _lastBatchKind = kind;
            AddDueGoals(batch, goals, kind);
            build = _goalBatch = batch;
            return true;
        }

        private void AddDueGoals(FlowFieldSurfaceGoalBatch batch, FlowFieldGoalSet goals, FlowFieldGoalKind kind)
        {
            IReadOnlyList<FlowFieldGoalSet.Entry> entries = goals.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                FlowFieldGoalSet.Entry entry = entries[i];
                if (entry.Kind != kind || !goals.IsBuildable(entry))
                    continue;
                if (!_goalScratch.TryGetValue(entry.Id, out FlowFieldSurfaceGoalScratch scratch))
                {
                    scratch = new FlowFieldSurfaceGoalScratch();
                    _goalScratch.Add(entry.Id, scratch);
                }
                batch.Items.Add(new FlowFieldSurfaceGoalBatch.Item
                {
                    Entry = entry,
                    RequestRevision = entry.RequestRevision,
                    Scratch = scratch,
                    Seeds = entry.Seeds,
                });
            }
        }

        // Frees the scratch of Goals the session removed.
        private void PruneGoalScratch(FlowFieldGoalSet goals)
        {
            _removedGoals.Clear();
            foreach (KeyValuePair<int, FlowFieldSurfaceGoalScratch> pair in _goalScratch)
            {
                if (goals.Find(pair.Key) == null)
                    _removedGoals.Add(pair.Key);
            }
            for (int i = 0; i < _removedGoals.Count; i++)
            {
                _goalScratch[_removedGoals[i]].Dispose();
                _goalScratch.Remove(_removedGoals[i]);
            }
        }

        private void CancelGoalBatch()
        {
            _goalBatch?.Drop();
            _goalBatch = null;
        }

        private void ReleaseGoalScratch()
        {
            CancelGoalBatch();
            foreach (FlowFieldSurfaceGoalScratch scratch in _goalScratch.Values)
                scratch.Dispose();
            _goalScratch.Clear();
        }

        // One step of the batch the session runs; the batch forwards here.
        internal FlowFieldBuildStep StepGoalBatch(FlowFieldSurfaceGoalBatch batch, in FlowFieldBuildBudget budget, bool waitForWorker)
        {
            batch.FrameBudget = budget;
            try
            {
                // A new grid, a stop or a restart dropped the fields: the
                // batch would publish nothing.
                if (_host.Goals.FieldEpoch != batch.FieldEpoch)
                {
                    batch.Drop();
                    return FlowFieldBuildStep.Rejected;
                }
                if (batch.Phase == BatchPhase.Prepare)
                    PrepareGoalBatch(batch);
                if (batch.Phase == BatchPhase.Solving)
                    PollGoalBatch(batch, waitForWorker);
                if (batch.Phase == BatchPhase.Composing)
                    ComposeGoalBatch(batch);

                switch (batch.Phase)
                {
                    case BatchPhase.Solving:
                        return FlowFieldBuildStep.Waiting;
                    case BatchPhase.Composing:
                        return FlowFieldBuildStep.Yield;
                    case BatchPhase.Publish:
                        return FlowFieldBuildStep.Completed;
                    default:
                        batch.Phase = BatchPhase.None;
                        return FlowFieldBuildStep.Rejected;
                }
            }
            finally
            {
                batch.FrameBudget = FlowFieldBuildBudget.Unlimited;
            }
        }

        // Prepares every Goal on its own workspace and starts the solves, in
        // one frame like the default Goal's base stages.
        private void PrepareGoalBatch(FlowFieldSurfaceGoalBatch batch)
        {
            FlowFieldGridSpace grid = batch.Base.Grid;
            FlowFieldSurfaceData surface = batch.Base.Scene.Surface;
            bool[] blocked = batch.Base.Agent.Blocked;
            for (int i = 0; i < batch.Items.Count; i++)
            {
                FlowFieldSurfaceGoalBatch.Item item = batch.Items[i];
                FlowFieldSurfaceWorkspace workspace = item.Scratch.Workspace;
                workspace.Resize(grid.CellCount);
                Array.Copy(blocked, workspace.Blocked, grid.CellCount);
                // The composition sends blocked cells out along the escape
                // directions; the published field keeps none, so they are
                // built here from the same blocked cells.
                FlowFieldSurfaceNavigationPreparation.BuildEscapeDirections(grid, surface, workspace);
                if (item.Seeds != null)
                {
                    StartSeeded(item, grid, surface, workspace);
                    continue;
                }
                FlowFieldGoalRequest request = item.Entry.Request;
                FlowFieldSurfaceGoalResolution resolution = FlowFieldSurfaceGoalResolver.Resolve(
                    grid,
                    hasGoal: true,
                    request.WorldPosition,
                    request.InfluenceRadius);
                FlowFieldSurfaceGoalBuildStatus status = FlowFieldSurfaceGoalResolver.Build(
                    resolution,
                    grid,
                    surface,
                    workspace,
                    item.Scratch.Tracker,
                    forceRebuild: true);
                if (status != FlowFieldSurfaceGoalBuildStatus.Built)
                {
                    // No walkable cell in the Goal's range: the field follows
                    // the Default Direction everywhere, as the default Goal's.
                    PrepareWithoutGoal(grid, surface, workspace);
                    item.Solved = true;
                    continue;
                }
                var solve = new FlowFieldSurfaceBfsRequest(
                    grid,
                    surface,
                    workspace,
                    resolution.LocalX,
                    resolution.LocalZ,
                    resolution.InfluenceRadius,
                    workspace.ResolvedGoalIndex);
                FlowFieldSurfaceGoalBatch.Item started = item;
                if (!item.Scratch.Solve.StartBfs(solve, _ => started.Solved = true))
                    throw new InvalidOperationException("FlowField BFS session could not be started.");
                item.Solving = true;
            }
            batch.Phase = BatchPhase.Solving;
        }

        // A world Goal: every walkable cell is in reach, and the search
        // starts from the seeds. Without seeds the field reaches nothing yet.
        private static void StartSeeded(
            FlowFieldSurfaceGoalBatch.Item item,
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldSurfaceWorkspace workspace)
        {
            FlowFieldGoalSeeds seeds = item.Seeds;
            workspace.ClearGoal();
            for (int index = 0; index < grid.CellCount; index++)
                workspace.InfluenceMask[index] = surface.IsSurfaceValid(index) && !workspace.Blocked[index];
            FlowFieldSurfaceGraphTraversal.BuildTopologyMasks(grid, surface, workspace);
            int goalCell = seeds.GoalCell >= 0 && workspace.InfluenceMask[seeds.GoalCell] ? seeds.GoalCell : -1;
            workspace.SetSeededGoal(goalCell);
            FlowFieldSurfaceGoalScratch scratch = item.Scratch;
            scratch.ResizePortals(grid.CellCount);
            seeds.FillPortals(scratch.PortalDirections, scratch.PortalCosts, scratch.PortalHeights);
            var solve = new FlowFieldSurfaceBfsRequest(
                grid,
                surface,
                workspace,
                seeds,
                goalCell,
                scratch.PortalDirections,
                scratch.PortalCosts,
                scratch.PortalHeights);
            if (solve.Seeds.Count == 0)
            {
                // Nothing to start from: every reachable cell stops.
                for (int index = 0; index < grid.CellCount; index++)
                {
                    workspace.Costs[index] = int.MaxValue;
                    bool open = workspace.InfluenceMask[index];
                    workspace.NextCells[index] = open ? FlowFieldNextCell.Unreachable : FlowFieldNextCell.Blocked;
                    workspace.GoalFlags[index] = open ? FlowFieldGoalFlags.Unreachable : FlowFieldGoalFlags.None;
                }
                item.Solved = true;
                return;
            }
            FlowFieldSurfaceGoalBatch.Item started = item;
            if (!scratch.Solve.StartBfs(solve, _ => started.Solved = true))
                throw new InvalidOperationException("FlowField BFS session could not be started.");
            item.Solving = true;
        }

        private static void PrepareWithoutGoal(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldSurfaceWorkspace workspace)
        {
            workspace.ClearGoal();
            for (int index = 0; index < grid.CellCount; index++)
                workspace.InfluenceMask[index] = surface.IsSurfaceValid(index) && !workspace.Blocked[index];
            FlowFieldSurfaceGraphTraversal.BuildTopologyMasks(grid, surface, workspace);
            FlowFieldSurfaceGraphTraversal.ApplyNoGoalSentinels(grid, surface, workspace);
        }

        private static void PollGoalBatch(FlowFieldSurfaceGoalBatch batch, bool waitForWorker)
        {
            bool waiting = false;
            for (int i = 0; i < batch.Items.Count; i++)
            {
                FlowFieldSurfaceGoalBatch.Item item = batch.Items[i];
                if (item.Solved)
                    continue;
                // A finished solve reports through its callback.
                item.Scratch.Solve.Poll(waitForWorker);
                waiting |= !item.Solved;
            }
            if (!waiting)
                batch.Phase = BatchPhase.Composing;
        }

        // Composes each Goal with the Modifiers over the whole grid, in turn,
        // within the frame budget.
        private void ComposeGoalBatch(FlowFieldSurfaceGoalBatch batch)
        {
            FlowFieldGridSpace grid = batch.Base.Grid;
            FlowFieldSurfaceData surface = batch.Base.Scene.Surface;
            FlowFieldSurfaceRequest request = _inputs.Requests.Latest;
            while (batch.ComposeIndex < batch.Items.Count)
            {
                FlowFieldSurfaceWorkspace workspace = batch.Items[batch.ComposeIndex].Scratch.Workspace;
                bool finished;
                using (_host.EnterUserCode())
                {
                    if (!batch.ComposeBegun)
                    {
                        _modifierComposition.BeginFinalField(
                            new FlowFieldSurfaceModifierBuildRequest(
                                grid,
                                surface,
                                workspace,
                                request.ObstacleCheckHeight,
                                request.ObstacleCheckCenterOffset),
                            _state.ResolvedDefaultDirection,
                            FlowFieldCellRect.Full(grid),
                            rebuildBase: true,
                            leavesOutUnprepared: true);
                        batch.ComposeBegun = true;
                    }
                    finished = _modifierComposition.StepFinalField(in batch.FrameBudget);
                }
                if (!finished)
                    return;
                batch.ComposeIndex++;
                batch.ComposeBegun = false;
            }
            batch.Phase = BatchPhase.Publish;
        }

        // Publishes each Goal still in the set as a Goal layer over the
        // layers the batch began with; true when any field reads differently.
        // A field dropped meanwhile (a new grid, a stop) publishes nothing.
        internal bool PublishGoalBatch(FlowFieldSurfaceGoalBatch batch)
        {
            batch.Phase = BatchPhase.None;
            FlowFieldGoalSet goals = _host.Goals;
            if (goals.FieldEpoch != batch.FieldEpoch)
                return false;
            bool changed = false;
            for (int i = 0; i < batch.Items.Count; i++)
            {
                FlowFieldSurfaceGoalBatch.Item item = batch.Items[i];
                if (!ReferenceEquals(goals.Find(item.Entry.Id), item.Entry))
                    continue;
                FlowFieldField field = new FlowFieldField(
                    batch.Base.Grid,
                    batch.Base.Scene,
                    batch.Base.Agent,
                    CreateGoalLayer(item.Scratch.Workspace));
                changed |= !FlowFieldField.HasSameContent(item.Entry.Field, field);
                item.Entry.Field = field;
                item.Entry.BuiltSeeds = item.Seeds;
                item.Entry.BuiltRequestRevision = item.RequestRevision;
                item.Entry.BuiltBaseStamp = batch.BaseStamp;
            }
            return changed;
        }

        // A published Goal layer owns its arrays: the workspace builds the
        // next field of the same Goal.
        private static FlowFieldGoalLayer CreateGoalLayer(FlowFieldSurfaceWorkspace workspace)
        {
            int count = workspace.Capacity;
            var topology = new uint[count];
            for (int i = 0; i < count; i++)
                topology[i] = workspace.TopologyMasks[i];
            return new FlowFieldGoalLayer(
                topology,
                (FlowFieldGoalFlags[])workspace.GoalFlags.Clone(),
                (int[])workspace.NextCells.Clone(),
                (Vector3[])workspace.BaseDirections.Clone(),
                (float[])workspace.BaseSpeedMultipliers.Clone(),
                (Vector3[])workspace.FinalDirections.Clone(),
                (float[])workspace.FinalSpeedMultipliers.Clone(),
                (bool[])workspace.ModifierInfluence.Clone(),
                workspace.HasActiveGoal,
                workspace.ResolvedGoalIndex,
                workspace.HasActiveGoal ? (int[])workspace.Costs.Clone() : null);
        }
    }
}
