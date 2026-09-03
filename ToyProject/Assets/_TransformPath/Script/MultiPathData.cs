using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>Length-indexed sequence of independent PathData providers.</summary>
    [DefaultExecutionOrder(-200)]
    public sealed class MultiPathData : MonoBehaviour, IPathSequenceProvider
    {
        #region Constants

#if UNITY_EDITOR
        private const float DEFAULT_MULTI_PATH_POINT_SIZE = 0.1f;
#endif

        #endregion


        #region Member Variables

        [SerializeField] private List<PathSegmentConfig> _segments =
            new List<PathSegmentConfig>();

        private float[] _segmentLengths;
        private float[] _segmentStartDistances;
        private int[] _childRevisions;
        private PathSegmentDescriptor[] _cachedDescriptors;
        private float _pathLength;
        private bool _isInitialized;
        private bool _isDirty;
        private bool _configurationErrorReported;
        private int _revision;
        private readonly List<PathData> _subscribedProviders =
            new List<PathData>();
        private readonly List<PathSegmentConfig> _validatedSegments =
            new List<PathSegmentConfig>();
        private readonly List<PathSegmentDescriptor> _validatedDescriptors =
            new List<PathSegmentDescriptor>();

        #endregion


        #region Properties

        public bool IsInitialized => _isInitialized;
        public bool IsReady => _isInitialized
            && !_isDirty
            && _segmentLengths != null
            && _segmentLengths.Length > 0
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
                return _segments.Count;
            }
        }

        public event Action PathChanged;

        #endregion


        #region Unity Events

        public void Init()
        {
            if (_isInitialized || !HasAuthoringConfiguration())
                return;

            if (!TryBuild(out string error))
                MarkConfigurationError(error);
        }

        public void Release()
        {
            UnsubscribeFromChildren();
            _isInitialized = false;
            _isDirty = false;
            _segmentLengths = null;
            _segmentStartDistances = null;
            _childRevisions = null;
            _cachedDescriptors = null;
            _pathLength = 0f;
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

        public void ConfigureSegments(IReadOnlyList<PathSegmentConfig> segments)
        {
            if (segments == null)
                throw new ArgumentNullException(nameof(segments));

            _segments.Clear();
            for (int i = 0; i < segments.Count; i++)
                _segments.Add(segments[i]);
            _isDirty = true;

            if (_isInitialized)
            {
                if (!TryBuild(out string error))
                    MarkConfigurationError(error);
            }
        }

        public PathSegmentConfig GetSegmentConfig(int index)
        {
            ThrowIfNotReady();
            if (index < 0 || index >= _segments.Count)
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
            return _segments[index].PathData.Sample(local);
        }

        public void Rebuild()
        {
            if (!_isInitialized)
            {
                Init();
                return;
            }

            if (!TryBuild(out string error))
                MarkConfigurationError(error);
        }

        #endregion


        #region Private Methods

        internal void MarkDirtyFromChild()
        {
            _isDirty = true;
            if (_isInitialized && !TryBuild(out string error))
                MarkConfigurationError(error);
        }

        private bool HasAuthoringConfiguration()
        {
            return _segments != null && _segments.Count > 0;
        }

        private bool TryBuild(out string error)
        {
            if (!TryBuildTemporary(
                    out List<PathSegmentConfig> nextSegments,
                    out float[] nextLengths,
                    out float[] nextStarts,
                    out int[] nextRevisions,
                    out PathSegmentDescriptor[] nextDescriptors,
                    out float nextTotalLength,
                    out error))
                return false;

            bool changed = !_isInitialized
                || _segmentLengths == null
                || _segmentLengths.Length != nextLengths.Length
                || !Mathf.Approximately(_pathLength, nextTotalLength);
            if (!changed)
            {
                for (int i = 0; i < nextLengths.Length; i++)
                {
                    if (!AreSameConfig(_segments[i], nextSegments[i])
                        || !Mathf.Approximately(_segmentLengths[i], nextLengths[i])
                        || _cachedDescriptors == null
                        || !PathProviderUtility.AreSameDescriptor(
                            _cachedDescriptors[i],
                            nextDescriptors[i]))
                    {
                        changed = true;
                        break;
                    }
                }
            }

            _segments.Clear();
            _segments.AddRange(nextSegments);
            _segmentLengths = nextLengths;
            _segmentStartDistances = nextStarts;
            _childRevisions = nextRevisions;
            _cachedDescriptors = nextDescriptors;
            _pathLength = nextTotalLength;
            _isInitialized = true;
            _isDirty = false;
            _configurationErrorReported = false;
            SubscribeToChildren();

            if (changed)
            {
                _revision++;
                NotifyPathChanged();
            }

            return true;
        }

        private bool TryBuildTemporary(
            out List<PathSegmentConfig> nextSegments,
            out float[] nextLengths,
            out float[] nextStarts,
            out int[] nextRevisions,
            out PathSegmentDescriptor[] nextDescriptors,
            out float nextTotalLength,
            out string error)
        {
            nextSegments = null;
            nextLengths = null;
            nextStarts = null;
            nextRevisions = null;
            nextDescriptors = null;
            nextTotalLength = 0f;

            if (_segments == null || _segments.Count == 0)
            {
                error = "MultiPathData requires at least one segment.";
                return false;
            }

            _validatedSegments.Clear();
            _validatedDescriptors.Clear();
            for (int i = 0; i < _segments.Count; i++)
            {
                PathSegmentConfig segment = _segments[i];
                PathData pathData = segment.PathData;
                if (pathData == null)
                {
                    error = $"Segment {i} has no PathData provider.";
                    return false;
                }
                if (!PathProviderUtility.TryGetDescriptor(
                        pathData,
                        0,
                        out PathSegmentDescriptor descriptor,
                        out string descriptorError))
                {
                    error = $"Segment {i} provider is invalid: {descriptorError}";
                    return false;
                }

                float length = descriptor.Provider.PathLength;
                if (!PathValueUtility.IsFinite(length) || length <= 0f)
                {
                    error = $"Segment {i} has an invalid path length.";
                    return false;
                }

                _validatedSegments.Add(segment);
                _validatedDescriptors.Add(descriptor);
                nextTotalLength += length;
            }

            if (!PathValueUtility.IsFinite(nextTotalLength) || nextTotalLength <= 0f)
            {
                error = "MultiPathData requires a measurable total length.";
                return false;
            }

            nextSegments = new List<PathSegmentConfig>(_validatedSegments);
            nextLengths = new float[nextSegments.Count];
            nextStarts = new float[nextSegments.Count];
            nextRevisions = new int[nextSegments.Count];
            nextDescriptors = new PathSegmentDescriptor[nextSegments.Count];
            float accumulated = 0f;
            for (int i = 0; i < nextSegments.Count; i++)
            {
                nextStarts[i] = accumulated;
                PathSegmentDescriptor descriptor = _validatedDescriptors[i];
                PathData pathData = nextSegments[i].PathData;
                nextLengths[i] = descriptor.Provider.PathLength;
                nextRevisions[i] = pathData.Revision;
                nextDescriptors[i] = new PathSegmentDescriptor(
                    descriptor.Provider,
                    PathMovementSettingsUtility.Clone(descriptor.MovementSettings),
                    nextSegments[i].PreservePreviousSpeed);
                accumulated += nextLengths[i];
            }

            error = null;
            return true;
        }

        private int FindSegmentIndex(float distance)
        {
            if (distance >= _pathLength)
                return _segmentLengths.Length - 1;

            return PathGeometryUtility.FindSegmentIndex(
                _segmentStartDistances,
                distance);
        }

        private void SubscribeToChildren()
        {
            UnsubscribeFromChildren();
            if (_segments == null)
                return;

            for (int i = 0; i < _segments.Count; i++)
            {
                PathData pathData = _segments[i].PathData;
                if (pathData == null || _subscribedProviders.Contains(pathData))
                    continue;
                pathData.PathChanged += MarkDirtyFromChild;
                _subscribedProviders.Add(pathData);
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
            if (_segments == null
                || _childRevisions == null
                || _segments.Count != _childRevisions.Length)
                return true;

            for (int i = 0; i < _segments.Count; i++)
            {
                if (_segments[i].PathData == null
                    || _segments[i].PathData.Revision != _childRevisions[i])
                    return true;
            }

            return false;
        }

        private static bool AreSameConfig(
            PathSegmentConfig left,
            PathSegmentConfig right)
        {
            return left.PathData == right.PathData
                && left.PreservePreviousSpeed == right.PreservePreviousSpeed;
        }

        private void NotifyPathChanged()
        {
            PathChanged?.Invoke();
        }

        private void MarkConfigurationError(string message)
        {
            UnsubscribeFromChildren();
            _isInitialized = false;
            _isDirty = true;
            _segmentLengths = null;
            _segmentStartDistances = null;
            _childRevisions = null;
            _cachedDescriptors = null;
            _pathLength = 0f;
            if (_configurationErrorReported)
                return;

            Debug.LogError($"MultiPathData '{name}' could not build: {message}", this);
            _configurationErrorReported = true;
        }

        private void ThrowIfNotReady()
        {
            if (!IsReady)
                throw new InvalidOperationException(
                    "MultiPathData is not initialized and ready. Rebuild after changing a segment.");
        }

        #endregion

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
