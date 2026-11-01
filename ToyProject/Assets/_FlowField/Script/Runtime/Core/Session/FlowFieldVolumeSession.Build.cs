using System;
using System.Diagnostics;
using System.Threading;
using UnityEngine;
using BuildContext = Common.FlowField.FlowFieldVolumeBuildContext;
using VolumeFieldData = Common.FlowField.FlowFieldVolumeFieldData;
using BuildPhase = Common.FlowField.FlowFieldVolumeBuildPhase;
using static Common.FlowField.FlowFieldVolumeNavigation;

namespace Common.FlowField
{
    /// <summary>The cooperative stages of one Volume3D build, up to its commit.</summary>
    internal sealed partial class FlowFieldVolumeSession
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
            // Keep large managed allocations as individually observable work
            // units. A build can therefore yield between arrays instead of
            // constructing the entire Volume3D staging graph in one frame.
            switch (build.AllocationStage)
            {
                case 0:
                    build.Blocked ??= FlowFieldVolumeSpareArrays.Rent(ref _spares.Blocked, build.Count);
                    if (build.PublishedBlocked != null)
                        Array.Copy(build.PublishedBlocked, build.Blocked, build.Count);
                    break;
                case 1: build.Topology ??= FlowFieldVolumeSpareArrays.Rent(ref _spares.Topology, build.Count); break;
                case 2: build.GoalFlags = FlowFieldVolumeSpareArrays.Rent(ref _spares.GoalFlags, build.Count); break;
                case 3: build.Next = FlowFieldVolumeSpareArrays.Rent(ref _spares.Next, build.Count); break;
                case 4: build.Directions = FlowFieldVolumeSpareArrays.Rent(ref _spares.Directions, build.Count); break;
                case 5: build.Speeds = FlowFieldVolumeSpareArrays.Rent(ref _spares.Speeds, build.Count); break;
                case 6: build.FinalDirections = FlowFieldVolumeSpareArrays.Rent(ref _spares.FinalDirections, build.Count); break;
                case 7: build.FinalSpeeds = FlowFieldVolumeSpareArrays.Rent(ref _spares.FinalSpeeds, build.Count); break;
                case 8: build.EscapeDirections ??= FlowFieldVolumeSpareArrays.Rent(ref _spares.EscapeDirections, build.Count); break;
                // A static bake loads its navigation and never searches.
                case 9:
                    if (build.Request.StaticBakeData == null)
                        build.Costs = FlowFieldVolumeSpareArrays.Rent(ref _spares.Costs, build.Count);
                    break;
                case 10:
                    if (build.Request.StaticBakeData == null)
                        build.Queue = FlowFieldVolumeSpareArrays.Rent(ref _spares.Queue, build.Count);
                    break;
                case 11:
                    if (build.Request.StaticBakeData == null)
                        build.Influence = FlowFieldVolumeSpareArrays.Rent(ref _spares.Influence, build.Count);
                    break;
            }

            build.AllocationStage++;
            return build.AllocationStage > 11;
        }

        private bool StepObstacles(BuildContext build)
        {
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
                _obstacles);
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
            // The final field starts as the base field, so only cells a Modifier
            // can reach need to be walked. They are rarely more than a small part.
            if (build.ModifierCells == null)
            {
                Array.Copy(build.Directions, build.FinalDirections, build.Count);
                Array.Copy(build.Speeds, build.FinalSpeeds, build.Count);
                if (build.Modifiers.Length == 0)
                    return true;
                Physics.SyncTransforms();
                build.ModifierCells = new FlowFieldCellRegionCursor(ModifierReach(build));
            }

            int count = build.ModifierCells.Next(build.Request.Grid, _obstacleCells);
            if (count == 0)
                return true;

            Vector3 cellHalf = Vector3.one * FlowFieldOverlapUtility.CellHalfExtent(build.Request.Grid.CellSize);
            _cellQuery.BeginModifierStep(
                FlowFieldVolumeCellQuery.StepBounds(build.Request.Grid, _obstacleCells, count, cellHalf),
                build.Modifiers);
            using FlowFieldExecutionBoundary.OperationScope userCode = Boundary.EnterUserCode();
            for (int i = 0; i < count; i++)
            {
                int index = _obstacleCells[i];
                if (!FlowFieldVectorUtility.CanApplyModifier(
                        build.Blocked[index],
                        build.GoalFlags[index],
                        build.Directions[index]))
                    continue;

                build.Request.Grid.FromFlatIndex(index, out int x, out int y, out int z);
                Vector3 center = CellCenter(build.Request.Grid, index);
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
                    FlowFieldVectorState candidate = modifier.Snapshot.Modify(in current, in context);
                    FlowFieldVectorUtility.ValidateVolumeModifierOutput(candidate);
                    current = candidate;
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

        private void CommitBuild(BuildContext build)
        {
            VolumeFieldData next = new VolumeFieldData(
                build.Request,
                build.Blocked,
                build.Topology,
                build.GoalFlags,
                build.Next,
                build.Directions,
                build.Speeds,
                build.FinalDirections,
                build.FinalSpeeds,
                build.EscapeDirections,
                build.GoalIndex,
                Interlocked.Increment(ref _nextVisualizationResultId));

            // A viewed Blocked array stays out of the spares through every field sharing it.
            next.HandedOut = _committed != null
                && _committed.HandedOut
                && ReferenceEquals(_committed.Blocked, next.Blocked);
            _spares.Retire(_committed, next);
            _committed = next;
            Revision++;
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
            NotifyFieldCommitted();
        }
    }
}
