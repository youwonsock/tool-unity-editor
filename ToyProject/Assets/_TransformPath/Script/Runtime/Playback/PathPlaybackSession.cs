using System;
using UnityEngine;

namespace Common.TransformPath
{
    internal enum EPathSnapshotRefreshDecision
    {
        Unchanged,
        Refresh,
        StopInvalidProvider,
        StopMovementChanged,
        StopEventsChanged,
        StopSequenceChanged,
    }


    /// <summary>Playback cache and request preparation for one follower.</summary>
    internal sealed class PathPlaybackSession
    {
        #region Constants

        private const float PROGRESS_EPSILON = 0.00001f;

        #endregion


        #region Member Variables

        public readonly EPathPlaybackKind Kind;
        public readonly IPathProvider Provider;
        public int NextEventIndex { get; set; }
        public PathEventSourceSnapshot EventSource { get; set; }

        public PathSequenceSnapshot Snapshot { get; set; }
        public int ProviderRevision { get; set; }
        public PathMovementSettings MovementSettings { get; }

        #endregion


        #region Properties

        public IPathSequenceProvider Sequence => Kind == EPathPlaybackKind.Sequence
            ? Provider as IPathSequenceProvider
            : null;

        public bool IsSequence => Kind == EPathPlaybackKind.Sequence;

        // A route is the segments of a sequence, or the whole provider as one segment.
        public int SegmentCount => IsSequence ? Snapshot.Count : 1;

        public float GetLength(int segment) => IsSequence ? Snapshot.GetLength(segment) : Provider.PathLength;

        public IPathEventSource GetEventSource(int segment) => IsSequence ? Snapshot.GetEventSource(segment) : EventSource;

        public Vector3 Sample(int segment, float progress) => IsSequence ? Snapshot.Sample(segment, progress) : Provider.Sample(progress);

        public float GetGlobalProgress(int segment, float progress) => IsSequence ? Snapshot.GetGlobalProgress(segment, progress) : progress;

        public float GetLocalProgress(int segment, float globalProgress) => IsSequence ? Snapshot.GetLocalProgress(segment, globalProgress) : Mathf.Clamp01(globalProgress);

        public int FindSegment(float globalProgress) => IsSequence ? Snapshot.FindSegment(globalProgress) : 0;

        public PathMovementSettings GetMovementSettings(int segment) => IsSequence ? Snapshot.GetDescriptor(segment).MovementSettings : MovementSettings;

        public bool PreservesPreviousSpeed(int segment) => IsSequence && Snapshot.GetDescriptor(segment).PreservePreviousSpeed;

        #endregion


        #region Public Methods

        // Playback that reaches the start of a path or segment by itself still
        // has every event of it ahead, including one exactly at the start.
        public void RestartEventCursor()
        {
            NextEventIndex = 0;
        }

        // A Seek skips the target position and everything before it.
        public void ResetEventCursor(IPathEventSource source, float progress)
        {
            NextEventIndex = 0;
            if (source == null)
                return;
            while (NextEventIndex < source.EventCount
                && source.GetEvent(NextEventIndex).NormalizedTime <= progress + PROGRESS_EPSILON)
                NextEventIndex++;
        }

        public EPathSnapshotRefreshDecision EvaluateRefresh(
            out PathSequenceSnapshot nextSequence,
            out PathEventSourceSnapshot nextEvents)
        {
            nextSequence = null;
            nextEvents = null;
            if (!Provider.IsReady)
                return EPathSnapshotRefreshDecision.StopInvalidProvider;
            if (Provider.Revision == ProviderRevision)
                return EPathSnapshotRefreshDecision.Unchanged;

            if (Sequence != null)
            {
                if (Sequence.SegmentCount != Snapshot.Count
                    || !PathSequenceSnapshot.TryGetCurrent(Sequence, out nextSequence, out _)
                    || !nextSequence.HasSamePlaybackDefinition(Snapshot))
                    return EPathSnapshotRefreshDecision.StopSequenceChanged;
                return EPathSnapshotRefreshDecision.Refresh;
            }

            if (Kind == EPathPlaybackKind.Single)
            {
                if (!(Provider is IPathMovementProvider movementProvider)
                    || !PathMovementSettingsUtility.AreSame(
                        MovementSettings, movementProvider.MovementSettings))
                    return EPathSnapshotRefreshDecision.StopMovementChanged;
            }

            // Compare the same stable ordering used when playback was prepared.
            // Return this candidate so a successful refresh does not copy it twice.
            nextEvents = PathEventSourceSnapshot.Create(Provider as IPathEventSource);
            if (!EventSource.HasSameContent(nextEvents))
                return EPathSnapshotRefreshDecision.StopEventsChanged;
            return EPathSnapshotRefreshDecision.Refresh;
        }

        public void ApplyRefresh(PathSequenceSnapshot nextSequence, PathEventSourceSnapshot nextEvents)
        {
            ProviderRevision = Provider.Revision;
            if (IsSequence)
                Snapshot = nextSequence;
            else
                EventSource = nextEvents;
        }

        internal static PathPlaybackSession CreateOrReuse(
            PathPlaybackRequest request,
            PathPlaybackSession current)
        {
            switch (request.Kind)
            {
                case EPathPlaybackKind.Single:
                    return CreateSingle(request, current);
                case EPathPlaybackKind.Aggregate:
                    return CreateAggregate(request, current);
                case EPathPlaybackKind.Sequence:
                    return CreateSequence(request, current);
                default:
                    throw new InvalidOperationException(
                        "Path playback request has an unsupported kind.");
            }
        }

        private PathPlaybackSession(
            EPathPlaybackKind kind,
            IPathProvider provider,
            PathMovementSettings movementSettings,
            PathSequenceSnapshot snapshot,
            PathEventSourceSnapshot eventSource)
        {
            Kind = kind;
            Provider = provider;
            MovementSettings = movementSettings;
            Snapshot = snapshot;
            EventSource = eventSource;
            ProviderRevision = provider.Revision;
        }

        #endregion


        #region Private Methods

        private bool CanReuseSingle(IPathProvider provider, int providerRevision)
        {
            return Kind == EPathPlaybackKind.Single
                && ReferenceEquals(Provider, provider)
                && ProviderRevision == providerRevision;
        }

        private bool CanReuseAggregate(
            IPathProvider provider,
            int providerRevision,
            PathMovementSettings movementSettings)
        {
            return Kind == EPathPlaybackKind.Aggregate
                && ReferenceEquals(Provider, provider)
                && ProviderRevision == providerRevision
                && PathMovementSettingsUtility.AreSame(
                    MovementSettings,
                    movementSettings);
        }

        private bool CanReuseSequence(
            IPathSequenceProvider provider,
            int providerRevision)
        {
            return Kind == EPathPlaybackKind.Sequence
                && ReferenceEquals(Provider, provider)
                && ProviderRevision == providerRevision
                && Snapshot != null;
        }

        private static PathPlaybackSession CreateSingle(
            PathPlaybackRequest request,
            PathPlaybackSession current)
        {
            IPathMovementProvider provider = request.Provider as IPathMovementProvider;
            if (provider == null)
                throw new InvalidOperationException(
                    "Single playback requires an IPathMovementProvider.");

            ValidateProvider(provider);
            if (current != null
                && current.CanReuseSingle(provider, provider.Revision))
                return current;

            PathMovementSettings settings = PathMovementSettingsUtility.Clone(
                provider.MovementSettings);
            PathMovementSettingsUtility.Validate(settings, nameof(provider));
            return new PathPlaybackSession(
                EPathPlaybackKind.Single,
                provider,
                settings,
                null,
                PathEventSourceSnapshot.Create(provider as IPathEventSource));
        }

        private static PathPlaybackSession CreateAggregate(
            PathPlaybackRequest request,
            PathPlaybackSession current)
        {
            ValidateProvider(request.Provider);
            PathMovementSettings overrideSettings = request.MovementOverride;
            if (current != null
                && current.CanReuseAggregate(
                    request.Provider,
                    request.Provider.Revision,
                    overrideSettings))
                return current;

            PathMovementSettings settings = PathMovementSettingsUtility.Clone(
                overrideSettings);
            PathMovementSettingsUtility.Validate(settings, nameof(request));

            return new PathPlaybackSession(
                EPathPlaybackKind.Aggregate,
                request.Provider,
                settings,
                null,
                PathEventSourceSnapshot.Create(request.Provider as IPathEventSource));
        }

        private static PathPlaybackSession CreateSequence(
            PathPlaybackRequest request,
            PathPlaybackSession current)
        {
            IPathSequenceProvider provider = request.Provider as IPathSequenceProvider;
            if (provider == null)
                throw new InvalidOperationException(
                    "Sequence playback requires an IPathSequenceProvider.");

            ValidateProvider(provider);
            if (current != null
                && current.CanReuseSequence(provider, provider.Revision))
                return current;

            if (!PathSequenceSnapshot.TryGetCurrent(
                    provider,
                    out PathSequenceSnapshot snapshot,
                    out string error))
                throw new InvalidOperationException(error);

            return new PathPlaybackSession(
                EPathPlaybackKind.Sequence,
                provider,
                default(PathMovementSettings),
                snapshot,
                null);
        }

        private static void ValidateProvider(IPathProvider provider)
        {
            if (provider == null)
                throw new ArgumentNullException(nameof(provider));
            if (!PathProviderUtility.TryValidateReady(provider, out string error))
                throw new InvalidOperationException(error);
        }


        #endregion
    }
}
