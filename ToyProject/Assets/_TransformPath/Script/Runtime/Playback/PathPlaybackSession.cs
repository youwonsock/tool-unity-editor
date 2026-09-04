using System;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>Playback cache and request preparation for one follower.</summary>
    internal sealed class PathPlaybackSession
    {
        #region Constants

        private const float PROGRESS_EPSILON = 0.00001f;

        #endregion


        #region Inner Classes / Structs

        internal sealed class PathEventCursor
        {
            public int NextIndex;

            public void Reset(IPathEventSource source, float progress)
            {
                NextIndex = 0;
                if (source == null)
                    return;

                while (NextIndex < source.EventCount
                    && source.GetEvent(NextIndex).NormalizedTime
                        <= progress + PROGRESS_EPSILON)
                    NextIndex++;
            }

            public bool HasNext(IPathEventSource source)
            {
                return source != null && NextIndex < source.EventCount;
            }
        }

        #endregion


        #region Member Variables

        public readonly EPathPlaybackKind Kind;
        public readonly IPathProvider Provider;
        public readonly PathEventCursor EventCursor;
        public readonly PathMovementSettings? ProviderMovementSettings;

        public PathSequenceSnapshot Snapshot { get; set; }
        public int ProviderRevision { get; set; }
        public PathMovementSettings MovementSettings { get; }

        #endregion


        #region Properties

        public IPathSequenceProvider Sequence => Kind == EPathPlaybackKind.Sequence
            ? Provider as IPathSequenceProvider
            : null;

        #endregion


        #region Public Methods

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
            PathMovementSettings? providerMovementSettings,
            PathSequenceSnapshot snapshot)
        {
            Kind = kind;
            Provider = provider;
            MovementSettings = movementSettings;
            ProviderMovementSettings = providerMovementSettings;
            Snapshot = snapshot;
            EventCursor = new PathEventCursor();
            ProviderRevision = provider.Revision;
        }

        #endregion


        #region Private Methods

        private bool CanReuseSingle(IPathProvider provider, int providerRevision)
        {
            return Kind == EPathPlaybackKind.Single
                && ReferenceEquals(Provider, provider)
                && ProviderRevision == providerRevision
                && ProviderMovementSettings.HasValue;
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
            if (provider == null || request.Provider is IPathSequenceProvider)
                throw new InvalidOperationException(
                    "Single playback requires a non-sequence IPathMovementProvider.");

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
                settings,
                null);
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

            PathMovementSettings? providerSettings = null;
            IPathMovementProvider movementProvider =
                request.Provider as IPathMovementProvider;
            if (movementProvider != null)
            {
                PathMovementSettings clonedProviderSettings =
                    PathMovementSettingsUtility.Clone(
                        movementProvider.MovementSettings);
                PathMovementSettingsUtility.Validate(
                    clonedProviderSettings,
                    nameof(request.Provider));
                providerSettings = clonedProviderSettings;
            }

            return new PathPlaybackSession(
                EPathPlaybackKind.Aggregate,
                request.Provider,
                settings,
                providerSettings,
                null);
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

            if (!PathSequenceSnapshot.TryCreate(
                    provider,
                    out PathSequenceSnapshot snapshot,
                    out string error))
                throw new InvalidOperationException(error);

            return new PathPlaybackSession(
                EPathPlaybackKind.Sequence,
                provider,
                default(PathMovementSettings),
                null,
                snapshot);
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
