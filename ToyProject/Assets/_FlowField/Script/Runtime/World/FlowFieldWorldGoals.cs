using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// The Goals of one world: each is an additional Goal of every area's
    /// sessions (one per agent type) under one id, whose search in an area
    /// starts from seeds this class keeps current once a frame, before the
    /// builds. The area holding the Goal seeds its cell; every area seeds the
    /// cells beside a touching area with that area's steps plus one; a Goal no
    /// loaded area holds seeds the outer cells with straight-line estimates.
    /// <para>
    /// An epoch guards against old steps that ran through an area whose way
    /// has changed: a Goal moving, an area joining or leaving, or any area's
    /// new input start a new epoch, and seeds come only from fields built in
    /// the current one. An area with no such neighbor yet keeps its field.
    /// </para>
    /// </summary>
    internal sealed class FlowFieldWorldGoals
    {
        private sealed class Goal
        {
            internal readonly int Id;
            internal readonly FlowFieldGoalKind Kind;
            internal FlowFieldGoalRequest Request;
            internal int Epoch = 1;

            internal Goal(int id, FlowFieldGoalKind kind, in FlowFieldGoalRequest request)
            {
                Id = id;
                Kind = kind;
                Request = request;
            }
        }

        // What one area session's seeds for one Goal were computed from.
        private sealed class SeedInputs
        {
            internal int Epoch = -1;
            internal readonly List<object> Fields = new List<object>(8);
            internal readonly List<int> Epochs = new List<int>(8);
        }

        private readonly FlowFieldWorldAreas _areas;
        private readonly List<Goal> _goals = new List<Goal>(4);
        private readonly Dictionary<(FlowFieldSession, int), SeedInputs> _inputs
            = new Dictionary<(FlowFieldSession, int), SeedInputs>();
        private readonly Dictionary<FlowFieldSession, int> _baseStamps = new Dictionary<FlowFieldSession, int>();
        private readonly Dictionary<FlowFieldSession, AreaStitch> _stitches = new Dictionary<FlowFieldSession, AreaStitch>();

        // The touching areas of one area session's type, for its world Goal reads.
        private sealed class AreaStitch : IFlowFieldStitchSource
        {
            internal FlowFieldWorldArea Area;
            internal string TypeName;

            public int Count => Area.Borders.Count;

            public bool TryGet(
                int index,
                out FlowFieldField field,
                out FlowFieldSafety regions,
                out IFlowFieldLatestCells latest,
                out int offsetX,
                out int offsetY,
                out int offsetZ)
            {
                field = null;
                regions = null;
                latest = null;
                FlowFieldWorldArea other = Area.Borders[index].Other;
                offsetX = other.Box.MinX - Area.Box.MinX;
                offsetY = other.Box.MinY - Area.Box.MinY;
                offsetZ = other.Box.MinZ - Area.Box.MinZ;
                FlowFieldSession session = SessionOf(other, TypeName);
                if (session == null || !session.TryGetPublishedField(out field))
                    return false;
                regions = session.Safety;
                latest = session.LatestCells;
                return true;
            }
        }
        private readonly SeedInputs _scratchInputs = new SeedInputs();
        private readonly List<int> _cells = new List<int>(64);
        private readonly List<int> _costs = new List<int>(64);
        private readonly List<int> _directions = new List<int>(64);
        private readonly List<float> _heights = new List<float>(64);
        private bool _areasChanged;

        internal FlowFieldWorldGoals(FlowFieldWorldAreas areas)
        {
            _areas = areas;
            _areas.Changed += _ => _areasChanged = true;
        }

        internal int Count => _goals.Count;

        internal int Add(FlowFieldGoalKind kind, in FlowFieldGoalRequest request)
        {
            var goal = new Goal(FlowFieldGoalSet.NextId(), kind, request);
            _goals.Add(goal);
            Update();
            return goal.Id;
        }

        internal bool TrySet(int id, in FlowFieldGoalRequest request)
        {
            Goal goal = Find(id);
            if (goal == null)
                return false;
            goal.Request = request;
            unchecked { goal.Epoch++; }
            for (int a = 0; a < _areas.All.Count; a++)
            {
                FlowFieldManager manager = _areas.All[a].Manager;
                for (int s = 0; s < manager.WorldSessionCount; s++)
                {
                    FlowFieldSession session = manager.WorldSession(s);
                    if (TakesGoals(session))
                        session.SetGoalCommand(id, request);
                }
            }
            Update();
            return true;
        }

        internal bool Remove(int id)
        {
            Goal goal = Find(id);
            if (goal == null)
                return false;
            _goals.Remove(goal);
            for (int a = 0; a < _areas.All.Count; a++)
            {
                FlowFieldManager manager = _areas.All[a].Manager;
                for (int s = 0; s < manager.WorldSessionCount; s++)
                {
                    FlowFieldSession session = manager.WorldSession(s);
                    _inputs.Remove((session, id));
                    if (session.IsInitialized || session.IsFaulted)
                        session.RemoveGoalCommand(id);
                }
            }
            return true;
        }

        internal bool Contains(int id) => Find(id) != null;

        private Goal Find(int id)
        {
            for (int i = 0; i < _goals.Count; i++)
            {
                if (_goals[i].Id == id)
                    return _goals[i];
            }
            return null;
        }

        // A session that builds Goals now. A StaticBaked area searches its
        // world Goals on its baked cells; its own Goal stays as baked.
        private static bool TakesGoals(FlowFieldSession session)
            => session.IsInitialized && !session.IsFaulted;

        /// <summary>
        /// Once a frame, before the builds: every area session holds every
        /// Goal, a changed input starts a new epoch, and seeds whose inputs
        /// changed are computed again.
        /// </summary>
        internal void Update()
        {
            if (_goals.Count == 0)
                return;
            bool newEpoch = _areasChanged;
            _areasChanged = false;
            IReadOnlyList<FlowFieldWorldArea> areas = _areas.All;
            for (int a = 0; a < areas.Count; a++)
            {
                FlowFieldManager manager = areas[a].Manager;
                for (int s = 0; s < manager.WorldSessionCount; s++)
                {
                    FlowFieldSession session = manager.WorldSession(s);
                    if (!TakesGoals(session))
                        continue;
                    if (!_stitches.TryGetValue(session, out AreaStitch stitch))
                    {
                        stitch = new AreaStitch();
                        _stitches.Add(session, stitch);
                    }
                    stitch.Area = areas[a];
                    stitch.TypeName = manager.WorldSessionName(s);
                    session.Stitch = stitch;
                    int stamp = session.Goals.BaseStamp;
                    if (!_baseStamps.TryGetValue(session, out int seen) || seen != stamp)
                    {
                        _baseStamps[session] = stamp;
                        newEpoch = true;
                    }
                    for (int g = 0; g < _goals.Count; g++)
                    {
                        Goal goal = _goals[g];
                        if (session.Goals.Find(goal.Id) == null)
                        {
                            session.AddGoalCommand(goal.Kind, goal.Request, goal.Id);
                            newEpoch = true;
                        }
                    }
                }
            }
            if (newEpoch)
            {
                for (int g = 0; g < _goals.Count; g++)
                    unchecked { _goals[g].Epoch++; }
            }
            for (int g = 0; g < _goals.Count; g++)
            {
                for (int a = 0; a < areas.Count; a++)
                {
                    FlowFieldManager manager = areas[a].Manager;
                    for (int s = 0; s < manager.WorldSessionCount; s++)
                    {
                        FlowFieldSession session = manager.WorldSession(s);
                        if (TakesGoals(session))
                            UpdateSeeds(_goals[g], areas[a], s, session);
                    }
                }
            }
        }

        private void UpdateSeeds(Goal goal, FlowFieldWorldArea area, int sessionIndex, FlowFieldSession session)
        {
            if (!session.TryGetPublishedField(out FlowFieldField own))
                return;
            string typeName = area.Manager.WorldSessionName(sessionIndex);
            SeedInputs inputs = Inputs(goal, area, typeName, own, session.Goals.Find(goal.Id));
            if (!_inputs.TryGetValue((session, goal.Id), out SeedInputs last))
            {
                last = new SeedInputs();
                _inputs.Add((session, goal.Id), last);
            }
            if (Same(inputs, last))
                return;
            Copy(inputs, last);

            FlowFieldGoalSeeds seeds = ComputeSeeds(goal, area, typeName, own, out bool holdsGoal, out bool fromNeighbors);
            // Until a neighbor has a field of this epoch, an area away from
            // the Goal keeps the field it has.
            if (!holdsGoal && !fromNeighbors && _areas.Find(goal.Request.WorldPosition) != null)
                return;
            session.SetGoalSeedsCommand(goal.Id, seeds);
        }

        // The area's own cells and Goal field, and each touching area's cells,
        // Goal field and the epoch it was built in. A field the area itself
        // just built under older seeds counts too, so it takes the epoch.
        private SeedInputs Inputs(
            Goal goal,
            FlowFieldWorldArea area,
            string typeName,
            FlowFieldField own,
            FlowFieldGoalSet.Entry ownEntry)
        {
            SeedInputs inputs = _scratchInputs;
            inputs.Epoch = goal.Epoch;
            inputs.Fields.Clear();
            inputs.Epochs.Clear();
            inputs.Fields.Add(own);
            inputs.Fields.Add(ownEntry?.Field);
            inputs.Epochs.Add(ownEntry?.BuiltSeeds?.Epoch ?? -1);
            for (int b = 0; b < area.Borders.Count; b++)
            {
                FlowFieldSession other = SessionOf(area.Borders[b].Other, typeName);
                FlowFieldGoalSet.Entry entry = other?.Goals.Find(goal.Id);
                FlowFieldField otherCells = null;
                other?.TryGetPublishedField(out otherCells);
                inputs.Fields.Add(otherCells);
                inputs.Fields.Add(entry?.Field);
                inputs.Epochs.Add(entry?.BuiltSeeds?.Epoch ?? -1);
            }
            return inputs;
        }

        private static bool Same(SeedInputs a, SeedInputs b)
        {
            if (a.Epoch != b.Epoch || a.Fields.Count != b.Fields.Count || a.Epochs.Count != b.Epochs.Count)
                return false;
            for (int i = 0; i < a.Fields.Count; i++)
            {
                if (!ReferenceEquals(a.Fields[i], b.Fields[i]))
                    return false;
            }
            for (int i = 0; i < a.Epochs.Count; i++)
            {
                if (a.Epochs[i] != b.Epochs[i])
                    return false;
            }
            return true;
        }

        private static void Copy(SeedInputs from, SeedInputs to)
        {
            to.Epoch = from.Epoch;
            to.Fields.Clear();
            to.Fields.AddRange(from.Fields);
            to.Epochs.Clear();
            to.Epochs.AddRange(from.Epochs);
        }

        // The area's session of an agent type, by name, while it takes Goals.
        private static FlowFieldSession SessionOf(FlowFieldWorldArea area, string typeName)
        {
            FlowFieldManager manager = area.Manager;
            for (int s = 0; s < manager.WorldSessionCount; s++)
            {
                if (manager.WorldSessionName(s) == typeName)
                {
                    FlowFieldSession session = manager.WorldSession(s);
                    return TakesGoals(session) ? session : null;
                }
            }
            return null;
        }

        private FlowFieldGoalSeeds ComputeSeeds(
            Goal goal,
            FlowFieldWorldArea area,
            string typeName,
            FlowFieldField own,
            out bool holdsGoal,
            out bool fromNeighbors)
        {
            _cells.Clear();
            _costs.Clear();
            _directions.Clear();
            _heights.Clear();
            FlowFieldGridSpace grid = area.Grid;
            Vector3 position = goal.Request.WorldPosition;
            LocalCell(grid, position, out int gx, out int gy, out int gz);
            int goalCell = -1;
            holdsGoal = grid.ContainsWorldPosition(position) && grid.TryLocal(gx, gy, gz, out goalCell);
            if (holdsGoal)
                Add(goalCell, 0, FlowFieldGoalSeeds.NoDirection, 0f);

            fromNeighbors = false;
            for (int b = 0; b < area.Borders.Count; b++)
            {
                FlowFieldWorldBorder border = area.Borders[b];
                FlowFieldSession other = SessionOf(border.Other, typeName);
                FlowFieldGoalSet.Entry entry = other?.Goals.Find(goal.Id);
                // Only steps of this epoch: an older field may lead back
                // through this area along a way that has changed.
                if (entry?.Field == null || entry.BuiltSeeds == null || entry.BuiltSeeds.Epoch != goal.Epoch
                    || !other.TryGetPublishedField(out FlowFieldField otherAgent))
                    continue;
                int[] distances = entry.Field.Goal.Distances;
                if (distances == null)
                    continue;
                fromNeighbors = true;
                for (int p = 0; p < border.Pairs.Length; p++)
                {
                    FlowFieldBorderPair pair = border.Pairs[p];
                    int steps = distances[pair.Other];
                    if (steps == FlowFieldGoalLayer.Unreachable
                        || !CanCross(area, own, pair.Cell, pair.Direction, border.Other, otherAgent, pair.Other, typeName))
                        continue;
                    Add(pair.Cell, steps + 1, pair.Direction, Height(otherAgent, pair.Other));
                }
            }

            if (_areas.Find(position) == null)
                AddEstimates(area, own, position);

            return new FlowFieldGoalSeeds(
                _cells.ToArray(),
                _costs.ToArray(),
                _directions.ToArray(),
                _heights.ToArray(),
                holdsGoal ? goalCell : -1,
                gx,
                gy,
                gz,
                goal.Epoch);
        }

        private void Add(int cell, int cost, int direction, float height)
        {
            _cells.Add(cell);
            _costs.Add(cost);
            _directions.Add(direction);
            _heights.Add(height);
        }

        // A Goal no loaded area holds: the area's walkable outer cells, toward
        // the outside, at the steps a straight way to the Goal would take.
        private void AddEstimates(FlowFieldWorldArea area, FlowFieldField own, Vector3 position)
        {
            FlowFieldGridSpace grid = area.Grid;
            FlowFieldWorld world = _areas.World;
            int goalX = LatticeCell(position.x, world.LatticeOrigin.x, world.CellSize);
            int goalY = area.IsVolume ? LatticeCell(position.y, world.LatticeOrigin.y, world.CellSize) : 0;
            int goalZ = LatticeCell(position.z, world.LatticeOrigin.z, world.CellSize);
            int steps = area.IsVolume ? FlowFieldNeighborUtility.VolumeCount : FlowFieldNeighborUtility.SurfaceCount;
            for (int cell = 0; cell < grid.CellCount; cell++)
            {
                grid.Locate(cell, out int lx, out int ly, out int lz);
                bool edge = lx == 0 || lx == grid.Width - 1 || lz == 0 || lz == grid.Depth - 1
                    || area.IsVolume && (ly == 0 || ly == grid.Height - 1);
                if (!edge || !Walkable(own, cell))
                    continue;
                area.Locate(cell, out int x, out int y, out int z);
                for (int direction = 0; direction < steps; direction++)
                {
                    int tx = x + FlowFieldNeighborUtility.DeltaX[direction];
                    int ty = y + (area.IsVolume ? FlowFieldNeighborUtility.DeltaY[direction] : 0);
                    int tz = z + FlowFieldNeighborUtility.DeltaZ[direction];
                    if (area.Box.Contains(tx, ty, tz) || FindArea(tx, ty, tz) != null)
                        continue;
                    int estimate = Math.Max(Math.Abs(goalX - tx), Math.Max(Math.Abs(goalY - ty), Math.Abs(goalZ - tz)));
                    Add(cell, estimate + 1, direction, own.Scene != null ? own.Scene.Surface.Heights[cell] : 0f);
                }
            }
        }

        // ----- Crossings ------------------------------------------------------

        /// <summary>
        /// Whether a mover of this type steps from the area's cell into the
        /// other area's cell, by the rule each mode's links follow inside a
        /// grid. Surface2D: both cells walkable within the step height, and on
        /// a diagonal both side cells too. Volume3D: both cells open, and on a
        /// diagonal every corner cell it passes beside.
        /// </summary>
        private bool CanCross(
            FlowFieldWorldArea area,
            FlowFieldField own,
            int cell,
            int direction,
            FlowFieldWorldArea otherArea,
            FlowFieldField other,
            int otherCell,
            string typeName)
        {
            if (!Walkable(own, cell) || !Walkable(other, otherCell))
                return false;
            area.Locate(cell, out int x, out int y, out int z);
            if (area.IsVolume)
            {
                for (int i = FlowFieldNeighborUtility.VolumeCornerStart[direction];
                     i < FlowFieldNeighborUtility.VolumeCornerStart[direction + 1];
                     i++)
                {
                    int corner = FlowFieldNeighborUtility.VolumeCornerDirections[i];
                    if (!TryWorldCell(x + FlowFieldNeighborUtility.DeltaX[corner], y + FlowFieldNeighborUtility.DeltaY[corner],
                            z + FlowFieldNeighborUtility.DeltaZ[corner], typeName, out FlowFieldField field, out int index)
                        || !Walkable(field, index))
                        return false;
                }
                return true;
            }

            float step = own.Scene.Surface.MaxStepHeight;
            if (!Connects(own, cell, other, otherCell, step))
                return false;
            int dx = FlowFieldNeighborUtility.DeltaX[direction];
            int dz = FlowFieldNeighborUtility.DeltaZ[direction];
            if (dx == 0 || dz == 0)
                return true;
            return TryWorldCell(x + dx, 0, z, typeName, out FlowFieldField besideX, out int sideX)
                && TryWorldCell(x, 0, z + dz, typeName, out FlowFieldField besideZ, out int sideZ)
                && Walkable(besideX, sideX)
                && Walkable(besideZ, sideZ)
                && Connects(own, cell, besideX, sideX, step)
                && Connects(own, cell, besideZ, sideZ, step)
                && Connects(besideX, sideX, other, otherCell, step)
                && Connects(besideZ, sideZ, other, otherCell, step);
        }

        private static bool Walkable(FlowFieldField field, int index)
            => !field.Agent.Blocked[index] && (field.Scene == null || field.Scene.Surface.IsSurfaceValid(index));

        private static bool Connects(FlowFieldField a, int i, FlowFieldField b, int j, float step)
            => a.Scene.Surface.IsSurfaceValid(i)
                && b.Scene.Surface.IsSurfaceValid(j)
                && Math.Abs(a.Scene.Surface.Heights[i] - b.Scene.Surface.Heights[j]) <= step;

        private static float Height(FlowFieldField field, int index)
            => field.Scene != null ? field.Scene.Surface.Heights[index] : 0f;

        // The published field of this type's session holding a lattice cell.
        private bool TryWorldCell(int x, int y, int z, string typeName, out FlowFieldField field, out int index)
        {
            field = null;
            index = -1;
            FlowFieldWorldArea area = FindArea(x, y, z);
            FlowFieldSession session = area != null ? SessionOf(area, typeName) : null;
            return session != null && session.TryGetPublishedField(out field) && area.TryCell(x, y, z, out index);
        }

        private FlowFieldWorldArea FindArea(int x, int y, int z)
        {
            IReadOnlyList<FlowFieldWorldArea> areas = _areas.All;
            for (int i = 0; i < areas.Count; i++)
            {
                if (areas[i].Box.Contains(x, y, z))
                    return areas[i];
            }
            return null;
        }

        // The cell of the grid a position falls in, possibly outside the grid.
        private static void LocalCell(FlowFieldGridSpace grid, Vector3 position, out int x, out int y, out int z)
        {
            x = (int)Math.Floor(((double)position.x - grid.Origin.x) / grid.CellSize);
            z = (int)Math.Floor(((double)position.z - grid.Origin.z) / grid.CellSize);
            y = grid.SpaceMode == FlowFieldSpaceMode.Volume3D
                ? (int)Math.Floor(((double)position.y - grid.Origin.y) / grid.CellSize)
                : 0;
        }

        private static int LatticeCell(float position, float origin, float cellSize)
            => (int)Math.Floor(((double)position - origin) / cellSize);
    }
}
