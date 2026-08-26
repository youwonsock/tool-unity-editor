using System;
using UnityEngine;

namespace Supercent.Common.TransformPath
{
    /// <summary>
    /// PathData가 공유하는 배열 기반 geometry 연산입니다. Unity 수명주기나 캐시 소유권은 갖지 않습니다.
    /// </summary>
    internal static class PathGeometryUtility
    {
        public static float[] CalculateCumulativeDistances(Vector3[] pathPoints)
        {
            if (pathPoints == null || pathPoints.Length == 0)
                return Array.Empty<float>();

            float[] distances = new float[pathPoints.Length];
            for (int i = 1; i < pathPoints.Length; i++)
                distances[i] = distances[i - 1] + Vector3.Distance(pathPoints[i - 1], pathPoints[i]);

            return distances;
        }

        public static int FindSegmentIndex(float[] distances, float targetDistance)
        {
            if (distances == null || distances.Length < 2)
                return -1;

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
            if (!wasInitialized || previousPoints == null || nextPoints == null)
                return false;

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
