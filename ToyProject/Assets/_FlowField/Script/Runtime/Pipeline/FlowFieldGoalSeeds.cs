using System;

namespace Common.FlowField
{
    /// <summary>
    /// Where the search of a world Goal starts in one area: the Goal's own cell
    /// in the area that holds it, cells beside a touching area with the steps
    /// that area's field still needs from there, and the area's outer cells
    /// with straight-line estimates toward a Goal no loaded area holds. A seed
    /// with a step direction leads across the border; the Goal cell has none.
    /// Seeds are sorted by cost, then by cell.
    /// </summary>
    internal sealed class FlowFieldGoalSeeds
    {
        internal const int NoDirection = -1;

        internal readonly int[] Cells;
        internal readonly int[] Costs;
        internal readonly int[] Directions;
        // Surface2D: the ground height of the cell across the border, which
        // the step's direction climbs to. Volume3D leaves it 0.
        internal readonly float[] Heights;
        // The Goal's cell in this area, or -1.
        internal readonly int GoalCell;
        // The Goal in this area's local cells, possibly outside the grid: the
        // directions break ties toward it.
        internal readonly int GoalX;
        internal readonly int GoalY;
        internal readonly int GoalZ;
        // The world Goal's epoch these seeds belong to.
        internal readonly int Epoch;

        internal int Count => Cells.Length;

        internal FlowFieldGoalSeeds(
            int[] cells,
            int[] costs,
            int[] directions,
            float[] heights,
            int goalCell,
            int goalX,
            int goalY,
            int goalZ,
            int epoch)
        {
            if (cells == null || costs == null || directions == null || heights == null
                || costs.Length != cells.Length || directions.Length != cells.Length || heights.Length != cells.Length)
                throw new ArgumentException("Goal seeds need one cost, direction and height per cell.");
            int[] order = new int[cells.Length];
            for (int i = 0; i < order.Length; i++)
                order[i] = i;
            Array.Sort(order, (a, b) => costs[a] != costs[b] ? costs[a].CompareTo(costs[b]) : cells[a].CompareTo(cells[b]));
            Cells = new int[order.Length];
            Costs = new int[order.Length];
            Directions = new int[order.Length];
            Heights = new float[order.Length];
            for (int i = 0; i < order.Length; i++)
            {
                Cells[i] = cells[order[i]];
                Costs[i] = costs[order[i]];
                Directions[i] = directions[order[i]];
                Heights[i] = heights[order[i]];
            }
            GoalCell = goalCell;
            GoalX = goalX;
            GoalY = goalY;
            GoalZ = goalZ;
            Epoch = epoch;
        }

        /// <summary>Whether both start the same search.</summary>
        internal bool SameSearch(FlowFieldGoalSeeds other)
        {
            if (other == null || other.Count != Count || other.GoalCell != GoalCell
                || other.GoalX != GoalX || other.GoalY != GoalY || other.GoalZ != GoalZ)
                return false;
            for (int i = 0; i < Count; i++)
            {
                if (Cells[i] != other.Cells[i] || Costs[i] != other.Costs[i] || Directions[i] != other.Directions[i]
                    || Heights[i] != other.Heights[i])
                    return false;
            }
            return true;
        }

        /// <summary>
        /// The per-cell portal arrays a solve reads: for each cell the cheapest
        /// seed that leads across the border, its cost and its height, or
        /// <see cref="NoDirection"/>.
        /// </summary>
        internal void FillPortals(int[] directions, int[] costs, float[] heights)
        {
            for (int i = 0; i < directions.Length; i++)
            {
                directions[i] = NoDirection;
                costs[i] = int.MaxValue;
                heights[i] = 0f;
            }
            for (int i = 0; i < Count; i++)
            {
                int cell = Cells[i];
                if (Directions[i] == NoDirection || Costs[i] >= costs[cell])
                    continue;
                directions[cell] = Directions[i];
                costs[cell] = Costs[i];
                heights[cell] = Heights[i];
            }
        }
    }
}
