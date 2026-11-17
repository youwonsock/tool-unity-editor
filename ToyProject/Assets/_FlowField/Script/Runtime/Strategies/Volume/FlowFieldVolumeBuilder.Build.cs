using System;
using System.Diagnostics;
using System.Threading;
using UnityEngine;
using BuildContext = Common.FlowField.FlowFieldVolumeBuildContext;
using VolumeFieldData = Common.FlowField.FlowFieldField<Common.FlowField.FlowFieldVolumeRequest>;
using BuildPhase = Common.FlowField.FlowFieldVolumeBuildPhase;
using static Common.FlowField.FlowFieldVolumeNavigation;

namespace Common.FlowField
{
    /// <summary>The cooperative stages of one Volume3D build, up to its commit.</summary>
    internal sealed partial class FlowFieldVolumeBuilder
    {
        private bool StepBuild(BuildContext build, in FlowFieldBuildBudget budget)
        {
            while (!budget.IsExpired)
            {
                switch (build.Phase)
                {
                    case BuildPhase.Allocate:
                        if (StepAllocate(build))
                        {
                            build.Cursor = 0;
                            build.Phase = build.Request.StaticBakeData != null
                                ? BuildPhase.StaticData
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
                    case BuildPhase.Obstacles:
                        if (StepObstacles(build))
                        {
                            build.Cursor = 0;
                            build.Phase = BuildPhase.Solve;
                        }
                        break;
                    case BuildPhase.Solve:
                        if (!StepSolve(build))
                            return false;
                        build.Cursor = 0;
                        build.Phase = BuildPhase.Modifiers;
                        break;
                    case BuildPhase.Modifiers:
                        if (StepModifiers(build))
                            build.Phase = BuildPhase.Complete;
                        break;
                    case BuildPhase.Complete:
                        return true;
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

        private bool StepSolve(BuildContext build)
        {
            if (!build.SolveRunning)
                build.StartSolve();
            if (!_waitForSolve && !build.SolveHandle.IsCompleted)
                return false;
            build.FinishSolve();
            return true;
        }

        private bool StepStaticData(BuildContext build)
        {
            AssertSolveIdle(build, nameof(StepStaticData));
            FlowFieldVolumeBakeData data = build.Request.StaticBakeData;
            int end = Math.Min(build.Cursor + ArrayElementsPerStep, build.Count);
            Vector3 defaultDirection = FlowFieldVectorUtility.NormalizeDefaultDirection(build.Request.DefaultDirection);
            for (; build.Cursor < end; build.Cursor++)
            {
                data.CopyCellToBuild(
                    build.Cursor,
                    build.Blocked,
                    build.Topology,
                    build.GoalFlags,
                    build.Next,
                    build.Directions,
                    build.Speeds,
                    build.EscapeDirections);
                // Default Direction is a runtime input, as on Surface2D. The
                // asset keeps its bake-time value only to rebuild its own arrays.
                if (!build.Blocked[build.Cursor] && build.Next[build.Cursor] == -1)
                {
                    build.Directions[build.Cursor] = defaultDirection;
                    build.Speeds[build.Cursor] = 1f;
                }
            }
            if (build.Cursor < build.Count)
                return false;
            build.GoalIndex = data.ResolvedGoalIndex;
            return true;
        }

        private bool StepAllocate(BuildContext build)
        {
            AssertSolveIdle(build, nameof(StepAllocate));
            // Keep large managed allocations as individually observable work
            // units. A build can therefore yield between arrays instead of
            // constructing the entire Volume3D staging graph in one frame.
            switch (build.AllocationStage)
            {
                case 0:
                    build.Blocked ??= FlowFieldSpareArrays.Rent(ref _spares.Blocked, build.Count);
                    if (build.PublishedBlocked != null)
                        Array.Copy(build.PublishedBlocked, build.Blocked, build.Count);
                    break;
                case 1: build.Topology ??= FlowFieldSpareArrays.Rent(ref _spares.Topology, build.Count); break;
                case 2: build.GoalFlags = FlowFieldSpareArrays.Rent(ref _spares.GoalFlags, build.Count); break;
                case 3: build.Next = FlowFieldSpareArrays.Rent(ref _spares.Next, build.Count); break;
                case 4: build.Directions = FlowFieldSpareArrays.Rent(ref _spares.Directions, build.Count); break;
                case 5: build.Speeds = FlowFieldSpareArrays.Rent(ref _spares.Speeds, build.Count); break;
                case 6: build.FinalDirections = FlowFieldSpareArrays.Rent(ref _spares.FinalDirections, build.Count); break;
                case 7: build.FinalSpeeds = FlowFieldSpareArrays.Rent(ref _spares.FinalSpeeds, build.Count); break;
                case 8: build.EscapeDirections ??= FlowFieldSpareArrays.Rent(ref _spares.EscapeDirections, build.Count); break;
                // A static bake loads its navigation and never searches.
                case 9:
                    if (build.Request.StaticBakeData == null)
                        build.Costs = FlowFieldSpareArrays.Rent(ref _spares.Costs, build.Count);
                    break;
                case 10:
                    if (build.Request.StaticBakeData == null)
                        build.Queue = FlowFieldSpareArrays.Rent(ref _spares.Queue, build.Count);
                    break;
                case 11:
                    if (build.Request.StaticBakeData == null)
                        build.Influence = FlowFieldSpareArrays.Rent(ref _spares.Influence, build.Count);
                    break;
            }

            build.AllocationStage++;
            return build.AllocationStage > 11;
        }

        private bool StepObstacles(BuildContext build)
        {
            AssertSolveIdle(build, nameof(StepObstacles));
            if (!build.ObstaclesSynced)
            {
                Physics.SyncTransforms();
                build.ObstaclesSynced = true;
            }
            int count = build.NextObstacleCells(_obstacleCells);
            if (count == 0)
                return true;

            FlowFieldGridSpace grid = build.Request.Grid;
            Vector3 half = ObstacleHalfExtents(grid.CellSize, build.Request.ObstacleClearance);
            _cellQuery.BeginObstacleStep(
                FlowFieldVolumeCellQuery.StepBounds(grid, _obstacleCells, count, half),
                build.Request.ObstacleLayer,
                _session.Obstacles);
            for (int i = 0; i < count; i++)
            {
                int index = _obstacleCells[i];
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
                        ref _registeredObstacleOverlapBuffer);
                }
            }
            return false;
        }

        private bool StepModifiers(BuildContext build)
        {
            AssertSolveIdle(build, nameof(StepModifiers));
            // The final field starts as the base field, so only cells a Modifier
            // can reach need to be walked. They are rarely more than a small part.
            if (build.ModifierCells == null)
            {
                Array.Copy(build.Directions, build.FinalDirections, build.Count);
                Array.Copy(build.Speeds, build.FinalSpeeds, build.Count);
                if (build.Modifiers.Length == 0)
                    return true;
                Physics.SyncTransforms();
                build.ModifierCells = new FlowFieldVolumeCellCursor(ModifierReach(build));
            }

            int count = build.ModifierCells.Next(build.Request.Grid, _obstacleCells);
            if (count == 0)
                return true;

            Vector3 cellHalf = Vector3.one * FlowFieldOverlapUtility.CellHalfExtent(build.Request.Grid.CellSize);
            _cellQuery.BeginModifierStep(
                FlowFieldVolumeCellQuery.StepBounds(build.Request.Grid, _obstacleCells, count, cellHalf),
                build.Modifiers);
            using FlowFieldExecutionBoundary.OperationScope userCode = _session.Boundary.EnterUserCode();
            for (int i = 0; i < count; i++)
            {
                int index = _obstacleCells[i];
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
                    if (!FlowFieldModifierMaskBuilder.IsUsableTrigger(collider))
                        continue;
                    int layerMask = 1 << collider.gameObject.layer;
                    if (!FlowFieldOverlapUtility.OverlapsTarget(
                            center,
                            cellHalf,
                            layerMask,
                            collider,
                            QueryTriggerInteraction.Collide,
                            ref _registeredObstacleOverlapBuffer))
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

        // One region around every Modifier, so a cell is walked only once and
        // each Modifier still applies in priority order.
        private static Bounds[] ModifierReach(BuildContext build)
        {
            Bounds reach = default;
            bool any = false;
            foreach (FlowFieldModifierWorkItem modifier in build.Modifiers)
            {
                if (!FlowFieldModifierMaskBuilder.IsUsableTrigger(modifier.InfluenceCollider))
                    continue;
                Bounds bounds = modifier.InfluenceCollider.bounds;
                if (any)
                {
                    reach.Encapsulate(bounds);
                }
                else
                {
                    reach = bounds;
                    any = true;
                }
            }
            if (!any)
                return Array.Empty<Bounds>();
            reach.Expand(build.Request.Grid.CellSize * 2f);
            return new[] { reach };
        }

        private bool CommitBuild(BuildContext build)
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
            bool changed = !FlowFieldField.HasSameContent(_committed, next);
            _spares.Retire(_committed, next);
            _committed = next;
            _committedReservationBytes = EstimateCommittedBytes(build.Count);
            if (!build.IsModifierRecompose)
            {
                // Changes recorded after this build started stay uncertain; a
                // continuously moving obstacle must not keep older regions unsafe.
                _safety.ClearObservedRegions();
                // The published field holds none of these.
                _spares.Costs = build.Costs ?? _spares.Costs;
                _spares.Queue = build.Queue ?? _spares.Queue;
                _spares.Influence = build.Influence ?? _spares.Influence;
            }
            _build = null;
            _buildReservationBytes = 0;
            return changed;
        }
    }
}
