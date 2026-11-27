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
    /// loaded area holds seeds the outer cells with the world bake's steps
    /// across them when it searched the Goal's cell, else with straight-line
    /// estimates.
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
            // A change since the epoch began waits for the epoch's wave to
            // settle, or for MaxEpochFrames, before the next epoch begins.
            internal bool EpochPending;
            internal int PendingSince = -1;
            // Nothing changed or built for this Goal in the last update.
            internal bool Quiet = true;

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
        // The longest a change waits for the epoch's wave: a world whose
        // inputs never stop changing still starts new epochs.
        private const int MaxEpochFrames = 30;

        private readonly SeedInputs _scratchInputs = new SeedInputs();
        private readonly HashSet<FlowFieldSession> _liveSessions = new HashSet<FlowFieldSession>();
        private readonly List<FlowFieldSession> _deadSessions = new List<FlowFieldSession>();
        private readonly List<(FlowFieldSession, int)> _deadInputs = new List<(FlowFieldSession, int)>();
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

        /// <summary>
        /// The last update changed no seeds and found no build due, and no
        /// epoch waits: the searches have spread as far as they reach. The
        /// world bake pumps until then.
        /// </summary>
        internal bool IsSettled
        {
            get
            {
                for (int g = 0; g < _goals.Count; g++)
                {
                    if (!_goals[g].Quiet || _goals[g].EpochPending)
                        return false;
                }
                return true;
            }
        }

        internal int Add(FlowFieldGoalKind kind, in FlowFieldGoalRequest request)
        {
            var goal = new Goal(FlowFieldGoalSet.NextId(), kind, request);
            _goals.Add(goal);
            Update();
            return goal.Id;
        }

        // The seeds carry the new position into every area's search, so the
        // areas whose seeds change build again and the others keep theirs.
        internal bool TrySet(int id, in FlowFieldGoalRequest request)
        {
            Goal goal = Find(id);
            if (goal == null)
                return false;
            goal.Request = request;
            _areas.EnterOperations();
            try
            {
                for (int a = 0; a < _areas.All.Count; a++)
                {
                    IFlowFieldWorldMember manager = _areas.All[a].Member;
                    for (int s = 0; s < manager.WorldSessionCount; s++)
                        manager.WorldSession(s).Goals.TrySetWorldRequest(id, request);
                }
                RequestEpoch(goal);
                Update();
            }
            finally
            {
                _areas.ExitOperations();
            }
            return true;
        }

        internal bool Remove(int id)
        {
            Goal goal = Find(id);
            if (goal == null)
                return false;
            _goals.Remove(goal);
            _areas.EnterOperations();
            try
            {
                for (int a = 0; a < _areas.All.Count; a++)
                {
                    IFlowFieldWorldMember manager = _areas.All[a].Member;
                    for (int s = 0; s < manager.WorldSessionCount; s++)
                    {
                        FlowFieldSession session = manager.WorldSession(s);
                        _inputs.Remove((session, id));
                        if (session.IsInitialized || session.IsFaulted)
                            session.RemoveGoalCommand(id);
                    }
                }
            }
            finally
            {
                _areas.ExitOperations();
            }
            return true;
        }

        private static void RequestEpoch(Goal goal)
        {
            if (goal.EpochPending)
                return;
            goal.EpochPending = true;
            goal.PendingSince = Time.frameCount;
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
            _areas.EnterOperations();
            try
            {
                UpdateGoals();
            }
            finally
            {
                _areas.ExitOperations();
            }
        }

        private void UpdateGoals()
        {
            bool newEpoch = _areasChanged;
            if (_areasChanged)
                PruneLeftSessions();
            _areasChanged = false;
            IReadOnlyList<FlowFieldWorldArea> areas = _areas.All;
            for (int a = 0; a < areas.Count; a++)
            {
                IFlowFieldWorldMember manager = areas[a].Member;
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
                            session.AddGoalCommand(goal.Kind, goal.Request, goal.Id, world: true);
                            newEpoch = true;
                        }
                    }
                }
            }
            int frame = Time.frameCount;
            for (int g = 0; g < _goals.Count; g++)
            {
                Goal goal = _goals[g];
                if (newEpoch)
                    RequestEpoch(goal);
                // A change waits until the current epoch's wave has settled,
                // so every area gets seeds of one epoch, but never longer
                // than MaxEpochFrames.
                if (goal.EpochPending && (goal.Quiet || frame - goal.PendingSince >= MaxEpochFrames))
                {
                    unchecked { goal.Epoch++; }
                    goal.EpochPending = false;
                    goal.PendingSince = -1;
                }
            }
            for (int g = 0; g < _goals.Count; g++)
            {
                Goal goal = _goals[g];
                bool active = false;
                for (int a = 0; a < areas.Count; a++)
                {
                    IFlowFieldWorldMember manager = areas[a].Member;
                    for (int s = 0; s < manager.WorldSessionCount; s++)
                    {
                        FlowFieldSession session = manager.WorldSession(s);
                        if (!TakesGoals(session))
                            continue;
                        active |= UpdateSeeds(goal, areas[a], s, session);
                        FlowFieldGoalSet.Entry entry = session.Goals.Find(goal.Id);
                        active |= entry != null && session.Goals.IsDue(entry);
                    }
                }
                goal.Quiet = !active;
            }
        }

        // Forgets the sessions of areas that left, so their fields go.
        private void PruneLeftSessions()
        {
            _liveSessions.Clear();
            IReadOnlyList<FlowFieldWorldArea> areas = _areas.All;
            for (int a = 0; a < areas.Count; a++)
            {
                IFlowFieldWorldMember manager = areas[a].Member;
                for (int s = 0; s < manager.WorldSessionCount; s++)
                    _liveSessions.Add(manager.WorldSession(s));
            }
            _deadSessions.Clear();
            foreach (FlowFieldSession session in _stitches.Keys)
            {
                if (!_liveSessions.Contains(session))
                    _deadSessions.Add(session);
            }
            foreach (FlowFieldSession session in _baseStamps.Keys)
            {
                if (!_liveSessions.Contains(session) && !_deadSessions.Contains(session))
                    _deadSessions.Add(session);
            }
            for (int i = 0; i < _deadSessions.Count; i++)
            {
                _stitches.Remove(_deadSessions[i]);
                _baseStamps.Remove(_deadSessions[i]);
                _deadSessions[i].Stitch = null;
            }
            _deadInputs.Clear();
            foreach ((FlowFieldSession, int) key in _inputs.Keys)
            {
                if (!_liveSessions.Contains(key.Item1))
                    _deadInputs.Add(key);
            }
            for (int i = 0; i < _deadInputs.Count; i++)
                _inputs.Remove(_deadInputs[i]);
        }

        // True when the area session's seeds changed.
        private bool UpdateSeeds(Goal goal, FlowFieldWorldArea area, int sessionIndex, FlowFieldSession session)
        {
            if (!session.TryGetPublishedField(out FlowFieldField own))
                return false;
            string typeName = area.Member.WorldSessionName(sessionIndex);
            FlowFieldGoalSet.Entry entry = session.Goals.Find(goal.Id);
            SeedInputs inputs = Inputs(goal, area, typeName, own, entry);
            if (!_inputs.TryGetValue((session, goal.Id), out SeedInputs last))
            {
                last = new SeedInputs();
                _inputs.Add((session, goal.Id), last);
            }
            if (Same(inputs, last))
                return false;
            Copy(inputs, last);

            FlowFieldGoalSeeds seeds = ComputeSeeds(goal, area, typeName, own, out bool holdsGoal, out bool fromNeighbors);
            // Until a neighbor has a field of this epoch, an area away from
            // the Goal keeps the field it has, or has none yet.
            if (!holdsGoal && !fromNeighbors && _areas.Find(goal.Request.WorldPosition) != null)
                return false;
            bool changed = entry?.Seeds == null || !seeds.SameSearch(entry.Seeds);
            session.SetGoalSeedsCommand(goal.Id, seeds);
            return changed;
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
            IFlowFieldWorldMember manager = area.Member;
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
            // A Goal in a blocked cell or one without ground starts from the
            // nearest walkable cell of its area, as one Manager's Goal does.
            if (holdsGoal && !Walkable(own, goalCell))
                goalCell = NearestWalkable(own, gx, gy, gz);
            holdsGoal &= goalCell >= 0;
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
                        || !CanCross(area, own, pair.Cell, pair.Direction, typeName, baked: null))
                        continue;
                    Add(pair.Cell, steps + 1, pair.Direction, Height(otherAgent, pair.Other));
                }
            }

            if (_areas.Find(position) == null)
                AddOutward(area, own, position, typeName, _areas.World.FindBakedGoal(position, typeName));

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
        // lattice cells no loaded area holds. With the bake's steps toward the
        // Goal's cell, a step into a cell of a baked area goes where the bake
        // reached that cell and the crossing rule allows it, at those steps
        // plus one. A step into a cell no baked area holds is the estimate's:
        // none from an area the bake holds as it is, which knew that cell was
        // no area's; the steps a straight way to the Goal would take from any
        // other area, and from every area without the bake's steps.
        private void AddOutward(
            FlowFieldWorldArea area,
            FlowFieldField own,
            Vector3 position,
            string typeName,
            FlowFieldWorldBakedGoal baked)
        {
            FlowFieldGridSpace grid = area.Grid;
            FlowFieldWorldAreas.LatticeCell(_areas.World, position, out int goalX, out int goalY, out int goalZ);
            int steps = area.IsVolume ? FlowFieldNeighborUtility.VolumeCount : FlowFieldNeighborUtility.SurfaceCount;
            bool areaBaked = baked != null && _areas.World.BakeHasArea(area.Box);
            for (int cell = 0; cell < grid.CellCount; cell++)
            {
                if (!area.IsOuterCell(cell) || !Walkable(own, cell))
                    continue;
                area.Locate(cell, out int x, out int y, out int z);
                for (int direction = 0; direction < steps; direction++)
                {
                    int tx = x + FlowFieldNeighborUtility.DeltaX[direction];
                    int ty = y + (area.IsVolume ? FlowFieldNeighborUtility.DeltaY[direction] : 0);
                    int tz = z + FlowFieldNeighborUtility.DeltaZ[direction];
                    if (area.Box.Contains(tx, ty, tz) || FindArea(tx, ty, tz) != null)
                        continue;
                    if (baked != null && _areas.World.BakeCovers(tx, ty, tz))
                    {
                        if (baked.TryGet(tx, ty, tz, out int bakedSteps, out float height)
                            && CanCross(area, own, cell, direction, typeName, baked))
                            Add(cell, bakedSteps + 1, direction, height);
                        continue;
                    }
                    if (areaBaked)
                        continue;
                    int estimate = Math.Max(Math.Abs(goalX - tx), Math.Max(Math.Abs(goalY - ty), Math.Abs(goalZ - tz)));
                    Add(cell, estimate + 1, direction, Height(own, cell));
                }
            }
        }

        // ----- Crossings ------------------------------------------------------

        /// <summary>
        /// Whether a mover of this type steps from the area's cell to the
        /// lattice cell in the direction, by the rule each mode's links follow
        /// inside a grid. Surface2D: both cells walkable within the step
        /// height, and on a diagonal both side cells too. Volume3D: both cells
        /// open, and on a diagonal every corner cell it passes beside. A cell
        /// of a loaded area reads its published field; any other cell, the
        /// baked Goal's cells, where a cell the bake reached counts as open.
        /// </summary>
        private bool CanCross(
            FlowFieldWorldArea area,
            FlowFieldField own,
            int cell,
            int direction,
            string typeName,
            FlowFieldWorldBakedGoal baked)
        {
            if (!Walkable(own, cell))
                return false;
            area.Locate(cell, out int x, out int y, out int z);
            int dx = FlowFieldNeighborUtility.DeltaX[direction];
            int dy = area.IsVolume ? FlowFieldNeighborUtility.DeltaY[direction] : 0;
            int dz = FlowFieldNeighborUtility.DeltaZ[direction];
            if (!TryOpenCell(x + dx, y + dy, z + dz, typeName, baked, out float target))
                return false;
            if (area.IsVolume)
            {
                for (int i = FlowFieldNeighborUtility.VolumeCornerStart[direction];
                     i < FlowFieldNeighborUtility.VolumeCornerStart[direction + 1];
                     i++)
                {
                    int corner = FlowFieldNeighborUtility.VolumeCornerDirections[i];
                    if (!TryOpenCell(x + FlowFieldNeighborUtility.DeltaX[corner], y + FlowFieldNeighborUtility.DeltaY[corner],
                            z + FlowFieldNeighborUtility.DeltaZ[corner], typeName, baked, out _))
                        return false;
                }
                return true;
            }

            float step = own.Scene.Surface.MaxStepHeight;
            float height = own.Scene.Surface.Heights[cell];
            if (Math.Abs(height - target) > step)
                return false;
            if (dx == 0 || dz == 0)
                return true;
            return TryOpenCell(x + dx, 0, z, typeName, baked, out float sideX)
                && TryOpenCell(x, 0, z + dz, typeName, baked, out float sideZ)
                && Math.Abs(height - sideX) <= step
                && Math.Abs(height - sideZ) <= step
                && Math.Abs(sideX - target) <= step
                && Math.Abs(sideZ - target) <= step;
        }

        // A lattice cell a mover of the type stands on, and its ground height:
        // walkable in the published field of the loaded area holding it, or,
        // where no loaded area does, reached by the baked Goal.
        private bool TryOpenCell(int x, int y, int z, string typeName, FlowFieldWorldBakedGoal baked, out float height)
        {
            height = 0f;
            FlowFieldWorldArea area = FindArea(x, y, z);
            if (area == null)
                return baked != null && baked.TryGet(x, y, z, out _, out height);
            FlowFieldSession session = SessionOf(area, typeName);
            if (session == null || !session.TryGetPublishedField(out FlowFieldField field)
                || !area.TryCell(x, y, z, out int index) || !Walkable(field, index))
                return false;
            height = Height(field, index);
            return true;
        }

        // The walkable cell nearest the local cell, by squared cell distance,
        // the lowest index on a tie; -1 when the area has none.
        private static int NearestWalkable(FlowFieldField field, int x, int y, int z)
        {
            FlowFieldGridSpace grid = field.Grid;
            int nearest = -1;
            long best = long.MaxValue;
            for (int cell = 0; cell < grid.CellCount; cell++)
            {
                if (!Walkable(field, cell))
                    continue;
                grid.Locate(cell, out int cx, out int cy, out int cz);
                long dx = cx - x;
                long dy = cy - y;
                long dz = cz - z;
                long distance = dx * dx + dy * dy + dz * dz;
                if (distance < best)
                {
                    best = distance;
                    nearest = cell;
                }
            }
            return nearest;
        }

        private static bool Walkable(FlowFieldField field, int index)
            => !field.Agent.Blocked[index] && (field.Scene == null || field.Scene.Surface.IsSurfaceValid(index));

        private static float Height(FlowFieldField field, int index)
            => field.Scene != null ? field.Scene.Surface.Heights[index] : 0f;

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
    }
}
