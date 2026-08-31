using UnityEngine;

namespace Common.FlowField
{
    internal static class FlowFieldBakeBoundsUtility
    {
        public const float MinCellSize = 0.01f;
        public const float MinBoundsHeight = 0.01f;
        public const int MaxCellCount = 100000;
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
                throw new System.ArgumentOutOfRangeException(nameof(candidate));

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
        /// Bake Bounds에서 월드 Grid 레이아웃을 계산합니다. 입력이 아직 유효한지
        /// 확인하는 저수준 검사이며, 표현할 수 없는 레이아웃이면 false를 반환합니다.
        /// </summary>
        /// <returns>레이아웃이 유효하면 true, 입력이 유효하지 않으면 false입니다.</returns>
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
                || cellSize < MinCellSize
                || localBounds.size.x <= 0f
                || localBounds.size.y < MinBoundsHeight
                || localBounds.size.z <= 0f)
            {
                return false;
            }

            int width = CalculateCellCount(localBounds.size.x, cellSize);
            int depth = CalculateCellCount(localBounds.size.z, cellSize);
            if (!TryValidateCellCount(width, depth, out _))
                return false;

            Bounds snapped = new Bounds(
                localBounds.center,
                new Vector3(width * cellSize, localBounds.size.y, depth * cellSize));
            worldBounds = new Bounds(managerWorldPosition + snapped.center, snapped.size);
            Vector3 origin = new Vector3(worldBounds.min.x, worldBounds.center.y, worldBounds.min.z);
            grid = FlowFieldGridSpace.FromCellGrid(origin, width, depth, cellSize);
            return grid.IsValid;
        }

        /// <summary>
        /// Grid 셀 수가 양수이고 지원 상한 안에 있는지 확인합니다. 범위를 벗어난
        /// 설정을 편집기에서 진단할 수 있도록 false를 정상적인 검증 결과로 반환합니다.
        /// </summary>
        /// <returns>셀 수가 지원 범위면 true, 아니면 false입니다.</returns>
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
        {
            if (!FlowFieldGridSpace.IsFinite(size) || size <= 0f)
                throw new System.ArgumentOutOfRangeException(nameof(size));
            int count = Mathf.FloorToInt(size / cellSize + 0.5f);
            if (count <= 0)
                throw new System.ArgumentOutOfRangeException(nameof(size), "Bounds do not contain any cells.");
            return count;
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
