using System;
using System.Collections.Generic;
using Unity.Jobs;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>The per-cell arrays a Volume3D build allocates, one per step, in order.</summary>
    internal enum FlowFieldVolumeAllocation
    {
        Blocked,
        Topology,
        GoalFlags,
        Next,
        Directions,
        Speeds,
        FinalDirections,
        FinalSpeeds,
        EscapeDirections,
        Costs,
        Queue,
        Influence,
    }

    internal enum FlowFieldVolumeBuildPhase
    {
        Allocate,
        // StaticBaked: the whole baked field.
        StaticData,
        // BakedBase: the baked obstacle results the build starts from.
        BakedObstacles,
        Obstacles,
        Solve,
        Modifiers,
        Complete,
    }

    /// <summary>Walks the cells of world-space regions, a few at a time.</summary>
    internal sealed class FlowFieldVolumeCellCursor
    {
        private readonly FlowFieldGridSpace _grid;
        private readonly FlowFieldCellRect[] _rects;
        private int _rect;
        private int _x;
        private int _y;
        private int _z;
        private bool _open;

        /// <summary>
        /// A region must include the cell size and any clearance: every cell
        /// whose query box can touch the change has its center inside. A region
        /// that misses the grid has no cells.
        /// </summary>
        internal FlowFieldVolumeCellCursor(FlowFieldGridSpace grid, Bounds[] regions)
        {
            _grid = grid;
            var rects = new List<FlowFieldCellRect>(regions.Length);
            foreach (Bounds region in regions)
            {
                FlowFieldCellRect rect = FlowFieldCellRect.FromBounds(grid, region);
                if (rect.IsValid)
                    rects.Add(rect);
            }
            _rects = rects.ToArray();
        }

        /// <summary>Writes the next cells and returns how many. Zero means finished.</summary>
        internal int Next(int[] cells)
        {
            int written = 0;
            while (written < cells.Length && _rect < _rects.Length)
            {
                FlowFieldCellRect rect = _rects[_rect];
                if (!_open)
                {
                    _open = true;
                    _x = rect.MinX;
                    _y = rect.MinY;
                    _z = rect.MinZ;
                }
                while (written < cells.Length && _y <= rect.MaxY)
                {
                    cells[written++] = _grid.ToFlatIndex(_x, _y, _z);
                    if (++_x > rect.MaxX)
                    {
                        _x = rect.MinX;
                        if (++_z > rect.MaxZ)
                        {
                            _z = rect.MinZ;
                            _y++;
                        }
                    }
                }
                if (_y > rect.MaxY)
                {
                    _rect++;
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
        internal FlowFieldVolumeAllocation AllocationStage;
        internal bool ObstaclesSynced;
        internal JobHandle SolveHandle;
        internal bool SolveRunning;
        private FlowFieldPinnedArrays _solvePins;
        private int[] _solveCancel;
        private int[] _solveGoal;
        internal readonly bool IsModifierRecompose;
        // RequestRebuild or NotifyCellsDirty asked this build to query every
        // obstacle; if it is discarded, the build that replaces it still must.
        internal bool RequiresFullQuery;
        // Blocked, Topology and EscapeDirections follow only from the obstacles,
        // so they are shared read-only with the published field.
        internal bool ReusesObstacleQueries;
        // Obstacle queries cover only these regions; other cells keep this published mask.
        internal bool[] PublishedBlocked;
        // BakedBase: the build copies the baked obstacle results, and its
        // queries cover only the regions the bake cannot know.
        internal bool LoadsBakedObstacles;
        private FlowFieldVolumeCellCursor _changedCells;
        internal FlowFieldVolumeCellCursor ModifierCells;

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
            FlowFieldField<FlowFieldVolumeRequest> published,
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

        // The obstacle stage then queries only the regions LimitToRegions names.
        internal void QueryOnlyChangedRegions(bool[] publishedBlocked) => PublishedBlocked = publishedBlocked;

        internal void LimitToRegions(Bounds[] regions) => _changedCells = new FlowFieldVolumeCellCursor(Request.Grid, regions);

        /// <summary>
        /// Writes the next cells the obstacle stage must query and returns how many.
        /// Zero means the stage is finished.
        /// </summary>
        internal int NextObstacleCells(int[] cells)
        {
            if (_changedCells != null)
                return _changedCells.Next(cells);
            int written = 0;
            for (; written < cells.Length && Cursor < Count; written++)
                cells[written] = Cursor++;
            return written;
        }

        /// <summary>
        /// Solves on worker threads as one job chain, parallel where a stage
        /// writes only its own cell. What follows only from the obstacles is
        /// kept when their result was reused.
        /// </summary>
        internal unsafe void StartSolve()
        {
            FlowFieldGridSpace grid = Request.Grid;
            _solvePins = new FlowFieldPinnedArrays(FlowFieldVolumeSolveData.PinnedArrayCount + 2);
            _solveCancel = new int[1];
            _solveGoal = new[] { -1 };
            try
            {
                var data = new FlowFieldVolumeSolveData
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
                    HasGoal = Request.HasGoal,
                    RequestedGoal = Request.HasGoal ? Request.GoalCell : -1,
                    GoalInfluenceRadius = Request.GoalInfluenceRadius,
                    DefaultDirection = FlowFieldVectorUtility.NormalizeDefaultDirection(Request.DefaultDirection),
                };
                data.FillNeighborTables();
                SolveHandle = data.ScheduleChain(buildObstacleResults: !ReusesObstacleQueries);
            }
            catch
            {
                // SolveRunning is still false, so CancelSolve would not release
                // these, and a schedule that failed has already waited for what
                // it began: free them here.
                _solvePins.Release();
                throw;
            }
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
