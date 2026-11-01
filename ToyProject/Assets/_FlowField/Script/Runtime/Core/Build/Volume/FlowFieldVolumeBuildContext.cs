using System;
using Unity.Jobs;
using UnityEngine;

namespace Common.FlowField
{
    internal enum FlowFieldVolumeBuildPhase
    {
        Allocate,
        StaticData,
        Obstacles,
        Solve,
        Modifiers,
        Complete,
    }

    /// <summary>Walks the cells of world-space regions, a few at a time.</summary>
    internal sealed class FlowFieldCellRegionCursor
    {
        private readonly Bounds[] _regions;
        private int _region;
        private int _x;
        private int _y;
        private int _z;
        private bool _open;

        internal FlowFieldCellRegionCursor(Bounds[] regions) => _regions = regions;

        /// <summary>Writes the next cells and returns how many. Zero means finished.</summary>
        internal int Next(FlowFieldGridSpace grid, int[] cells)
        {
            int written = 0;
            while (written < cells.Length && _region < _regions.Length)
            {
                // A region must include the cell size and any clearance: every cell
                // whose query box can touch the change has its center inside.
                Vector3 min = _regions[_region].min - grid.Origin;
                Vector3 max = _regions[_region].max - grid.Origin;
                int minX = Mathf.Clamp(Mathf.FloorToInt(min.x / grid.CellSize), 0, grid.Width - 1);
                int minY = Mathf.Clamp(Mathf.FloorToInt(min.y / grid.CellSize), 0, grid.Height - 1);
                int minZ = Mathf.Clamp(Mathf.FloorToInt(min.z / grid.CellSize), 0, grid.Depth - 1);
                int maxX = Mathf.Clamp(Mathf.FloorToInt(max.x / grid.CellSize), 0, grid.Width - 1);
                int maxY = Mathf.Clamp(Mathf.FloorToInt(max.y / grid.CellSize), 0, grid.Height - 1);
                int maxZ = Mathf.Clamp(Mathf.FloorToInt(max.z / grid.CellSize), 0, grid.Depth - 1);
                if (!_open)
                {
                    _open = true;
                    _x = minX;
                    _y = minY;
                    _z = minZ;
                }
                while (written < cells.Length && _y <= maxY)
                {
                    cells[written++] = grid.ToFlatIndex(_x, _y, _z);
                    if (++_x > maxX)
                    {
                        _x = minX;
                        if (++_z > maxZ)
                        {
                            _z = minZ;
                            _y++;
                        }
                    }
                }
                if (_y > maxY)
                {
                    _region++;
                    _open = false;
                }
            }
            return written;
        }
    }

    /// <summary>
    /// Private workspace and continuation cursors for one Volume3D build.
    /// </summary>
    internal sealed class FlowFieldVolumeBuildContext
    {
        internal readonly FlowFieldVolumeRequest Request;
        internal int Count => Request.Grid.CellCount;
        internal bool[] Blocked;
        internal uint[] Topology;
        internal FlowFieldGoalFlags[] GoalFlags;
        internal int[] Next;
        internal Vector3[] Directions;
        internal float[] Speeds;
        internal Vector3[] FinalDirections;
        internal float[] FinalSpeeds;
        internal Vector3[] EscapeDirections;
        internal int[] Costs;
        internal int[] Queue;
        internal bool[] Influence;
        internal readonly FlowFieldModifierWorkItem[] Modifiers;
        internal FlowFieldVolumeBuildPhase Phase;
        internal int Cursor;
        internal int GoalIndex = -1;
        internal int AllocationStage;
        internal bool ObstaclesSynced;
        internal JobHandle SolveHandle;
        internal bool SolveRunning;
        private FlowFieldPinnedArrays _solvePins;
        private int[] _solveCancel;
        private int[] _solveGoal;
        internal readonly bool IsModifierRecompose;
        // Blocked and Topology are shared read-only with the published field.
        internal bool ReusesObstacleQueries;
        // Obstacle queries cover only these regions; other cells keep this published mask.
        internal bool[] PublishedBlocked;
        private FlowFieldCellRegionCursor _changedCells;
        internal FlowFieldCellRegionCursor ModifierCells;

        internal FlowFieldVolumeBuildContext(
            in FlowFieldVolumeRequest request,
            FlowFieldModifierWorkItem[] modifiers)
        {
            Request = request;
            Modifiers = modifiers ?? Array.Empty<FlowFieldModifierWorkItem>();
            Phase = FlowFieldVolumeBuildPhase.Allocate;
        }

        /// <summary>
        /// Composes the published navigation with new Modifier snapshots. The
        /// published arrays are shared read-only; only the final field is new.
        /// </summary>
        internal FlowFieldVolumeBuildContext(
            FlowFieldVolumeFieldData published,
            FlowFieldModifierWorkItem[] modifiers,
            Vector3[] finalDirections,
            float[] finalSpeeds)
            : this(published.Request, modifiers)
        {
            IsModifierRecompose = true;
            Blocked = published.Blocked;
            Topology = published.Topology;
            GoalFlags = published.GoalFlags;
            Next = published.NextCells;
            Directions = published.BaseDirections;
            Speeds = published.BaseSpeeds;
            EscapeDirections = published.EscapeDirections;
            GoalIndex = published.GoalIndex;
            FinalDirections = finalDirections;
            FinalSpeeds = finalSpeeds;
            Phase = FlowFieldVolumeBuildPhase.Modifiers;
        }

        internal void QueryOnlyChangedRegions(bool[] publishedBlocked, Bounds[] regions)
        {
            PublishedBlocked = publishedBlocked;
            _changedCells = new FlowFieldCellRegionCursor(regions);
        }

        /// <summary>
        /// Writes the next cells the obstacle stage must query and returns how many.
        /// Zero means the stage is finished.
        /// </summary>
        internal int NextObstacleCells(int[] cells)
        {
            if (_changedCells != null)
                return _changedCells.Next(Request.Grid, cells);
            int written = 0;
            for (; written < cells.Length && Cursor < Count; written++)
                cells[written] = Cursor++;
            return written;
        }

        /// <summary>Solves on a worker thread. Topology is kept when the obstacle result was reused.</summary>
        internal unsafe void StartSolve()
        {
            FlowFieldGridSpace grid = Request.Grid;
            _solvePins = new FlowFieldPinnedArrays(FlowFieldVolumeSolveJob.PinnedArrayCount + 2);
            _solveCancel = new int[1];
            _solveGoal = new[] { -1 };
            var job = new FlowFieldVolumeSolveJob
            {
                Blocked = _solvePins.Pin(Blocked),
                Topology = _solvePins.Pin(Topology),
                GoalFlags = _solvePins.Pin(GoalFlags),
                Next = _solvePins.Pin(Next),
                Directions = _solvePins.Pin(Directions),
                Speeds = _solvePins.Pin(Speeds),
                EscapeDirections = _solvePins.Pin(EscapeDirections),
                Costs = _solvePins.Pin(Costs),
                Queue = _solvePins.Pin(Queue),
                Influence = _solvePins.Pin(Influence),
                Cancel = _solvePins.Pin(_solveCancel),
                ResolvedGoal = _solvePins.Pin(_solveGoal),
                Width = grid.Width,
                Height = grid.Height,
                Depth = grid.Depth,
                CellSize = grid.CellSize,
                BuildTopology = !ReusesObstacleQueries,
                HasGoal = Request.HasGoal,
                RequestedGoal = Request.HasGoal ? Request.GoalCell : -1,
                GoalInfluenceRadius = Request.GoalInfluenceRadius,
                DefaultDirection = FlowFieldVectorUtility.NormalizeDefaultDirection(Request.DefaultDirection),
            };
            for (int direction = 0; direction < FlowFieldNeighborUtility.VolumeCount; direction++)
            {
                job.DeltaX[direction] = FlowFieldNeighborUtility.DeltaX[direction];
                job.DeltaY[direction] = FlowFieldNeighborUtility.DeltaY[direction];
                job.DeltaZ[direction] = FlowFieldNeighborUtility.DeltaZ[direction];
            }
            for (int face = 0; face < FlowFieldNeighborUtility.VolumeFaceDirections.Length; face++)
                job.FaceDirections[face] = FlowFieldNeighborUtility.VolumeFaceDirections[face];

            SolveHandle = job.Schedule();
            SolveRunning = true;
            JobHandle.ScheduleBatchedJobs();
        }

        internal void FinishSolve()
        {
            SolveHandle.Complete();
            SolveRunning = false;
            _solvePins.Release();
            GoalIndex = _solveGoal[0];
        }

        private void CancelSolve()
        {
            if (!SolveRunning)
                return;
            System.Threading.Volatile.Write(ref _solveCancel[0], 1);
            SolveHandle.Complete();
            SolveRunning = false;
            _solvePins.Release();
        }

        internal void Dispose()
        {
            CancelSolve();
        }
    }
}
