using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// What a world bake found, kept in the world asset: the lattice boxes of
    /// the areas it searched, and for each Goal it searched and each agent
    /// type, the steps to the Goal from every reached outer cell of those
    /// areas. An area beside a lattice cell of a baked area that is not loaded
    /// seeds the step into it from these steps, so a world Goal no loaded area
    /// holds leads the way the bake found. A baked area takes no step into a
    /// cell no baked area holds; an area the bake does not hold takes the
    /// straight-line estimate there. The bake keeps the world settings it ran
    /// with and is unused under others.
    /// </summary>
    [Serializable]
    internal sealed class FlowFieldWorldBake
    {
        [SerializeField] private FlowFieldSpaceMode _spaceMode;
        [SerializeField] private float _cellSize;
        [SerializeField] private Vector3 _latticeOrigin;
        // Min x, y, z and size x, y, z of each baked area in turn.
        [SerializeField] private int[] _areas = Array.Empty<int>();
        [SerializeField] private FlowFieldWorldBakedGoal[] _goals = Array.Empty<FlowFieldWorldBakedGoal>();

        internal static FlowFieldWorldBake Create(
            FlowFieldWorld world,
            IReadOnlyList<FlowFieldLatticeBox> areas,
            FlowFieldWorldBakedGoal[] goals)
        {
            var boxes = new int[areas.Count * 6];
            for (int i = 0; i < areas.Count; i++)
            {
                FlowFieldLatticeBox box = areas[i];
                boxes[i * 6] = box.MinX;
                boxes[i * 6 + 1] = box.MinY;
                boxes[i * 6 + 2] = box.MinZ;
                boxes[i * 6 + 3] = box.SizeX;
                boxes[i * 6 + 4] = box.SizeY;
                boxes[i * 6 + 5] = box.SizeZ;
            }
            return new FlowFieldWorldBake
            {
                _spaceMode = world.SpaceMode,
                _cellSize = world.CellSize,
                _latticeOrigin = world.LatticeOrigin,
                _areas = boxes,
                _goals = goals ?? Array.Empty<FlowFieldWorldBakedGoal>(),
            };
        }

        internal int AreaCount => (_areas?.Length ?? 0) / 6;

        internal FlowFieldLatticeBox Area(int index)
            => new FlowFieldLatticeBox(
                _areas[index * 6],
                _areas[index * 6 + 1],
                _areas[index * 6 + 2],
                _areas[index * 6 + 3],
                _areas[index * 6 + 4],
                _areas[index * 6 + 5]);

        /// <summary>Whether a baked area holds the lattice cell.</summary>
        internal bool Covers(int x, int y, int z)
        {
            for (int i = 0; i < AreaCount; i++)
            {
                if (Area(i).Contains(x, y, z))
                    return true;
            }
            return false;
        }

        internal bool HasArea(in FlowFieldLatticeBox box)
        {
            for (int i = 0; i < AreaCount; i++)
            {
                FlowFieldLatticeBox area = Area(i);
                if (area.MinX == box.MinX && area.MinY == box.MinY && area.MinZ == box.MinZ
                    && area.SizeX == box.SizeX && area.SizeY == box.SizeY && area.SizeZ == box.SizeZ)
                    return true;
            }
            return false;
        }

        internal int GoalCount => _goals?.Length ?? 0;

        internal FlowFieldWorldBakedGoal Goal(int index) => _goals[index];

        /// <summary>Whether the bake ran with the world's Space Mode, Cell Size and Lattice Origin.</summary>
        internal bool Matches(FlowFieldWorld world)
            => _cellSize > 0f
                && world.SpaceMode == _spaceMode
                && Mathf.Abs(world.CellSize - _cellSize) <= FlowFieldManagerConfig.ValueEpsilon
                && world.LatticeOrigin == _latticeOrigin;

        /// <summary>The steps toward a Goal in the lattice cell for the agent type, or null.</summary>
        internal FlowFieldWorldBakedGoal Find(int x, int y, int z, string agentType)
        {
            for (int i = 0; i < GoalCount; i++)
            {
                FlowFieldWorldBakedGoal goal = _goals[i];
                if (goal != null && goal.IsAt(x, y, z) && string.Equals(goal.AgentType, agentType, StringComparison.Ordinal))
                    return goal;
            }
            return null;
        }
    }

    /// <summary>
    /// The steps one agent type takes to one baked Goal from the outer cells
    /// of every area the bake reached it from, with their ground heights in
    /// Surface2D. A cell the Goal cannot reach is left out.
    /// </summary>
    [Serializable]
    internal sealed class FlowFieldWorldBakedGoal
    {
        [SerializeField] private string _agentType;
        [SerializeField] private int _goalX;
        [SerializeField] private int _goalY;
        [SerializeField] private int _goalZ;
        // Lattice x, y and z of each cell in turn.
        [SerializeField] private int[] _cells = Array.Empty<int>();
        [SerializeField] private int[] _steps = Array.Empty<int>();
        // Surface2D only; empty in Volume3D.
        [SerializeField] private float[] _heights = Array.Empty<float>();

        // Built on first use for the arrays it was built from, which a
        // deserialize replaces.
        [NonSerialized] private Dictionary<(int, int, int), int> _index;
        [NonSerialized] private int[] _indexedCells;

        internal static FlowFieldWorldBakedGoal Create(
            string agentType,
            int goalX,
            int goalY,
            int goalZ,
            int[] cells,
            int[] steps,
            float[] heights)
            => new FlowFieldWorldBakedGoal
            {
                _agentType = agentType,
                _goalX = goalX,
                _goalY = goalY,
                _goalZ = goalZ,
                _cells = cells,
                _steps = steps,
                _heights = heights,
            };

        internal string AgentType => _agentType;
        internal int GoalX => _goalX;
        internal int GoalY => _goalY;
        internal int GoalZ => _goalZ;
        internal int CellCount => _steps?.Length ?? 0;

        internal bool IsAt(int x, int y, int z) => _goalX == x && _goalY == y && _goalZ == z;

        /// <summary>The baked steps from a lattice cell, and its ground height in Surface2D.</summary>
        internal bool TryGet(int x, int y, int z, out int steps, out float height)
        {
            steps = FlowFieldGoalLayer.Unreachable;
            height = 0f;
            Dictionary<(int, int, int), int> index = Index();
            if (index == null || !index.TryGetValue((x, y, z), out int i))
                return false;
            steps = _steps[i];
            if (_heights != null && i < _heights.Length)
                height = _heights[i];
            return true;
        }

        private Dictionary<(int, int, int), int> Index()
        {
            if (_cells == null || _steps == null || _cells.Length != _steps.Length * 3)
                return null;
            if (_index != null && ReferenceEquals(_indexedCells, _cells))
                return _index;
            var index = new Dictionary<(int, int, int), int>(_steps.Length);
            for (int i = 0; i < _steps.Length; i++)
                index[(_cells[i * 3], _cells[i * 3 + 1], _cells[i * 3 + 2])] = i;
            _index = index;
            _indexedCells = _cells;
            return index;
        }
    }
}
