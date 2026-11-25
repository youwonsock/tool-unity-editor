using System;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// Immutable playback route: one or more segments laid end to end. A single
    /// path is a route of one segment, a sequence a route of many, so playback
    /// never branches on the kind of request. Providers cache the route of their
    /// current revision and every follower of that path shares it.
    /// </summary>
    internal sealed class PathRoute
    {
        internal readonly struct Segment
        {
            public readonly IPathProvider Sampler;
            public readonly float Start;
            public readonly float Length;
            public readonly PathMovementSettings Movement;
            public readonly bool PreservePreviousSpeed;
            public readonly PathRuntimeEvent[] Events;

            public Segment(IPathProvider sampler, float start, float length,
                PathMovementSettings movement, bool preservePreviousSpeed, PathRuntimeEvent[] events)
            {
                Sampler = sampler;
                Start = start;
                Length = length;
                Movement = movement;
                PreservePreviousSpeed = preservePreviousSpeed;
                Events = events ?? Array.Empty<PathRuntimeEvent>();
            }
        }

        private enum ESource
        {
            Provider,
            Aggregate,
        }

        private readonly Segment[] _segments;
        private readonly float[] _starts;
        private readonly ESource _source;

        public IPathProvider Provider { get; }
        public int Revision { get; }
        public int DefinitionRevision { get; }
        public float TotalLength { get; }
        public int SegmentCount => _segments.Length;
        public bool IsSequence { get; }

        private PathRoute(IPathProvider provider, Segment[] segments, ESource source, bool isSequence)
        {
            Provider = provider;
            Revision = provider.Revision;
            DefinitionRevision = provider.DefinitionRevision;
            _segments = segments;
            _source = source;
            IsSequence = isSequence;
            _starts = new float[segments.Length];
            float total = 0f;
            for (int i = 0; i < segments.Length; i++)
            {
                _starts[i] = segments[i].Start;
                total += segments[i].Length;
            }
            TotalLength = total;
        }

        /// <summary>The route of a provider's own segments and movement.</summary>
        public static PathRoute FromProvider(IPathProvider provider, Segment[] segments, bool isSequence)
        {
            return new PathRoute(provider, segments, ESource.Provider, isSequence);
        }

        /// <summary>The whole provider as one segment moving with the given settings.</summary>
        public static PathRoute Aggregate(IPathProvider provider, PathMovementSettings movement)
        {
            return new PathRoute(
                provider,
                new[] { new Segment(provider, 0f, provider.PathLength, movement, false, provider.WholeRouteEvents) },
                ESource.Aggregate,
                false);
        }

        /// <summary>The same kind of route built from the provider's current revision.</summary>
        public PathRoute Refresh()
        {
            return _source == ESource.Aggregate
                ? Aggregate(Provider, _segments[0].Movement)
                : Provider.GetRoute();
        }

        public float GetLength(int segment) => _segments[segment].Length;

        public PathMovementSettings GetMovementSettings(int segment) => _segments[segment].Movement;

        public PathRuntimeEvent[] GetEvents(int segment) => _segments[segment].Events;

        public bool PreservesPreviousSpeed(int segment) => _segments[segment].PreservePreviousSpeed;

        /// <summary>Whether the segment's own path can still be sampled.</summary>
        public bool IsSegmentReady(int segment) =>
            segment >= 0 && segment < _segments.Length && _segments[segment].Sampler.IsReady;

        public Vector3 Sample(int segment, float localProgress) =>
            _segments[segment].Sampler.Sample(Mathf.Clamp01(localProgress));

        /// <summary>The point at a progress of 0..1 along the whole route.</summary>
        public Vector3 Sample(float globalProgress)
        {
            int segment = FindSegment(globalProgress);
            return Sample(segment, GetLocalProgress(segment, globalProgress));
        }

        /// <summary>The end of the last segment is exactly 1, whatever the float sums of the lengths give.</summary>
        public float GetGlobalProgress(int segment, float localProgress)
        {
            if (_segments.Length == 1 || (segment == _segments.Length - 1 && localProgress >= 1f))
                return Mathf.Clamp01(localProgress);
            return Mathf.Clamp01(
                (_starts[segment] + _segments[segment].Length * Mathf.Clamp01(localProgress)) / TotalLength);
        }

        /// <summary>A global progress of 1 is exactly the end of the last segment.</summary>
        public float GetLocalProgress(int segment, float globalProgress)
        {
            if (_segments.Length == 1 || (segment == _segments.Length - 1 && globalProgress >= 1f))
                return Mathf.Clamp01(globalProgress);
            float length = _segments[segment].Length;
            return length <= Mathf.Epsilon
                ? 0f
                : Mathf.Clamp01((Mathf.Clamp01(globalProgress) * TotalLength - _starts[segment]) / length);
        }

        /// <summary>The segment holding a global progress. Segment ends are [start, end), the last one includes 1.</summary>
        public int FindSegment(float globalProgress)
        {
            int last = _segments.Length - 1;
            if (globalProgress >= 1f)
                return last;
            float progress = Mathf.Clamp01(globalProgress);
            int segment = PathGeometryUtility.FindSegmentStartIndex(_starts, progress * TotalLength);
            // The search runs in distance, but callers hold progress: a segment start's
            // own progress (start / total) can come back a hair short of the start and
            // land at the end of the segment before. The boundary is decided in progress.
            while (segment < last && GetGlobalProgress(segment + 1, 0f) <= progress)
                segment++;
            while (segment > 0 && GetGlobalProgress(segment, 0f) > progress)
                segment--;
            return segment;
        }
    }
}
