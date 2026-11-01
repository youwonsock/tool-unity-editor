using System;
using Unity.Jobs;
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
    /// BFS itself is started separately so a caller keeps its own
    /// commit/session lifetime rules.
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
        internal FlowFieldGridSpace Grid { get; }
        internal FlowFieldSurfaceData Surface { get; }
        internal FlowFieldWorkspace Workspace { get; }
        internal bool HasGoal { get; }
        internal int GoalX { get; }
        internal int GoalZ { get; }
        internal float GoalInfluenceRadius { get; }
        internal int GoalIndex { get; }

        internal FlowFieldBfsRequest(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldWorkspace workspace,
            bool hasGoal,
            int goalX,
            int goalZ,
            float goalInfluenceRadius,
            int goalIndex)
        {
            Grid = grid;
            Surface = surface;
            Workspace = workspace;
            HasGoal = hasGoal;
            GoalX = goalX;
            GoalZ = goalZ;
            GoalInfluenceRadius = goalInfluenceRadius;
            GoalIndex = goalIndex;
        }

    }

    /// <summary>One Surface2D solve: the job, its pinned arrays and its cancel flag.</summary>
    internal sealed class FlowFieldSurfaceSolve
    {
        private readonly FlowFieldPinnedArrays _pins;
        private readonly int[] _cancel;
        internal readonly FlowFieldSurfaceSolveJob Job;

        private FlowFieldSurfaceSolve(FlowFieldPinnedArrays pins, int[] cancel, in FlowFieldSurfaceSolveJob job)
        {
            _pins = pins;
            _cancel = cancel;
            Job = job;
        }

        internal static unsafe FlowFieldSurfaceSolve Create(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldWorkspace workspace,
            int goalIndex)
        {
            var pins = new FlowFieldPinnedArrays(FlowFieldSurfaceSolveJob.PinnedArrayCount);
            var cancel = new int[1];
            var job = new FlowFieldSurfaceSolveJob
            {
                Costs = pins.Pin(workspace.Costs),
                Queue = pins.Pin(workspace.Queue),
                Topology = pins.Pin(workspace.TopologyMasks),
                NextCells = pins.Pin(workspace.NextCells),
                GoalFlags = pins.Pin(workspace.GoalFlags),
                GoalDirections = pins.Pin(workspace.GoalDirections),
                Blocked = pins.Pin(workspace.Blocked),
                Influence = pins.Pin(workspace.InfluenceMask),
                SurfaceFlags = pins.Pin(surface.Flags),
                Heights = pins.Pin(surface.Heights),
                Normals = pins.Pin(surface.Normals),
                Cancel = pins.Pin(cancel),
                Width = grid.Width,
                Depth = grid.Depth,
                CellSize = grid.CellSize,
                Origin = grid.Origin,
                Goal = goalIndex,
                DirectionEpsilonSqr = FlowFieldVectorUtility.DIRECTION_EPSILON_SQR,
                ProjectionEpsilon = Mathf.Epsilon,
            };
            for (int direction = 0; direction < FlowFieldNeighborUtility.SurfaceCount; direction++)
            {
                job.DeltaX[direction] = FlowFieldNeighborUtility.DeltaX[direction];
                job.DeltaZ[direction] = FlowFieldNeighborUtility.DeltaZ[direction];
            }
            return new FlowFieldSurfaceSolve(pins, cancel, job);
        }

        internal void RequestCancel() => System.Threading.Volatile.Write(ref _cancel[0], 1);

        internal void Release() => _pins.Release();
    }

    internal sealed partial class FlowFieldSurfaceBuildPipeline : IFlowFieldSolveBackend
    {
        private FlowFieldSurfaceSolve _solve;
        private JobHandle _handle;
        private FlowFieldBfsRequest _request;
        private Action<FlowFieldBfsRequest> _completed;
        private bool _disposed;

        public bool StartBfs(in FlowFieldBfsRequest request, Action<FlowFieldBfsRequest> completed)
        {
            if (_disposed || completed == null)
                return false;
            ValidateRequest(request);

            if (!request.HasGoal)
            {
                request.Workspace.ClearGoal();
                completed(request);
                return true;
            }

            _solve = FlowFieldSurfaceSolve.Create(
                request.Grid,
                request.Surface,
                request.Workspace,
                request.GoalIndex);
            _request = request;
            _completed = completed;
            _handle = _solve.Job.Schedule();
            JobHandle.ScheduleBatchedJobs();
            return true;
        }

        public void Poll(bool wait)
        {
            if (_solve == null || !wait && !_handle.IsCompleted)
                return;
            _handle.Complete();
            _solve.Release();
            _solve = null;
            Action<FlowFieldBfsRequest> completed = _completed;
            FlowFieldBfsRequest request = _request;
            _completed = null;
            _request = default;
            completed(request);
        }

        public void Cancel()
        {
            if (_solve == null)
                return;
            _solve.RequestCancel();
            _handle.Complete();
            _solve.Release();
            _solve = null;
            _completed = null;
            _request = default;
        }

        /// <summary>Solves on the calling thread, for the Editor preview and bakes.</summary>
        internal static void SolveNow(in FlowFieldBfsRequest request)
        {
            if (!request.HasGoal)
            {
                request.Workspace.ClearGoal();
                return;
            }
            if (request.GoalIndex >= 0)
            {
                // The Goal stage prepared this request.
                FlowFieldSolver.SolvePreparedGoal(
                    request.Grid,
                    request.Surface,
                    request.Workspace,
                    request.GoalIndex);
                return;
            }
            if (!FlowFieldSolver.BuildGoal(
                    request.Grid,
                    request.Surface,
                    request.Workspace,
                    request.GoalX,
                    request.GoalZ,
                    request.GoalInfluenceRadius,
                    out _))
                request.Workspace.ClearGoal();
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
            Cancel();
        }
    }

}
