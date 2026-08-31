using System;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// PathData가 공유하는 배열 기반 geometry 연산입니다. Unity 수명주기나 캐시 소유권은 갖지 않습니다.
    /// </summary>
    internal static class PathGeometryUtility
    {
        public static float[] CalculateCumulativeDistances(Vector3[] pathPoints)
        {
            if (pathPoints == null)
                throw new ArgumentNullException(nameof(pathPoints));
            if (pathPoints.Length == 0)
                throw new ArgumentException("At least one path point is required.", nameof(pathPoints));

            float[] distances = new float[pathPoints.Length];
            for (int i = 1; i < pathPoints.Length; i++)
                distances[i] = distances[i - 1] + Vector3.Distance(pathPoints[i - 1], pathPoints[i]);

            return distances;
        }

        public static int FindSegmentIndex(float[] distances, float targetDistance)
        {
            if (distances == null)
                throw new ArgumentNullException(nameof(distances));
            if (distances.Length < 2)
                throw new ArgumentException("At least two cumulative distances are required.", nameof(distances));
            if (float.IsNaN(targetDistance) || float.IsInfinity(targetDistance))
                throw new ArgumentOutOfRangeException(nameof(targetDistance));

            int left = 0;
            int right = distances.Length - 1;
            while (left < right - 1)
            {
                int mid = (left + right) / 2;
                if (distances[mid] < targetDistance)
                    left = mid;
                else if (distances[mid] > targetDistance)
                    right = mid;
                else
                    return mid;
            }

            return left;
        }

        public static bool IsSameResult(
            bool wasInitialized,
            Vector3[] previousPoints,
            float previousLength,
            Vector3[] nextPoints,
            float nextLength)
        {
            if (!wasInitialized)
                return false;
            if (previousPoints == null)
                throw new ArgumentNullException(nameof(previousPoints));
            if (nextPoints == null)
                throw new ArgumentNullException(nameof(nextPoints));

            if (previousPoints.Length != nextPoints.Length
                || !Mathf.Approximately(previousLength, nextLength))
                return false;

            for (int i = 0; i < nextPoints.Length; i++)
            {
                if (previousPoints[i] != nextPoints[i])
                    return false;
            }

            return true;
        }
    }
}
