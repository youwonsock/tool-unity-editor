using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// What the queue currently allows one follower. Without a registration it
    /// allows everything; a new registration blocks until its first result.
    /// </summary>
    internal sealed class PathQueueConstraintState
    {
        private PathQueueRegistration _registration;
        private int _frameId = -1;

        public bool IsBlocked { get; private set; }
        public float SpeedMultiplier { get; private set; } = 1f;
        public float MaxGlobalProgress { get; private set; } = 1f;
        public bool HasRegistration => _registration.IsValid;
        public PathQueueRegistration Registration => _registration;

        public void Attach(PathQueueRegistration registration)
        {
            _registration = registration;
            WaitForNextResult();
        }

        // A Seek keeps the ownership but must not use limits from before it.
        public void WaitForNextResult()
        {
            _frameId = -1;
            Set(true, 0f, 0f);
        }

        public void Apply(PathQueueRegistration registration, PathQueueConstraint constraint, int routeRevision)
        {
            if (!_registration.IsValid
                || !registration.Matches(_registration)
                || !constraint.Registration.Matches(registration))
                return;
            // Registration identifies ownership for the whole playback. Geometry
            // revisions keep that ownership but must use freshly calculated limits,
            // including the blocked result sent before our snapshot refreshes.
            if (constraint.RouteRevision != routeRevision || constraint.FrameId < _frameId)
                return;

            _frameId = constraint.FrameId;
            Set(constraint.IsBlocked, constraint.SpeedMultiplier, constraint.MaxGlobalNormalizedTime);
        }

        public void Release(PathQueueRegistration registration)
        {
            if (_registration.IsValid && registration.Matches(_registration))
                Clear();
        }

        public void Clear()
        {
            _registration = default(PathQueueRegistration);
            _frameId = -1;
            Set(false, 1f, 1f);
        }

        private void Set(bool blocked, float speedMultiplier, float maxGlobalProgress)
        {
            IsBlocked = blocked;
            SpeedMultiplier = Mathf.Clamp01(speedMultiplier);
            MaxGlobalProgress = Mathf.Clamp01(maxGlobalProgress);
        }
    }
}
