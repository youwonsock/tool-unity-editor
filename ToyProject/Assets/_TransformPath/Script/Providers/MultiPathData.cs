using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// PathData segments played one after another. It rebuilds whenever one of
    /// its paths publishes a change. Only a path that never woke is checked on
    /// each read, since Unity destroys it without calling its OnDestroy.
    /// </summary>
    public sealed class MultiPathData : PathProviderBehaviour
    {
        private const float DEFAULT_MULTI_PATH_POINT_SIZE = 0.1f;

        [SerializeField] private List<PathSegmentAuthoring> _segments = new List<PathSegmentAuthoring>();

        // The built segments; each one's sampler is the PathData it plays.
        [NonSerialized] private PathRoute.Segment[] _built;
        [NonSerialized] private int[] _childRevisions;
        [NonSerialized] private float _pathLength;
        [NonSerialized] private bool _failed;
        [NonSerialized] private bool _building;
        [NonSerialized] private bool _changedWhileBuilding;
        [NonSerialized] private readonly List<PathData> _subscribed = new List<PathData>();
        // Segment paths that had not woken at the last build. Usually none in Play Mode.
        [NonSerialized] private PathData[] _unwatched = Array.Empty<PathData>();

        private protected override bool IsBuilt => _built != null && !_failed && UnwatchedPathsAlive();

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
                return _built.Length;
            }
        }

        public int AuthoringSegmentCount => _segments == null ? 0 : _segments.Count;

        private void Awake()
        {
            if (Application.isPlaying)
                Wake();
        }

        /// <summary>
        /// What Awake does in Play Mode. A segment path without control points yet,
        /// such as one that gets them at runtime, would only fail the build, so the
        /// sequence listens to its paths instead and builds when that path publishes
        /// its first build, or at its own first use.
        /// </summary>
        internal void Wake()
        {
            if (_segments == null || _segments.Count == 0)
                return;
            if (WaitsForPoints)
                Subscribe();
            else
                Rebuild();
        }

        /// <summary>Whether a segment path has no control points yet.</summary>
        internal override bool WaitsForPoints
        {
            get
            {
                if (_segments == null)
                    return false;
                for (int i = 0; i < _segments.Count; i++)
                {
                    PathData path = _segments[i].Path;
                    if (path != null && path.WaitsForPoints)
                        return true;
                }
                return false;
            }
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

        public PathData GetSegmentPath(int index) => (PathData)GetBuiltSegment(index).Sampler;

        public PathMovementSettings GetSegmentMovement(int index) => GetBuiltSegment(index).Movement;

        public float GetSegmentStartDistance(int index) => GetBuiltSegment(index).Start;

        public float GetSegmentLength(int index) => GetBuiltSegment(index).Length;

        private PathRoute.Segment GetBuiltSegment(int index)
        {
            ThrowIfNotReady();
            if (index < 0 || index >= _built.Length)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _built[index];
        }

        public override Vector3 Sample(float progress)
        {
            PathValueUtility.ThrowIfNotFinite(progress, nameof(progress));
            return GetRoute().Sample(progress);
        }

        // A build that reads its paths again: building a path for the first time
        // publishes it, and a listener of that path can change one already read.
        private const int MAX_BUILD_PASSES = 3;

        private protected override bool TryBuild(out bool geometryChanged, out bool definitionChanged, out string error)
        {
            _building = true;
            geometryChanged = false;
            definitionChanged = false;
            try
            {
                for (int pass = 1; ; pass++)
                {
                    _changedWhileBuilding = false;
                    bool built = TryBuildSegments(out bool geometry, out bool definition, out error);
                    geometryChanged |= geometry;
                    definitionChanged |= definition;
                    // The notices of this build's own paths were ignored; read them again until none came.
                    if (!built || !_changedWhileBuilding || pass == MAX_BUILD_PASSES)
                        return built;
                }
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

            var built = new PathRoute.Segment[count];
            var revisions = new int[count];
            float total = 0f;
            for (int i = 0; i < count; i++)
            {
                PathSegmentAuthoring segment = _segments[i];
                PathData path = segment.Path;
                if (path == null)
                {
                    error = $"Segment {i} has no PathData assigned.";
                    return false;
                }
                path.EnsureBuilt();
                if (!path.IsReady)
                {
                    error = $"Segment {i} path is not ready.";
                    return false;
                }
                if (!segment.TryResolveMovement(out PathMovementSettings movement, out error))
                {
                    error = $"Segment {i} movement settings are invalid: {error}";
                    return false;
                }
                float length = path.PathLength;
                built[i] = new PathRoute.Segment(path, total, length, movement, segment.PreservePreviousSpeed,
                    path.RuntimeEvents);
                revisions[i] = path.Revision;
                total += length;
            }
            if (!PathValueUtility.IsFinite(total) || total <= 0f)
            {
                error = "MultiPathData requires a measurable total length.";
                return false;
            }
            // Building a later path can publish a change whose listener breaks an
            // earlier one; this build ignores its own paths' notices, so it looks again.
            for (int i = 0; i < count; i++)
            {
                if (!built[i].Sampler.IsReady)
                {
                    error = $"Segment {i} path is not ready.";
                    return false;
                }
            }

            geometryChanged = !HasSame(_childRevisions, revisions) || !Mathf.Approximately(_pathLength, total);
            definitionChanged = _built == null || !HasSameStructure(built);
            _unwatched = FindUnwatched(built);
            _built = built;
            _childRevisions = revisions;
            _pathLength = total;
            _failed = false;
            error = null;
            return true;
        }

        private protected override void MarkFailed() => _failed = true;

        // A sequence plays its paths' events per segment; as one whole route it has none.
        private protected override PathRuntimeEvent[] WholeRouteEvents => Array.Empty<PathRuntimeEvent>();

        // The route never changes the array, and each build makes a new one.
        private protected override PathRoute CreateRoute() => PathRoute.FromProvider(this, _built, true);

        // What a sequence plays: each segment's path, its resolved movement and
        // its events. A path's own movement counts only through a segment that
        // takes it, so an Override segment keeps playing when it changes.
        private bool HasSameStructure(PathRoute.Segment[] built)
        {
            if (_built.Length != built.Length)
                return false;
            for (int i = 0; i < built.Length; i++)
            {
                if (!ReferenceEquals(_built[i].Sampler, built[i].Sampler)
                    || _built[i].PreservePreviousSpeed != built[i].PreservePreviousSpeed
                    || !_built[i].Movement.IsSameAs(built[i].Movement)
                    || !PathRuntimeEvent.AreSame(_built[i].Events, built[i].Events))
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
                if (path == null || _subscribed.Contains(path))
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
            {
                _changedWhileBuilding = true;
                return;
            }
            // A path that failed or was destroyed reported it already, so this
            // sequence only goes out of use until the path is back.
            if (HasUnavailablePath())
                MarkUnavailable();
            else
                Rebuild();
        }

        // A segment's path destroyed while it was never active tells no one: Unity
        // skips its OnDestroy. The sequence, played whole or by segment, is out of
        // use then. A path that woke since will report it, so it is not checked.
        private bool UnwatchedPathsAlive()
        {
            for (int i = 0; i < _unwatched.Length; i++)
            {
                PathData path = _unwatched[i];
                if (!path.HasAwoken && path == null)
                    return false;
            }
            return true;
        }

        private static PathData[] FindUnwatched(PathRoute.Segment[] built)
        {
            List<PathData> unwatched = null;
            for (int i = 0; i < built.Length; i++)
            {
                var path = (PathData)built[i].Sampler;
                if (path.HasAwoken || (unwatched != null && unwatched.Contains(path)))
                    continue;
                unwatched ??= new List<PathData>();
                unwatched.Add(path);
            }
            return unwatched == null ? Array.Empty<PathData>() : unwatched.ToArray();
        }

        // A path that failed or went away reported it already, and one still waiting
        // for its points would only fail: either keeps the sequence out of use quietly.
        // A path with points that nothing asked to build yet builds with the sequence.
        private bool HasUnavailablePath()
        {
            if (_segments == null)
                return true;
            for (int i = 0; i < _segments.Count; i++)
            {
                PathData path = _segments[i].Path;
                if (path == null || (!path.IsReady && (path.WasBuildRequested || path.WaitsForPoints)))
                    return true;
            }
            return false;
        }

        // Editor settings, serialized in every build so both share one data layout.
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
    }
}
