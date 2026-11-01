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
                                    ? BuildPhase.Goal
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
                            build.Phase = BuildPhase.Topology;
                        }
                        break;
                    case BuildPhase.Topology:
                        if (StepTopology(build))
                        {
                            build.Cursor = 0;
                            build.Phase = BuildPhase.Goal;
                        }
                        break;
                    case BuildPhase.Goal:
                        if (StepGoal(build))
                        {
                            build.Cursor = 0;
                            build.Phase = build.Request.HasGoal && build.GoalIndex >= 0
                                ? BuildPhase.Bfs
                                : BuildPhase.Directions;
                        }
                        break;
                    case BuildPhase.Bfs:
                        if (StepBfs(build))
                        {
                            build.Cursor = 0;
                            build.Phase = BuildPhase.Directions;
                        }
                        else if (build.GpuPending)
                        {
                            return false;
                        }
                        break;
                    case BuildPhase.Directions:
                        if (StepDirections(build))
                        {
                            build.Cursor = 0;
                            build.Phase = BuildPhase.Escape;
                        }
                        break;
                    case BuildPhase.Escape:
                        if (StepEscape(build))
                        {
                            build.Cursor = 0;
                            build.Phase = BuildPhase.Modifiers;
                        }
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
                case 0: build.Blocked ??= new bool[build.Count]; break;
                case 1: build.Topology ??= new uint[build.Count]; break;
                case 2: build.GoalFlags = new FlowFieldGoalFlags[build.Count]; break;
                case 3: build.Next = new int[build.Count]; break;
                case 4: build.Directions = new Vector3[build.Count]; break;
                case 5: build.Speeds = new float[build.Count]; break;
                case 6: build.FinalDirections = new Vector3[build.Count]; break;
                case 7: build.FinalSpeeds = new float[build.Count]; break;
                case 8: build.EscapeDirections = new Vector3[build.Count]; break;
                // A static bake loads its navigation and never searches.
                case 9:
                    if (build.Request.StaticBakeData == null)
                        build.Costs = RentBuildArray(ref _spareCosts, build.Count);
                    break;
                case 10:
                    if (build.Request.StaticBakeData == null)
                        build.Queue = RentBuildArray(ref _spareQueue, build.Count);
                    break;
                case 11:
                    if (build.Request.StaticBakeData == null)
                        build.Influence = RentBuildArray(ref _spareInfluence, build.Count);
                    break;
                case 12:
                    if (build.Request.StaticBakeData != null)
                    {
                        build.AllocationStage++;
                        return true;
                    }
                    int end = Math.Min(build.Cursor + ArrayElementsPerStep, build.Count);
                    for (; build.Cursor < end; build.Cursor++)
                    {
                        build.Next[build.Cursor] = -1;
                        build.Costs[build.Cursor] = Unreachable;
                    }
                    if (build.Cursor < build.Count)
                        return false;
                    build.Cursor = 0;
                    build.AllocationStage++;
                    return true;
            }

            build.AllocationStage++;
            return false;
        }

        private bool StepObstacles(BuildContext build)
        {
            if (build.Cursor == 0)
                Physics.SyncTransforms();
            int end = Math.Min(build.Cursor + CellsPerStep, build.Count);
            FlowFieldGridSpace grid = build.Request.Grid;
            Vector3 half = ObstacleHalfExtents(grid.CellSize, build.Request.ObstacleClearance);
            _cellQuery.BeginObstacleStep(
                FlowFieldVolumeCellQuery.StepBounds(grid, build.Cursor, end, half),
                build.Request.ObstacleLayer,
                _obstacles);
            for (; build.Cursor < end; build.Cursor++)
            {
                int index = build.Cursor;
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
            return build.Cursor >= build.Count;
        }

        private bool StepBfs(BuildContext build)
        {
            if (!build.GpuAttempted)
            {
                build.GpuAttempted = true;
                if (TryStartGpuBfs(build))
                {
                    // AsyncGPUReadback owns the continuation. Do not spin the
                    // cooperative scheduler while the request is in flight.
                    return false;
                }
            }

            if (build.GpuPending)
                return false;

            if (build.GpuResultPending)
            {
                if (!StepGpuResult(build))
                    return false;
                if (build.Phase != BuildPhase.Bfs)
                    return false;
            }

            if (build.GpuFailed != null
                && !StepGpuFallbackReset(build))
                return false;

            return FlowFieldVolumeNavigation.StepManagedBfs(build);
        }

        private bool TryStartGpuBfs(BuildContext build)
        {
            if (_gpuDisabled || _computeSolver == null || !_computeSolver.IsSupported
                || build.Request.HasGoal == false || build.GoalIndex < 0)
                return false;
            // A discarded build's readback is still draining. Only this build
            // falls back; the solver is free again for the next one.
            if (_computeSolver.IsRunning)
                return false;

            long currentGpuBytes = _computeSolver.AllocatedBytes;
            long requestedGpuBytes = FlowFieldComputeSolver.EstimateAllocationBytes(
                build.Count,
                volume: true);
            long additionalGpuBytes = Math.Max(0L, requestedGpuBytes - currentGpuBytes);
            if (checked(TrackedMemoryBytes + additionalGpuBytes) > MemoryBudgetBytes)
            {
                // A buffer-size change is a backend choice, not a failed
                // field request. Continue with the managed solver under the
                // same staged build and input snapshot.
                DisableGpu("GPU 버퍼가 512 MiB 메모리 예산을 넘습니다.");
                return false;
            }

            FlowFieldComputeRequest request = new FlowFieldComputeRequest(
                build.Request.Grid,
                build.Blocked,
                build.Influence,
                build.Topology,
                build.GoalIndex,
                build.Request.MaxGpuWaves);
            build.GpuPending = true;
            bool accepted;
            try
            {
                accepted = _computeSolver.Start(
                    request,
                    (completedRequest, result) => OnGpuCompleted(build, completedRequest, result),
                    (failedRequest, kind, exception) => OnGpuFailed(build, failedRequest, kind, exception));
            }
            catch (Exception exception)
            {
                build.GpuPending = false;
                DisableGpu(exception.Message);
                return false;
            }

            if (!accepted)
            {
                build.GpuPending = false;
                DisableGpu("GPU 계산 요청이 거부됐습니다.");
                return false;
            }
            return true;
        }

        private void DisableGpu(string reason)
        {
            _gpuDisabled = true;
            if (GpuFallbackReason != null)
                return;
            GpuFallbackReason = reason;
            UnityEngine.Debug.LogWarning($"[FlowField] Volume3D GPU 계산을 끄고 Managed로 전환합니다: {reason}");
        }

        private void OnGpuCompleted(
            BuildContext build,
            FlowFieldComputeRequest request,
            Unity.Collections.NativeArray<GpuFlowCell> result)
        {
            if (_build != build)
                return;
            // Keep the callback bounded: the persistent readback storage is
            // owned by FlowFieldComputeSolver until the next dispatch, while
            // validation and CPU fallback are resumed by the scheduler.
            build.GpuResult = result;
            build.GpuPending = false;
            build.GpuResultPending = true;
            build.GpuValidationCursor = 0;
        }

        private void OnGpuFailed(
            BuildContext build,
            FlowFieldComputeRequest request,
            FlowFieldComputeFailureKind kind,
            Exception exception)
        {
            if (_build != build)
                return;
            DisableGpu(exception != null ? $"{kind}: {exception.Message}" : kind.ToString());
            build.GpuPending = false;
            build.GpuFailed = exception
                ?? new InvalidOperationException($"Volume3D GPU BFS failed ({kind}).");
            build.GpuFallbackResetCursor = 0;
        }

        private bool StepGpuResult(BuildContext build)
        {
            if (!build.GpuResult.IsCreated || build.GpuResult.Length != build.Count)
            {
                HandleGpuValidationFailure(
                    build,
                    new InvalidOperationException("Volume3D GPU result length does not match the active grid."));
                return true;
            }

            int end = Math.Min(
                build.GpuValidationCursor + ArrayElementsPerStep,
                build.Count);
            try
            {
                FlowFieldVolumeGpuResultValidator.Apply(
                    build,
                    build.GpuResult,
                    build.GpuValidationCursor,
                    end);
            }
            catch (Exception exception)
            {
                HandleGpuValidationFailure(build, exception);
                return true;
            }

            build.GpuValidationCursor = end;
            if (build.GpuValidationCursor < build.Count)
                return false;

            build.GpuResultPending = false;
            build.GpuValidationCursor = 0;
            build.Cursor = 0;
            build.Phase = BuildPhase.Escape;
            return true;
        }

        private void HandleGpuValidationFailure(BuildContext build, Exception exception)
        {
            DisableGpu(exception?.Message ?? "GPU 결과 검증에 실패했습니다.");
            build.GpuResultPending = false;
            build.GpuFailed = exception
                ?? new InvalidOperationException("Volume3D GPU result validation failed.");
            build.GpuFallbackResetCursor = 0;
            build.Cursor = 0;
        }

        private bool StepModifiers(BuildContext build)
        {
            if (build.Cursor == 0 && build.Modifiers.Length > 0)
                Physics.SyncTransforms();

            int end = Math.Min(build.Cursor + CellsPerStep, build.Count);
            Vector3 cellHalf = Vector3.one * FlowFieldOverlapUtility.CellHalfExtent(build.Request.Grid.CellSize);
            if (build.Modifiers.Length > 0)
            {
                _cellQuery.BeginModifierStep(
                    FlowFieldVolumeCellQuery.StepBounds(build.Request.Grid, build.Cursor, end, cellHalf),
                    build.Modifiers);
            }
            using FlowFieldExecutionBoundary.OperationScope userCode = Boundary.EnterUserCode();
            for (; build.Cursor < end; build.Cursor++)
            {
                int index = build.Cursor;
                if (!FlowFieldVectorUtility.CanApplyModifier(
                        build.Blocked[index],
                        build.GoalFlags[index],
                        build.Directions[index]))
                {
                    build.FinalDirections[index] = build.Directions[index];
                    build.FinalSpeeds[index] = build.Speeds[index];
                    continue;
                }

                build.FinalDirections[index] = build.Directions[index];
                build.FinalSpeeds[index] = build.Speeds[index];
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
            return build.Cursor >= build.Count;
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

            _committed = next;
            Revision++;
            _committedReservationBytes = EstimateCommittedBytes(build.Count);
            if (!build.IsModifierRecompose)
            {
                // Changes recorded after this build started stay uncertain; a
                // continuously moving obstacle must not keep older regions unsafe.
                _safety.ClearObservedRegions();
                // The published field holds none of these; a completed build has
                // no GPU readback left that could still read them.
                _spareCosts = build.Costs ?? _spareCosts;
                _spareQueue = build.Queue ?? _spareQueue;
                _spareInfluence = build.Influence ?? _spareInfluence;
            }
            _build = null;
            _buildReservationBytes = 0;
            NotifyFieldCommitted();
        }
    }
}
