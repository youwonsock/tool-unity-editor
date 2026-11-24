using System;

namespace Common.TransformPath
{
    /// <summary>
    /// What to play: one path with its own movement, a sequence with each
    /// segment's movement, a shape with its own movement, or any provider as a
    /// whole with given settings.
    /// </summary>
    public readonly struct PathPlaybackRequest
    {
        // A default request is a Provider request without a provider, which CreateRoute rejects.
        private enum EKind
        {
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

        public static PathPlaybackRequest Single(PathData path, bool loop = false) => Own(path, loop);

        public static PathPlaybackRequest Sequence(MultiPathData sequence, bool loop = false) => Own(sequence, loop);

        /// <summary>Plays a shape with its own movement.</summary>
        public static PathPlaybackRequest Shape(PathShapeProvider shape, bool loop = false) => Own(shape, loop);

        /// <summary>
        /// Plays a path, a sequence or a shape as one path with these settings
        /// instead of its own movement. A PathData keeps its events; the others play without events.
        /// </summary>
        public static PathPlaybackRequest Aggregate(PathProviderBehaviour provider, PathMovementSettings movement, bool loop = false)
        {
            return new PathPlaybackRequest(EKind.Aggregate, provider, movement, loop);
        }

        /// <summary>Any provider with its own movement: a path, a sequence or a shape.</summary>
        internal static PathPlaybackRequest Own(PathProviderBehaviour provider, bool loop)
        {
            return new PathPlaybackRequest(EKind.Provider, provider, default(PathMovementSettings), loop);
        }

        /// <summary>Validates the request and returns the route to play. Throws before anything changes.</summary>
        internal PathRoute CreateRoute()
        {
            if (Provider != null)
                Provider.EnsureBuilt();
            PathProviderUtility.ThrowIfNotReady(Provider, nameof(Provider));
            if (_kind == EKind.Provider)
                return Provider.GetRoute();
            _movement.Validate("movement");
            return PathRoute.Aggregate(Provider, _movement);
        }
    }
}
