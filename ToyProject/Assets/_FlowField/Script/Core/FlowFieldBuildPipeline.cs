using System;
using Unity.Collections;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Read-only calculation view shared by runtime raycasts and static asset
    /// snapshots.  The current SurfaceBakeData implementation remains the
    /// serialized compatibility boundary; this value object keeps the build
    /// request independent from which side produced that data.
    /// </summary>
    internal readonly struct FlowFieldSurfaceData
    {
        internal FlowFieldSurfaceBakeData Source { get; }

        internal bool IsValid => Source != null && Source.HasValidData;

        internal FlowFieldSurfaceData(FlowFieldSurfaceBakeData source)
        {
            Source = source;
        }

        internal static FlowFieldSurfaceData From(FlowFieldSurfaceBakeData source)
            => new FlowFieldSurfaceData(source);
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

        internal FlowFieldBuildRequest(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldObstacleRequest obstacles,
            FlowFieldGoalResolution goal,
            FlowFieldDirtyFlags dirtyFlags,
            int maxGpuWaves,
            int version)
        {
            Grid = grid;
            Surface = surface;
            Obstacles = obstacles;
            Goal = goal;
            DirtyFlags = dirtyFlags;
            MaxGpuWaves = maxGpuWaves;
            Version = version;
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
            FlowFieldGoalBuildStatus goalStatus = FlowFieldGoalBuildStatus.None)
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
                        request.Surface.Source,
                        workspace,
                        out _);
                    hasWalkableSurface = HasWalkableSurface(request.Grid, request.Surface.Source, workspace);
                }
            }

            if (rebuildGoal || obstacleChanged)
                goalStatus = PrepareGoal(request, workspace, goalTracker);

            return new FlowFieldBuildResult(
                request.Surface,
                workspace,
                workspace.ResolvedGoalIndex,
                request.Version,
                excludedColliderCount,
                obstacleChanged,
                obstacleDirtyRegion,
                hasWalkableSurface,
                goalStatus);
        }

        private static bool HasWalkableSurface(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
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
            FlowFieldGoalTracker goalTracker)
        {
            FlowFieldGoalTracker tracker = goalTracker ?? new FlowFieldGoalTracker();
            FlowFieldGoalBuildStatus status = FlowFieldGoalPipeline.Build(
                request.Goal,
                request.Grid,
                request.Surface.Source,
                workspace,
                tracker);

            if (status == FlowFieldGoalBuildStatus.Built)
                return status;

            // A missing Goal or a Goal with no walkable cell is still a valid
            // base field. Restore the all-walkable influence/topology view so
            // the composer can use the configured default direction without
            // retaining a previous Goal's topology.
            workspace.ClearGoal();
            for (int index = 0; index < request.Grid.CellCount; index++)
            {
                workspace.InfluenceMask[index] = request.Surface.Source.IsSurfaceValid(index)
                    && !workspace.Blocked[index];
            }
            FlowFieldGraphTraversal.BuildTopologyMasks(
                request.Grid,
                request.Surface.Source,
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
        internal FlowFieldSurfaceBakeData Surface { get; }
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
            FlowFieldSurfaceBakeData surface,
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

    internal sealed class FlowFieldBuildPipeline : IDisposable
    {
        private FlowFieldComputeSolver _computeSolver;
        private bool _gpuDisabled;
        private bool _disposed;

        internal bool GpuDisabled => _gpuDisabled;

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

        internal bool StartBfs(
            in FlowFieldBfsRequest request,
            Action<FlowFieldBfsRequest> completed,
            Action<FlowFieldBfsRequest, Exception> failed)
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
                _gpuDisabled = true;
                return RunManaged(request, completed, failed, exception);
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
                    completed(requestCopy);
                }
                catch (Exception exception)
                {
                    _gpuDisabled = true;
                    RunManaged(requestCopy, completed, failed, exception);
                }
            }

            void OnGpuFailed(
                FlowFieldComputeRequest compute,
                FlowFieldComputeFailureKind kind,
                Exception exception)
            {
                _gpuDisabled = true;
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

                completed(request);
                return true;
            }
            catch (Exception exception)
            {
                failed(request, gpuException ?? exception);
                return true;
            }
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
            if (request.Surface == null || !request.Surface.HasValidData)
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

    /// <summary>
    /// Named BFS backend façade used by build-session code.  Keeping the
    /// façade separate lets callers depend on a solver runner without taking
    /// a dependency on the broader build coordinator implementation.
    /// </summary>
    internal sealed class FlowFieldBfsRunner : IDisposable
    {
        private readonly FlowFieldBuildPipeline _pipeline;

        internal bool GpuDisabled => _pipeline == null || _pipeline.GpuDisabled;

        internal FlowFieldBfsRunner(ComputeShader shader)
        {
            _pipeline = new FlowFieldBuildPipeline(shader);
        }

        internal bool Start(
            in FlowFieldBfsRequest request,
            Action<FlowFieldBfsRequest> completed,
            Action<FlowFieldBfsRequest, Exception> failed)
            => _pipeline != null && _pipeline.StartBfs(request, completed, failed);

        internal static bool BuildManaged(in FlowFieldBfsRequest request)
            => FlowFieldBuildPipeline.BuildManaged(request);

        public void Dispose()
            => _pipeline?.Dispose();
    }
}
