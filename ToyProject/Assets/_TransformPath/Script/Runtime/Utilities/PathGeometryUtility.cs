using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>Temporary collections owned by one geometry builder.</summary>
    internal sealed class PathGeometryBuildBuffer
    {
        #region Member Variables

        public readonly List<Vector3> ControlPoints = new List<Vector3>();
        public readonly List<Vector3> BuildPoints = new List<Vector3>();
        public readonly List<float> SegmentDistances = new List<float>();
        public readonly List<Vector3> SplineControlPoints = new List<Vector3>();

        #endregion
    }

    /// <summary>Immutable result published by the geometry builder.</summary>
    internal readonly struct PathGeometryResult
    {
        #region Properties

        public Vector3[] Points { get; }
        public float[] CumulativeDistances { get; }
        public float Length { get; }

        #endregion


        #region Public Methods

        public PathGeometryResult(
            Vector3[] points,
            float[] cumulativeDistances,
            float length)
        {
            Points = points;
            CumulativeDistances = cumulativeDistances;
            Length = length;
        }

        #endregion
    }

    /// <summary>
    /// Shared pure geometry algorithms for runtime paths and editor previews.
    /// Cache ownership and Unity lifecycle remain with the caller.
    /// </summary>
    internal static class PathGeometryUtility
    {
        #region Constants

        private const int MIN_PATH_POINTS = 2;

        #endregion


        #region Public Methods

        public static bool TryBuild(
            IReadOnlyList<Vector3> controlPoints,
            PathData.ECurveType curveType,
            int segmentCount,
            PathGeometryBuildBuffer buffer,
            out PathGeometryResult result,
            out string error)
        {
            result = default(PathGeometryResult);
            if (controlPoints == null || controlPoints.Count < MIN_PATH_POINTS)
            {
                error = "At least two control points are required.";
                return false;
            }

            if (segmentCount < MIN_PATH_POINTS)
            {
                error = "Segment count must be at least two.";
                return false;
            }

            if (buffer == null)
            {
                error = "A geometry build buffer is required.";
                return false;
            }

            for (int i = 0; i < controlPoints.Count; i++)
            {
                if (!PathValueUtility.IsFinite(controlPoints[i]))
                {
                    error = $"Control point {i} is not finite.";
                    return false;
                }
            }

            buffer.ControlPoints.Clear();
            buffer.ControlPoints.AddRange(controlPoints);
            buffer.BuildPoints.Clear();
            buffer.SegmentDistances.Clear();
            buffer.SplineControlPoints.Clear();

            switch (curveType)
            {
                case PathData.ECurveType.Linear:
                    GenerateLinearPath(buffer, segmentCount);
                    break;
                case PathData.ECurveType.SplineApproximating:
                    GenerateSplinePath(buffer, segmentCount);
                    break;
                case PathData.ECurveType.SplineInterpolating:
                    GenerateCatmullRomPath(buffer, segmentCount);
                    break;
                default:
                    error = "Curve type is invalid.";
                    return false;
            }

            if (buffer.BuildPoints.Count < MIN_PATH_POINTS)
            {
                error = "Geometry generation produced too few points.";
                return false;
            }

            Vector3[] points = buffer.BuildPoints.ToArray();
            float[] cumulativeDistances = CalculateCumulativeDistances(points);
            float length = cumulativeDistances[cumulativeDistances.Length - 1];
            if (!PathValueUtility.IsFinite(length) || length <= 0f)
            {
                error = "Path control points must produce a measurable path length.";
                return false;
            }

            result = new PathGeometryResult(points, cumulativeDistances, length);
            error = null;
            return true;
        }

        public static float[] CalculateCumulativeDistances(Vector3[] pathPoints)
        {
            if (pathPoints == null || pathPoints.Length == 0)
                return new float[0];

            float[] distances = new float[pathPoints.Length];
            for (int i = 1; i < pathPoints.Length; i++)
                distances[i] = distances[i - 1] + Vector3.Distance(pathPoints[i - 1], pathPoints[i]);
            return distances;
        }

        public static int FindSegmentIndex(float[] distances, float targetDistance)
        {
            if (distances == null || distances.Length < 2)
                return 0;

            int left = 0;
            int right = distances.Length - 1;
            while (left < right - 1)
            {
                int middle = (left + right) / 2;
                if (distances[middle] < targetDistance)
                    left = middle;
                else if (distances[middle] > targetDistance)
                    right = middle;
                else
                    return middle;
            }

            return left;
        }

        public static bool IsSameResult(
            Vector3[] leftPoints,
            float leftLength,
            PathGeometryResult right)
        {
            if (leftPoints == null
                || right.Points == null
                || leftPoints.Length != right.Points.Length
                || !Mathf.Approximately(leftLength, right.Length))
                return false;

            return AreSamePoints(leftPoints, right.Points);
        }

        public static bool AreSamePoints(
            IReadOnlyList<Vector3> left,
            IReadOnlyList<Vector3> right)
        {
            if (left == null || right == null || left.Count != right.Count)
                return false;
            for (int i = 0; i < left.Count; i++)
            {
                if (left[i] != right[i])
                    return false;
            }

            return true;
        }

        #endregion


        #region Private Methods

        private static void GenerateLinearPath(
            PathGeometryBuildBuffer buffer,
            int segmentCount)
        {
            float totalDistance = 0f;
            for (int i = 0; i < buffer.ControlPoints.Count - 1; i++)
            {
                float distance = Vector3.Distance(
                    buffer.ControlPoints[i],
                    buffer.ControlPoints[i + 1]);
                buffer.SegmentDistances.Add(distance);
                totalDistance += distance;
            }

            for (int i = 0; i <= segmentCount; i++)
            {
                float targetDistance = (float)i / segmentCount * totalDistance;
                buffer.BuildPoints.Add(GetLinearPointAtDistance(buffer, targetDistance, totalDistance));
            }
        }

        private static Vector3 GetLinearPointAtDistance(
            PathGeometryBuildBuffer buffer,
            float targetDistance,
            float totalDistance)
        {
            if (targetDistance <= 0f)
                return buffer.ControlPoints[0];
            if (targetDistance >= totalDistance)
                return buffer.ControlPoints[buffer.ControlPoints.Count - 1];

            float currentDistance = 0f;
            for (int i = 0; i < buffer.SegmentDistances.Count; i++)
            {
                float segmentLength = buffer.SegmentDistances[i];
                float endDistance = currentDistance + segmentLength;
                if (targetDistance <= endDistance)
                {
                    if (segmentLength <= Mathf.Epsilon)
                        return buffer.ControlPoints[i];
                    return Vector3.Lerp(
                        buffer.ControlPoints[i],
                        buffer.ControlPoints[i + 1],
                        (targetDistance - currentDistance) / segmentLength);
                }

                currentDistance = endDistance;
            }

            return buffer.ControlPoints[buffer.ControlPoints.Count - 1];
        }

        private static void GenerateSplinePath(
            PathGeometryBuildBuffer buffer,
            int segmentCount)
        {
            PrepareControlPoints(buffer);
            for (int i = 0; i <= segmentCount; i++)
                buffer.BuildPoints.Add(GetBSplinePoint(buffer, (float)i / segmentCount));

            buffer.BuildPoints[0] = buffer.ControlPoints[0];
            buffer.BuildPoints[buffer.BuildPoints.Count - 1] =
                buffer.ControlPoints[buffer.ControlPoints.Count - 1];
        }

        private static void GenerateCatmullRomPath(
            PathGeometryBuildBuffer buffer,
            int segmentCount)
        {
            PrepareControlPoints(buffer);
            int pathSegments = buffer.ControlPoints.Count - 1;
            for (int i = 0; i <= segmentCount; i++)
            {
                float pathParameter = (float)i / segmentCount * pathSegments;
                int segmentIndex = Mathf.Min(
                    Mathf.FloorToInt(pathParameter),
                    pathSegments - 1);
                float localT = pathParameter - segmentIndex;
                buffer.BuildPoints.Add(CalculateCatmullRom(
                    buffer.SplineControlPoints[segmentIndex],
                    buffer.SplineControlPoints[segmentIndex + 1],
                    buffer.SplineControlPoints[segmentIndex + 2],
                    buffer.SplineControlPoints[segmentIndex + 3],
                    localT));
            }

            buffer.BuildPoints[0] = buffer.ControlPoints[0];
            buffer.BuildPoints[buffer.BuildPoints.Count - 1] =
                buffer.ControlPoints[buffer.ControlPoints.Count - 1];
        }

        private static void PrepareControlPoints(PathGeometryBuildBuffer buffer)
        {
            buffer.SplineControlPoints.Clear();
            buffer.SplineControlPoints.Add(
                buffer.ControlPoints[0] + buffer.ControlPoints[0] - buffer.ControlPoints[1]);
            for (int i = 0; i < buffer.ControlPoints.Count; i++)
                buffer.SplineControlPoints.Add(buffer.ControlPoints[i]);

            int last = buffer.ControlPoints.Count - 1;
            buffer.SplineControlPoints.Add(
                buffer.ControlPoints[last]
                + buffer.ControlPoints[last]
                - buffer.ControlPoints[last - 1]);
        }

        private static Vector3 GetBSplinePoint(
            PathGeometryBuildBuffer buffer,
            float t)
        {
            int segmentCount = buffer.SplineControlPoints.Count - 3;
            float scaled = t * segmentCount;
            int index = Mathf.Min(Mathf.FloorToInt(scaled), segmentCount - 1);
            return CalculateBSpline(
                buffer.SplineControlPoints[index],
                buffer.SplineControlPoints[index + 1],
                buffer.SplineControlPoints[index + 2],
                buffer.SplineControlPoints[index + 3],
                scaled - index);
        }

        private static Vector3 CalculateBSpline(
            Vector3 p0,
            Vector3 p1,
            Vector3 p2,
            Vector3 p3,
            float t)
        {
            const float ONE_SIXTH = 1f / 6f;
            float t2 = t * t;
            float t3 = t2 * t;
            float b0 = ONE_SIXTH * (1f - t) * (1f - t) * (1f - t);
            float b1 = ONE_SIXTH * (3f * t3 - 6f * t2 + 4f);
            float b2 = ONE_SIXTH * (-3f * t3 + 3f * t2 + 3f * t + 1f);
            float b3 = ONE_SIXTH * t3;
            return b0 * p0 + b1 * p1 + b2 * p2 + b3 * p3;
        }

        private static Vector3 CalculateCatmullRom(
            Vector3 p0,
            Vector3 p1,
            Vector3 p2,
            Vector3 p3,
            float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            return 0.5f * (2f * p1
                + (-p0 + p2) * t
                + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
                + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        #endregion
    }
}
