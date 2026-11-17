using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath
{
    [Serializable]
    public struct PathEventEntry
    {
        [Range(0f, PathConstants.MAX_EVENT_POSITION)]
        public float NormalizedTime;
        public PathEventSettingSO EventSetting;
    }

    /// <summary>
    /// A path through Transform control points, sampled by distance. Changing a
    /// setting rebuilds it at once; moving a control point needs
    /// <see cref="PathProviderBehaviour.Rebuild"/>. Events are copied at build
    /// time, so playback never reads the authoring assets.
    /// </summary>
    public sealed class PathData : PathProviderBehaviour
    {
        [SerializeField] private List<Transform> _pathPoints = new List<Transform>();
        [Header("Path events (normalized time → PathEventSettingSO)")]
        [SerializeField] private List<PathEventEntry> _pathEvents = new List<PathEventEntry>();
        [SerializeField, Min(2)] private int _segmentCount = PathConstants.DEFAULT_SEGMENT_COUNT;
        [SerializeField] private EPathCurveType _curveType = EPathCurveType.Linear;

        [Header("Movement")]
        [SerializeField] private EPathMoveType _moveType = EPathMoveType.TimeBased;
        [SerializeField, Min(0.001f)] private float _moveValue = 5f;
        [SerializeField] private AnimationCurve _timeCurve = null;

        private PathPolyline _polyline;
        private PathMovementSettings _movement;
        private PathRuntimeEvent[] _events = Array.Empty<PathRuntimeEvent>();
        private IReadOnlyList<PathRuntimeEvent> _eventsView = Array.Empty<PathRuntimeEvent>();
        private bool _failed;

        private readonly List<Vector3> _pointScratch = new List<Vector3>();
        private readonly PathGeometryBuildBuffer _buildBuffer = new PathGeometryBuildBuffer();

        public override bool IsReady => _polyline != null && !_failed;

        public override float PathLength
        {
            get
            {
                ThrowIfNotReady();
                return _polyline.Length;
            }
        }

        public EPathCurveType CurveType
        {
            get => _curveType;
            set
            {
                if (!Enum.IsDefined(typeof(EPathCurveType), value))
                    throw new ArgumentOutOfRangeException(nameof(value));
                if (_curveType == value)
                    return;
                _curveType = value;
                RebuildIfRequested();
            }
        }

        public int SegmentCount
        {
            get => _segmentCount;
            set
            {
                if (value < PathConstants.MIN_PATH_POINTS)
                    throw new ArgumentOutOfRangeException(nameof(value), "SegmentCount must be at least two.");
                if (_segmentCount == value)
                    return;
                _segmentCount = value;
                RebuildIfRequested();
            }
        }

        /// <summary>
        /// The published movement once built, the authored one before. Setting
        /// it validates the value and rebuilds.
        /// </summary>
        public PathMovementSettings MovementSettings
        {
            get => _polyline != null ? _movement : GetAuthoringMovement();
            set
            {
                value.Validate(nameof(value));
                _moveType = value.MoveType;
                _moveValue = value.Value;
                _timeCurve = value.CopyTimeCurve();
                RebuildIfRequested();
            }
        }

        public int SamplePointCount
        {
            get
            {
                ThrowIfNotReady();
                return _polyline.PointCount;
            }
        }

        /// <summary>The published events in position order.</summary>
        public IReadOnlyList<PathRuntimeEvent> Events => _eventsView;
        public int AuthoringEventCount => _pathEvents == null ? 0 : _pathEvents.Count;
        internal PathRuntimeEvent[] RuntimeEvents => _events;
        internal PathPolyline Polyline => _polyline;
        /// <summary>The authored control points, for the Editor. Never modify the list.</summary>
        internal IReadOnlyList<Transform> PathPoints => _pathPoints;

        private void Awake()
        {
            if (Application.isPlaying && _pathPoints != null && _pathPoints.Count >= PathConstants.MIN_PATH_POINTS)
                Rebuild();
        }

        /// <summary>Replaces the control points and rebuilds.</summary>
        public void SetPathPoints(IReadOnlyList<Transform> points)
        {
            if (points == null)
                throw new ArgumentNullException(nameof(points));
            _pathPoints = new List<Transform>(points);
            Rebuild();
        }

        /// <summary>Copies the control point positions. Returns false when one is missing.</summary>
        public bool CopyControlPoints(List<Vector3> destination)
        {
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            destination.Clear();
            if (_pathPoints == null)
                return false;
            for (int i = 0; i < _pathPoints.Count; i++)
            {
                Transform point = _pathPoints[i];
                if (point == null || !PathValueUtility.IsFinite(point.position))
                    return false;
                destination.Add(point.position);
            }
            return destination.Count >= PathConstants.MIN_PATH_POINTS;
        }

        public Vector3 GetSamplePoint(int index)
        {
            ThrowIfNotReady();
            if (index < 0 || index >= _polyline.PointCount)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _polyline.GetPoint(index);
        }

        public PathEventEntry GetAuthoringEvent(int index)
        {
            if (_pathEvents == null || index < 0 || index >= _pathEvents.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _pathEvents[index];
        }

        public override Vector3 Sample(float progress)
        {
            ThrowIfNotReady();
            PathValueUtility.ThrowIfNotFinite(progress, nameof(progress));
            return _polyline.Sample(progress);
        }

        /// <summary>Sorts the authored events by position and clamps them to the valid range.</summary>
        public bool SortPathEventsByNormalizedTime()
        {
            if (_pathEvents == null)
                return false;
            bool changed = PathSortUtility.StableInsertionSort(_pathEvents, CompareEntries);
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
            return Mathf.Clamp(normalizedTime, 0f, PathConstants.MAX_EVENT_POSITION);
        }

        private static readonly Comparison<PathEventEntry> CompareEntries =
            (left, right) => left.NormalizedTime.CompareTo(right.NormalizedTime);

        protected override bool TryBuild(out bool geometryChanged, out bool definitionChanged, out string error)
        {
            geometryChanged = false;
            definitionChanged = false;
            if (!CopyControlPoints(_pointScratch))
            {
                error = "PathData requires at least two valid control points.";
                return false;
            }
            if (!Enum.IsDefined(typeof(EPathCurveType), _curveType))
            {
                error = "Curve type is invalid.";
                return false;
            }
            if (!TryBuildEvents(out PathRuntimeEvent[] events, out error))
                return false;
            PathMovementSettings movement = GetAuthoringMovement();
            if (!movement.TryValidate(out error))
                return false;
            if (!PathGeometryUtility.TryBuild(_pointScratch, _curveType, _segmentCount, _buildBuffer,
                    out PathPolyline polyline, out error))
                return false;

            geometryChanged = !polyline.IsSameAs(_polyline);
            definitionChanged = _polyline == null
                || !movement.IsSameAs(_movement)
                || !PathRuntimeEvent.AreSame(_events, events);
            _polyline = polyline;
            _movement = movement;
            if (!ReferenceEquals(_events, events))
                _eventsView = Array.AsReadOnly(events);
            _events = events;
            _failed = false;
            return true;
        }

        protected override void MarkFailed() => _failed = true;

        private protected override PathRoute CreateRoute()
        {
            return PathRoute.FromProvider(
                this,
                new[] { new PathRoute.Segment(this, 0f, _polyline.Length, _movement, false, _events) },
                false);
        }

        private bool TryBuildEvents(out PathRuntimeEvent[] events, out string error)
        {
            events = Array.Empty<PathRuntimeEvent>();
            if (_pathEvents == null || _pathEvents.Count == 0)
            {
                error = null;
                return true;
            }

            var result = new PathRuntimeEvent[_pathEvents.Count];
            for (int i = 0; i < _pathEvents.Count; i++)
            {
                PathEventEntry entry = _pathEvents[i];
                if (!PathValueUtility.IsInRange(entry.NormalizedTime, 0f, PathConstants.MAX_EVENT_POSITION))
                {
                    error = $"_pathEvents[{i}].NormalizedTime is outside the valid range.";
                    return false;
                }
                if (entry.EventSetting == null)
                {
                    error = $"_pathEvents[{i}].EventSetting is required.";
                    return false;
                }
                try
                {
                    result[i] = new PathRuntimeEvent(entry.NormalizedTime, PathEventDefinition.FromAuthoring(entry.EventSetting));
                }
                catch (Exception exception)
                {
                    error = $"Path event definitions are invalid: {exception.Message}";
                    return false;
                }
            }
            PathRuntimeEvent.SortByPosition(result);
            events = result;
            error = null;
            return true;
        }

        private PathMovementSettings GetAuthoringMovement()
        {
            return new PathMovementSettings(_moveType, _moveValue, _timeCurve);
        }

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

        internal bool ShowPathInEditor => _showPathInEditor;
        internal int PreviewSamplingType => (int)_previewSamplingType;
        internal int PreviewSampleCount => _previewSampleCount;
        internal Color PointColor => _pointColor;
        internal Color PathColor => _pathColor;
        internal Color SamplePointColor => _samplePointColor;
        internal Color EventPointColor => _eventPointColor;
        internal PathPreviewStyle PreviewStyle => new PathPreviewStyle(_lineWidth, _pointSize, _samplePointSize, _eventPointSize);
#endif
    }

#if UNITY_EDITOR
    /// <summary>Sizes of a path's Scene View preview.</summary>
    internal readonly struct PathPreviewStyle
    {
        public readonly float LineWidth;
        public readonly float PointSize;
        public readonly float SamplePointSize;
        public readonly float EventPointSize;

        public PathPreviewStyle(float lineWidth, float pointSize, float samplePointSize, float eventPointSize)
        {
            LineWidth = lineWidth;
            PointSize = pointSize;
            SamplePointSize = samplePointSize;
            EventPointSize = eventPointSize;
        }
    }
#endif
}
