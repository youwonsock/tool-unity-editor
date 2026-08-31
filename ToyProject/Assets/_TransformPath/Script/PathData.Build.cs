using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath
{
    public partial class PathData
    {
        #region Build

        private bool CollectValidWorldPoints(IReadOnlyList<Transform> sources)
        {
            _validPointsScratch.Clear();

            if (sources == null)
                throw new ArgumentNullException(nameof(sources));

            for (int i = 0; i < sources.Count; i++)
            {
                Transform point = sources[i];
                if (point != null)
                {
                    if (!IsFinite(point.position))
                        throw new ArgumentOutOfRangeException($"{nameof(sources)}[{i}]", "Control point position must be finite.");
                    _validPointsScratch.Add(point.position);
                }
                else
                {
                    throw new ArgumentNullException($"{nameof(sources)}[{i}]", "Control point references cannot be null.");
                }
            }

            return _validPointsScratch.Count >= MIN_PATH_POINTS;
        }

        private void BuildFromPoints(List<Vector3> validPoints)
        {
            if (validPoints == null)
                throw new ArgumentNullException(nameof(validPoints));
            if (validPoints.Count < MIN_PATH_POINTS)
                throw new ArgumentException("At least two control points are required.", nameof(validPoints));
            if (_segmentCount < 2)
                throw new ArgumentOutOfRangeException(nameof(_segmentCount));
            if (_samplingCount < 1)
                throw new ArgumentOutOfRangeException(nameof(_samplingCount));
            if (!Enum.IsDefined(typeof(ECurveType), _curveType))
                throw new ArgumentOutOfRangeException(nameof(_curveType));
            if (!Enum.IsDefined(typeof(ESamplingType), _samplingType))
                throw new ArgumentOutOfRangeException(nameof(_samplingType));

            ValidatePathEvents();

            for (int i = 0; i < validPoints.Count; i++)
            {
                Vector3 point = validPoints[i];
                if (float.IsNaN(point.x) || float.IsInfinity(point.x)
                    || float.IsNaN(point.y) || float.IsInfinity(point.y)
                    || float.IsNaN(point.z) || float.IsInfinity(point.z))
                    throw new ArgumentOutOfRangeException(nameof(validPoints));
            }

            _buildPointsScratch.Clear();

            switch (_curveType)
            {
                case ECurveType.Linear:
                    GenerateLinearPath(validPoints, _buildPointsScratch);
                    break;
                case ECurveType.SplineApproximating:
                    GenerateSplinePath(validPoints, _buildPointsScratch);
                    break;
                case ECurveType.SplineInterpolating:
                    GenerateCatmullRomPath(validPoints, _buildPointsScratch);
                    break;
            }

            Vector3[] nextPathPoints = _buildPointsScratch.ToArray();
            float[] nextDistances = PathGeometryUtility.CalculateCumulativeDistances(nextPathPoints);
            float nextPathLength = nextDistances.Length > 0 ? nextDistances[nextDistances.Length - 1] : 0f;
            if (!IsFinite(nextPathLength) || nextPathLength <= 0f)
                throw new ArgumentException("Path control points must produce a measurable path length.", nameof(validPoints));
            bool resultChanged = !PathGeometryUtility.IsSameResult(
                _isInitialized,
                _cachedPathPoints,
                _cachedPathLength,
                nextPathPoints,
                nextPathLength);

            _cachedPathPoints = nextPathPoints;
            _cachedDistances = nextDistances;
            _cachedPathLength = nextPathLength;
            _isInitialized = true;
            _fault = null;
            _lastPathPointCount = _pathPoints != null
                ? _pathPoints.Count
                : validPoints.Count;

            NotifyPathBuild(resultChanged);

#if UNITY_EDITOR
            _cachedSamplePoints = null;
            _cachedSampleCount = -1;
            _cachedESamplingType = _samplingType;
            Debug.Log($"PathData 초기화 완료: 점 개수={_cachedPathPoints.Length}, 경로 길이={_cachedPathLength:F2}m, ECurveType={_curveType}");
#endif
        }

        private void ValidatePathEvents()
        {
            if (_pathEvents == null)
                throw new ArgumentException("Path event list cannot be null.", nameof(_pathEvents));

            for (int i = 0; i < _pathEvents.Count; i++)
            {
                PathEventEntry entry = _pathEvents[i];
                if (!IsFinite(entry.NormalizedTime)
                    || entry.NormalizedTime < 0f
                    || entry.NormalizedTime > MAX_PATH_EVENT_NORMALIZED_TIME)
                    throw new ArgumentOutOfRangeException($"{nameof(_pathEvents)}[{i}].{nameof(PathEventEntry.NormalizedTime)}");
                if (entry.EventSetting == null)
                    throw new ArgumentNullException($"{nameof(_pathEvents)}[{i}].{nameof(PathEventEntry.EventSetting)}");
            }
        }

        private void GenerateLinearPath(List<Vector3> validPoints, List<Vector3> points)
        {
            int segmentCount = _segmentCount;
            float totalDistance = 0f;
            _segmentDistancesScratch.Clear();

            for (int i = 0; i < validPoints.Count - 1; i++)
            {
                float distance = Vector3.Distance(validPoints[i], validPoints[i + 1]);
                _segmentDistancesScratch.Add(distance);
                totalDistance += distance;
            }

            for (int i = 0; i <= segmentCount; i++)
            {
                float t = (float)i / segmentCount;
                float targetDistance = t * totalDistance;
                points.Add(GetLinearPointAtDistance(validPoints, _segmentDistancesScratch, targetDistance, totalDistance));
            }
        }

        private void GenerateSplinePath(List<Vector3> validPoints, List<Vector3> points)
        {
            if (validPoints.Count < MIN_PATH_POINTS)
                throw new ArgumentException("Spline paths require at least two control points.", nameof(validPoints));

            PrepareControlPoints(validPoints);

            int segmentCount = _segmentCount;
            for (int i = 0; i <= segmentCount; i++)
            {
                float t = (float)i / segmentCount;
                points.Add(GetBSplinePoint(_controlPointsScratch, t));
            }

            if (points.Count > 0)
                points[0] = validPoints[0];
            if (points.Count > 1)
                points[points.Count - 1] = validPoints[validPoints.Count - 1];
        }

        private void GenerateCatmullRomPath(List<Vector3> validPoints, List<Vector3> points)
        {
            if (validPoints.Count < MIN_PATH_POINTS)
                throw new ArgumentException("Catmull-Rom paths require at least two control points.", nameof(validPoints));

            PrepareControlPoints(validPoints);
            int pathSegments = validPoints.Count - 1;
            int segmentCount = _segmentCount;

            for (int i = 0; i <= segmentCount; i++)
            {
                float tGlobal = (float)i / segmentCount;
                float pathParam = tGlobal * pathSegments;
                int segmentIndex = Mathf.FloorToInt(pathParam);

                if (segmentIndex >= pathSegments)
                    segmentIndex = pathSegments - 1;

                float localT = pathParam - segmentIndex;
                Vector3 p0 = _controlPointsScratch[segmentIndex];
                Vector3 p1 = _controlPointsScratch[segmentIndex + 1];
                Vector3 p2 = _controlPointsScratch[segmentIndex + 2];
                Vector3 p3 = _controlPointsScratch[segmentIndex + 3];

                points.Add(CalculateCatmullRom(p0, p1, p2, p3, localT));
            }
        }

        private void PrepareControlPoints(List<Vector3> validPoints)
        {
            _controlPointsScratch.Clear();

            Vector3 firstExtend = validPoints[0] + (validPoints[0] - validPoints[1]);
            Vector3 lastExtend = validPoints[validPoints.Count - 1]
                + (validPoints[validPoints.Count - 1] - validPoints[validPoints.Count - 2]);

            _controlPointsScratch.Add(firstExtend);
            _controlPointsScratch.AddRange(validPoints);
            _controlPointsScratch.Add(lastExtend);
        }

        private Vector3 GetBSplinePoint(List<Vector3> controlPoints, float t)
        {
            const int MIN_CONTROL_POINTS = 4;

            if (controlPoints.Count < MIN_CONTROL_POINTS)
                throw new ArgumentException("B-spline evaluation requires four control points.", nameof(controlPoints));

            int segmentCount = controlPoints.Count - 3;
            float scaledT = t * segmentCount;
            int segmentIndex = Mathf.FloorToInt(scaledT);

            if (segmentIndex >= segmentCount)
                segmentIndex = segmentCount - 1;

            float localT = scaledT - segmentIndex;
            Vector3 p0 = controlPoints[segmentIndex];
            Vector3 p1 = controlPoints[segmentIndex + 1];
            Vector3 p2 = controlPoints[segmentIndex + 2];
            Vector3 p3 = controlPoints[segmentIndex + 3];

            return CalculateBSpline(p0, p1, p2, p3, localT);
        }

        private Vector3 CalculateBSpline(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
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

        private static Vector3 CalculateCatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            const float HALF = 0.5f;

            return HALF * (
                (2f * p1) +
                (-p0 + p2) * t +
                (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        private Vector3 GetLinearPointAtDistance(
            List<Vector3> validPoints,
            List<float> segmentDistances,
            float targetDistance,
            float totalDistance)
        {
            if (targetDistance <= 0f)
                return validPoints[0];

            if (targetDistance >= totalDistance)
                return validPoints[validPoints.Count - 1];

            float currentDistance = 0f;
            for (int i = 0; i < segmentDistances.Count; i++)
            {
                float segmentLength = segmentDistances[i];
                float segmentEnd = currentDistance + segmentLength;

                if (targetDistance <= segmentEnd)
                {
                    if (segmentLength <= 0f)
                        return validPoints[i];

                    float segmentT = (targetDistance - currentDistance) / segmentLength;
                    return Vector3.Lerp(validPoints[i], validPoints[i + 1], segmentT);
                }

                currentDistance = segmentEnd;
            }

            return validPoints[validPoints.Count - 1];
        }

        #endregion
    }
}
