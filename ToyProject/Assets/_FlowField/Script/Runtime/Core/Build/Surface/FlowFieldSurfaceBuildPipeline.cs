using System;
using Unity.Collections;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Inputs consumed by Surface2D base preparation. Grid and Surface come
    /// from the obstacle request so the two stages share one source identity.
    /// </summary>
    internal readonly struct FlowFieldSurfaceBuildRequest
    {
        internal FlowFieldGridSpace Grid => Obstacles.Grid;
        internal FlowFieldSurfaceData Surface => Obstacles.Surface;
        internal FlowFieldObstacleRequest Obstacles { get; }
        internal FlowFieldGoalResolution Goal { get; }
        internal bool SurfaceChanged { get; }

        internal FlowFieldSurfaceBuildRequest(
            FlowFieldObstacleRequest obstacles,
            FlowFieldGoalResolution goal,
            bool surfaceChanged = false)
        {
            Obstacles = obstacles;
            Goal = goal;
            SurfaceChanged = surfaceChanged;
        }
    }

    /// <summary>
    /// Effective changes from base preparation. Navigation arrays remain in
    /// the session's staging workspace; this result carries only decisions.
    /// </summary>
    internal readonly struct FlowFieldBuildResult
    {
        internal bool ObstacleMaskChanged { get; }
        internal FlowFieldCellRect ObstacleDirtyRegion { get; }
        internal bool HasWalkableSurface { get; }
        internal FlowFieldGoalBuildStatus GoalStatus { get; }
        internal bool GoalChanged => GoalStatus == FlowFieldGoalBuildStatus.Built
            || GoalStatus == FlowFieldGoalBuildStatus.NoWalkableSurface
            || GoalStatus == FlowFieldGoalBuildStatus.Invalid;

        internal FlowFieldBuildResult(
            bool obstacleMaskChanged,
            FlowFieldCellRect obstacleDirtyRegion,
            bool hasWalkableSurface,
            FlowFieldGoalBuildStatus goalStatus)
        {
            ObstacleMaskChanged = obstacleMaskChanged;
            ObstacleDirtyRegion = obstacleDirtyRegion;
            HasWalkableSurface = hasWalkableSurface;
            GoalStatus = goalStatus;
        }
    }

    /// <summary>
    /// Runs the shared, synchronous base-field preparation stages.  Runtime
    /// and editor adapters can choose which dirty obstacle stages to execute,
    /// but the actual mask, escape, Goal and topology code remains here.
    /// BFS itself is started separately so a caller can select the GPU runner
    /// and keep its own commit/session lifetime rules.
    /// </summary>
    internal sealed partial class FlowFieldSurfaceBuildPipeline
    {
        internal static FlowFieldBuildResult PrepareBase(
            in FlowFieldSurfaceBuildRequest request,
            FlowFieldObstaclePipeline obstaclePipeline,
            FlowFieldGoalTracker goalTracker,
            bool rebuildStaticObstacles,
            bool rebuildDynamicObstacles,
            bool rebuildGoal)
        {
            ValidateRequest(request, obstaclePipeline);
            FlowFieldWorkspace workspace = request.Obstacles.Workspace;
            bool obstacleChanged = false;
            FlowFieldCellRect obstacleDirtyRegion = FlowFieldCellRect.Invalid;
            bool hasWalkableSurface = false;
            FlowFieldGoalBuildStatus goalStatus = FlowFieldGoalBuildStatus.None;

            if (rebuildStaticObstacles || rebuildDynamicObstacles)
            {
                FlowFieldObstacleResult obstacleResult = obstaclePipeline.RebuildMasks(
                    request.Obstacles,
                    rebuildStaticObstacles,
                    rebuildDynamicObstacles);
                obstacleChanged = obstacleResult.MaskChanged;
                obstacleDirtyRegion = obstacleResult.DirtyRegion;
                if (obstacleChanged)
                {
                    obstaclePipeline.CommitCombinedAndBuildEscape(
                        request.Grid,
                        request.Surface,
                        workspace,
                        out _);
                }
                // The first obstacle preparation can legitimately produce an
                // all-false mask (there are simply no colliders).  It is still
                // a completed probe, so compute the diagnostic from the
                // effective scratch/committed mask instead of reporting a
                // spurious "all cells blocked" warning.
                hasWalkableSurface = HasWalkableSurface(request.Grid, request.Surface, workspace);
            }

            if (rebuildGoal || obstacleChanged)
                goalStatus = PrepareGoal(
                    request,
                    workspace,
                    goalTracker,
                    forceRebuild: obstacleChanged || request.SurfaceChanged);

            return new FlowFieldBuildResult(
                obstacleChanged,
                obstacleDirtyRegion,
                hasWalkableSurface,
                goalStatus);
        }

        private static bool HasWalkableSurface(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldWorkspace workspace)
        {
            for (int index = 0; index < grid.CellCount; index++)
            {
                if (surface.IsSurfaceValid(index) && !workspace.Blocked[index])
                    return true;
            }

            return false;
        }

        private static FlowFieldGoalBuildStatus PrepareGoal(
            in FlowFieldSurfaceBuildRequest request,
            FlowFieldWorkspace workspace,
            FlowFieldGoalTracker goalTracker,
            bool forceRebuild)
        {
            FlowFieldGoalTracker tracker = goalTracker ?? new FlowFieldGoalTracker();
            FlowFieldGoalBuildStatus status = FlowFieldGoalPipeline.Build(
                request.Goal,
                request.Grid,
                request.Surface,
                workspace,
                tracker,
                forceRebuild);

            if (status == FlowFieldGoalBuildStatus.Built
                || status == FlowFieldGoalBuildStatus.Unchanged)
                return status;

            // A missing Goal or a Goal with no walkable cell is still a valid
            // base field. Restore the all-walkable influence/topology view so
            // the composer can use the configured default direction without
            // retaining a previous Goal's topology.
            workspace.ClearGoal();
            for (int index = 0; index < request.Grid.CellCount; index++)
            {
                workspace.InfluenceMask[index] = request.Surface.IsSurfaceValid(index)
                    && !workspace.Blocked[index];
            }
            FlowFieldGraphTraversal.BuildTopologyMasks(
                request.Grid,
                request.Surface,
                workspace);
            FlowFieldGraphTraversal.ApplyNoGoalSentinels(
                request.Grid,
                request.Surface,
                workspace);
            return status;
        }

        private static void ValidateRequest(
            in FlowFieldSurfaceBuildRequest request,
            FlowFieldObstaclePipeline obstaclePipeline)
        {
            if (!request.Grid.IsValid)
                throw new ArgumentException("FlowField base build requires a valid grid.", nameof(request));
            if (!request.Surface.IsValid)
                throw new ArgumentException("FlowField base build requires a valid surface view.", nameof(request));
            if (obstaclePipeline == null)
                throw new ArgumentNullException(nameof(obstaclePipeline));
            if (request.Obstacles.Workspace == null
                || request.Obstacles.Workspace.Capacity != request.Grid.CellCount)
                throw new ArgumentException("FlowField base workspace capacity does not match the grid.", nameof(request));
            if (!request.Goal.HasActiveGoal && request.Goal.IsValid)
                throw new ArgumentException("A Goal resolution without an active Goal is inconsistent.", nameof(request));
            if (request.Goal.HasActiveGoal && !request.Goal.IsValid)
                throw new ArgumentException("An active FlowField Goal must resolve to an in-bounds cell.", nameof(request));
            if (request.Goal.IsValid)
            {
                if (!request.Grid.IsLocalInBounds(request.Goal.LocalX, request.Goal.LocalZ))
                    throw new ArgumentOutOfRangeException(nameof(request.Goal), "Resolved Goal cell is outside the grid.");
                if (request.Goal.SourceCellIndex != request.Grid.ToFlatIndex(request.Goal.LocalX, request.Goal.LocalZ))
                    throw new ArgumentException("Resolved Goal index does not match its local coordinates.", nameof(request.Goal));
                if (!FlowFieldGridSpace.IsFinite(request.Goal.InfluenceRadius)
                    || request.Goal.InfluenceRadius < 0f
                    || !FlowFieldGridSpace.IsFinite(request.Goal.RequestedWorld))
                    throw new ArgumentOutOfRangeException(nameof(request.Goal), "Goal resolution contains a non-finite value.");
            }
        }
    }

    /// <summary>
    /// The common BFS portion of a FlowField build.  Runtime and editor code
    /// prepare the same workspace and enter this runner; only their commit
    /// side effects differ.
    /// </summary>
    internal readonly struct FlowFieldBfsRequest
    {
        private readonly Func<bool> _isCurrent;
        // Solvers write staging arrays before notifying the session. Check
        // ownership here as well as at the session's publication boundary.
        internal bool IsCurrent => _isCurrent == null || _isCurrent();
        internal FlowFieldGridSpace Grid { get; }
        internal FlowFieldSurfaceData Surface { get; }
        internal FlowFieldWorkspace Workspace { get; }
        internal bool HasGoal { get; }
        internal int GoalX { get; }
        internal int GoalZ { get; }
        internal float GoalInfluenceRadius { get; }
        internal int GoalIndex { get; }
        internal int MaxGpuWaves { get; }

        internal FlowFieldBfsRequest(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldWorkspace workspace,
            bool hasGoal,
            int goalX,
            int goalZ,
            float goalInfluenceRadius,
            int goalIndex,
            int maxGpuWaves,
            Func<bool> isCurrent = null)
        {
            _isCurrent = isCurrent;
            Grid = grid;
            Surface = surface;
            Workspace = workspace;
            HasGoal = hasGoal;
            GoalX = goalX;
            GoalZ = goalZ;
            GoalInfluenceRadius = goalInfluenceRadius;
            GoalIndex = goalIndex;
            MaxGpuWaves = maxGpuWaves;
        }

    }

    internal sealed partial class FlowFieldSurfaceBuildPipeline : IFlowFieldGpuBackend
    {
        private FlowFieldComputeSolver _computeSolver;
        private bool _gpuDisabled;
        private bool _disposed;

        internal bool GpuDisabled => _gpuDisabled;
        internal string GpuFallbackReason { get; private set; }
        public bool SupportsGpu => !_gpuDisabled && _computeSolver != null && _computeSolver.IsSupported;

        internal FlowFieldSurfaceBuildPipeline(ComputeShader shader)
        {
            if (shader == null)
            {
                _gpuDisabled = true;
                return;
            }

            try
            {
                _computeSolver = new FlowFieldComputeSolver(shader);
                if (!_computeSolver.IsSupported)
                    DisableGpu("Compute Shader 또는 AsyncGPUReadback을 지원하지 않습니다.");
            }
            catch (Exception exception)
            {
                DisableGpu(exception.Message);
                _computeSolver = null;
            }
        }

        private void DisableGpu(string reason)
        {
            _gpuDisabled = true;
            GpuFallbackReason ??= reason;
        }

        public bool StartBfs(
            in FlowFieldBfsRequest request,
            Action<FlowFieldBfsRequest> completed,
            Action<FlowFieldBfsRequest, Exception> failed,
            bool allowManagedFallback = true)
        {
            if (_disposed || completed == null || failed == null || !request.IsCurrent)
                return false;
            ValidateRequest(request);

            if (!request.HasGoal)
            {
                request.Workspace.ClearGoal();
                completed(request);
                return true;
            }

            if (_gpuDisabled || _computeSolver == null || !_computeSolver.IsSupported)
            {
                if (!allowManagedFallback)
                {
                    failed(request, new PlatformNotSupportedException("FlowField GPU backend is unavailable."));
                    return false;
                }
                return RunManaged(request, completed, failed);
            }

            var attempt = new GpuAttempt(this, request, completed, failed, allowManagedFallback);
            try
            {
                FlowFieldComputeRequest computeRequest = new FlowFieldComputeRequest(
                    request.Grid,
                    request.Surface,
                    request.Workspace,
                    request.GoalIndex,
                    request.MaxGpuWaves);
                bool accepted = _computeSolver.Start(
                    computeRequest,
                    attempt.OnCompleted,
                    attempt.OnFailed);
                if (accepted)
                    return true;
            }
            catch (Exception exception)
            {
                if (!allowManagedFallback)
                {
                    failed(request, exception);
                    return false;
                }
                DisableGpu(exception.Message);
                return RunManaged(request, completed, failed, exception);
            }

            if (!allowManagedFallback)
            {
                failed(request, new InvalidOperationException("FlowField GPU dispatch was not accepted."));
                return false;
            }
            DisableGpu("GPU 계산 요청이 거부됐습니다.");
            return RunManaged(request, completed, failed, null);
        }

        private sealed class GpuAttempt
        {
            private readonly FlowFieldSurfaceBuildPipeline _owner;
            private readonly FlowFieldBfsRequest _request;
            private readonly Action<FlowFieldBfsRequest> _completed;
            private readonly Action<FlowFieldBfsRequest, Exception> _failed;
            private readonly bool _allowManagedFallback;

            internal GpuAttempt(
                FlowFieldSurfaceBuildPipeline owner,
                in FlowFieldBfsRequest request,
                Action<FlowFieldBfsRequest> completed,
                Action<FlowFieldBfsRequest, Exception> failed,
                bool allowManagedFallback)
            {
                _owner = owner;
                _request = request;
                _completed = completed;
                _failed = failed;
                _allowManagedFallback = allowManagedFallback;
            }

            internal void OnCompleted(
                FlowFieldComputeRequest compute,
                NativeArray<GpuFlowCell> result)
            {
                if (!_request.IsCurrent)
                    return;
                try
                {
                    ApplyGpuResult(compute, result);
                }
                catch (Exception exception)
                {
                    if (!_allowManagedFallback)
                    {
                        _failed(_request, exception);
                        return;
                    }
                    _owner.DisableGpu(exception.Message);
                    RunManaged(_request, _completed, _failed, exception);
                    return;
                }
                // Consumer composition/commit is deliberately outside the
                // GPU validation catch. A compose error must Fault the
                // session once, never rerun BFS through the fallback path.
                _completed(_request);
            }

            internal void OnFailed(
                FlowFieldComputeRequest compute,
                FlowFieldComputeFailureKind kind,
                Exception exception)
            {
                if (!_request.IsCurrent)
                    return;
                _owner.DisableGpu(exception != null ? $"{kind}: {exception.Message}" : kind.ToString());
                if (!_allowManagedFallback)
                {
                    _failed(_request, exception);
                    return;
                }
                RunManaged(_request, _completed, _failed, exception);
            }
        }

        private static bool RunManaged(
            in FlowFieldBfsRequest request,
            Action<FlowFieldBfsRequest> completed,
            Action<FlowFieldBfsRequest, Exception> failed,
            Exception gpuException = null)
        {
            if (!request.IsCurrent)
                return false;
            try
            {
                if (request.HasGoal && request.GoalIndex >= 0 && gpuException == null)
                {
                    // The Goal stage prepared this request. GPU fallback below
                    // prepares again because a failed result may have written.
                    FlowFieldSolver.SolvePreparedGoal(
                        request.Grid,
                        request.Surface,
                        request.Workspace,
                        request.GoalIndex);
                }
                else if (request.HasGoal)
                {
                    if (!FlowFieldSolver.BuildGoal(
                            request.Grid,
                            request.Surface,
                            request.Workspace,
                            request.GoalX,
                            request.GoalZ,
                            request.GoalInfluenceRadius,
                            out int resolvedGoalIndex))
                    {
                        request.Workspace.ClearGoal();
                    }
                    else if (resolvedGoalIndex != request.GoalIndex)
                    {
                        throw new InvalidOperationException(
                            "Managed BFS resolved a different Goal index than the prepared request.");
                    }
                }
                else
                {
                    request.Workspace.ClearGoal();
                }

            }
            catch (Exception exception)
            {
                // The Managed path is the source of truth for a fallback
                // failure. Preserve the GPU diagnostic as an inner error,
                // but never hide the exception raised by the actual fallback
                // solver (tests and callers rely on that transition).
                failed(
                    request,
                    gpuException == null
                        ? exception
                        : new AggregateException(
                            "Managed FlowField BFS failed after GPU fallback.",
                            gpuException,
                            exception));
                return true;
            }

            // Keep consumer callbacks outside the solver catch boundary.
            completed(request);
            return true;
        }

        /// <summary>
        /// Synchronous managed entry point used by the editor preview and by
        /// static bake staging when no asynchronous GPU session is needed.
        /// It deliberately executes the same goal preparation and FIFO solver
        /// path as the runtime fallback.
        /// </summary>
        internal static bool BuildManaged(in FlowFieldBfsRequest request)
        {
            Exception failure = null;
            bool completed = false;
            RunManaged(
                request,
                _ => completed = true,
                (_, exception) => failure = exception);
            if (failure != null)
                throw failure;
            return completed;
        }

        private static void ApplyGpuResult(
            in FlowFieldComputeRequest request,
            NativeArray<GpuFlowCell> result)
        {
            if (!result.IsCreated || result.Length != request.Grid.CellCount)
                throw new InvalidOperationException("FlowField GPU result length does not match the active grid.");

            for (int index = 0; index < request.Grid.CellCount; index++)
            {
                GpuFlowCell cell = result[index];
                if (cell.NextCell < -3 || cell.NextCell >= request.Grid.CellCount)
                    throw new InvalidOperationException("FlowField GPU result contains an invalid NextCell sentinel.");

                Vector3 direction = cell.Direction;
                if (!FlowFieldGridSpace.IsFinite(direction))
                    throw new InvalidOperationException("FlowField GPU result contains a non-finite direction.");

                if (cell.NextCell >= 0)
                {
                    if (!FlowFieldGraphTraversal.IsCellTraversable(
                            request.Surface,
                            request.Workspace,
                            index))
                        throw new InvalidOperationException("FlowField GPU directed a blocked or invalid cell.");

                    if (cell.NextCell == index)
                    {
                        if (index != request.Workspace.ResolvedGoalIndex)
                            throw new InvalidOperationException("FlowField GPU produced a non-goal anchor.");
                        direction = Vector3.zero;
                    }
                    else
                    {
                        request.Grid.FromFlatIndex(index, out int x, out int z);
                        request.Grid.FromFlatIndex(cell.NextCell, out int nextX, out int nextZ);
                        int directionIndex = FlowFieldNeighborUtility.FindDirectionIndex(
                            nextX - x,
                            nextZ - z);
                        if (directionIndex < 0
                            || (request.Workspace.TopologyMasks[index] & (1 << directionIndex)) == 0)
                            throw new InvalidOperationException("FlowField GPU produced a non-topological NextCell.");
                        if (!FlowFieldGraphTraversal.IsCellTraversable(
                                request.Surface,
                                request.Workspace,
                                cell.NextCell))
                            throw new InvalidOperationException("FlowField GPU produced a blocked or invalid NextCell.");

                        direction = Vector3.ProjectOnPlane(
                            direction,
                            request.Surface.GetSurfaceNormal(index));
                        if (direction.sqrMagnitude <= FlowFieldVectorUtility.DIRECTION_EPSILON_SQR)
                            throw new InvalidOperationException("FlowField GPU produced a zero direction for a traversable cell.");
                        direction.Normalize();
                    }
                }
                else
                {
                    direction = Vector3.zero;
                }

                request.Workspace.GoalDirections[index] = direction;
                request.Workspace.NextCells[index] = cell.NextCell;
                if (cell.NextCell >= 0)
                {
                    request.Workspace.GoalFlags[index] = FlowFieldGoalFlags.Directed;
                    if (cell.NextCell == index)
                        request.Workspace.GoalFlags[index] |= FlowFieldGoalFlags.Anchor;
                }
                else if (cell.NextCell == -3)
                {
                    request.Workspace.GoalFlags[index] = FlowFieldGoalFlags.Unreachable;
                }
                else
                {
                    request.Workspace.GoalFlags[index] = FlowFieldGoalFlags.None;
                }
            }
        }

        private static void ValidateRequest(in FlowFieldBfsRequest request)
        {
            if (!request.Grid.IsValid)
                throw new ArgumentException("FlowField BFS requires a valid grid.", nameof(request));
            if (!request.Surface.IsValid)
                throw new ArgumentException("FlowField BFS requires a valid Surface Bake.", nameof(request));
            if (request.Workspace == null || request.Workspace.Capacity != request.Grid.CellCount)
                throw new ArgumentException("FlowField BFS workspace capacity does not match the grid.", nameof(request));
            if (!request.HasGoal)
                return;
            if (!request.Grid.IsLocalInBounds(request.GoalX, request.GoalZ)
                || request.GoalIndex < 0
                || request.GoalIndex >= request.Grid.CellCount)
                throw new ArgumentOutOfRangeException(nameof(request.GoalIndex));
            if (!FlowFieldGridSpace.IsFinite(request.GoalInfluenceRadius)
                || request.GoalInfluenceRadius < 0f)
                throw new ArgumentOutOfRangeException(nameof(request.GoalInfluenceRadius));
            if (request.Workspace.ResolvedGoalIndex != request.GoalIndex
                || !FlowFieldGraphTraversal.IsCellTraversable(
                    request.Surface,
                    request.Workspace,
                    request.GoalIndex))
                throw new InvalidOperationException(
                    "FlowField BFS Goal must be the resolved walkable cell in the prepared workspace.");
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _computeSolver?.Dispose();
            _computeSolver = null;
        }
    }

}
