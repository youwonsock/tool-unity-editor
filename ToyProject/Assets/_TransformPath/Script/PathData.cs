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
    public sealed class PathData : MonoBehaviour, IPathMovementProvider, IPathEventSource
    {
        #region Constants

        public const float MAX_PATH_EVENT_NORMALIZED_TIME = 0.995f;

        private const int MIN_PATH_POINTS = 2;
        private const int DEFAULT_SEGMENT_COUNT = 500;

        #endregion


        #region Inner Classes / Structs

        public enum ECurveType
        {
            Linear = 0,
            SplineApproximating = 1,
            SplineInterpolating = 2,
        }

        #endregion


        #region Member Variables

        [SerializeField] private List<Transform> _pathPoints = new List<Transform>();
        [Header("Path events (normalized time → PathEventSettingSO)")]
        [SerializeField] private List<PathEventEntry> _pathEvents = new List<PathEventEntry>();
        [SerializeField, Min(2)] private int _segmentCount = DEFAULT_SEGMENT_COUNT;
        [SerializeField] private ECurveType _curveType = ECurveType.Linear;

        [Header("Movement")]
        [SerializeField] private EPathMoveType _moveType = EPathMoveType.TimeBased;
        [SerializeField, Min(0.001f)] private float _moveValue = 5f;
        [SerializeField] private AnimationCurve _timeCurve = null;

        private Vector3[] _cachedPathPoints;
        private float[] _cachedDistances;
        private float _cachedPathLength;
        private bool _isInitialized;
        private bool _hasConfiguredVectorPoints;
        private bool _configurationErrorReported;
        private bool _movementSettingsPublished;
        private PathMovementSettings _publishedMovementSettings;
        private int _revision;

        private readonly List<Vector3> _configuredPoints = new List<Vector3>();
        private readonly List<Vector3> _validPointsScratch = new List<Vector3>();
        private readonly PathGeometryBuildBuffer _geometryBuildBuffer =
            new PathGeometryBuildBuffer();

        #endregion


        #region Properties

        public bool IsInitialized => _isInitialized;
        public bool IsReady => _isInitialized
            && _cachedPathPoints != null
            && _cachedDistances != null
            && _cachedPathPoints.Length >= MIN_PATH_POINTS
            && _cachedPathLength > 0f
            && _movementSettingsPublished;
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
        public PathMovementSettings MovementSettings =>
            PathMovementSettingsUtility.Clone(_publishedMovementSettings);
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

        #endregion


        #region Unity Events

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
            _isInitialized = false;
            _cachedPathPoints = null;
            _cachedDistances = null;
            _cachedPathLength = 0f;
            _movementSettingsPublished = false;
        }

        private void Awake()
        {
            if (Application.isPlaying && HasAuthoringConfiguration())
                Init();
        }

        private void OnDestroy()
        {
            Release();
        }

        #endregion


        #region Public Methods

        public void ConfigureMovementSettings(PathMovementSettings settings)
        {
            PathMovementSettingsUtility.Validate(settings, nameof(settings));
            PathMovementSettings next = PathMovementSettingsUtility.Clone(settings);
            bool changed = !_movementSettingsPublished
                || !PathMovementSettingsUtility.AreSame(_publishedMovementSettings, next);

            _moveType = next.MoveType;
            _moveValue = next.Value;
            _timeCurve = PathMovementSettingsUtility.CloneCurve(next.TimeCurve);
            _publishedMovementSettings = next;
            _movementSettingsPublished = true;

            if (changed)
            {
                _revision++;
                NotifyPathChanged();
            }
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
                if (point == null || !PathValueUtility.IsFinite(point.position))
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
                if (!PathValueUtility.IsFinite(points[i]))
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
            if (!PathValueUtility.IsFinite(normalizedTime))
                throw new ArgumentOutOfRangeException(nameof(normalizedTime));
            return SampleInternal(Mathf.Clamp01(normalizedTime));
        }

        public Vector3 SampleDistance(float distance)
        {
            ThrowIfNotReady();
            if (!PathValueUtility.IsFinite(distance))
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

        #endregion


        #region Private Methods

        private bool HasAuthoringConfiguration()
        {
            return _hasConfiguredVectorPoints
                ? _configuredPoints.Count >= MIN_PATH_POINTS
                : _pathPoints != null && _pathPoints.Count >= MIN_PATH_POINTS;
        }

        private void TryBuildFromCurrentConfiguration()
        {
            if (_hasConfiguredVectorPoints)
            {
                if (_configuredPoints.Count < MIN_PATH_POINTS)
                {
                    MarkConfigurationError("PathData requires at least two configured control points.");
                    return;
                }

                if (!BuildFromPoints(_configuredPoints, out string configuredError))
                    MarkConfigurationError(configuredError);
                return;
            }

            if (!CollectValidWorldPoints(_pathPoints))
            {
                MarkConfigurationError("PathData requires at least two valid control points.");
                return;
            }

            if (!BuildFromPoints(_validPointsScratch, out string buildError))
                MarkConfigurationError(buildError);
        }

        private bool CollectValidWorldPoints(IReadOnlyList<Transform> sources)
        {
            _validPointsScratch.Clear();
            if (sources == null)
                return false;

            for (int i = 0; i < sources.Count; i++)
            {
                Transform source = sources[i];
                if (source == null || !PathValueUtility.IsFinite(source.position))
                    return false;
                _validPointsScratch.Add(source.position);
            }

            return _validPointsScratch.Count >= MIN_PATH_POINTS;
        }

        private bool BuildFromPoints(
            IReadOnlyList<Vector3> validPoints,
            out string error)
        {
            if (validPoints == null || validPoints.Count < MIN_PATH_POINTS)
            {
                error = "At least two control points are required.";
                return false;
            }
            if (_segmentCount < MIN_PATH_POINTS)
            {
                error = "Segment count must be at least two.";
                return false;
            }
            if (!Enum.IsDefined(typeof(ECurveType), _curveType))
            {
                error = "Curve type is invalid.";
                return false;
            }
            if (!ValidatePathEvents(out error))
                return false;
            PathMovementSettings nextMovementSettings = GetAuthoringMovementSettings();
            if (!PathMovementSettingsUtility.TryValidate(nextMovementSettings, out error))
                return false;

            if (!PathGeometryUtility.TryBuild(
                    validPoints,
                    _curveType,
                    _segmentCount,
                    _geometryBuildBuffer,
                    out PathGeometryResult geometry,
                    out error))
                return false;

            bool changed = !PathGeometryUtility.IsSameResult(
                _cachedPathPoints,
                _cachedPathLength,
                geometry);

            bool movementChanged = !_movementSettingsPublished
                || !PathMovementSettingsUtility.AreSame(_publishedMovementSettings, nextMovementSettings);

            // Publish only after the complete temporary result passed validation.
            _cachedPathPoints = geometry.Points;
            _cachedDistances = geometry.CumulativeDistances;
            _cachedPathLength = geometry.Length;
            _publishedMovementSettings = PathMovementSettingsUtility.Clone(nextMovementSettings);
            _movementSettingsPublished = true;
            _isInitialized = true;
            _configurationErrorReported = false;
            if (changed || movementChanged)
            {
                _revision++;
                NotifyPathChanged();
            }

            error = null;
            return true;
        }

        private bool ValidatePathEvents(out string error)
        {
            if (_pathEvents == null)
            {
                error = "Path event list cannot be null.";
                return false;
            }
            for (int i = 0; i < _pathEvents.Count; i++)
            {
                PathEventEntry entry = _pathEvents[i];
                if (!PathValueUtility.IsFinite(entry.NormalizedTime)
                    || entry.NormalizedTime < 0f
                    || entry.NormalizedTime > MAX_PATH_EVENT_NORMALIZED_TIME)
                {
                    error = $"_pathEvents[{i}].NormalizedTime is outside the valid range.";
                    return false;
                }
                if (entry.EventSetting == null)
                {
                    error = $"_pathEvents[{i}].EventSetting is required.";
                    return false;
                }
            }

            error = null;
            return true;
        }

        private PathMovementSettings GetAuthoringMovementSettings()
        {
            AnimationCurve curve = _timeCurve;
            if (_moveType == EPathMoveType.TimeBased && curve == null)
                curve = AnimationCurve.Linear(0f, 0f, 1f, 1f);
            return new PathMovementSettings(_moveType, _moveValue, curve);
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
            PathChanged?.Invoke();
        }

        private void MarkConfigurationError(string message)
        {
            _isInitialized = false;
            _cachedPathPoints = null;
            _cachedDistances = null;
            _cachedPathLength = 0f;
            _movementSettingsPublished = false;
            if (_configurationErrorReported)
                return;

            Debug.LogError($"PathData '{name}' could not build: {message}", this);
            _configurationErrorReported = true;
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

        #endregion

#if UNITY_EDITOR
#pragma warning disable 0414
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
#pragma warning restore 0414
#endif

    }
}
