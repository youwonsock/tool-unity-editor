using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// PathData segments played one after another. It rebuilds whenever one of
    /// its paths publishes a change, so it never needs to poll them.
    /// </summary>
    public sealed class MultiPathData : PathProviderBehaviour
    {
#if UNITY_EDITOR
        private const float DEFAULT_MULTI_PATH_POINT_SIZE = 0.1f;
#endif

        [SerializeField] private List<PathSegmentAuthoring> _segments = new List<PathSegmentAuthoring>();

        private PathData[] _paths;
        private PathMovementSettings[] _movements;
        private bool[] _preserve;
        private float[] _starts;
        private float[] _lengths;
        private int[] _childRevisions;
        private int[] _childDefinitionRevisions;
        private float _pathLength;
        private bool _failed;
        private bool _building;
        private readonly List<PathData> _subscribed = new List<PathData>();

        public override bool IsReady => _paths != null && !_failed;

        public override float PathLength
        {
            get
            {
                ThrowIfNotReady();
                return _pathLength;
            }
        }

        public int SegmentCount
        {
            get
            {
                ThrowIfNotReady();
                return _paths.Length;
            }
        }

        public int AuthoringSegmentCount => _segments == null ? 0 : _segments.Count;

        private void Awake()
        {
            if (Application.isPlaying && _segments != null && _segments.Count > 0)
                Rebuild();
        }

        protected override void OnDestroy()
        {
            Unsubscribe();
            base.OnDestroy();
        }

        /// <summary>Replaces the segments and rebuilds.</summary>
        public void SetSegments(IReadOnlyList<PathSegmentAuthoring> segments)
        {
            if (segments == null)
                throw new ArgumentNullException(nameof(segments));
            _segments = new List<PathSegmentAuthoring>(segments);
            Rebuild();
        }

        public PathSegmentAuthoring GetAuthoringSegment(int index)
        {
            if (_segments == null || index < 0 || index >= _segments.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _segments[index];
        }

        public PathData GetSegmentPath(int index)
        {
            ThrowIfNotReady();
            return _paths[index];
        }

        public PathMovementSettings GetSegmentMovement(int index)
        {
            ThrowIfNotReady();
            return _movements[index];
        }

        public float GetSegmentStartDistance(int index)
        {
            ThrowIfNotReady();
            return _starts[index];
        }

        public float GetSegmentLength(int index)
        {
            ThrowIfNotReady();
            return _lengths[index];
        }

        public override Vector3 Sample(float progress)
        {
            PathValueUtility.ThrowIfNotFinite(progress, nameof(progress));
            return GetRoute().Sample(progress);
        }

        protected override bool TryBuild(out bool geometryChanged, out bool definitionChanged, out string error)
        {
            _building = true;
            try
            {
                return TryBuildSegments(out geometryChanged, out definitionChanged, out error);
            }
            finally
            {
                _building = false;
            }
        }

        private bool TryBuildSegments(out bool geometryChanged, out bool definitionChanged, out string error)
        {
            geometryChanged = false;
            definitionChanged = false;
            // Listen to every authored path, built or not, so a path that becomes
            // ready later rebuilds this sequence.
            Subscribe();
            int count = _segments == null ? 0 : _segments.Count;
            if (count == 0)
            {
                error = "MultiPathData requires at least one segment.";
                return false;
            }

            var paths = new PathData[count];
            var movements = new PathMovementSettings[count];
            var preserve = new bool[count];
            var starts = new float[count];
            var lengths = new float[count];
            var revisions = new int[count];
            var definitionRevisions = new int[count];
            float total = 0f;
            for (int i = 0; i < count; i++)
            {
                PathSegmentAuthoring segment = _segments[i];
                PathData path = segment.Path;
                if (path == null)
                {
                    error = $"Segment {i} provider must be a PathData.";
                    return false;
                }
                path.EnsureBuilt();
                if (!path.IsReady)
                {
                    error = $"Segment {i} path is not ready.";
                    return false;
                }
                if (!segment.TryResolveMovement(out movements[i], out error))
                {
                    error = $"Segment {i} movement settings are invalid: {error}";
                    return false;
                }
                paths[i] = path;
                preserve[i] = segment.PreservePreviousSpeed;
                starts[i] = total;
                lengths[i] = path.PathLength;
                revisions[i] = path.Revision;
                definitionRevisions[i] = path.DefinitionRevision;
                total += lengths[i];
            }
            if (!PathValueUtility.IsFinite(total) || total <= 0f)
            {
                error = "MultiPathData requires a measurable total length.";
                return false;
            }

            geometryChanged = !HasSame(_childRevisions, revisions) || !Mathf.Approximately(_pathLength, total);
            definitionChanged = _paths == null
                || !HasSameStructure(paths, movements, preserve)
                || !HasSame(_childDefinitionRevisions, definitionRevisions);
            _paths = paths;
            _movements = movements;
            _preserve = preserve;
            _starts = starts;
            _lengths = lengths;
            _childRevisions = revisions;
            _childDefinitionRevisions = definitionRevisions;
            _pathLength = total;
            _failed = false;
            error = null;
            return true;
        }

        protected override void MarkFailed() => _failed = true;

        private protected override PathRoute CreateRoute()
        {
            var segments = new PathRoute.Segment[_paths.Length];
            for (int i = 0; i < segments.Length; i++)
                segments[i] = new PathRoute.Segment(_paths[i], _starts[i], _lengths[i], _movements[i], _preserve[i],
                    _paths[i].RuntimeEvents);
            return PathRoute.FromProvider(this, segments, true);
        }

        private bool HasSameStructure(PathData[] paths, PathMovementSettings[] movements, bool[] preserve)
        {
            if (_paths.Length != paths.Length)
                return false;
            for (int i = 0; i < paths.Length; i++)
            {
                if (!ReferenceEquals(_paths[i], paths[i])
                    || _preserve[i] != preserve[i]
                    || !_movements[i].IsSameAs(movements[i]))
                    return false;
            }
            return true;
        }

        private static bool HasSame(int[] left, int[] right)
        {
            if (left == null || left.Length != right.Length)
                return false;
            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                    return false;
            }
            return true;
        }

        private void Subscribe()
        {
            Unsubscribe();
            if (_segments == null)
                return;
            for (int i = 0; i < _segments.Count; i++)
            {
                PathData path = _segments[i].Path;
                if (path == null || ReferenceEquals(path, this) || _subscribed.Contains(path))
                    continue;
                path.PathChanged += HandlePathChanged;
                _subscribed.Add(path);
            }
        }

        private void Unsubscribe()
        {
            for (int i = 0; i < _subscribed.Count; i++)
            {
                if (_subscribed[i] != null)
                    _subscribed[i].PathChanged -= HandlePathChanged;
            }
            _subscribed.Clear();
        }

        // A path this build itself asked to build reports here too. The build reads it afterwards.
        private void HandlePathChanged()
        {
            if (_building)
                return;
            // A path that failed or was destroyed reported it already, so this
            // sequence only goes out of use until the path is back.
            if (HasUnavailablePath())
                MarkUnavailable();
            else
                Rebuild();
        }

        private bool HasUnavailablePath()
        {
            if (_segments == null)
                return true;
            for (int i = 0; i < _segments.Count; i++)
            {
                PathData path = _segments[i].Path;
                if (path == null || !path.IsReady)
                    return true;
            }
            return false;
        }

#if UNITY_EDITOR
        [Header("Editor Only")]
        [SerializeField] private bool _autoLinkPathPoints = true;

        [Header("MultiPath → all PathData drawing")]
        [SerializeField, Range(0.1f, 20f)] private float _multiPathLineWidth = 2f;
        [SerializeField, Range(0f, 1f)] private float _multiPathPointSize = DEFAULT_MULTI_PATH_POINT_SIZE;
        [SerializeField, Range(0f, 1f)] private float _multiPathSamplePointSize;
        [SerializeField, Range(0f, 1f)] private float _multiPathEventPointSize = 0.15f;

        internal bool AutoLinkPathPoints => _autoLinkPathPoints;
        internal PathPreviewStyle PreviewStyleTemplate => new PathPreviewStyle(
            _multiPathLineWidth, _multiPathPointSize, _multiPathSamplePointSize, _multiPathEventPointSize);
#endif
    }
}
