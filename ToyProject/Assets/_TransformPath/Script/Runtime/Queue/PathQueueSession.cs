using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// Owns one queue's registration, constraint attachment, and hold/restart
    /// state. Releases retain the old token across reentrant callbacks.
    /// </summary>
    internal sealed class PathQueueSession
    {
        public bool ManualBlock { get; set; }
        public bool IsBlocked { get; set; }
        public float SpeedMultiplier { get; set; } = 1f;
        public float ResumeDelayRemaining { get; set; }

        public void Step(float deltaTime, bool hasState, PathQueueState state,
            float spacing, float resumeDelay, float hysteresis, float smoothRate)
        {
            bool managerBlocked = !hasState || state.IsBlocked;
            float target = hasState ? Mathf.Clamp01(state.SpeedMultiplier) : 0f;
            if (ManualBlock || managerBlocked)
            {
                ResumeDelayRemaining = resumeDelay;
                IsBlocked = true;
                target = 0f;
            }
            else
            {
                ResumeDelayRemaining = Mathf.Max(0f, ResumeDelayRemaining - deltaTime);
                bool enoughDistance = !state.DistanceToAhead.HasValue
                    || state.DistanceToAhead.Value >= spacing + hysteresis;
                if (IsBlocked && (ResumeDelayRemaining > 0f || !enoughDistance))
                    target = 0f;
                else
                    IsBlocked = false;
            }

            smoothRate = Mathf.Max(0f, smoothRate);
            SpeedMultiplier = smoothRate <= 0f
                ? target
                : Mathf.Lerp(SpeedMultiplier, target, Mathf.Clamp01(deltaTime * smoothRate));
            if (Mathf.Abs(SpeedMultiplier - target) <= 0.0001f)
                SpeedMultiplier = target;
        }

        private QueuedPathManager _manager;
        private IPathConstraintTarget _constraintTarget;
        public PathQueueRegistration Registration { get; private set; }
        public bool IsRegistered => Registration.IsValid;

        public bool TryAcquire(QueuedPathManager manager, IQueuedPathAgent agent,
            IPathConstraintTarget constraintTarget)
        {
            _manager = manager;
            _constraintTarget = constraintTarget;
            Registration = manager.Register(agent);
            if (constraintTarget != null && constraintTarget.AttachQueueConstraint(Registration))
                return true;
            Release(EPathQueueDetachReason.Explicit);
            return false;
        }

        public void Release(EPathQueueDetachReason reason)
        {
            if (!IsRegistered) return;
            PathQueueRegistration token = Registration;
            IPathConstraintTarget target = _constraintTarget;
            try
            {
                if (_manager != null) _manager.Unregister(token, reason);
            }
            finally
            {
                target?.ReleaseQueueConstraint(token);
                Forget(token);
            }
        }

        public void Forget(PathQueueRegistration token)
        {
            if (!Registration.Matches(token)) return;
            Registration = default(PathQueueRegistration);
            _manager = null;
            _constraintTarget = null;
        }

    }
}
