using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>Immutable O(S) descriptor and length snapshot for sequence playback.</summary>
    internal sealed class PathSequenceSnapshot
    {
        #region Member Variables

        private readonly PathSegmentDescriptor[] _descriptors;
        private readonly float[] _lengths;
        private readonly float[] _starts;
        private readonly float _totalLength;

        #endregion


        #region Properties

        public int Count => _descriptors.Length;

        #endregion


        #region Public Methods

        public static bool TryCreate(
            IPathSequenceProvider provider,
            out PathSequenceSnapshot snapshot,
            out string error)
        {
            snapshot = null;
            if (!PathProviderUtility.TryGetRouteSegmentCount(provider, out int count, out error))
                return false;

            PathSegmentDescriptor[] descriptors = new PathSegmentDescriptor[count];
            float[] lengths = new float[count];
            float[] starts = new float[count];
            float totalLength = 0f;
            for (int i = 0; i < count; i++)
            {
                if (!PathProviderUtility.TryGetDescriptor(
                        provider,
                        i,
                        out PathSegmentDescriptor descriptor,
                        out error))
                    return false;

                descriptors[i] = new PathSegmentDescriptor(
                    descriptor.Provider,
                    PathMovementSettingsUtility.Clone(descriptor.MovementSettings),
                    descriptor.PreservePreviousSpeed);
                lengths[i] = provider.GetSegmentLength(i);
                starts[i] = totalLength;
                if (!PathValueUtility.IsFinite(lengths[i])
                    || lengths[i] <= 0f
                    || !PathValueUtility.IsFinite(totalLength + lengths[i]))
                {
                    error = $"Sequence segment {i} has an invalid length.";
                    return false;
                }

                if (!Mathf.Approximately(lengths[i], descriptor.Provider.PathLength))
                {
                    error = $"Sequence segment {i} length does not match its provider.";
                    return false;
                }

                totalLength += lengths[i];
            }

            if (!PathValueUtility.IsFinite(totalLength) || totalLength <= 0f)
            {
                error = "Sequence total length must be positive.";
                return false;
            }

            snapshot = new PathSequenceSnapshot(
                descriptors,
                lengths,
                starts,
                totalLength);
            error = null;
            return true;
        }

        public PathSegmentDescriptor GetDescriptor(int index)
        {
            return _descriptors[index];
        }

        public float GetLength(int index)
        {
            return _lengths[index];
        }

        public IPathEventSource GetEventSource(int index)
        {
            return _descriptors[index].Provider as IPathEventSource;
        }

        public Vector3 Sample(int index, float localProgress)
        {
            return _descriptors[index].Provider.Sample(Mathf.Clamp01(localProgress));
        }

        public float GetGlobalProgress(int index, float localProgress)
        {
            return Mathf.Clamp01(
                (_starts[index] + _lengths[index] * Mathf.Clamp01(localProgress))
                / _totalLength);
        }

        public int FindSegment(float globalProgress)
        {
            if (globalProgress >= 1f)
                return _descriptors.Length - 1;

            float distance = Mathf.Clamp01(globalProgress) * _totalLength;
            int low = 0;
            int high = _starts.Length - 1;
            while (low < high)
            {
                int middle = (low + high + 1) / 2;
                if (_starts[middle] <= distance)
                    low = middle;
                else
                    high = middle - 1;
            }

            return low;
        }

        public float GetLocalProgress(int index, float globalProgress)
        {
            float distance = Mathf.Clamp01(globalProgress) * _totalLength;
            return _lengths[index] <= Mathf.Epsilon
                ? 0f
                : Mathf.Clamp01((distance - _starts[index]) / _lengths[index]);
        }

        public bool HasSameStructure(PathSequenceSnapshot other)
        {
            if (other == null || other.Count != Count)
                return false;
            for (int i = 0; i < Count; i++)
            {
                if (!PathProviderUtility.AreSameDescriptor(
                        _descriptors[i],
                        other._descriptors[i]))
                    return false;
            }

            return true;
        }

        #endregion


        #region Private Methods

        private PathSequenceSnapshot(
            PathSegmentDescriptor[] descriptors,
            float[] lengths,
            float[] starts,
            float totalLength)
        {
            _descriptors = descriptors;
            _lengths = lengths;
            _starts = starts;
            _totalLength = totalLength;
        }

        #endregion
    }
}
