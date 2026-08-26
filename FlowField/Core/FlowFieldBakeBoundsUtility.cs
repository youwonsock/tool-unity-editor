using UnityEngine;

namespace Supercent.Common.FlowField
{
    internal static class FlowFieldBakeBoundsUtility
    {
        public const float MinCellSize = 0.01f;
        public const float MinBoundsHeight = 0.01f;
        public const int MaxCellCount = 100000;
        private const float DefaultCellSize = 0.5f;
        private const float CompareEpsilon = 0.0001f;

        public static Bounds DefaultLocalBounds
            => new Bounds(new Vector3(20f, 0f, 20f), new Vector3(40f, 10f, 40f));

        public static float SanitizeCellSize(float cellSize)
            => FlowFieldGridSpace.IsFinite(cellSize)
                ? Mathf.Max(MinCellSize, cellSize)
                : DefaultCellSize;

        public static Bounds SnapCenterAnchored(Bounds bounds, float cellSize)
        {
            float resolvedCellSize = SanitizeCellSize(cellSize);
            Bounds fallback = DefaultLocalBounds;
            Vector3 center = FlowFieldGridSpace.IsFinite(bounds.center)
                ? bounds.center
                : fallback.center;
            Vector3 size = FlowFieldGridSpace.IsFinite(bounds.size)
                ? bounds.size
                : fallback.size;
            size.x = SnapHorizontalSize(size.x, resolvedCellSize);
            size.y = Mathf.Max(MinBoundsHeight, Mathf.Abs(size.y));
            size.z = SnapHorizontalSize(size.z, resolvedCellSize);
            return new Bounds(center, size);
        }

        public static Bounds SnapResizedKeepingOppositeFace(
            Bounds previous,
            Bounds candidate,
            float cellSize)
        {
            Bounds resolvedPrevious = SnapCenterAnchored(previous, cellSize);
            Bounds resolvedCandidate = SnapCenterAnchored(candidate, cellSize);
            if (!FlowFieldGridSpace.IsFinite(previous.center)
                || !FlowFieldGridSpace.IsFinite(previous.size)
                || !FlowFieldGridSpace.IsFinite(candidate.center)
                || !FlowFieldGridSpace.IsFinite(candidate.size))
            {
                return resolvedCandidate;
            }

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

        public static bool TryCreateWorldLayout(
            Vector3 managerWorldPosition,
            Bounds localBounds,
            float cellSize,
            out Bounds worldBounds,
            out FlowFieldGridSpace grid)
        {
            worldBounds = default;
            grid = default;
            if (!FlowFieldGridSpace.IsFinite(managerWorldPosition)
                || !FlowFieldGridSpace.IsFinite(localBounds.center)
                || !FlowFieldGridSpace.IsFinite(localBounds.size)
                || !FlowFieldGridSpace.IsFinite(cellSize)
                || cellSize < MinCellSize)
            {
                return false;
            }

            Bounds snapped = SnapCenterAnchored(localBounds, cellSize);
            int width = CalculateCellCount(snapped.size.x, cellSize);
            int depth = CalculateCellCount(snapped.size.z, cellSize);
            worldBounds = new Bounds(managerWorldPosition + snapped.center, snapped.size);
            Vector3 origin = new Vector3(worldBounds.min.x, worldBounds.center.y, worldBounds.min.z);
            grid = FlowFieldGridSpace.FromCellGrid(origin, width, depth, cellSize);
            return grid.IsValid;
        }

        public static bool TryValidateCellCount(
            int width,
            int depth,
            out int cellCount)
        {
            cellCount = 0;
            if (width <= 0 || depth <= 0)
                return false;

            long count = (long)width * depth;
            if (count <= 0 || count > MaxCellCount || count > int.MaxValue)
                return false;

            cellCount = (int)count;
            return true;
        }

        public static bool Approximately(Bounds left, Bounds right)
            => (left.center - right.center).sqrMagnitude <= CompareEpsilon * CompareEpsilon
                && (left.size - right.size).sqrMagnitude <= CompareEpsilon * CompareEpsilon;

        private static int CalculateCellCount(float size, float cellSize)
            => Mathf.Max(1, Mathf.FloorToInt(Mathf.Abs(size) / cellSize + 0.5f));

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
