namespace Common.TransformPath
{
    public readonly struct PathQueueRegistration
    {
        private readonly object _owner;
        public ulong RegistrationId { get; }
        public ulong PlaybackId { get; }
        // Revision at acquisition; ownership survives geometry-only changes.
        // Each PathQueueConstraint carries the revision of its own calculation.
        public int RouteRevision { get; }

        internal object Owner => _owner;

        internal PathQueueRegistration(
            object owner,
            ulong registrationId,
            ulong playbackId,
            int routeRevision)
        {
            _owner = owner;
            RegistrationId = registrationId;
            PlaybackId = playbackId;
            RouteRevision = routeRevision;
        }

        public bool IsValid => RegistrationId != 0;

        internal bool Matches(PathQueueRegistration other)
        {
            return IsValid && other.IsValid && BelongsTo(other.Owner)
                && RegistrationId == other.RegistrationId && PlaybackId == other.PlaybackId;
        }

        internal bool BelongsTo(object owner)
        {
            return _owner != null
                && owner != null
                && ReferenceEquals(_owner, owner);
        }
    }

    public enum EPathQueueDetachReason
    {
        Explicit,
        Paused,
        Stopped,
        Completed,
        ProviderInvalid,
        ManagerReleased,
        Replaced,
        ConstraintViolation,
    }

    public readonly struct PathQueueConstraint
    {
        public PathQueueRegistration Registration { get; }
        public bool IsBlocked { get; }
        public float SpeedMultiplier { get; }
        public float MaxGlobalNormalizedTime { get; }
        public int FrameId { get; }
        public int RouteRevision { get; }

        public PathQueueConstraint(
            PathQueueRegistration registration,
            bool isBlocked,
            float speedMultiplier,
            float maxGlobalNormalizedTime,
            int frameId,
            int routeRevision)
        {
            Registration = registration;
            IsBlocked = isBlocked;
            SpeedMultiplier = speedMultiplier;
            MaxGlobalNormalizedTime = maxGlobalNormalizedTime;
            FrameId = frameId;
            RouteRevision = routeRevision;
        }
    }

    public readonly struct PathQueueState
    {
        public IQueuedPathAgent Ahead { get; }
        public float? DistanceToAhead { get; }
        public bool IsBlocked { get; }
        public float SpeedMultiplier { get; }
        public float MaxGlobalNormalizedTime { get; }
        public int RouteRevision { get; }
        public ulong PlaybackId { get; }
        public int FrameId { get; }

        public PathQueueState(
            IQueuedPathAgent ahead,
            float? distanceToAhead,
            bool isBlocked,
            float speedMultiplier,
            float maxGlobalNormalizedTime,
            int routeRevision,
            ulong playbackId,
            int frameId)
        {
            Ahead = ahead;
            DistanceToAhead = distanceToAhead;
            IsBlocked = isBlocked;
            SpeedMultiplier = speedMultiplier;
            MaxGlobalNormalizedTime = maxGlobalNormalizedTime;
            RouteRevision = routeRevision;
            PlaybackId = playbackId;
            FrameId = frameId;
        }
    }

    public interface IPathConstraintTarget
    {
        bool AttachQueueConstraint(PathQueueRegistration registration);
        void ApplyQueueConstraint(
            PathQueueRegistration registration,
            PathQueueConstraint constraint);
        void ReleaseQueueConstraint(PathQueueRegistration registration);
    }

    public interface IQueuedPathAgent
    {
        ulong PlaybackId { get; }
        IPathProvider QueueProvider { get; }
        bool IsMoving { get; }
        float GlobalNormalizedTime { get; }
        int SnapshotRevision { get; }
        PathQueueAgentSettings QueueSettings { get; }
        void ApplyQueueState(PathQueueRegistration registration, PathQueueState state);
        void OnQueueDetached(PathQueueRegistration registration, EPathQueueDetachReason reason);
    }
}
