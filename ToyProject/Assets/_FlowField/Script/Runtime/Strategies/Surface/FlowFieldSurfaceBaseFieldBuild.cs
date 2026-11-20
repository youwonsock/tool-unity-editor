using System;
using Unity.Jobs;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// The synchronous part of a Surface2D base build: rebuilds the obstacle
    /// masks it is asked to (and the escape directions when they changed),
    /// then prepares the Goal and topology the solve reads. The base stage
    /// calls it in one frame; the solve then runs on a worker thread (the
    /// instance part below).
    /// </summary>
    internal sealed partial class FlowFieldSurfaceBaseFieldBuild
    {
        internal static FlowFieldSurfaceBuildResult PrepareBase(
            in FlowFieldSurfaceBuildRequest request,
            FlowFieldSurfaceObstacleMasks obstacleMasks,
            FlowFieldSurfaceGoalTracker goalTracker,
            bool rebuildStaticObstacles,
            bool rebuildDynamicObstacles,
            bool rebuildGoal)
        {
            ValidateRequest(request, obstacleMasks);
            FlowFieldSurfaceWorkspace workspace = request.Obstacles.Workspace;
            bool obstacleChanged = false;
            bool hasWalkableSurface = false;
            FlowFieldSurfaceGoalBuildStatus goalStatus = FlowFieldSurfaceGoalBuildStatus.None;

            if (rebuildStaticObstacles || rebuildDynamicObstacles)
            {
                obstacleChanged = obstacleMasks.RebuildMasks(
                    request.Obstacles,
                    rebuildStaticObstacles,
                    rebuildDynamicObstacles);
                if (obstacleChanged)
                {
                    obstacleMasks.CommitCombinedAndBuildEscape(
                        request.Grid,
                        request.Surface,
                        workspace);
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

            return new FlowFieldSurfaceBuildResult(
                obstacleChanged,
                hasWalkableSurface,
                goalStatus);
        }

        private static bool HasWalkableSurface(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldSurfaceWorkspace workspace)
        {
            for (int index = 0; index < grid.CellCount; index++)
            {
                if (surface.IsSurfaceValid(index) && !workspace.Blocked[index])
                    return true;
            }

            return false;
        }

        private static FlowFieldSurfaceGoalBuildStatus PrepareGoal(
            in FlowFieldSurfaceBuildRequest request,
            FlowFieldSurfaceWorkspace workspace,
            FlowFieldSurfaceGoalTracker goalTracker,
            bool forceRebuild)
        {
            FlowFieldSurfaceGoalTracker tracker = goalTracker;
            FlowFieldSurfaceGoalBuildStatus status = FlowFieldSurfaceGoalResolver.Build(
                request.Goal,
                request.Grid,
                request.Surface,
                workspace,
                tracker,
                forceRebuild);

            if (status == FlowFieldSurfaceGoalBuildStatus.Built
                || status == FlowFieldSurfaceGoalBuildStatus.Unchanged)
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
            FlowFieldSurfaceGraphTraversal.BuildTopologyMasks(
                request.Grid,
                request.Surface,
                workspace);
            FlowFieldSurfaceGraphTraversal.ApplyNoGoalSentinels(
                request.Grid,
                request.Surface,
                workspace);
            return status;
        }

        private static void ValidateRequest(
            in FlowFieldSurfaceBuildRequest request,
            FlowFieldSurfaceObstacleMasks obstacleMasks)
        {
            if (!request.Grid.IsValid)
                throw new ArgumentException("FlowField base build requires a valid grid.", nameof(request));
            if (!request.Surface.IsValid)
                throw new ArgumentException("FlowField base build requires a valid surface view.", nameof(request));
            if (obstacleMasks == null)
                throw new ArgumentNullException(nameof(obstacleMasks));
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
            FlowFieldSurfaceWorkspace workspace,
            int goalIndex)
        {
            var pins = new FlowFieldPinnedArrays(FlowFieldSurfaceSolveJob.PinnedArrayCount);
            var cancel = new int[1];
            FlowFieldSurfaceSolveJob job;
            try
            {
                job = new FlowFieldSurfaceSolveJob
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
                    DirectionEpsilonSqr = FlowFieldVectorUtility.DirectionEpsilonSqr,
                    ProjectionEpsilon = Mathf.Epsilon,
                };
            }
            catch
            {
                // Nothing is scheduled yet: free what was pinned before the failure.
                pins.Release();
                throw;
            }
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

    /// <summary>
    /// The Surface2D solve on a worker thread. It owns the running job; the
    /// builder owns the request and decides what a completed solve publishes.
    /// </summary>
    internal sealed partial class FlowFieldSurfaceBaseFieldBuild : IDisposable
    {
        private FlowFieldSurfaceSolve _solve;
        private JobHandle _handle;
        private FlowFieldSurfaceBfsRequest _request;
        private Action<FlowFieldSurfaceBfsRequest> _completed;
        private bool _disposed;

        // completed is called only from Poll, inside the builder's StepBuild.
        internal bool StartBfs(in FlowFieldSurfaceBfsRequest request, Action<FlowFieldSurfaceBfsRequest> completed)
        {
            if (_disposed || completed == null)
                return false;
            ValidateRequest(request);

            _solve = FlowFieldSurfaceSolve.Create(
                request.Grid,
                request.Surface,
                request.Workspace,
                request.GoalIndex);
            _request = request;
            _completed = completed;
            _handle = _solve.Job.Schedule();
            request.Workspace.SolveInFlight = true;
            JobHandle.ScheduleBatchedJobs();
            return true;
        }

        /// <summary>Reports a finished solve. With wait set, it first waits for one to finish.</summary>
        internal void Poll(bool wait)
        {
            if (_solve == null || !wait && !_handle.IsCompleted)
                return;
            _handle.Complete();
            _request.Workspace.SolveInFlight = false;
            _solve.Release();
            _solve = null;
            Action<FlowFieldSurfaceBfsRequest> completed = _completed;
            FlowFieldSurfaceBfsRequest request = _request;
            _completed = null;
            _request = default;
            completed(request);
        }

        /// <summary>Stops a running solve before its workspace is used again.</summary>
        internal void Cancel()
        {
            if (_solve == null)
                return;
            _solve.RequestCancel();
            _handle.Complete();
            _request.Workspace.SolveInFlight = false;
            _solve.Release();
            _solve = null;
            _completed = null;
            _request = default;
        }

        private static void ValidateRequest(in FlowFieldSurfaceBfsRequest request)
        {
            if (!request.Grid.IsValid)
                throw new ArgumentException("FlowField BFS requires a valid grid.", nameof(request));
            if (!request.Surface.IsValid)
                throw new ArgumentException("FlowField BFS requires a valid Surface Bake.", nameof(request));
            if (request.Workspace == null || request.Workspace.Capacity != request.Grid.CellCount)
                throw new ArgumentException("FlowField BFS workspace capacity does not match the grid.", nameof(request));
            if (!request.Grid.IsLocalInBounds(request.GoalX, request.GoalZ)
                || request.GoalIndex < 0
                || request.GoalIndex >= request.Grid.CellCount)
                throw new ArgumentOutOfRangeException(nameof(request.GoalIndex));
            if (!FlowFieldGridSpace.IsFinite(request.GoalInfluenceRadius)
                || request.GoalInfluenceRadius < 0f)
                throw new ArgumentOutOfRangeException(nameof(request.GoalInfluenceRadius));
            if (request.Workspace.ResolvedGoalIndex != request.GoalIndex
                || !FlowFieldSurfaceGraphTraversal.IsCellTraversable(
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
