using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// Length-indexed sequence provider. Inspector data is converted to
    /// interface-only descriptors in one transaction before it becomes live.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public sealed class MultiPathData : MonoBehaviour, IPathSequenceProvider
    {
#if UNITY_EDITOR
        private const float DEFAULT_MULTI_PATH_POINT_SIZE = 0.1f;
#endif

        [SerializeField] private List<PathSegmentAuthoring> _segments =
            new List<PathSegmentAuthoring>();

        private float[] _segmentLengths;
        private float[] _segmentStartDistances;
        private int[] _childRevisions;
        private PathSegmentDescriptor[] _cachedDescriptors;
        private PathSegmentDescriptor[] _runtimeDescriptors;
        private float _pathLength;
        private bool _isInitialized;
        private bool _isDirty;
        private bool _usingRuntimeInput;
        private bool _configurationErrorReported;
        private int _revision;
        private readonly List<IPathProvider> _subscribedProviders =
            new List<IPathProvider>();

        public bool IsInitialized => _isInitialized;
        public bool IsReady => _isInitialized
            && !_isDirty
            && _cachedDescriptors != null
            && _cachedDescriptors.Length > 0
            && !HasChildRevisionChanged();
        public int Revision => _revision;

        public float PathLength
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
                return _cachedDescriptors.Length;
            }
        }

        public event Action PathChanged;

        public void Init()
        {
            if (_isInitialized && IsReady)
                return;

            if (!TryBuild(out string error))
                ReportBuildFailure(error);
        }

        public void Release()
        {
            UnsubscribeFromChildren();
            _isInitialized = false;
            _isDirty = false;
            _usingRuntimeInput = false;
            _runtimeDescriptors = null;
            _segmentLengths = null;
            _segmentStartDistances = null;
            _childRevisions = null;
            _cachedDescriptors = null;
            _pathLength = 0f;
        }

        private void Awake()
        {
            if (Application.isPlaying && HasInputConfiguration())
                Init();
        }

        private void OnDestroy()
        {
            Release();
        }

        /// <summary>
        /// Applies a copied runtime descriptor list only after every provider,
        /// length, movement setting, and reference cycle has been validated.
        /// </summary>
        public void ConfigureSegments(IReadOnlyList<PathSegmentDescriptor> segments)
        {
            if (segments == null)
                throw new ArgumentNullException(nameof(segments));

            PathSegmentDescriptor[] candidate = CopyDescriptors(segments);
            if (!TryBuildCandidate(
                    candidate,
                    out float[] lengths,
                    out float[] starts,
                    out int[] revisions,
                    out float totalLength,
                    out string error))
                throw new InvalidOperationException(error);

            bool changed = !HasSamePublishedData(
                candidate,
                lengths,
                revisions,
                totalLength);
            _usingRuntimeInput = true;
            _runtimeDescriptors = candidate;
            CommitCandidate(
                candidate,
                lengths,
                starts,
                revisions,
                totalLength,
                changed);
            if (changed)
                NotifyPathChanged();
        }

        public PathSegmentAuthoring GetAuthoringSegment(int index)
        {
            if (_segments == null || index < 0 || index >= _segments.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _segments[index];
        }

        public PathSegmentDescriptor GetSegment(int index)
        {
            ThrowIfNotReady();
            if (index < 0 || index >= _cachedDescriptors.Length)
                throw new ArgumentOutOfRangeException(nameof(index));

            PathSegmentDescriptor descriptor = _cachedDescriptors[index];
            return new PathSegmentDescriptor(
                descriptor.Provider,
                PathMovementSettingsUtility.Clone(descriptor.MovementSettings),
                descriptor.PreservePreviousSpeed);
        }

        public float GetSegmentStartDistance(int index)
        {
            ThrowIfNotReady();
            if (index < 0 || index >= _segmentStartDistances.Length)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _segmentStartDistances[index];
        }

        public float GetSegmentLength(int index)
        {
            ThrowIfNotReady();
            if (index < 0 || index >= _segmentLengths.Length)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _segmentLengths[index];
        }

        public Vector3 Sample(float normalizedTime)
        {
            ThrowIfNotReady();
            if (!PathValueUtility.IsFinite(normalizedTime))
                throw new ArgumentOutOfRangeException(nameof(normalizedTime));
            return SampleDistance(Mathf.Clamp01(normalizedTime) * _pathLength);
        }

        public Vector3 SampleDistance(float distance)
        {
            ThrowIfNotReady();
            if (!PathValueUtility.IsFinite(distance))
                throw new ArgumentOutOfRangeException(nameof(distance));

            float clamped = Mathf.Clamp(distance, 0f, _pathLength);
            int index = FindSegmentIndex(clamped);
            float length = _segmentLengths[index];
            float local = length > Mathf.Epsilon
                ? (clamped - _segmentStartDistances[index]) / length
                : 0f;
            return _cachedDescriptors[index].Provider.Sample(local);
        }

        public void Rebuild()
        {
            if (!TryBuild(out string error))
                ReportBuildFailure(error);
        }

        internal void MarkDirtyFromChild()
        {
            _isDirty = true;
            if (!TryBuild(out string error))
                ReportBuildFailure(error);
        }

        private bool HasInputConfiguration()
        {
            return (_usingRuntimeInput && _runtimeDescriptors != null
                    && _runtimeDescriptors.Length > 0)
                || (_segments != null && _segments.Count > 0);
        }

        private bool TryBuild(out string error)
        {
            PathSegmentDescriptor[] candidate;
            if (_usingRuntimeInput)
            {
                candidate = CopyDescriptors(_runtimeDescriptors);
            }
            else
            {
                if (_segments == null || _segments.Count == 0)
                {
                    error = "MultiPathData requires at least one segment.";
                    return false;
                }

                candidate = new PathSegmentDescriptor[_segments.Count];
                for (int i = 0; i < _segments.Count; i++)
                {
                    PathSegmentAuthoring authoring = _segments[i];
                    if (authoring.Provider == null)
                    {
                        error = $"Segment {i} has no provider.";
                        return false;
                    }
                    if (!authoring.TryResolveMovementSettings(
                            out PathMovementSettings movement,
                            out error))
                    {
                        error = $"Segment {i} movement settings are invalid: {error}";
                        return false;
                    }
                    candidate[i] = new PathSegmentDescriptor(
                        authoring.Provider,
                        movement,
                        authoring.PreservePreviousSpeed);
                }
            }

            if (!TryBuildCandidate(
                    candidate,
                    out float[] lengths,
                    out float[] starts,
                    out int[] revisions,
                    out float totalLength,
                    out error))
                return false;

            bool changed = !HasSamePublishedData(
                candidate,
                lengths,
                revisions,
                totalLength);
            CommitCandidate(
                candidate,
                lengths,
                starts,
                revisions,
                totalLength,
                changed);
            if (changed)
                NotifyPathChanged();
            return true;
        }

        private bool TryBuildCandidate(
            PathSegmentDescriptor[] candidate,
            out float[] lengths,
            out float[] starts,
            out int[] revisions,
            out float totalLength,
            out string error)
        {
            lengths = null;
            starts = null;
            revisions = null;
            totalLength = 0f;

            if (candidate == null || candidate.Length == 0)
            {
                error = "MultiPathData requires at least one segment.";
                return false;
            }

            for (int i = 0; i < candidate.Length; i++)
            {
                PathSegmentDescriptor descriptor = candidate[i];
                if (!PathProviderUtility.TryValidateReady(
                        descriptor.Provider,
                        out error))
                {
                    error = $"Segment {i} provider is not ready: {error}";
                    return false;
                }
                if (!PathMovementSettingsUtility.TryValidate(
                        descriptor.MovementSettings,
                        out error))
                {
                    error = $"Segment {i} movement settings are invalid: {error}";
                    return false;
                }
                if (!(descriptor.Provider is PathData))
                {
                    error = $"Segment {i} provider must be a PathData.";
                    return false;
                }

                float length = descriptor.Provider.PathLength;
                if (!PathValueUtility.IsFinite(length) || length <= 0f)
                {
                    error = $"Segment {i} has an invalid path length.";
                    return false;
                }
                totalLength += length;
            }

            if (!PathValueUtility.IsFinite(totalLength) || totalLength <= 0f)
            {
                error = "MultiPathData requires a measurable total length.";
                return false;
            }

            lengths = new float[candidate.Length];
            starts = new float[candidate.Length];
            revisions = new int[candidate.Length];
            float accumulated = 0f;
            for (int i = 0; i < candidate.Length; i++)
            {
                starts[i] = accumulated;
                lengths[i] = candidate[i].Provider.PathLength;
                revisions[i] = candidate[i].Provider.Revision;
                accumulated += lengths[i];
            }

            error = null;
            return true;
        }

        private bool HasSamePublishedData(
            PathSegmentDescriptor[] candidate,
            float[] lengths,
            int[] revisions,
            float totalLength)
        {
            if (!_isInitialized
                || _cachedDescriptors == null
                || _segmentLengths == null
                || _childRevisions == null
                || _cachedDescriptors.Length != candidate.Length
                || _segmentLengths.Length != lengths.Length
                || _childRevisions.Length != revisions.Length
                || !Mathf.Approximately(_pathLength, totalLength))
                return false;

            for (int i = 0; i < candidate.Length; i++)
            {
                if (_childRevisions[i] != revisions[i]
                    || !Mathf.Approximately(_segmentLengths[i], lengths[i])
                    || !PathProviderUtility.AreSameDescriptor(
                        _cachedDescriptors[i],
                        candidate[i]))
                    return false;
            }
            return true;
        }

        private void CommitCandidate(
            PathSegmentDescriptor[] candidate,
            float[] lengths,
            float[] starts,
            int[] revisions,
            float totalLength,
            bool incrementRevision)
        {
            UnsubscribeFromChildren();
            _cachedDescriptors = CopyDescriptors(candidate);
            _segmentLengths = lengths;
            _segmentStartDistances = starts;
            _childRevisions = revisions;
            _pathLength = totalLength;
            _isInitialized = true;
            _isDirty = false;
            _configurationErrorReported = false;
            SubscribeToChildren(_cachedDescriptors);
            if (incrementRevision)
                _revision++;
        }

        private int FindSegmentIndex(float distance)
        {
            if (distance >= _pathLength)
                return _segmentLengths.Length - 1;
            return PathGeometryUtility.FindSegmentStartIndex(
                _segmentStartDistances,
                distance);
        }

        private void SubscribeToChildren(PathSegmentDescriptor[] descriptors)
        {
            if (descriptors == null)
                return;
            for (int i = 0; i < descriptors.Length; i++)
            {
                IPathProvider provider = descriptors[i].Provider;
                if (provider == null || _subscribedProviders.Contains(provider))
                    continue;
                provider.PathChanged += MarkDirtyFromChild;
                _subscribedProviders.Add(provider);
            }
        }

        private void UnsubscribeFromChildren()
        {
            for (int i = 0; i < _subscribedProviders.Count; i++)
            {
                if (_subscribedProviders[i] != null)
                    _subscribedProviders[i].PathChanged -= MarkDirtyFromChild;
            }
            _subscribedProviders.Clear();
        }

        private bool HasChildRevisionChanged()
        {
            if (_cachedDescriptors == null
                || _childRevisions == null
                || _cachedDescriptors.Length != _childRevisions.Length)
                return true;
            for (int i = 0; i < _cachedDescriptors.Length; i++)
            {
                IPathProvider provider = _cachedDescriptors[i].Provider;
                if (provider == null
                    || !provider.IsReady
                    || provider.Revision != _childRevisions[i])
                    return true;
            }
            return false;
        }

        private static PathSegmentDescriptor[] CopyDescriptors(
            IReadOnlyList<PathSegmentDescriptor> source)
        {
            if (source == null)
                return null;
            PathSegmentDescriptor[] result =
                new PathSegmentDescriptor[source.Count];
            for (int i = 0; i < source.Count; i++)
            {
                PathSegmentDescriptor descriptor = source[i];
                result[i] = new PathSegmentDescriptor(
                    descriptor.Provider,
                    PathMovementSettingsUtility.Clone(descriptor.MovementSettings),
                    descriptor.PreservePreviousSpeed);
            }
            return result;
        }

        private void NotifyPathChanged()
        {
            PathProviderUtility.RaisePathChanged(PathChanged);
        }

        private void ReportBuildFailure(string message)
        {
            _isDirty = true;
            if (_configurationErrorReported)
                return;
            Debug.LogError(
                $"MultiPathData '{name}' could not build: {message}",
                this);
            _configurationErrorReported = true;
        }

        private void ThrowIfNotReady()
        {
            if (!IsReady)
                throw new InvalidOperationException(
                    "MultiPathData is not initialized and ready. Rebuild after changing a segment.");
        }

#if UNITY_EDITOR
#pragma warning disable 0414
        [Header("Editor Only")]
        [SerializeField] private bool _autoLinkPathPoints = true;

        [Header("MultiPath → all PathData drawing")]
        [SerializeField, Range(0.1f, 20f)] private float _multiPathLineWidth = 2f;
        [SerializeField, Range(0f, 1f)] private float _multiPathPointSize =
            DEFAULT_MULTI_PATH_POINT_SIZE;
        [SerializeField, Range(0f, 1f)] private float _multiPathSamplePointSize;
        [SerializeField, Range(0f, 1f)] private float _multiPathEventPointSize = 0.15f;
#pragma warning restore 0414
#endif
    }
}
