namespace Common.TransformPath
{
    public readonly struct PathQueueAgentSettings
    {
        public float ActorSpacing { get; }
        public bool UseManagerSpacing { get; }
        public bool EnableGradualSlowdown { get; }
        public bool EnableOvertakeProtection { get; }

        public PathQueueAgentSettings(
            float actorSpacing,
            bool useManagerSpacing,
            bool enableGradualSlowdown,
            bool enableOvertakeProtection)
        {
            ActorSpacing = actorSpacing;
            UseManagerSpacing = useManagerSpacing;
            EnableGradualSlowdown = enableGradualSlowdown;
            EnableOvertakeProtection = enableOvertakeProtection;
        }
    }

    public readonly struct PathQueueRegistration
    {
        private readonly object _owner;
        public ulong RegistrationId { get; }
        public ulong PlaybackId { get; }
        public int RouteRevision { get; }

        internal object Owner => _owner;

        public PathQueueRegistration(
            ulong registrationId,
            ulong playbackId,
            int routeRevision)
            : this(null, registrationId, playbackId, routeRevision)
        {
        }

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
            ulong playbackId = 0,
            int frameId = 0)
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
