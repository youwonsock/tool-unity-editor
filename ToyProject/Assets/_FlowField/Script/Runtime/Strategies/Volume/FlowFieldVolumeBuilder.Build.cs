using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using BuildContext = Common.FlowField.FlowFieldVolumeBuildContext;
using VolumeFieldData = Common.FlowField.FlowFieldField<Common.FlowField.FlowFieldVolumeRequest>;
using BuildPhase = Common.FlowField.FlowFieldVolumeBuildPhase;

namespace Common.FlowField
{
    /// <summary>The cooperative stages of one Volume3D build, up to its commit.</summary>
    internal sealed partial class FlowFieldVolumeBuilder
    {
        private bool StepBuild(BuildContext build, in FlowFieldBuildBudget budget, bool waitForSolve)
        {
            while (!budget.IsExpired)
            {
                switch (build.Phase)
                {
                    case BuildPhase.Allocate:
                        if (StepAllocate(build))
                        {
                            build.Cursor = 0;
                            build.Phase = build.Request.BakeMode == FlowFieldBakeMode.StaticBaked
                                ? BuildPhase.StaticData
                                : build.LoadsBakedObstacles
                                    ? BuildPhase.BakedObstacles
                                    : build.ReusesObstacleQueries
                                        ? BuildPhase.Solve
                                        : BuildPhase.Obstacles;
                        }
                        break;
                    case BuildPhase.StaticData:
                        if (StepStaticData(build))
                        {
                            build.Cursor = 0;
                            build.Phase = BuildPhase.Modifiers;
                        }
                        break;
                    case BuildPhase.BakedObstacles:
                        if (StepBakedObstacles(build))
                        {
                            build.Cursor = 0;
                            build.Phase = build.ReusesObstacleQueries ? BuildPhase.Solve : BuildPhase.Obstacles;
                        }
                        break;
                    case BuildPhase.Obstacles:
                        if (StepObstacles(build))
                        {
                            build.Cursor = 0;
                            build.Phase = BuildPhase.Solve;
                        }
                        break;
                    case BuildPhase.Solve:
                        if (!StepSolve(build, waitForSolve))
                            return false;
                        build.Cursor = 0;
                        build.Phase = BuildPhase.Modifiers;
                        break;
                    case BuildPhase.Modifiers:
                        if (StepModifiers(build))
                            build.Phase = BuildPhase.Complete;
                        break;
                    case BuildPhase.Complete:
                        // The budget can end between the last Modifier step
                        // and the publish; one destroyed meanwhile sends the
                        // build back to compose without it.
                        if (!HasDestroyedModifier(build))
                            return true;
                        build.Phase = BuildPhase.Modifiers;
                        break;
                }
            }
            return false;
        }

        // The solve job holds pinned pointers into the build arrays, which the
        // Job safety system cannot see. No other stage may run until it ends.
        [Conditional("UNITY_ASSERTIONS")]
        private static void AssertSolveIdle(BuildContext build, string stage)
        {
            if (build.SolveRunning)
                UnityEngine.Debug.LogAssertion(
                    $"[FlowField] Volume {stage} ran while its solve job was running.");
        }

        // A direct owner (a bake) waits for the worker; the scheduler polls it.
        private bool StepSolve(BuildContext build, bool waitForSolve)
        {
            if (!build.SolveRunning)
                build.StartSolve();
            if (!waitForSolve && !build.SolveHandle.IsCompleted)
                return false;
            build.FinishSolve();
            return true;
        }

        private bool StepStaticData(BuildContext build)
        {
            AssertSolveIdle(build, nameof(StepStaticData));
            FlowFieldVolumeBakeData data = build.Request.BakeData;
            int count = Math.Min(ArrayElementsPerStep, build.Count - build.Cursor);
            int end = build.Cursor + count;
            data.CopyCellsToBuild(
                build.Cursor,
                count,
                build.Blocked,
                build.Topology,
                build.GoalFlags,
                build.Next,
                build.Directions,
                build.Speeds,
                build.EscapeDirections);
            // Default Direction is a runtime input, as on Surface2D. The
            // asset keeps its bake-time value only to rebuild its own arrays.
            Vector3 defaultDirection = FlowFieldVectorUtility.NormalizeDefaultDirection(build.Request.DefaultDirection);
            for (int index = build.Cursor; index < end; index++)
            {
                if (!build.Blocked[index] && build.Next[index] == FlowFieldNextCell.DefaultDirection)
                {
                    build.Directions[index] = defaultDirection;
                    build.Speeds[index] = 1f;
                }
            }
            build.Cursor = end;
            if (build.Cursor < build.Count)
                return false;
            build.GoalIndex = data.ResolvedGoalIndex;
            return true;
        }

        private bool StepBakedObstacles(BuildContext build)
        {
            AssertSolveIdle(build, nameof(StepBakedObstacles));
            int count = Math.Min(ArrayElementsPerStep, build.Count - build.Cursor);
            build.Request.BakeData.CopyObstaclesToBuild(
                build.Cursor,
                count,
                build.Blocked,
                build.Topology,
                build.EscapeDirections);
            build.Cursor += count;
            return build.Cursor >= build.Count;
        }

        private bool StepAllocate(BuildContext build)
        {
            AssertSolveIdle(build, nameof(StepAllocate));
            // Keep large managed allocations as individually observable work
            // units. A build can therefore yield between arrays instead of
            // constructing the entire Volume3D staging graph in one frame.
            // A taken spare keeps its old values: Blocked is filled by the copy
            // below, the obstacle queries or the bake copy, every other array
            // by the static bake copy or the solve jobs, which write each cell
            // (Escape clears all before its search), and the final field by
            // the Modifier stage's full copy of the base field.
            switch (build.AllocationStage)
            {
                case FlowFieldVolumeAllocation.Blocked:
                    build.Blocked ??= FlowFieldSpareArrays.Take(ref _fields.Spares.Blocked, build.Count);
                    if (build.PublishedBlocked != null)
                        Array.Copy(build.PublishedBlocked, build.Blocked, build.Count);
                    break;
                case FlowFieldVolumeAllocation.Topology: build.Topology ??= FlowFieldSpareArrays.Take(ref _fields.Spares.Topology, build.Count); break;
                case FlowFieldVolumeAllocation.GoalFlags: build.GoalFlags = FlowFieldSpareArrays.Take(ref _fields.Spares.GoalFlags, build.Count); break;
                case FlowFieldVolumeAllocation.Next: build.Next = FlowFieldSpareArrays.Take(ref _fields.Spares.Next, build.Count); break;
                case FlowFieldVolumeAllocation.Directions: build.Directions = FlowFieldSpareArrays.Take(ref _fields.Spares.Directions, build.Count); break;
                case FlowFieldVolumeAllocation.Speeds: build.Speeds = FlowFieldSpareArrays.Take(ref _fields.Spares.Speeds, build.Count); break;
                case FlowFieldVolumeAllocation.FinalDirections: build.FinalDirections = FlowFieldSpareArrays.Take(ref _fields.Spares.FinalDirections, build.Count); break;
                case FlowFieldVolumeAllocation.FinalSpeeds: build.FinalSpeeds = FlowFieldSpareArrays.Take(ref _fields.Spares.FinalSpeeds, build.Count); break;
                case FlowFieldVolumeAllocation.EscapeDirections: build.EscapeDirections ??= FlowFieldSpareArrays.Take(ref _fields.Spares.EscapeDirections, build.Count); break;
                // A StaticBaked build loads its navigation and never searches.
                case FlowFieldVolumeAllocation.Costs:
                    if (build.Request.BakeMode != FlowFieldBakeMode.StaticBaked)
                        build.Costs = FlowFieldSpareArrays.Take(ref _fields.Spares.Costs, build.Count);
                    break;
                case FlowFieldVolumeAllocation.Queue:
                    if (build.Request.BakeMode != FlowFieldBakeMode.StaticBaked)
                        build.Queue = FlowFieldSpareArrays.Take(ref _fields.Spares.Queue, build.Count);
                    break;
                case FlowFieldVolumeAllocation.Influence:
                    if (build.Request.BakeMode != FlowFieldBakeMode.StaticBaked)
                        build.Influence = FlowFieldSpareArrays.Take(ref _fields.Spares.Influence, build.Count);
                    break;
            }

            build.AllocationStage++;
            return build.AllocationStage > FlowFieldVolumeAllocation.Influence;
        }

        private bool StepObstacles(BuildContext build)
        {
            AssertSolveIdle(build, nameof(StepObstacles));
            if (!build.ObstaclesSynced)
            {
                Physics.SyncTransforms();
                _cellQuery.CaptureRegistered(_host.Obstacles);
                // The session observed the uncertain regions after this build
                // began, so they are read here, before the first query.
                if (build.PublishedBlocked != null)
                    build.LimitToRegions(_safety.ObservedRegions());
                else if (build.LoadsBakedObstacles)
                    build.LimitToRegions(BakedBaseRegions(build));
                build.ObstaclesSynced = true;
            }
            int count = build.NextObstacleCells(_stepCells);
            if (count == 0)
                return true;

            FlowFieldGridSpace grid = build.Request.Grid;
            Vector3 half = ObstacleHalfExtents(grid.CellSize, build.Request.ObstacleClearance);
            _cellQuery.BeginObstacleStep(
                FlowFieldVolumeCellQuery.StepBounds(grid, _stepCells, count, half),
                build.Request.ObstacleLayer);
            for (int i = 0; i < count; i++)
            {
                int index = _stepCells[i];
                grid.FromFlatIndex(index, out int x, out int y, out int z);
                Vector3 center = grid.LocalToWorldCenter(x, y, z);
                var cellBox = new Bounds(center, half * 2f);
                build.Blocked[index] = _cellQuery.MayHitLayer(cellBox)
                    && Physics.CheckBox(
                        center,
                        half,
                        Quaternion.identity,
                        build.Request.ObstacleLayer,
                        QueryTriggerInteraction.Ignore);
                if (!build.Blocked[index])
                {
                    build.Blocked[index] = _cellQuery.IntersectsRegistered(
                        center,
                        half,
                        cellBox,
                        ref _overlapBuffer);
                }
            }
            return false;
        }

        private bool StepModifiers(BuildContext build)
        {
            AssertSolveIdle(build, nameof(StepModifiers));
            // A Modifier destroyed without being unregistered is gone, so this
            // build leaves it out: the final field starts again from the base
            // without it. The session removes it when the build ends. One that
            // unregistered first already owes a recompose, so this build
            // finishes with it. Each step and the publish make this check.
            if (DropDestroyedModifiers(build))
            {
                build.ModifierCells = null;
                build.Cursor = 0;
            }
            // The final field starts as the base field, so only cells a Modifier
            // can reach need to be walked. They are rarely more than a small part.
            if (build.ModifierCells == null)
            {
                // Copied in steps like the other array stages: 16 MB at a
                // million cells. Every way into this stage starts the cursor at 0.
                if (build.Cursor < build.Count)
                {
                    int copied = Math.Min(ArrayElementsPerStep, build.Count - build.Cursor);
                    Array.Copy(build.Directions, build.Cursor, build.FinalDirections, build.Cursor, copied);
                    Array.Copy(build.Speeds, build.Cursor, build.FinalSpeeds, build.Cursor, copied);
                    build.Cursor += copied;
                    return false;
                }
                if (build.Modifiers.Length == 0)
                    return true;
                Physics.SyncTransforms();
                build.ModifierCells = new FlowFieldVolumeCellCursor(build.Request.Grid, ModifierReach(build));
            }

            int count = build.ModifierCells.Next(_stepCells);
            if (count == 0)
                return true;

            Vector3 cellHalf = Vector3.one * FlowFieldOverlapUtility.CellHalfExtent(build.Request.Grid.CellSize);
            _cellQuery.BeginModifierStep(
                FlowFieldVolumeCellQuery.StepBounds(build.Request.Grid, _stepCells, count, cellHalf),
                build.Modifiers);
            using FlowFieldExecutionBoundary.OperationScope userCode = _host.EnterUserCode();
            for (int i = 0; i < count; i++)
            {
                int index = _stepCells[i];
                if (!FlowFieldVectorUtility.CanApplyModifier(
                        build.Blocked[index],
                        build.GoalFlags[index],
                        build.Directions[index]))
                    continue;

                build.Request.Grid.FromFlatIndex(index, out int x, out int y, out int z);
                Vector3 center = build.Request.Grid.CellCenter(index);
                FlowFieldVectorState current = new FlowFieldVectorState(
                    build.FinalDirections[index],
                    build.FinalSpeeds[index]);
                for (int modifierIndex = 0; modifierIndex < build.Modifiers.Length; modifierIndex++)
                {
                    if (!_cellQuery.MayTouchModifier(modifierIndex, new Bounds(center, cellHalf * 2f)))
                        continue;
                    FlowFieldModifierWorkItem modifier = build.Modifiers[modifierIndex];
                    Collider collider = modifier.InfluenceCollider;
                    if (!FlowFieldModifierInfluence.IsUsableTrigger(collider))
                        continue;
                    int layerMask = 1 << collider.gameObject.layer;
                    if (!FlowFieldOverlapUtility.OverlapsTarget(
                            center,
                            cellHalf,
                            layerMask,
                            collider,
                            QueryTriggerInteraction.Collide,
                            ref _overlapBuffer))
                        continue;
                    FlowFieldVectorModifierContext context = new FlowFieldVectorModifierContext(
                        index,
                        x,
                        y,
                        z,
                        center,
                        Vector3.zero,
                        build.Request.Grid,
                        (build.GoalFlags[index] & FlowFieldGoalFlags.Directed) != 0);
                    current = FlowFieldVectorUtility.ApplyModifier(modifier.Snapshot, in current, in context);
                }
                build.FinalDirections[index] = current.Direction;
                build.FinalSpeeds[index] = current.SpeedMultiplier;
            }
            return false;
        }

        // Destroyed while still registered: never unregistered.
        private bool IsGone(in FlowFieldModifierWorkItem modifier)
            => modifier.IsDestroyed && _host.Modifiers.Contains(modifier.Modifier);

        private bool HasDestroyedModifier(BuildContext build)
        {
            foreach (FlowFieldModifierWorkItem modifier in build.Modifiers)
            {
                if (IsGone(in modifier))
                    return true;
            }
            return false;
        }

        private bool DropDestroyedModifiers(BuildContext build)
        {
            if (!HasDestroyedModifier(build))
                return false;
            var kept = new List<FlowFieldModifierWorkItem>(build.Modifiers.Length);
            foreach (FlowFieldModifierWorkItem modifier in build.Modifiers)
            {
                if (!IsGone(in modifier))
                    kept.Add(modifier);
            }
            build.Modifiers = kept.ToArray();
            return true;
        }

        // One cell box per Modifier, joined where boxes overlap: a cell walked
        // twice would apply its Modifiers twice, and every Modifier that
        // reaches a cell must still apply there in priority order.
        private static FlowFieldCellRect[] ModifierReach(BuildContext build)
        {
            FlowFieldGridSpace grid = build.Request.Grid;
            var rects = new List<FlowFieldCellRect>(build.Modifiers.Length);
            foreach (FlowFieldModifierWorkItem modifier in build.Modifiers)
            {
                if (!FlowFieldModifierInfluence.IsUsableTrigger(modifier.InfluenceCollider))
                    continue;
                Bounds bounds = modifier.InfluenceCollider.bounds;
                bounds.Expand(grid.CellSize * 2f);
                FlowFieldCellRect rect = FlowFieldCellRect.FromBounds(grid, bounds);
                if (!rect.IsValid)
                    continue;
                // The boxes so far do not overlap; absorb every one this box
                // reaches, again after each union grows it.
                bool joined;
                do
                {
                    joined = false;
                    for (int i = rects.Count - 1; i >= 0; i--)
                    {
                        if (!FlowFieldCellRect.Intersect(rects[i], rect).IsValid)
                            continue;
                        rect = FlowFieldCellRect.Union(rects[i], rect);
                        rects.RemoveAt(i);
                        joined = true;
                    }
                }
                while (joined);
                rects.Add(rect);
            }
            return rects.ToArray();
        }

        internal bool CommitBuild(BuildContext build)
        {
            // The solver or the static bake has already resolved the Goal. An
            // anchor flag must never change the published Goal identity.
            int goalIndex = build.Request.HasGoal ? build.GoalIndex : -1;
            VolumeFieldData next = new VolumeFieldData(
                build.Request,
                build.Request.Grid,
                surface: null,
                build.Blocked,
                build.Topology,
                build.GoalFlags,
                build.Next,
                build.Directions,
                build.Speeds,
                build.FinalDirections,
                build.FinalSpeeds,
                build.EscapeDirections,
                modifierInfluence: null,
                hasActiveGoal: goalIndex >= 0,
                goalIndex);

            // The request snapshot and the safety regions update either way;
            // only a field that reads differently is a new Revision.
            bool changed = _fields.Publish(next);
            if (!build.IsModifierRecompose)
            {
                // The published field holds none of these.
                _fields.Spares.Costs = build.Costs ?? _fields.Spares.Costs;
                _fields.Spares.Queue = build.Queue ?? _fields.Spares.Queue;
                _fields.Spares.Influence = build.Influence ?? _fields.Spares.Influence;
            }
            build.End();
            _build = null;
            return changed;
        }


        public FlowFieldBuildPolicy BuildPolicy => new FlowFieldBuildPolicy(beginsAfterBudget: false, maxBuildsBegunPerPump: int.MaxValue);

        // Every build but a Modifier recompose queries the obstacles.
        public bool TryBeginBuild(out FlowFieldBuildStart start, out IFlowFieldBuild build)
        {
            bool started = TryBeginPendingBuild();
            start = new FlowFieldBuildStart(started && !_build.IsModifierRecompose, null);
            build = started ? _build : null;
            return started;
        }

        // One step of the build the session runs; its context forwards here.
        internal FlowFieldBuildStep Step(BuildContext build, in FlowFieldBuildBudget budget, bool waitForWorker)
        {
            if (build.IsWaitingForWorker && !waitForWorker)
                return FlowFieldBuildStep.Waiting;
            if (StepBuild(build, budget, waitForWorker))
                return FlowFieldBuildStep.Completed;
            return build.SolveRunning ? FlowFieldBuildStep.Waiting : FlowFieldBuildStep.Yield;
        }

        public bool HasOwedInput => _inputs.IsOwed;

        private bool TryBeginPendingBuild()
        {
            if (!_inputs.IsOwed)
                return false;

            // A Modifier change recomposes the published navigation. Before
            // any field is published it builds in full like any request.
            bool recompose = !_inputs.Requests.Pending && _fields.Committed != null;
            FlowFieldVolumeRequest request = recompose ? _fields.Committed.Request : _inputs.Requests.Latest;
            _inputs.ClearOwed();
            FlowFieldModifierWorkItem[] modifierSnapshots;
            using (_host.EnterUserCode())
                modifierSnapshots = _host.Modifiers.CaptureSnapshots();
            // A recompose queries no obstacles, so it resolves no safety region.
            if (recompose)
            {
                _build = new BuildContext(
                    this,
                    _fields.Committed,
                    modifierSnapshots,
                    FlowFieldSpareArrays.Take(ref _fields.Spares.FinalDirections, request.Grid.CellCount),
                    FlowFieldSpareArrays.Take(ref _fields.Spares.FinalSpeeds, request.Grid.CellCount));
            }
            else
            {
                bool reuseObstacles = CanReuseObstacleQueries(request);
                bool requeryChangedCells = !reuseObstacles && CanRequeryChangedCells(request);
                _inputs.TakeFullQueries(out bool requiresFullQuery, out bool queryEveryCell);
                _build = new BuildContext(this, request, modifierSnapshots);
                _build.RequiresFullQuery = requiresFullQuery;
                _build.QueriesEveryCell = queryEveryCell;
                if (reuseObstacles)
                {
                    _build.ReusesObstacleQueries = true;
                    _build.Blocked = _fields.Committed.Blocked;
                    _build.Topology = _fields.Committed.Topology;
                    _build.EscapeDirections = _fields.Committed.EscapeDirections;
                }
                else if (requeryChangedCells)
                {
                    _build.QueryOnlyChangedRegions(_fields.Committed.Blocked);
                }
                // While the sweep runs, a reload queries every cell instead,
                // as Surface2D's swept mask scans every obstacle: the sweep's
                // first pass after Init or a restart only records, so a
                // Collider moved before it would never be seen.
                else if (request.BakeMode == FlowFieldBakeMode.BakedBase
                    && !queryEveryCell
                    && !_host.SweepsObstacles)
                {
                    // BakedBase starts again from the bake. With nothing that
                    // changed since, its obstacle results stand as baked.
                    _build.LoadsBakedObstacles = true;
                    _build.ReusesObstacleQueries = _safety.UncertainRegionCount == 0
                        && _host.Obstacles.Count == 0
                        && !_inputs.BakedBaseTouched.HasValue;
                }
            }
            return true;
        }

        /// <summary>
        /// Tracked obstacle changes report their regions, so the rest of the published
        /// mask stays correct. Only an explicit rebuild (RequestRebuild,
        /// NotifyCellsDirty), new obstacle settings or turning the sweep on or off
        /// query every cell, which picks up a collider that moved without being
        /// tracked; in BakedBase an explicit rebuild starts again from the bake
        /// unless the sweep runs.
        /// </summary>
        private bool CanRequeryChangedCells(in FlowFieldVolumeRequest request)
            => !_inputs.ObstacleQueryRequired
                && _fields.Committed != null
                && request.BakeMode != FlowFieldBakeMode.StaticBaked
                && _safety.UncertainRegionCount > 0
                && _fields.Committed.Request.HasSameObstacleInputs(request);

        /// <summary>
        /// The obstacle result stands while nothing about the obstacles changed:
        /// a Goal, Default Direction or Modifier change reuses it, an explicit
        /// rebuild, a tracked change or new obstacle settings do not.
        /// </summary>
        private bool CanReuseObstacleQueries(in FlowFieldVolumeRequest request)
            => !_inputs.ObstacleQueryRequired
                && _fields.Committed != null
                && request.BakeMode != FlowFieldBakeMode.StaticBaked
                && _safety.UncertainRegionCount == 0
                && _fields.Committed.Request.HasSameObstacleInputs(request);

        // A BakedBase build that starts again from the bake queries what the
        // bake cannot know: the uncertain regions, every region a change
        // reached since Initialize, and the registered obstacles.
        private Bounds[] BakedBaseRegions(BuildContext build)
        {
            var regions = new System.Collections.Generic.List<Bounds>(_safety.ObservedRegions());
            if (_inputs.BakedBaseTouched.HasValue)
                regions.Add(_inputs.BakedBaseTouched.Value);
            float margin = FlowFieldObstacleReach.Margin(build.Request.Grid.CellSize, build.Request.ObstacleClearance);
            IFlowFieldObstacleView registry = _host.Obstacles;
            for (int i = 0; i < registry.Count; i++)
            {
                regions.Add(FlowFieldObstacleReach.Expand(registry.GetBounds(i), margin));
                Collider collider = registry.GetCollider(i);
                if (collider != null)
                    regions.Add(FlowFieldObstacleReach.Expand(collider.bounds, margin));
            }
            return regions.ToArray();
        }

        private void DiscardBuild()
        {
            BuildContext previous = _build;
            _build = null;
            // The build that replaces it still owes the full query it was asked for.
            if (previous != null)
                _inputs.ReturnFullQueries(previous.RequiresFullQuery, previous.QueriesEveryCell);
            previous?.End();
        }
    }
}
