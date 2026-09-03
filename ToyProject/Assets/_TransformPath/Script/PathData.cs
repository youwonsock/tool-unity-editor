using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath
{
    [Serializable]
    public struct PathEventEntry
    {
        [Range(0f, PathData.MAX_PATH_EVENT_NORMALIZED_TIME)]
        public float NormalizedTime;
        public PathEventSettingSO EventSetting;
    }

    /// <summary>
    /// A rebuilt, arc-length sampled path provider. Runtime consumers only see
    /// the canonical sample and event-index APIs; editor preview sampling is
    /// editor-only and is compiled out of player builds.
    /// </summary>
    [DefaultExecutionOrder(-300)]
    public sealed class PathData : MonoBehaviour, IPathProvider, IPathEventSource
    {
        public const float MAX_PATH_EVENT_NORMALIZED_TIME = 0.995f;

        private const int MIN_PATH_POINTS = 2;
        private const int DEFAULT_SEGMENT_COUNT = 500;

        public enum ECurveType
        {
            Linear = 0,
            SplineApproximating = 1,
            SplineInterpolating = 2,
        }

        [SerializeField] private List<Transform> _pathPoints = new List<Transform>();
        [Header("Path events (normalized time → PathEventSettingSO)")]
        [SerializeField] private List<PathEventEntry> _pathEvents = new List<PathEventEntry>();
        [SerializeField, Min(2)] private int _segmentCount = DEFAULT_SEGMENT_COUNT;
        [SerializeField] private ECurveType _curveType = ECurveType.Linear;

#if UNITY_EDITOR
        private enum EPreviewSamplingType
        {
            Uniform,
            DeterministicRandom,
            DistanceBased,
        }

        [Header("Editor Preview Only")]
        [SerializeField] private bool _showPathInEditor = true;
        [SerializeField] private EPreviewSamplingType _previewSamplingType = EPreviewSamplingType.Uniform;
        [SerializeField, Min(0)] private int _previewSampleCount = 10;
        [SerializeField] private Color _pointColor = Color.red;
        [SerializeField] private Color _pathColor = Color.blue;
        [SerializeField] private Color _samplePointColor = Color.yellow;
        [SerializeField] private Color _eventPointColor = Color.green;
        [SerializeField, Range(0.1f, 20f)] private float _lineWidth = 2f;
        [SerializeField, Range(0f, 1f)] private float _pointSize = 0.1f;
        [SerializeField, Range(0f, 1f)] private float _samplePointSize = 0f;
        [SerializeField, Range(0f, 1f)] private float _eventPointSize = 0.15f;
#endif

        private Vector3[] _cachedPathPoints;
        private float[] _cachedDistances;
        private float _cachedPathLength;
        private bool _isInitialized;
        private bool _hasConfiguredVectorPoints;
        private bool _configurationErrorReported;
        private int _revision;

        private readonly List<Vector3> _configuredPoints = new List<Vector3>();
        private readonly List<Vector3> _validPointsScratch = new List<Vector3>();
        private readonly List<Vector3> _buildPointsScratch = new List<Vector3>();
        private readonly List<float> _segmentDistancesScratch = new List<float>();
        private readonly List<Vector3> _controlPointsScratch = new List<Vector3>();

        public bool IsInitialized => _isInitialized;
        public bool IsReady => _isInitialized
            && _cachedPathPoints != null
            && _cachedDistances != null
            && _cachedPathPoints.Length >= MIN_PATH_POINTS
            && _cachedPathLength > 0f;
        public int Revision => _revision;
        public float PathLength
        {
            get
            {
                ThrowIfNotReady();
                return _cachedPathLength;
            }
        }
        public ECurveType CurveType => _curveType;
        public int BuildSegmentCount => _segmentCount;
        public int SamplePointCount
        {
            get
            {
                ThrowIfNotReady();
                return _cachedPathPoints.Length;
            }
        }
        public int EventCount => _pathEvents == null ? 0 : _pathEvents.Count;

        public event Action PathChanged;

        private void Awake()
        {
            if (Application.isPlaying && HasAuthoringConfiguration())
                Init();
        }

        private void OnDestroy()
        {
            Release();
        }

        public void Init()
        {
            if (_isInitialized)
                return;

            if (!HasAuthoringConfiguration())
                return;

            TryBuildFromCurrentConfiguration();
        }

        public void Release()
        {
            if (!_isInitialized && _cachedPathPoints == null && _cachedDistances == null)
                return;

            _isInitialized = false;
            _cachedPathPoints = null;
            _cachedDistances = null;
            _cachedPathLength = 0f;
        }

        public void ConfigureBuildSettings(PathBuildSettings settings)
        {
            ValidateCurveType(settings.CurveType);
            if (settings.SegmentCount < MIN_PATH_POINTS)
                throw new ArgumentOutOfRangeException(nameof(settings), "SegmentCount must be at least two.");

            bool changed = _curveType != settings.CurveType || _segmentCount != settings.SegmentCount;
            _curveType = settings.CurveType;
            _segmentCount = settings.SegmentCount;
            if (changed && _isInitialized)
                Rebuild();
        }

        public void SetCurveType(ECurveType curveType)
        {
            ValidateCurveType(curveType);
            if (_curveType == curveType)
                return;

            _curveType = curveType;
            if (_isInitialized)
                Rebuild();
        }

        public void SetSegmentCount(int segmentCount)
        {
            if (segmentCount < MIN_PATH_POINTS)
                throw new ArgumentOutOfRangeException(nameof(segmentCount));
            if (_segmentCount == segmentCount)
                return;

            _segmentCount = segmentCount;
            if (_isInitialized)
                Rebuild();
        }

        /// <summary>Copies the authoring control points without exposing internal lists.</summary>
        public bool CopyControlPoints(List<Vector3> destination, bool clearDestination = true)
        {
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));

            if (clearDestination)
                destination.Clear();

            if (_hasConfiguredVectorPoints)
            {
                destination.AddRange(_configuredPoints);
                return destination.Count >= MIN_PATH_POINTS;
            }

            if (_pathPoints == null)
                return false;

            for (int i = 0; i < _pathPoints.Count; i++)
            {
                Transform point = _pathPoints[i];
                if (point == null || !IsFinite(point.position))
                    return false;
                destination.Add(point.position);
            }

            return destination.Count >= MIN_PATH_POINTS;
        }

        public void ConfigureControlPoints(IReadOnlyList<Vector3> points)
        {
            if (points == null)
                throw new ArgumentNullException(nameof(points));
            if (points.Count < MIN_PATH_POINTS)
                throw new ArgumentException("At least two control points are required.", nameof(points));

            _configuredPoints.Clear();
            for (int i = 0; i < points.Count; i++)
            {
                if (!IsFinite(points[i]))
                    throw new ArgumentOutOfRangeException(nameof(points));
                _configuredPoints.Add(points[i]);
            }

            _hasConfiguredVectorPoints = true;
            if (_isInitialized)
                Rebuild();
        }

        public Vector3 GetSamplePoint(int index)
        {
            ThrowIfNotReady();
            if (index < 0 || index >= _cachedPathPoints.Length)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _cachedPathPoints[index];
        }

        public PathEventEntry GetEvent(int index)
        {
            if (_pathEvents == null || index < 0 || index >= _pathEvents.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _pathEvents[index];
        }

        public Vector3 Sample(float normalizedTime)
        {
            ThrowIfNotReady();
            if (!IsFinite(normalizedTime))
                throw new ArgumentOutOfRangeException(nameof(normalizedTime));
            return SampleInternal(Mathf.Clamp01(normalizedTime));
        }

        public Vector3 SampleDistance(float distance)
        {
            ThrowIfNotReady();
            if (!IsFinite(distance))
                throw new ArgumentOutOfRangeException(nameof(distance));
            return SampleDistanceInternal(Mathf.Clamp(distance, 0f, _cachedPathLength));
        }

        public void Rebuild()
        {
            if (!_isInitialized)
            {
                Init();
                return;
            }

            TryBuildFromCurrentConfiguration();
        }

        public bool SortPathEventsByNormalizedTime()
        {
            if (_pathEvents == null || _pathEvents.Count < 2)
                return ClampPathEventNormalizedTimes();

            bool changed = false;
            for (int i = 1; i < _pathEvents.Count; i++)
            {
                if (_pathEvents[i].NormalizedTime < _pathEvents[i - 1].NormalizedTime)
                {
                    _pathEvents.Sort((left, right) => left.NormalizedTime.CompareTo(right.NormalizedTime));
                    changed = true;
                    break;
                }
            }

            return ClampPathEventNormalizedTimes() || changed;
        }

        public bool ClampPathEventNormalizedTimes()
        {
            if (_pathEvents == null)
                return false;

            bool changed = false;
            for (int i = 0; i < _pathEvents.Count; i++)
            {
                PathEventEntry entry = _pathEvents[i];
                float clamped = ClampPathEventNormalizedTime(entry.NormalizedTime);
                if (Mathf.Approximately(clamped, entry.NormalizedTime))
                    continue;
                entry.NormalizedTime = clamped;
                _pathEvents[i] = entry;
                changed = true;
            }

            return changed;
        }

        public static float ClampPathEventNormalizedTime(float normalizedTime)
        {
            return Mathf.Clamp(normalizedTime, 0f, MAX_PATH_EVENT_NORMALIZED_TIME);
        }

        private bool HasAuthoringConfiguration()
        {
            return _hasConfiguredVectorPoints
                ? _configuredPoints.Count >= MIN_PATH_POINTS
                : _pathPoints != null && _pathPoints.Count >= MIN_PATH_POINTS;
        }

        private void TryBuildFromCurrentConfiguration()
        {
            try
            {
                if (_hasConfiguredVectorPoints)
                {
                    if (_configuredPoints.Count < MIN_PATH_POINTS)
                        throw new ArgumentException("PathData requires at least two configured control points.");
                    BuildFromPoints(_configuredPoints);
                }
                else
                {
                    if (!CollectValidWorldPoints(_pathPoints))
                        throw new ArgumentException("PathData requires at least two valid control points.");
                    BuildFromPoints(_validPointsScratch);
                }

                _configurationErrorReported = false;
            }
            catch (Exception exception)
            {
                _isInitialized = false;
                _cachedPathPoints = null;
                _cachedDistances = null;
                _cachedPathLength = 0f;
                if (!_configurationErrorReported)
                {
                    Debug.LogError($"PathData '{name}' could not build: {exception.Message}", this);
                    _configurationErrorReported = true;
                }
            }
        }

        private bool CollectValidWorldPoints(IReadOnlyList<Transform> sources)
        {
            _validPointsScratch.Clear();
            if (sources == null)
                return false;

            for (int i = 0; i < sources.Count; i++)
            {
                Transform source = sources[i];
                if (source == null || !IsFinite(source.position))
                    return false;
                _validPointsScratch.Add(source.position);
            }

            return _validPointsScratch.Count >= MIN_PATH_POINTS;
        }

        private void BuildFromPoints(IReadOnlyList<Vector3> validPoints)
        {
            if (validPoints == null || validPoints.Count < MIN_PATH_POINTS)
                throw new ArgumentException("At least two control points are required.", nameof(validPoints));
            if (_segmentCount < MIN_PATH_POINTS)
                throw new ArgumentOutOfRangeException(nameof(_segmentCount));
            ValidateCurveType(_curveType);
            ValidatePathEvents();

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

            Vector3[] nextPoints = _buildPointsScratch.ToArray();
            float[] nextDistances = PathGeometryUtility.CalculateCumulativeDistances(nextPoints);
            float nextLength = nextDistances[nextDistances.Length - 1];
            if (!IsFinite(nextLength) || nextLength <= 0f)
                throw new ArgumentException("Path control points must produce a measurable path length.");

            bool changed = !_isInitialized
                || _cachedPathPoints == null
                || _cachedPathPoints.Length != nextPoints.Length
                || !Mathf.Approximately(_cachedPathLength, nextLength);
            if (!changed)
            {
                for (int i = 0; i < nextPoints.Length; i++)
                {
                    if (_cachedPathPoints[i] != nextPoints[i])
                    {
                        changed = true;
                        break;
                    }
                }
            }

            // Publish only after the complete temporary result passed validation.
            _cachedPathPoints = nextPoints;
            _cachedDistances = nextDistances;
            _cachedPathLength = nextLength;
            _isInitialized = true;
            if (changed)
            {
                _revision++;
                NotifyPathChanged();
            }

        }

        private void ValidatePathEvents()
        {
            if (_pathEvents == null)
                throw new ArgumentException("Path event list cannot be null.");
            for (int i = 0; i < _pathEvents.Count; i++)
            {
                PathEventEntry entry = _pathEvents[i];
                if (!IsFinite(entry.NormalizedTime)
                    || entry.NormalizedTime < 0f
                    || entry.NormalizedTime > MAX_PATH_EVENT_NORMALIZED_TIME)
                    throw new ArgumentOutOfRangeException($"_pathEvents[{i}].NormalizedTime");
                if (entry.EventSetting == null)
                    throw new ArgumentNullException($"_pathEvents[{i}].EventSetting");
            }
        }

        private Vector3 SampleInternal(float normalizedTime)
        {
            if (normalizedTime <= 0f)
                return _cachedPathPoints[0];
            if (normalizedTime >= 1f)
                return _cachedPathPoints[_cachedPathPoints.Length - 1];

            return SampleDistanceInternal(normalizedTime * _cachedPathLength);
        }

        private Vector3 SampleDistanceInternal(float distance)
        {
            if (distance <= 0f)
                return _cachedPathPoints[0];
            if (distance >= _cachedPathLength)
                return _cachedPathPoints[_cachedPathPoints.Length - 1];

            int index = PathGeometryUtility.FindSegmentIndex(_cachedDistances, distance);
            float start = _cachedDistances[index];
            float end = _cachedDistances[index + 1];
            float length = end - start;
            if (length <= Mathf.Epsilon)
                return _cachedPathPoints[index];
            return Vector3.Lerp(_cachedPathPoints[index], _cachedPathPoints[index + 1], (distance - start) / length);
        }

        private void NotifyPathChanged()
        {
            Delegate[] listeners = PathChanged?.GetInvocationList();
            if (listeners == null)
                return;
            for (int i = 0; i < listeners.Length; i++)
            {
                try
                {
                    ((Action)listeners[i])();
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, this);
                }
            }
        }

        private void ThrowIfNotReady()
        {
            if (!IsReady)
                throw new InvalidOperationException("PathData is not initialized and ready.");
        }

        private static void ValidateCurveType(ECurveType curveType)
        {
            if (!Enum.IsDefined(typeof(ECurveType), curveType))
                throw new ArgumentOutOfRangeException(nameof(curveType));
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private void GenerateLinearPath(IReadOnlyList<Vector3> validPoints, List<Vector3> points)
        {
            float totalDistance = 0f;
            _segmentDistancesScratch.Clear();
            for (int i = 0; i < validPoints.Count - 1; i++)
            {
                float distance = Vector3.Distance(validPoints[i], validPoints[i + 1]);
                _segmentDistancesScratch.Add(distance);
                totalDistance += distance;
            }

            for (int i = 0; i <= _segmentCount; i++)
            {
                float targetDistance = (float)i / _segmentCount * totalDistance;
                points.Add(GetLinearPointAtDistance(validPoints, targetDistance, totalDistance));
            }
        }

        private Vector3 GetLinearPointAtDistance(IReadOnlyList<Vector3> points, float targetDistance, float totalDistance)
        {
            if (targetDistance <= 0f)
                return points[0];
            if (targetDistance >= totalDistance)
                return points[points.Count - 1];

            float currentDistance = 0f;
            for (int i = 0; i < _segmentDistancesScratch.Count; i++)
            {
                float segmentLength = _segmentDistancesScratch[i];
                float endDistance = currentDistance + segmentLength;
                if (targetDistance <= endDistance)
                {
                    if (segmentLength <= Mathf.Epsilon)
                        return points[i];
                    return Vector3.Lerp(points[i], points[i + 1], (targetDistance - currentDistance) / segmentLength);
                }
                currentDistance = endDistance;
            }
            return points[points.Count - 1];
        }

        private void GenerateSplinePath(IReadOnlyList<Vector3> validPoints, List<Vector3> points)
        {
            PrepareControlPoints(validPoints);
            for (int i = 0; i <= _segmentCount; i++)
                points.Add(GetBSplinePoint((float)i / _segmentCount));
            points[0] = validPoints[0];
            points[points.Count - 1] = validPoints[validPoints.Count - 1];
        }

        private void GenerateCatmullRomPath(IReadOnlyList<Vector3> validPoints, List<Vector3> points)
        {
            PrepareControlPoints(validPoints);
            int pathSegments = validPoints.Count - 1;
            for (int i = 0; i <= _segmentCount; i++)
            {
                float pathParameter = (float)i / _segmentCount * pathSegments;
                int segmentIndex = Mathf.Min(Mathf.FloorToInt(pathParameter), pathSegments - 1);
                float localT = pathParameter - segmentIndex;
                points.Add(CalculateCatmullRom(
                    _controlPointsScratch[segmentIndex],
                    _controlPointsScratch[segmentIndex + 1],
                    _controlPointsScratch[segmentIndex + 2],
                    _controlPointsScratch[segmentIndex + 3],
                    localT));
            }
            points[0] = validPoints[0];
            points[points.Count - 1] = validPoints[validPoints.Count - 1];
        }

        private void PrepareControlPoints(IReadOnlyList<Vector3> validPoints)
        {
            _controlPointsScratch.Clear();
            _controlPointsScratch.Add(validPoints[0] + validPoints[0] - validPoints[1]);
            for (int i = 0; i < validPoints.Count; i++)
                _controlPointsScratch.Add(validPoints[i]);
            int last = validPoints.Count - 1;
            _controlPointsScratch.Add(validPoints[last] + validPoints[last] - validPoints[last - 1]);
        }

        private Vector3 GetBSplinePoint(float t)
        {
            int segmentCount = _controlPointsScratch.Count - 3;
            float scaled = t * segmentCount;
            int index = Mathf.Min(Mathf.FloorToInt(scaled), segmentCount - 1);
            return CalculateBSpline(
                _controlPointsScratch[index],
                _controlPointsScratch[index + 1],
                _controlPointsScratch[index + 2],
                _controlPointsScratch[index + 3],
                scaled - index);
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

    }
}
