using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// A length-indexed sequence of independent PathData providers.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public sealed class MultiPathData : MonoBehaviour, IPathSequenceProvider
    {
        [SerializeField] private List<PathSegmentConfig> _segments = new List<PathSegmentConfig>();

        private float[] _segmentLengths;
        private float[] _segmentStartDistances;
        private int[] _childRevisions;
        private float _pathLength;
        private bool _isInitialized;
        private bool _isDirty;
        private bool _configurationErrorReported;
        private int _revision;
        private readonly List<PathData> _subscribedProviders = new List<PathData>();
        private readonly List<PathSegmentConfig> _validatedSegments = new List<PathSegmentConfig>();

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
            TryBuild();
        }

        public void Release()
        {
            UnsubscribeFromChildren();
            _isInitialized = false;
            _isDirty = false;
            _segmentLengths = null;
            _segmentStartDistances = null;
            _childRevisions = null;
            _pathLength = 0f;
        }

        public void ConfigureSegments(IReadOnlyList<PathSegmentConfig> segments)
        {
            if (segments == null)
                throw new ArgumentNullException(nameof(segments));

            _segments.Clear();
            for (int i = 0; i < segments.Count; i++)
                _segments.Add(segments[i]);
            _isDirty = true;

            if (_isInitialized)
                Rebuild();
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
            PathSegmentConfig config = GetSegmentConfig(index);
            return new PathSegmentDescriptor(config.PathData, config.MoveType, config.Value, config.TimeCurve);
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
            if (!IsFinite(normalizedTime))
                throw new ArgumentOutOfRangeException(nameof(normalizedTime));
            return SampleDistance(Mathf.Clamp01(normalizedTime) * _pathLength);
        }

        public Vector3 SampleDistance(float distance)
        {
            ThrowIfNotReady();
            if (!IsFinite(distance))
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
            TryBuild();
        }

        internal void MarkDirtyFromChild()
        {
            _isDirty = true;
            if (_isInitialized)
                TryBuild();
        }

        private bool HasAuthoringConfiguration()
        {
            return _segments != null && _segments.Count > 0;
        }

        private void TryBuild()
        {
            try
            {
                ValidateAndBuildTemporary(
                    out List<PathSegmentConfig> nextSegments,
                    out float[] nextLengths,
                    out float[] nextStarts,
                    out int[] nextRevisions,
                    out float nextTotalLength);

                bool changed = !_isInitialized
                    || _segmentLengths == null
                    || _segmentLengths.Length != nextLengths.Length
                    || !Mathf.Approximately(_pathLength, nextTotalLength);
                if (!changed)
                {
                    for (int i = 0; i < nextLengths.Length; i++)
                    {
                        PathSegmentConfig oldConfig = _segments[i];
                        PathSegmentConfig newConfig = nextSegments[i];
                        if (!AreSameConfig(oldConfig, newConfig)
                            || !Mathf.Approximately(_segmentLengths[i], nextLengths[i]))
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
            }
            catch (Exception exception)
            {
                _isInitialized = false;
                _isDirty = true;
                _segmentLengths = null;
                _segmentStartDistances = null;
                _childRevisions = null;
                _pathLength = 0f;
                UnsubscribeFromChildren();
                if (!_configurationErrorReported)
                {
                    Debug.LogError($"MultiPathData '{name}' could not build: {exception.Message}", this);
                    _configurationErrorReported = true;
                }
            }
        }

        private void ValidateAndBuildTemporary(
            out List<PathSegmentConfig> nextSegments,
            out float[] nextLengths,
            out float[] nextStarts,
            out int[] nextRevisions,
            out float nextTotalLength)
        {
            if (_segments == null || _segments.Count == 0)
                throw new ArgumentException("MultiPathData requires at least one segment.");

            _validatedSegments.Clear();
            nextTotalLength = 0f;
            for (int i = 0; i < _segments.Count; i++)
            {
                PathSegmentConfig segment = _segments[i];
                if (segment.PathData == null)
                    throw new ArgumentException($"Segment {i} has no PathData provider.");
                if (!segment.PathData.IsReady)
                    throw new InvalidOperationException($"Segment {i} PathData is not ready.");
                if (!Enum.IsDefined(typeof(EPathMoveType), segment.MoveType))
                    throw new ArgumentOutOfRangeException($"segments[{i}].MoveType");
                if (!IsFinite(segment.Value) || segment.Value <= 0f)
                    throw new ArgumentOutOfRangeException($"segments[{i}].Value");
                if (segment.MoveType == EPathMoveType.TimeBased
                    && (segment.TimeCurve == null || segment.TimeCurve.length == 0))
                    throw new ArgumentException($"Segment {i} requires a non-empty TimeCurve.");

                _validatedSegments.Add(segment);
                nextTotalLength += segment.PathData.PathLength;
            }

            if (!IsFinite(nextTotalLength) || nextTotalLength <= 0f)
                throw new ArgumentException("MultiPathData requires a measurable total length.");

            nextSegments = new List<PathSegmentConfig>(_validatedSegments);
            nextLengths = new float[nextSegments.Count];
            nextStarts = new float[nextSegments.Count];
            nextRevisions = new int[nextSegments.Count];
            float accumulated = 0f;
            for (int i = 0; i < nextSegments.Count; i++)
            {
                nextStarts[i] = accumulated;
                nextLengths[i] = nextSegments[i].PathData.PathLength;
                nextRevisions[i] = nextSegments[i].PathData.Revision;
                accumulated += nextLengths[i];
            }
        }

        private int FindSegmentIndex(float distance)
        {
            if (distance >= _pathLength)
                return _segmentLengths.Length - 1;

            int low = 0;
            int high = _segmentStartDistances.Length - 1;
            while (low < high)
            {
                int middle = (low + high + 1) / 2;
                if (_segmentStartDistances[middle] <= distance)
                    low = middle;
                else
                    high = middle - 1;
            }
            return low;
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
            if (_segments == null || _childRevisions == null || _segments.Count != _childRevisions.Length)
                return true;
            for (int i = 0; i < _segments.Count; i++)
            {
                if (_segments[i].PathData == null || _segments[i].PathData.Revision != _childRevisions[i])
                    return true;
            }
            return false;
        }

        private static bool AreSameConfig(PathSegmentConfig left, PathSegmentConfig right)
        {
            return left.PathData == right.PathData
                && left.MoveType == right.MoveType
                && Mathf.Approximately(left.Value, right.Value)
                && left.TimeCurve == right.TimeCurve;
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
                throw new InvalidOperationException("MultiPathData is not initialized and ready. Rebuild after changing a segment.");
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
