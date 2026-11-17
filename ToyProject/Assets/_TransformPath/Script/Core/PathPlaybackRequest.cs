using System;

namespace Common.TransformPath
{
    /// <summary>
    /// What to play: one path with its own movement, a sequence with each
    /// segment's movement, or any provider as a whole with given settings.
    /// </summary>
    public readonly struct PathPlaybackRequest
    {
        private enum EKind
        {
            None,
            Provider,
            Aggregate,
        }

        private readonly EKind _kind;
        private readonly PathMovementSettings _movement;

        public PathProviderBehaviour Provider { get; }
        public bool Loop { get; }

        private PathPlaybackRequest(EKind kind, PathProviderBehaviour provider, PathMovementSettings movement, bool loop)
        {
            _kind = kind;
            Provider = provider;
            _movement = movement;
            Loop = loop;
        }

        public static PathPlaybackRequest Single(PathData path, bool loop = false)
        {
            return new PathPlaybackRequest(EKind.Provider, path, default(PathMovementSettings), loop);
        }

        public static PathPlaybackRequest Sequence(MultiPathData sequence, bool loop = false)
        {
            return new PathPlaybackRequest(EKind.Provider, sequence, default(PathMovementSettings), loop);
        }

        /// <summary>Plays a path or a sequence as one path with these settings, ignoring its own movement and events.</summary>
        public static PathPlaybackRequest Aggregate(PathProviderBehaviour provider, PathMovementSettings movement, bool loop = false)
        {
            return new PathPlaybackRequest(EKind.Aggregate, provider, movement, loop);
        }

        /// <summary>Validates the request and returns the route to play. Throws before anything changes.</summary>
        internal PathRoute CreateRoute()
        {
            if (Provider != null)
                Provider.EnsureBuilt();
            PathProviderUtility.ThrowIfNotReady(Provider, nameof(Provider));
            switch (_kind)
            {
                case EKind.Provider:
                    return Provider.GetRoute();
                case EKind.Aggregate:
                    _movement.Validate("movement");
                    return PathRoute.Aggregate(Provider, _movement);
                default:
                    throw new InvalidOperationException("The playback request is empty.");
            }
        }
    }
}
