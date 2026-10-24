using System;

namespace Common.TransformPath
{
    internal enum EPathPlaybackKind
    {
        Single,
        Aggregate,
        Sequence,
    }

    /// <summary>
    /// Runtime-only description of one playback request. The request is not
    /// serialized; authoring data remains on the referenced provider.
    /// </summary>
    public readonly struct PathPlaybackRequest
    {
        private readonly IPathProvider _provider;
        private readonly bool _loop;
        private readonly EPathPlaybackKind _kind;
        private readonly PathMovementSettings _movementOverride;

        public IPathProvider Provider => _provider;
        public bool Loop => _loop;

        internal EPathPlaybackKind Kind => _kind;
        internal PathMovementSettings MovementOverride => _movementOverride;

        private PathPlaybackRequest(
            EPathPlaybackKind kind,
            IPathProvider provider,
            bool loop,
            PathMovementSettings movementOverride)
        {
            _kind = kind;
            _provider = provider;
            _loop = loop;
            _movementOverride = movementOverride;
        }

        public static PathPlaybackRequest Single(
            IPathMovementProvider provider,
            bool loop = false)
        {
            return new PathPlaybackRequest(
                EPathPlaybackKind.Single,
                provider,
                loop,
                default(PathMovementSettings));
        }

        public static PathPlaybackRequest Aggregate(
            IPathProvider provider,
            PathMovementSettings movement,
            bool loop = false)
        {
            return new PathPlaybackRequest(
                EPathPlaybackKind.Aggregate,
                provider,
                loop,
                movement);
        }

        public static PathPlaybackRequest Sequence(
            IPathSequenceProvider provider,
            bool loop = false)
        {
            return new PathPlaybackRequest(
                EPathPlaybackKind.Sequence,
                provider,
                loop,
                default(PathMovementSettings));
        }
    }

}
