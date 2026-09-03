using System;
using Unity.Collections;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Narrow backend contract consumed by FlowFieldSession. Tests can inject
    /// a deterministic or delayed implementation while production uses the
    /// Compute/Managed implementation below.
    /// </summary>
    internal interface IFlowFieldBfsBackend : IDisposable
    {
        bool SupportsGpu { get; }

        bool StartBfs(
            in FlowFieldBfsRequest request,
            Action<FlowFieldBfsRequest> completed,
            Action<FlowFieldBfsRequest, Exception> failed,
            bool allowManagedFallback = true);
    }

    /// <summary>
    /// Complete, shared build input.  Runtime and editor adapters fill this
    /// value from their respective input sources before invoking the same
    /// stage implementations.
    /// </summary>
    internal readonly struct FlowFieldBuildRequest
    {
        internal FlowFieldGridSpace Grid { get; }
        internal FlowFieldSurfaceData Surface { get; }
        internal FlowFieldObstacleRequest Obstacles { get; }
        internal FlowFieldGoalResolution Goal { get; }
        internal FlowFieldDirtyFlags DirtyFlags { get; }
        internal int MaxGpuWaves { get; }
        internal int Version { get; }
        internal bool SurfaceChanged { get; }

        internal FlowFieldBuildRequest(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldObstacleRequest obstacles,
            FlowFieldGoalResolution goal,
            FlowFieldDirtyFlags dirtyFlags,
            int maxGpuWaves,
            int version,
            bool surfaceChanged = false)
        {
            Grid = grid;
            Surface = surface;
            Obstacles = obstacles;
            Goal = goal;
            DirtyFlags = dirtyFlags;
            MaxGpuWaves = maxGpuWaves;
            Version = version;
            SurfaceChanged = surfaceChanged;
        }
    }

    /// <summary>
    /// Base-field output passed from the shared solver to a runtime or editor
    /// commit adapter. Arrays intentionally reference the staging workspace;
    /// no modifier/final direction data is persisted here.
    /// </summary>
    internal readonly struct FlowFieldBuildResult
    {
        internal FlowFieldSurfaceData Surface { get; }
        internal FlowFieldWorkspace Workspace { get; }
        internal int ResolvedGoalIndex { get; }
        internal int Version { get; }
        internal int ExcludedColliderCount { get; }
        internal bool ObstacleMaskChanged { get; }
        internal FlowFieldCellRect ObstacleDirtyRegion { get; }
        internal bool HasWalkableSurface { get; }
        internal FlowFieldGoalBuildStatus GoalStatus { get; }
        internal FlowFieldBuildDelta Delta { get; }

        internal bool[] Blocked => Workspace?.Blocked;
        internal Vector3[] EscapeDirections => Workspace?.EscapeDirections;
        internal byte[] TopologyMasks => Workspace?.TopologyMasks;
        internal Vector3[] GoalDirections => Workspace?.GoalDirections;
        internal int[] NextCells => Workspace?.NextCells;

        internal FlowFieldBuildResult(
            FlowFieldSurfaceData surface,
            FlowFieldWorkspace workspace,
            int resolvedGoalIndex,
            int version,
            int excludedColliderCount = 0,
            bool obstacleMaskChanged = false,
            FlowFieldCellRect obstacleDirtyRegion = default,
            bool hasWalkableSurface = false,
            FlowFieldGoalBuildStatus goalStatus = FlowFieldGoalBuildStatus.None,
            bool surfaceChanged = false)
        {
            Surface = surface;
            Workspace = workspace;
            ResolvedGoalIndex = resolvedGoalIndex;
            Version = version;
            ExcludedColliderCount = excludedColliderCount;
            ObstacleMaskChanged = obstacleMaskChanged;
            ObstacleDirtyRegion = obstacleDirtyRegion;
            HasWalkableSurface = hasWalkableSurface;
            GoalStatus = goalStatus;
            bool goalChanged = goalStatus == FlowFieldGoalBuildStatus.Built
                || goalStatus == FlowFieldGoalBuildStatus.NoWalkableSurface
                || goalStatus == FlowFieldGoalBuildStatus.Invalid;
            Delta = new FlowFieldBuildDelta(
                surfaceChanged: surfaceChanged,
                obstacleMaskChanged: obstacleMaskChanged,
                goalChanged: goalChanged,
                needsBfs: goalStatus == FlowFieldGoalBuildStatus.Built || obstacleMaskChanged,
                finalDirtyRect: surfaceChanged || goalChanged
                    ? FlowFieldCellRect.Full(surface == null ? default : surface.Grid)
                    : obstacleDirtyRegion);
        }
    }

    /// <summary>
    /// Effective build changes after input probes have been evaluated. Raw
    /// dirty flags are intentionally not exposed to pipeline consumers.
    /// </summary>
    internal readonly struct FlowFieldBuildDelta
    {
        internal bool SurfaceChanged { get; }
        internal bool ObstacleMaskChanged { get; }
        internal bool GoalChanged { get; }
        internal bool NeedsBfs { get; }
        internal FlowFieldCellRect FinalDirtyRect { get; }

        internal FlowFieldBuildDelta(
            bool surfaceChanged,
            bool obstacleMaskChanged,
            bool goalChanged,
            bool needsBfs,
            FlowFieldCellRect finalDirtyRect)
        {
            SurfaceChanged = surfaceChanged;
            ObstacleMaskChanged = obstacleMaskChanged;
            GoalChanged = goalChanged;
            NeedsBfs = needsBfs;
            FinalDirtyRect = finalDirtyRect;
        }
    }

    /// <summary>
    /// Runs the shared, synchronous base-field preparation stages.  Runtime
    /// and editor adapters can choose which dirty obstacle stages to execute,
    /// but the actual mask, escape, Goal and topology code remains here.
    /// BFS itself is started separately so a caller can select the GPU runner
    /// and keep its own commit/session lifetime rules.
    /// </summary>
    internal static class FlowFieldBuildCoordinator
    {
        internal static FlowFieldBuildResult PrepareBase(
            in FlowFieldBuildRequest request,
            FlowFieldObstaclePipeline obstaclePipeline,
            FlowFieldGoalTracker goalTracker,
            bool rebuildStaticObstacles,
            bool rebuildDynamicObstacles,
            bool rebuildGoal)
        {
            ValidateRequest(request, obstaclePipeline);
            FlowFieldWorkspace workspace = request.Obstacles.Workspace;
            bool obstacleChanged = false;
            int excludedColliderCount = 0;
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
                excludedColliderCount = obstacleResult.ExcludedColliderCount;
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
                request.Surface,
                workspace,
                workspace.ResolvedGoalIndex,
                request.Version,
                excludedColliderCount,
                obstacleChanged,
                obstacleDirtyRegion,
                hasWalkableSurface,
                goalStatus,
                request.SurfaceChanged);
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
            in FlowFieldBuildRequest request,
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
            in FlowFieldBuildRequest request,
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
        internal FlowFieldGridSpace Grid { get; }
        internal FlowFieldSurfaceData Surface { get; }
        internal FlowFieldWorkspace Workspace { get; }
        internal bool HasGoal { get; }
        internal int GoalX { get; }
        internal int GoalZ { get; }
        internal float GoalInfluenceRadius { get; }
        internal int GoalIndex { get; }
        internal int MaxGpuWaves { get; }
        internal int Version { get; }

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
            int version)
        {
            Grid = grid;
            Surface = surface;
            Workspace = workspace;
            HasGoal = hasGoal;
            GoalX = goalX;
            GoalZ = goalZ;
            GoalInfluenceRadius = goalInfluenceRadius;
            GoalIndex = goalIndex;
            MaxGpuWaves = maxGpuWaves;
            Version = version;
        }

    }

    internal sealed class FlowFieldBuildPipeline : IFlowFieldBfsBackend
    {
        private FlowFieldComputeSolver _computeSolver;
        private bool _gpuDisabled;
        private bool _disposed;

        internal bool GpuDisabled => _gpuDisabled;
        public bool SupportsGpu => !_gpuDisabled && _computeSolver != null && _computeSolver.IsSupported;

        internal FlowFieldBuildPipeline(ComputeShader shader)
        {
            if (shader == null)
            {
                _gpuDisabled = true;
                return;
            }

            try
            {
                _computeSolver = new FlowFieldComputeSolver(shader);
                _gpuDisabled = !_computeSolver.IsSupported;
            }
            catch
            {
                _gpuDisabled = true;
                _computeSolver = null;
            }
        }

        /// <summary>
        /// Shared Surface/Obstacle/Goal/Topology preparation entry point.
        /// The instance side of this type owns the asynchronous GPU backend;
        /// this static entry point keeps the stage coordinator discoverable
        /// from the same pipeline used by runtime BFS sessions.
        /// </summary>
        internal static FlowFieldBuildResult PrepareBase(
            in FlowFieldBuildRequest request,
            FlowFieldObstaclePipeline obstaclePipeline,
            FlowFieldGoalTracker goalTracker,
            bool rebuildStaticObstacles,
            bool rebuildDynamicObstacles,
            bool rebuildGoal)
            => FlowFieldBuildCoordinator.PrepareBase(
                request,
                obstaclePipeline,
                goalTracker,
                rebuildStaticObstacles,
                rebuildDynamicObstacles,
                rebuildGoal);

        public bool StartBfs(
            in FlowFieldBfsRequest request,
            Action<FlowFieldBfsRequest> completed,
            Action<FlowFieldBfsRequest, Exception> failed,
            bool allowManagedFallback = true)
        {
            if (_disposed || completed == null || failed == null)
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

            FlowFieldBfsRequest requestCopy = request;
            try
            {
                FlowFieldComputeRequest computeRequest = new FlowFieldComputeRequest(
                    request.Grid,
                    request.Surface,
                    request.Workspace,
                    request.GoalIndex,
                    request.MaxGpuWaves,
                    request.Version);
                bool accepted = _computeSolver.Start(
                    computeRequest,
                    OnGpuCompleted,
                    OnGpuFailed);
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
                _gpuDisabled = true;
                return RunManaged(request, completed, failed, exception);
            }

            if (!allowManagedFallback)
            {
                failed(request, new InvalidOperationException("FlowField GPU dispatch was not accepted."));
                return false;
            }
            _gpuDisabled = true;
            return RunManaged(request, completed, failed, null);

            void OnGpuCompleted(
                FlowFieldComputeRequest compute,
                NativeArray<GpuFlowCell> result)
            {
                try
                {
                    ApplyGpuResult(compute, result);
                }
                catch (Exception exception)
                {
                    if (!allowManagedFallback)
                    {
                        failed(requestCopy, exception);
                        return;
                    }
                    _gpuDisabled = true;
                    RunManaged(requestCopy, completed, failed, exception);
                    return;
                }
                // Consumer composition/commit is deliberately outside the
                // GPU validation catch. A compose error must Fault the
                // session once, never rerun BFS through the fallback path.
                completed(requestCopy);
            }

            void OnGpuFailed(
                FlowFieldComputeRequest compute,
                FlowFieldComputeFailureKind kind,
                Exception exception)
            {
                _gpuDisabled = true;
                if (!allowManagedFallback)
                {
                    failed(requestCopy, exception);
                    return;
                }
                RunManaged(requestCopy, completed, failed, exception);
            }
        }

        private static bool RunManaged(
            in FlowFieldBfsRequest request,
            Action<FlowFieldBfsRequest> completed,
            Action<FlowFieldBfsRequest, Exception> failed,
            Exception gpuException = null)
        {
            try
            {
                if (request.HasGoal)
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
