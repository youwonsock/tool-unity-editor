#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>Editor-only cache that reuses the runtime geometry builder.</summary>
    internal static class PathDataScenePreviewCache
    {
        private static readonly Dictionary<int, PathDataScenePreview> CACHES =
            new Dictionary<int, PathDataScenePreview>();

        public static bool TryGet(PathData pathData, out PathDataScenePreview preview)
        {
            preview = null;
            if (pathData == null)
                return false;

            int id = pathData.GetInstanceID();
            if (!CACHES.TryGetValue(id, out preview))
            {
                preview = new PathDataScenePreview();
                CACHES.Add(id, preview);
            }

            return preview.Update(pathData);
        }
    }

    internal sealed class PathDataScenePreview
    {
        private const int MIN_PATH_POINTS = 2;

        private readonly List<Vector3> _controlPoints = new List<Vector3>();
        private readonly List<Vector3> _lastControlPoints = new List<Vector3>();
        private readonly PathGeometryBuildBuffer _geometryBuildBuffer =
            new PathGeometryBuildBuffer();

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
            if (pathData == null
                || !pathData.CopyControlPoints(_controlPoints)
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
                && PathGeometryUtility.AreSamePoints(
                    _controlPoints,
                    _lastControlPoints))
                return IsValid;

            if (!PathGeometryUtility.TryBuild(
                    _controlPoints,
                    curveType,
                    segmentCount,
                    _geometryBuildBuffer,
                    out PathGeometryResult geometry,
                    out _))
            {
                Invalidate();
                return false;
            }

            _sampledPoints = geometry.Points;
            _cumulativeDistances = geometry.CumulativeDistances;
            _pathLength = geometry.Length;
            _curveType = curveType;
            _segmentCount = segmentCount;
            _lastControlPoints.Clear();
            _lastControlPoints.AddRange(_controlPoints);
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
            int index = PathGeometryUtility.FindSegmentIndex(
                _cumulativeDistances,
                targetDistance);
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

        private void Invalidate()
        {
            _sampledPoints = null;
            _cumulativeDistances = null;
            _pathLength = 0f;
            _hasSignature = false;
            _lastControlPoints.Clear();
        }

    }
}
#endif
