#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// Editor-only arc-length preview cache. It mirrors PathData's geometry
    /// sampling without touching the runtime initialization or revision state.
    /// </summary>
    internal static class PathDataScenePreviewCache
    {
        private static readonly Dictionary<int, PathDataScenePreview> Caches =
            new Dictionary<int, PathDataScenePreview>();

        public static bool TryGet(PathData pathData, out PathDataScenePreview preview)
        {
            preview = null;
            if (pathData == null)
                return false;

            int id = pathData.GetInstanceID();
            if (!Caches.TryGetValue(id, out preview))
            {
                preview = new PathDataScenePreview();
                Caches.Add(id, preview);
            }

            return preview.Update(pathData);
        }
    }

    internal sealed class PathDataScenePreview
    {
        private const int MIN_PATH_POINTS = 2;

        private readonly List<Vector3> _controlPoints = new List<Vector3>();
        private readonly List<Vector3> _buildPoints = new List<Vector3>();
        private readonly List<float> _segmentDistances = new List<float>();
        private readonly List<Vector3> _splineControlPoints = new List<Vector3>();

        private Vector3[] _sampledPoints;
        private float[] _cumulativeDistances;
        private float _pathLength;
        private PathData.ECurveType _curveType;
        private int _segmentCount;
        private bool _hasSignature;

        public bool IsValid => _sampledPoints != null
            && _cumulativeDistances != null
            && _sampledPoints.Length >= MIN_PATH_POINTS
            && _pathLength > 0f;

        public Vector3[] SampledPoints => _sampledPoints;
        public int SampledPointCount => _sampledPoints == null ? 0 : _sampledPoints.Length;
        public float PathLength => _pathLength;

        public bool Update(PathData pathData)
        {
            if (pathData == null)
            {
                Invalidate();
                return false;
            }

            bool hasValidControlPoints;
            try
            {
                hasValidControlPoints = pathData.CopyControlPoints(_controlPoints);
            }
            catch (Exception)
            {
                hasValidControlPoints = false;
            }

            if (!hasValidControlPoints
                || _controlPoints.Count < MIN_PATH_POINTS
                || pathData.BuildSegmentCount < MIN_PATH_POINTS)
            {
                Invalidate();
                return false;
            }

            PathData.ECurveType curveType = pathData.CurveType;
            int segmentCount = pathData.BuildSegmentCount;
            if (_hasSignature
                && _curveType == curveType
                && _segmentCount == segmentCount
                && HasSameControlPoints())
            {
                return IsValid;
            }

            if (!TryBuild(curveType, segmentCount))
            {
                Invalidate();
                return false;
            }

            _curveType = curveType;
            _segmentCount = segmentCount;
            _lastControlPoints.Clear();
            _lastControlPoints.AddRange(_controlPoints);
            _lastControlPointCount = _controlPoints.Count;
            _hasSignature = true;
            return true;
        }

        public Vector3 Sample(float normalizedTime)
        {
            if (!IsValid)
                return Vector3.zero;

            if (normalizedTime <= 0f)
                return _sampledPoints[0];
            if (normalizedTime >= 1f)
                return _sampledPoints[_sampledPoints.Length - 1];

            float targetDistance = Mathf.Clamp01(normalizedTime) * _pathLength;
            int index = FindSegmentIndex(targetDistance);
            float start = _cumulativeDistances[index];
            float end = _cumulativeDistances[index + 1];
            float segmentLength = end - start;
            if (segmentLength <= Mathf.Epsilon)
                return _sampledPoints[index];

            return Vector3.Lerp(
                _sampledPoints[index],
                _sampledPoints[index + 1],
                (targetDistance - start) / segmentLength);
        }

        private bool TryBuild(PathData.ECurveType curveType, int segmentCount)
        {
            _buildPoints.Clear();
            _segmentDistances.Clear();
            _splineControlPoints.Clear();

            switch (curveType)
            {
                case PathData.ECurveType.Linear:
                    GenerateLinearPath(segmentCount);
                    break;
                case PathData.ECurveType.SplineApproximating:
                    GenerateSplinePath(segmentCount);
                    break;
                case PathData.ECurveType.SplineInterpolating:
                    GenerateCatmullRomPath(segmentCount);
                    break;
                default:
                    return false;
            }

            if (_buildPoints.Count < MIN_PATH_POINTS)
                return false;

            _sampledPoints = _buildPoints.ToArray();
            _cumulativeDistances = new float[_sampledPoints.Length];
            for (int i = 1; i < _sampledPoints.Length; i++)
            {
                float distance = Vector3.Distance(_sampledPoints[i - 1], _sampledPoints[i]);
                if (!IsFinite(distance))
                    return false;
                _cumulativeDistances[i] = _cumulativeDistances[i - 1] + distance;
            }

            _pathLength = _cumulativeDistances[_cumulativeDistances.Length - 1];
            return IsFinite(_pathLength) && _pathLength > 0f;
        }

        private void GenerateLinearPath(int segmentCount)
        {
            float totalDistance = 0f;
            for (int i = 0; i < _controlPoints.Count - 1; i++)
            {
                float distance = Vector3.Distance(_controlPoints[i], _controlPoints[i + 1]);
                _segmentDistances.Add(distance);
                totalDistance += distance;
            }

            for (int i = 0; i <= segmentCount; i++)
            {
                float targetDistance = (float)i / segmentCount * totalDistance;
                _buildPoints.Add(GetLinearPointAtDistance(targetDistance, totalDistance));
            }
        }

        private Vector3 GetLinearPointAtDistance(float targetDistance, float totalDistance)
        {
            if (targetDistance <= 0f)
                return _controlPoints[0];
            if (targetDistance >= totalDistance)
                return _controlPoints[_controlPoints.Count - 1];

            float currentDistance = 0f;
            for (int i = 0; i < _segmentDistances.Count; i++)
            {
                float segmentLength = _segmentDistances[i];
                float endDistance = currentDistance + segmentLength;
                if (targetDistance <= endDistance)
                {
                    if (segmentLength <= Mathf.Epsilon)
                        return _controlPoints[i];
                    return Vector3.Lerp(
                        _controlPoints[i],
                        _controlPoints[i + 1],
                        (targetDistance - currentDistance) / segmentLength);
                }
                currentDistance = endDistance;
            }

            return _controlPoints[_controlPoints.Count - 1];
        }

        private void GenerateSplinePath(int segmentCount)
        {
            PrepareControlPoints();
            for (int i = 0; i <= segmentCount; i++)
                _buildPoints.Add(GetBSplinePoint((float)i / segmentCount));
            _buildPoints[0] = _controlPoints[0];
            _buildPoints[_buildPoints.Count - 1] = _controlPoints[_controlPoints.Count - 1];
        }

        private void GenerateCatmullRomPath(int segmentCount)
        {
            PrepareControlPoints();
            int pathSegments = _controlPoints.Count - 1;
            for (int i = 0; i <= segmentCount; i++)
            {
                float pathParameter = (float)i / segmentCount * pathSegments;
                int segmentIndex = Mathf.Min(Mathf.FloorToInt(pathParameter), pathSegments - 1);
                float localT = pathParameter - segmentIndex;
                _buildPoints.Add(CalculateCatmullRom(
                    _splineControlPoints[segmentIndex],
                    _splineControlPoints[segmentIndex + 1],
                    _splineControlPoints[segmentIndex + 2],
                    _splineControlPoints[segmentIndex + 3],
                    localT));
            }
            _buildPoints[0] = _controlPoints[0];
            _buildPoints[_buildPoints.Count - 1] = _controlPoints[_controlPoints.Count - 1];
        }

        private void PrepareControlPoints()
        {
            _splineControlPoints.Clear();
            _splineControlPoints.Add(_controlPoints[0] + _controlPoints[0] - _controlPoints[1]);
            for (int i = 0; i < _controlPoints.Count; i++)
                _splineControlPoints.Add(_controlPoints[i]);
            int last = _controlPoints.Count - 1;
            _splineControlPoints.Add(_controlPoints[last] + _controlPoints[last] - _controlPoints[last - 1]);
        }

        private Vector3 GetBSplinePoint(float t)
        {
            int segmentCount = _splineControlPoints.Count - 3;
            float scaled = t * segmentCount;
            int index = Mathf.Min(Mathf.FloorToInt(scaled), segmentCount - 1);
            return CalculateBSpline(
                _splineControlPoints[index],
                _splineControlPoints[index + 1],
                _splineControlPoints[index + 2],
                _splineControlPoints[index + 3],
                scaled - index);
        }

        private bool HasSameControlPoints()
        {
            return _controlPoints.Count == _lastControlPointCount
                && AreSamePoints(_controlPoints, _lastControlPoints);
        }

        private int _lastControlPointCount;
        private readonly List<Vector3> _lastControlPoints = new List<Vector3>();

        private static bool AreSamePoints(List<Vector3> left, List<Vector3> right)
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

        private int FindSegmentIndex(float targetDistance)
        {
            int left = 0;
            int right = _cumulativeDistances.Length - 1;
            while (left < right - 1)
            {
                int middle = (left + right) / 2;
                if (_cumulativeDistances[middle] < targetDistance)
                    left = middle;
                else if (_cumulativeDistances[middle] > targetDistance)
                    right = middle;
                else
                    return middle;
            }
            return left;
        }

        private void Invalidate()
        {
            _sampledPoints = null;
            _cumulativeDistances = null;
            _pathLength = 0f;
            _hasSignature = false;
            _lastControlPoints.Clear();
            _lastControlPointCount = 0;
        }

        private static Vector3 CalculateBSpline(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            const float oneSixth = 1f / 6f;
            float t2 = t * t;
            float t3 = t2 * t;
            float b0 = oneSixth * (1f - t) * (1f - t) * (1f - t);
            float b1 = oneSixth * (3f * t3 - 6f * t2 + 4f);
            float b2 = oneSixth * (-3f * t3 + 3f * t2 + 3f * t + 1f);
            float b3 = oneSixth * t3;
            return b0 * p0 + b1 * p1 + b2 * p2 + b3 * p3;
        }

        private static Vector3 CalculateCatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            return 0.5f * (2f * p1
                + (-p0 + p2) * t
                + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
                + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
#endif
