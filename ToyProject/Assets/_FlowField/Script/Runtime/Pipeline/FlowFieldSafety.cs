using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// The regions where the published field may be wrong, the same in both
    /// space modes. An obstacle change marks its region the moment it is
    /// notified; the region stays uncertain until a build that queried it after
    /// the change is published, and movers in it stop until then.
    /// </summary>
    internal sealed class FlowFieldSafety
    {
        private const int MaxUncertainBounds = 64;
        // Surface2D compares regions with cells in XZ only.
        private readonly bool _planar;
        // Changes no running build has observed yet.
        private readonly List<Bounds> _uncertainBounds = new List<Bounds>(8);
        // Changes the running build observes, still unsafe until it is published.
        private readonly List<Bounds> _observingBounds = new List<Bounds>(8);

        internal FlowFieldSafety(bool planar) => _planar = planar;

        internal int UncertainRegionCount => _uncertainBounds.Count + _observingBounds.Count;

        internal void ClearRegions()
        {
            _uncertainBounds.Clear();
            _observingBounds.Clear();
        }

        /// <summary>
        /// Every build queries the obstacles in these regions after it starts, so
        /// it resolves the regions recorded so far. A discarded build keeps them
        /// here until its replacement publishes.
        /// </summary>
        internal void BeginBuild()
        {
            for (int index = 0; index < _uncertainBounds.Count; index++)
                AddBounds(_observingBounds, _uncertainBounds[index]);
            _uncertainBounds.Clear();
        }

        internal void ClearObservedRegions() => _observingBounds.Clear();

        internal Bounds[] ObservedRegions() => _observingBounds.ToArray();

        // An obstacle mask/clearance change invalidates the complete field.
        // Preserve its distinct policy: no dirty-region expansion.
        internal void InvalidateRegion(Bounds bounds) => AddBounds(_uncertainBounds, bounds);

        /// <summary>A changed obstacle region, grown by the cells and clearance it can reach.</summary>
        internal void MarkDirty(Bounds bounds, float margin)
        {
            if (!FlowFieldGridSpace.IsFinite(bounds.center) || !FlowFieldGridSpace.IsFinite(bounds.size))
                throw new ArgumentOutOfRangeException(nameof(bounds));
            Bounds expanded = bounds;
            expanded.Expand(Vector3.one * (margin * 2f));
            AddBounds(_uncertainBounds, expanded);
        }

        /// <summary>Whether a cell centred at <paramref name="center"/> lies in an uncertain region.</summary>
        internal bool IsCellUncertain(FlowFieldGridSpace grid, Vector3 center)
        {
            if (UncertainRegionCount == 0)
                return false;
            Bounds cell = new Bounds(center, Vector3.one * grid.CellSize);
            return Intersects(cell, _uncertainBounds) || Intersects(cell, _observingBounds);
        }

        private bool Intersects(Bounds cell, List<Bounds> regions)
        {
            for (int i = 0; i < regions.Count; i++)
            {
                Bounds region = regions[i];
                if (_planar
                    ? cell.min.x <= region.max.x && cell.max.x >= region.min.x
                        && cell.min.z <= region.max.z && cell.max.z >= region.min.z
                    : region.Intersects(cell))
                    return true;
            }
            return false;
        }

        private static void AddBounds(List<Bounds> list, Bounds bounds)
        {
            for (int index = 0; index < list.Count; index++)
            {
                if (!list[index].Intersects(bounds))
                    continue;
                list[index] = UnionBounds(list[index], bounds);
                return;
            }

            if (list.Count < MaxUncertainBounds)
            {
                list.Add(bounds);
                return;
            }

            // Once the bounded dirty-list is full, retaining a conservative
            // union is safer than evicting an area and treating it as clear.
            list[0] = UnionBounds(list[0], bounds);
        }

        private static Bounds UnionBounds(Bounds left, Bounds right)
        {
            left.Encapsulate(right.min);
            left.Encapsulate(right.max);
            return left;
        }
    }
}
