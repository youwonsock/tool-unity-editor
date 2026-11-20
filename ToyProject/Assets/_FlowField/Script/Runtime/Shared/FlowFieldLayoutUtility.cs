using System;
using UnityEngine;

namespace Common.FlowField
{
    internal static class FlowFieldLayoutUtility
    {
        public const float MinCellSize = 0.01f;
        public const float MinBoundsHeight = 0.01f;
        public const int MaxSurfaceCellCount = 100000;
        public const int MaxVolumeCellCount = 1000000;
        private const float CompareEpsilon = 0.0001f;

        public static Bounds DefaultLocalBounds
            => new Bounds(new Vector3(20f, 0f, 20f), new Vector3(40f, 10f, 40f));

        public static float ValidateCellSize(float cellSize)
        {
            if (!FlowFieldGridSpace.IsFinite(cellSize) || cellSize < MinCellSize)
                throw new System.ArgumentOutOfRangeException(nameof(cellSize));
            return cellSize;
        }

        public static Bounds SnapCenterAnchored(Bounds bounds, float cellSize)
        {
            float resolvedCellSize = ValidateCellSize(cellSize);
            if (!FlowFieldGridSpace.IsFinite(bounds.center)
                || !FlowFieldGridSpace.IsFinite(bounds.size)
                || bounds.size.x <= 0f || bounds.size.y <= 0f || bounds.size.z <= 0f)
                throw new System.ArgumentOutOfRangeException(nameof(bounds));
            Vector3 center = bounds.center;
            Vector3 size = bounds.size;
            size.x = SnapHorizontalSize(size.x, resolvedCellSize);
            if (size.y < MinBoundsHeight)
                throw new System.ArgumentOutOfRangeException(nameof(bounds), "Bake Bounds height is below the minimum.");
            size.z = SnapHorizontalSize(size.z, resolvedCellSize);
            return new Bounds(center, size);
        }

        public static Bounds SnapVolumeCenterAnchored(Bounds bounds, float cellSize)
        {
            float resolvedCellSize = ValidateCellSize(cellSize);
            if (!FlowFieldGridSpace.IsFinite(bounds.center)
                || !FlowFieldGridSpace.IsFinite(bounds.size)
                || bounds.size.x <= 0f || bounds.size.y <= 0f || bounds.size.z <= 0f)
                throw new ArgumentOutOfRangeException(nameof(bounds));
            return new Bounds(
                bounds.center,
                new Vector3(
                    SnapVolumeAxis(bounds.size.x, resolvedCellSize),
                    SnapVolumeAxis(bounds.size.y, resolvedCellSize),
                    SnapVolumeAxis(bounds.size.z, resolvedCellSize)));
        }

        public static Bounds SnapResizedKeepingOppositeFace(
            Bounds previous,
            Bounds candidate,
            float cellSize)
            => SnapResizedKeepingOppositeFace(previous, candidate, cellSize, snapY: false);

        public static Bounds SnapVolumeResizedKeepingOppositeFace(
            Bounds previous,
            Bounds candidate,
            float cellSize)
            => SnapResizedKeepingOppositeFace(previous, candidate, cellSize, snapY: true);

        private static Bounds SnapResizedKeepingOppositeFace(
            Bounds previous,
            Bounds candidate,
            float cellSize,
            bool snapY)
        {
            if (!FlowFieldGridSpace.IsFinite(previous.center)
                || !FlowFieldGridSpace.IsFinite(previous.size)
                || !FlowFieldGridSpace.IsFinite(candidate.center)
                || !FlowFieldGridSpace.IsFinite(candidate.size))
                throw new System.ArgumentOutOfRangeException(nameof(candidate));
            Bounds resolvedPrevious = snapY
                ? SnapVolumeCenterAnchored(previous, cellSize)
                : SnapCenterAnchored(previous, cellSize);
            // A dragged handle can pass through zero. The smallest box is one cell.
            Vector3 smallest = new Vector3(cellSize, snapY ? cellSize : MinBoundsHeight, cellSize);
            var limited = new Bounds(candidate.center, Vector3.Max(candidate.size, smallest));
            Bounds resolvedCandidate = snapY
                ? SnapVolumeCenterAnchored(limited, cellSize)
                : SnapCenterAnchored(limited, cellSize);

            Vector3 previousMin = resolvedPrevious.min;
            Vector3 previousMax = resolvedPrevious.max;
            Vector3 candidateMin = candidate.min;
            Vector3 candidateMax = candidate.max;
            Vector3 resolvedCenter = resolvedCandidate.center;
            Vector3 resolvedSize = resolvedCandidate.size;
            resolvedCenter.x = ResolveAnchoredCenter(
                previousMin.x,
                previousMax.x,
                candidateMin.x,
                candidateMax.x,
                resolvedSize.x);
            resolvedCenter.y = ResolveAnchoredCenter(
                previousMin.y,
                previousMax.y,
                candidateMin.y,
                candidateMax.y,
                resolvedSize.y);
            resolvedCenter.z = ResolveAnchoredCenter(
                previousMin.z,
                previousMax.z,
                candidateMin.z,
                candidateMax.z,
                resolvedSize.z);
            return new Bounds(resolvedCenter, resolvedSize);
        }

        /// <summary>
        /// Computes the world grid layout of the Bake Bounds. It is the low-level
        /// check of the inputs and returns false for a layout it cannot represent.
        /// </summary>
        /// <returns>True for a valid layout, false for invalid inputs.</returns>
        public static bool TryCreateWorldLayout(
            Vector3 managerWorldPosition,
            Bounds localBounds,
            float cellSize,
            out Bounds worldBounds,
            out FlowFieldGridSpace grid)
        {
            worldBounds = default;
            grid = default;
            if (!HasFiniteLayoutInput(managerWorldPosition, localBounds, cellSize)
                || localBounds.size.x <= 0f
                || localBounds.size.y < MinBoundsHeight
                || localBounds.size.z <= 0f)
            {
                return false;
            }

            if (!TryCalculateCellCount(localBounds.size.x, cellSize, out int width)
                || !TryCalculateCellCount(localBounds.size.z, cellSize, out int depth)
                || !TryValidateCellCount(width, depth, out _))
                return false;

            if (!TryCreateSnappedWorldBounds(
                    managerWorldPosition,
                    localBounds.center,
                    new Vector3(width * cellSize, localBounds.size.y, depth * cellSize),
                    out worldBounds))
                return false;
            Vector3 origin = new Vector3(worldBounds.min.x, worldBounds.center.y, worldBounds.min.z);
            grid = FlowFieldGridSpace.FromCellGrid(origin, width, depth, cellSize);
            return grid.IsValid;
        }

        public static bool TryCreateVolumeWorldLayout(
            Vector3 managerWorldPosition,
            Bounds localBounds,
            float cellSize,
            out Bounds worldBounds,
            out FlowFieldGridSpace grid)
        {
            worldBounds = default;
            grid = default;
            if (!HasFiniteLayoutInput(managerWorldPosition, localBounds, cellSize)
                || localBounds.size.x <= 0f
                || localBounds.size.y <= 0f
                || localBounds.size.z <= 0f)
                return false;

            if (!TryCalculateVolumeAxisCellCount(localBounds.size.x, cellSize, out int width)
                || !TryCalculateVolumeAxisCellCount(localBounds.size.y, cellSize, out int height)
                || !TryCalculateVolumeAxisCellCount(localBounds.size.z, cellSize, out int depth)
                || !TryValidateVolumeCellCount(width, height, depth, out _))
                return false;

            if (!TryCreateSnappedWorldBounds(
                    managerWorldPosition,
                    localBounds.center,
                    new Vector3(width * cellSize, height * cellSize, depth * cellSize),
                    out worldBounds))
                return false;
            grid = FlowFieldGridSpace.FromVolumeCellGrid(
                worldBounds.min,
                width,
                height,
                depth,
                cellSize);
            return grid.IsValid;
        }

        private static bool HasFiniteLayoutInput(Vector3 managerWorldPosition, Bounds localBounds, float cellSize)
            => FlowFieldGridSpace.IsFinite(managerWorldPosition)
                && FlowFieldGridSpace.IsFinite(localBounds.center)
                && FlowFieldGridSpace.IsFinite(localBounds.size)
                && FlowFieldGridSpace.IsFinite(cellSize)
                && cellSize >= MinCellSize;

        private static bool TryCreateSnappedWorldBounds(
            Vector3 managerWorldPosition,
            Vector3 localCenter,
            Vector3 snappedSize,
            out Bounds worldBounds)
        {
            worldBounds = new Bounds(managerWorldPosition + localCenter, snappedSize);
            return FlowFieldGridSpace.IsFinite(worldBounds.center)
                && FlowFieldGridSpace.IsFinite(worldBounds.size);
        }

        /// <summary>
        /// Checks that the cell count is positive and within the supported limit.
        /// It returns false instead of throwing, so the Editor can report the setting.
        /// </summary>
        /// <returns>True within the supported range, otherwise false.</returns>
        public static bool TryValidateCellCount(
            int width,
            int depth,
            out int cellCount)
        {
            cellCount = 0;
            if (width <= 0 || depth <= 0)
                return false;

            long count = (long)width * depth;
            if (count <= 0 || count > MaxSurfaceCellCount || count > int.MaxValue)
                return false;

            cellCount = (int)count;
            return true;
        }

        public static bool TryValidateVolumeCellCount(
            int width,
            int height,
            int depth,
            out int cellCount)
        {
            cellCount = 0;
            if (width <= 0 || height <= 0 || depth <= 0)
                return false;

            long count;
            try
            {
                count = checked((long)width * height * depth);
            }
            catch (OverflowException)
            {
                return false;
            }
            if (count <= 0 || count > MaxVolumeCellCount || count > int.MaxValue)
                return false;

            cellCount = (int)count;
            return true;
        }

        /// <summary>The tolerance settings and requests compare their values with.</summary>
        internal static bool Approximately(float left, float right)
            => Mathf.Abs(left - right) <= CompareEpsilon;

        public static bool Approximately(Bounds left, Bounds right)
            => FlowFieldGridSpace.Approximately(
                    left.center,
                    right.center,
                    CompareEpsilon * CompareEpsilon)
                && FlowFieldGridSpace.Approximately(
                    left.size,
                    right.size,
                    CompareEpsilon * CompareEpsilon);

        private static int CalculateCellCount(float size, float cellSize)
        {
            if (!TryCalculateCellCount(size, cellSize, out int count))
                throw new System.ArgumentOutOfRangeException(nameof(size));
            return count;
        }

        private static bool TryCalculateCellCount(float size, float cellSize, out int count)
        {
            count = 0;
            if (!FlowFieldGridSpace.IsFinite(size)
                || size <= 0f
                || !FlowFieldGridSpace.IsFinite(cellSize)
                || cellSize <= 0f)
                return false;

            double rounded = Math.Floor((double)size / cellSize + 0.5d);
            if (rounded <= 0d || rounded > int.MaxValue)
                return false;
            count = (int)rounded;
            return true;
        }

        private static bool TryCalculateVolumeAxisCellCount(
            float size,
            float cellSize,
            out int count)
        {
            count = 0;
            if (!FlowFieldGridSpace.IsFinite(size)
                || size <= 0f
                || !FlowFieldGridSpace.IsFinite(cellSize)
                || cellSize <= 0f)
                return false;

            double rounded = Math.Floor((double)size / cellSize + 0.5d);
            rounded = Math.Max(1d, rounded);
            if (rounded > int.MaxValue)
                return false;
            count = (int)rounded;
            return true;
        }

        // Matches TryCreateVolumeWorldLayout, where a thin axis still holds one cell.
        private static float SnapVolumeAxis(float size, float cellSize)
        {
            if (!TryCalculateVolumeAxisCellCount(size, cellSize, out int count))
                throw new System.ArgumentOutOfRangeException(nameof(size));
            return count * cellSize;
        }

        private static float SnapHorizontalSize(float size, float cellSize)
            => CalculateCellCount(size, cellSize) * cellSize;

        private static float ResolveAnchoredCenter(
            float previousMin,
            float previousMax,
            float candidateMin,
            float candidateMax,
            float snappedSize)
        {
            float minDelta = Mathf.Abs(candidateMin - previousMin);
            float maxDelta = Mathf.Abs(candidateMax - previousMax);
            return minDelta > maxDelta
                ? previousMax - snappedSize * 0.5f
                : previousMin + snappedSize * 0.5f;
        }
    }
}
