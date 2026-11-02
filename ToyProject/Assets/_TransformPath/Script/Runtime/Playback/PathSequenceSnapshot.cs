using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>Immutable O(S) descriptor and length snapshot for sequence playback.</summary>
    internal sealed class PathSequenceSnapshot
    {
        #region Member Variables

        private readonly PathSegmentDescriptor[] _descriptors;
        private readonly PathEventSourceSnapshot[] _eventSources;
        private readonly float[] _lengths;
        private readonly float[] _starts;
        private readonly float _totalLength;
        private int _providerRevision;
        private PathSequenceSnapshot _comparedWith;
        private bool _comparedSame;

        private static readonly ConditionalWeakTable<IPathSequenceProvider, PathSequenceSnapshot> Latest =
            new ConditionalWeakTable<IPathSequenceProvider, PathSequenceSnapshot>();

        #endregion


        #region Properties

        public int Count => _descriptors.Length;

        #endregion


        #region Public Methods

        /// <summary>
        /// The snapshot of this provider's current revision. It never changes, so
        /// every follower of the sequence can use the same one.
        /// </summary>
        public static bool TryGetCurrent(
            IPathSequenceProvider provider,
            out PathSequenceSnapshot snapshot,
            out string error)
        {
            error = null;
            int revision = provider.Revision;
            if (Latest.TryGetValue(provider, out PathSequenceSnapshot latest) && latest._providerRevision == revision)
            {
                snapshot = latest;
                return true;
            }
            if (!TryCreate(provider, out snapshot, out error))
                return false;
            snapshot._providerRevision = revision;
            // Otherwise a path that keeps changing chains every snapshot it had.
            if (latest != null)
                latest._comparedWith = null;
            Latest.Remove(provider);
            Latest.Add(provider, snapshot);
            return true;
        }

        private static bool TryCreate(
            IPathSequenceProvider provider,
            out PathSequenceSnapshot snapshot,
            out string error)
        {
            snapshot = null;
            if (!PathProviderUtility.TryGetRouteSegmentCount(provider, out int count, out error))
                return false;

            PathSegmentDescriptor[] descriptors = new PathSegmentDescriptor[count];
            PathEventSourceSnapshot[] eventSources =
                new PathEventSourceSnapshot[count];
            float[] lengths = new float[count];
            float[] starts = new float[count];
            float totalLength = 0f;
            for (int i = 0; i < count; i++)
            {
                try
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
                    eventSources[i] = PathEventSourceSnapshot.GetCurrent(descriptor.Provider);
                    lengths[i] = provider.GetSegmentLength(i);
                    float declaredStart = provider.GetSegmentStartDistance(i);
                    starts[i] = totalLength;
                    if (!PathValueUtility.IsFinite(declaredStart)
                        || !Mathf.Approximately(declaredStart, totalLength)
                        || !PathValueUtility.IsFinite(lengths[i])
                        || lengths[i] <= 0f
                        || !PathValueUtility.IsFinite(totalLength + lengths[i]))
                    {
                        error = $"Sequence segment {i} has inconsistent distance data.";
                        return false;
                    }

                    if (!Mathf.Approximately(lengths[i], descriptor.Provider.PathLength))
                    {
                        error = $"Sequence segment {i} length does not match its provider.";
                        return false;
                    }

                    totalLength += lengths[i];
                }
                catch (Exception exception)
                {
                    error = $"Sequence segment {i} could not be inspected: {exception.Message}";
                    return false;
                }
            }

            if (!PathValueUtility.IsFinite(totalLength)
                || totalLength <= 0f
                || !Mathf.Approximately(totalLength, provider.PathLength))
            {
                error = "Sequence total length must be positive.";
                return false;
            }

            snapshot = new PathSequenceSnapshot(
                descriptors,
                eventSources,
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
            return _eventSources[index];
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
            return PathGeometryUtility.FindSegmentStartIndex(_starts, distance);
        }

        public float GetLocalProgress(int index, float globalProgress)
        {
            float distance = Mathf.Clamp01(globalProgress) * _totalLength;
            return _lengths[index] <= Mathf.Epsilon
                ? 0f
                : Mathf.Clamp01((distance - _starts[index]) / _lengths[index]);
        }

        // Every follower of a sequence asks the same question when it changes.
        public bool HasSamePlaybackDefinition(PathSequenceSnapshot other)
        {
            if (!ReferenceEquals(other, _comparedWith))
            {
                _comparedSame = CompareDefinition(other);
                _comparedWith = other;
            }
            return _comparedSame;
        }

        private bool CompareDefinition(PathSequenceSnapshot other)
        {
            if (other == null || other.Count != Count)
                return false;
            for (int i = 0; i < Count; i++)
            {
                if (!PathProviderUtility.AreSameDescriptor(
                        _descriptors[i],
                        other._descriptors[i]))
                    return false;
                if (!_eventSources[i].HasSameContent(other._eventSources[i]))
                    return false;
            }

            return true;
        }

        #endregion


        #region Private Methods

        private PathSequenceSnapshot(
            PathSegmentDescriptor[] descriptors,
            PathEventSourceSnapshot[] eventSources,
            float[] lengths,
            float[] starts,
            float totalLength)
        {
            _descriptors = descriptors;
            _eventSources = eventSources;
            _lengths = lengths;
            _starts = starts;
            _totalLength = totalLength;
        }

        #endregion
    }
}
