using System;
using UnityEngine;

namespace Common.TransformPath
{
    public enum EPathQueueLeaveReason
    {
        /// <summary>The follower's playback was stopped.</summary>
        Stopped,
        /// <summary>The follower reached the end of the route.</summary>
        Completed,
        /// <summary>The route is not ready, or the follower plays another route.</summary>
        ProviderInvalid,
        /// <summary>
        /// The manager was disabled or changed its route, or the route's movement,
        /// events or segments changed under the member's playback.
        /// </summary>
        ManagerReleased,
    }

    /// <summary>
    /// Queue settings and waiting state of one <see cref="PathFollower"/>. Play
    /// through <see cref="StartQueuedPlayback(QueuedPathManager, PathPlaybackRequest, float)"/>
    /// and control the playback on the follower. The manager computes the
    /// spacing; this component eases its result and limits the follower.
    /// </summary>
    /// <remarks>
    /// One member per follower: it uses the PathFollower on its own GameObject,
    /// so a follower can never sit in two queues.
    /// </remarks>
    [RequireComponent(typeof(PathFollower))]
    [DisallowMultipleComponent]
    public sealed class PathQueueMember : MonoBehaviour
    {
        [Header("Components")]
        [SerializeField] private QueuedPathManager _manager;

        [Header("Queue")]
        [SerializeField, Min(0f)] private float _actorSpacing = PathConstants.DEFAULT_QUEUE_SPACING;
        [SerializeField] private bool _useManagerSpacing = true;
        [SerializeField] private bool _enableGradualSlowdown = true;
        [SerializeField] private bool _enableOvertakeProtection = true;
        [SerializeField, Min(0f)] private float _resumeMovementDelay = PathConstants.DEFAULT_RESUME_DELAY;
        [SerializeField, Min(0f)] private float _unblockHysteresis = PathConstants.DEFAULT_UNBLOCK_HYSTERESIS;
        [SerializeField, Min(0f)] private float _speedSmoothRate = PathConstants.DEFAULT_SPEED_SMOOTH_RATE;

        [NonSerialized] private PathFollower _follower;
        private QueuedPathManager _activeManager;
        private PathQueueWait _wait = PathQueueWait.Free;
        private float _requestedMaxProgress = 1f;
        private int _motionEpoch;
        private bool _showingPause;
        private bool _reportedBlocked;
        private float _reportedSpeedMultiplier = 1f;
        private readonly PathSignal<PathQueueMember> _blocked = new PathSignal<PathQueueMember>();
        private readonly PathSignal<PathQueueMember> _resumed = new PathSignal<PathQueueMember>();
        private readonly PathSignal<PathQueueMember, float> _speedChanged = new PathSignal<PathQueueMember, float>();
        private readonly PathSignal<PathQueueMember, EPathQueueLeaveReason> _invalidated =
            new PathSignal<PathQueueMember, EPathQueueLeaveReason>();

        /// <summary>The PathFollower on this GameObject.</summary>
        public PathFollower Follower
        {
            get
            {
                if (_follower == null)
                    TryGetComponent(out _follower);
                return _follower;
            }
        }

        /// <summary>The manager <see cref="StartQueuedPlayback(PathPlaybackRequest)"/> uses.</summary>
        public QueuedPathManager Manager
        {
            get => _manager;
            set => _manager = value;
        }

        /// <summary>The manager whose queue this member joined, or null outside a queue.</summary>
        public QueuedPathManager ActiveManager => _activeManager;
        public bool IsInQueue => _activeManager != null && _activeManager.Contains(this);
        public bool IsBlocked => _wait.IsBlocked;
        public bool IsHeld => _wait.ManualBlock;
        public float CurrentSpeedMultiplier => _wait.SpeedMultiplier;

        public float ActorSpacing
        {
            get => _actorSpacing;
            set
            {
                PathValueUtility.ThrowIfNegative(value, nameof(value));
                _actorSpacing = value;
            }
        }

        public bool UseManagerSpacing
        {
            get => _useManagerSpacing;
            set => _useManagerSpacing = value;
        }

        public bool EnableGradualSlowdown
        {
            get => _enableGradualSlowdown;
            set => _enableGradualSlowdown = value;
        }

        public bool EnableOvertakeProtection
        {
            get => _enableOvertakeProtection;
            set => _enableOvertakeProtection = value;
        }

        /// <summary>The member started waiting, for the queue or a hold.</summary>
        public event Action<PathQueueMember> Blocked
        {
            add => _blocked.Add(value);
            remove => _blocked.Remove(value);
        }

        /// <summary>The member moves again, or <see cref="Leave"/> ended its wait.</summary>
        public event Action<PathQueueMember> Resumed
        {
            add => _resumed.Add(value);
            remove => _resumed.Remove(value);
        }

        public event Action<PathQueueMember, float> SpeedChanged
        {
            add => _speedChanged.Add(value);
            remove => _speedChanged.Remove(value);
        }

        /// <summary>
        /// The queue dropped the member because its route or manager went away.
        /// A playback of that route stopped, paused or not; a follower that
        /// already plays another route keeps playing.
        /// </summary>
        public event Action<PathQueueMember, EPathQueueLeaveReason> QueueInvalidated
        {
            add => _invalidated.Add(value);
            remove => _invalidated.Remove(value);
        }

        private void Awake()
        {
            if (_manager == null)
                _manager = GetComponentInParent<QueuedPathManager>();
        }

        // A disabled member leaves the queue quietly. Its follower keeps playing,
        // without the queue's limit and without a Resumed notification.
        private void OnDisable()
        {
            RemoveFromManager();
            ResetWait(false);
        }

        // A member destroyed while disabled never ran OnDisable again.
        private void OnDestroy()
        {
            RemoveFromManager();
            ResetWait(false);
        }

        #region Commands

        /// <summary>Plays in the Inspector's manager.</summary>
        public bool StartQueuedPlayback(PathPlaybackRequest request) => StartQueuedPlayback(_manager, request);

        /// <summary>
        /// Plays the request and joins the manager. The manager and the request
        /// use the same provider instance. With a max progress below 1 the
        /// member stops there and does not complete.
        /// </summary>
        public bool StartQueuedPlayback(QueuedPathManager manager, PathPlaybackRequest request, float maxProgress = 1f)
        {
            if (manager == null || !manager.isActiveAndEnabled || !manager.IsInitialized)
                throw new InvalidOperationException("QueuedPathManager must be initialized before starting a queued move.");
            // A disabled member would never hear the queue or leave it.
            if (!isActiveAndEnabled)
                throw new InvalidOperationException($"PathQueueMember '{name}' must be active and enabled to join a queue.");
            ThrowIfQueueCalculating(manager);
            PathValueUtility.ThrowIfNotFinite(maxProgress, nameof(maxProgress));
            PathFollower follower = RequireFollower();
            // A disabled follower never moves, so it would hold the members behind it for good.
            if (!follower.isActiveAndEnabled)
                throw new InvalidOperationException($"PathQueueMember '{name}' needs an enabled PathFollower to join a queue.");
            using var operation = PathExecutionBoundary.Enter();
            // Validate before configuring or leaving, so an invalid request changes nothing.
            request.CreateRoute();
            PathProviderBehaviour route = manager.RouteForStart(request.Provider);
            if (!ReferenceEquals(route, request.Provider))
                throw new InvalidOperationException("Queue member and manager must use the same route provider instance.");
            if (!ReferenceEquals(manager.RouteProvider, route))
                manager.ConfigureRoute(route);

            if (!ReferenceEquals(_activeManager, manager))
                RemoveFromManager();
            bool changed = follower.Play(request);
            _activeManager = manager;
            _requestedMaxProgress = Mathf.Clamp01(maxProgress);
            manager.Join(this);
            AwaitFirstResult();
            return changed;
        }

        /// <summary>Leaves the queue and plays the request on its own.</summary>
        public bool StartFreePlayback(PathPlaybackRequest request)
        {
            ThrowIfQueueCalculating(null);
            PathFollower follower = RequireFollower();
            using var operation = PathExecutionBoundary.Enter();
            // Validate before leaving, so an invalid request changes nothing.
            request.CreateRoute();
            Leave();
            return follower.Play(request);
        }

        /// <summary>Leaves the queue and keeps playing. Listeners hear the wait end.</summary>
        public void Leave()
        {
            ThrowIfQueueCalculating(null);
            using var operation = PathExecutionBoundary.Enter();
            RemoveFromManager();
            _requestedMaxProgress = 1f;
            ResetWait(true);
            ReportChanges();
        }

        /// <summary>Waits in place while staying in the queue. Outside a queue it waits from the next queued start.</summary>
        public void Hold()
        {
            _wait.ManualBlock = true;
            if (!IsInQueue)
                return;
            _wait.ResumeDelayRemaining = _resumeMovementDelay;
            _wait.IsBlocked = true;
            _wait.SpeedMultiplier = 0f;
            ApplyLimit();
        }

        public void ReleaseHold()
        {
            _wait.ManualBlock = false;
            _wait.ResumeDelayRemaining = _resumeMovementDelay;
            ApplyLimit();
        }

        #endregion

        #region Manager

        /// <summary>The manager's result for this frame. Runs before the follower moves.</summary>
        internal void ApplyQueueStatus(PathQueueStatus status, float deltaTime)
        {
            PathFollower follower = Follower;

            // A paused member stays in line for those behind it but does not
            // count down its own wait. It waits for a first result after it resumes.
            if (follower.State == EPathFollowerState.Paused)
            {
                if (!_showingPause)
                {
                    _showingPause = true;
                    _wait.IsBlocked = true;
                    _wait.SpeedMultiplier = 0f;
                    ApplyLimit();
                    // IsBlocked is true now, so listeners hear Blocked too.
                    ReportChanges();
                }
                return;
            }
            // A jump (new playback, Seek, loop seam) or a resume waits as a new start would.
            if (follower.MotionEpoch != _motionEpoch || _showingPause)
                AwaitFirstResult();

            _wait.Step(deltaTime, status, status.Spacing,
                _resumeMovementDelay, _unblockHysteresis, _speedSmoothRate);
            ApplyLimit();
            ReportChanges();
        }

        /// <summary>The manager dropped this member from the queue of this route.</summary>
        internal void OnRemovedByManager(EPathQueueLeaveReason reason, PathProviderBehaviour route)
        {
            _activeManager = null;
            bool invalidated = reason == EPathQueueLeaveReason.ProviderInvalid
                || reason == EPathQueueLeaveReason.ManagerReleased;
            // Waiting ends without a Resumed notification. Stop and Complete speak for themselves.
            ResetWait(reason == EPathQueueLeaveReason.Completed);
            _reportedBlocked = false;
            if (!invalidated)
                return;
            PathFollower follower = Follower;
            if (follower != null && follower.PlaybackId != 0 && ReferenceEquals(follower.CurrentProvider, route))
                follower.Stop();
            _invalidated.Publish(this, reason);
        }

        #endregion

        #region Private Methods

        private PathFollower RequireFollower()
        {
            PathFollower follower = Follower;
            if (follower == null)
                throw new InvalidOperationException("PathQueueMember requires a PathFollower.");
            return follower;
        }

        // A spacing policy may not change the line it is asked about.
        private void ThrowIfQueueCalculating(QueuedPathManager manager)
        {
            if (_activeManager != null)
                _activeManager.ThrowIfCalculating();
            if (manager != null)
                manager.ThrowIfCalculating();
        }

        private void RemoveFromManager()
        {
            if (_activeManager != null)
                _activeManager.Remove(this);
            _activeManager = null;
        }

        // A new start waits for the manager's next result and the restart delay.
        private void AwaitFirstResult()
        {
            _motionEpoch = Follower.MotionEpoch;
            _showingPause = false;
            _wait.IsBlocked = true;
            _wait.ResumeDelayRemaining = _resumeMovementDelay;
            _wait.SpeedMultiplier = 0f;
            ApplyLimit();
        }

        private void ResetWait(bool clearHold)
        {
            bool hold = !clearHold && _wait.ManualBlock;
            _wait = PathQueueWait.Free;
            _wait.ManualBlock = hold;
            _showingPause = false;
            Follower?.SetMotionLimit(PathMotionLimit.None);
        }

        private void ApplyLimit()
        {
            if (!IsInQueue)
                return;
            float maxProgress = _activeManager.TryGetMemberStatus(this, out PathQueueStatus status)
                ? Mathf.Min(_requestedMaxProgress, status.MaxProgress)
                : _requestedMaxProgress;
            Follower.SetMotionLimit(new PathMotionLimit(_wait.IsBlocked, _wait.SpeedMultiplier, maxProgress, true));
        }

        private void ReportChanges()
        {
            if (_wait.IsBlocked != _reportedBlocked)
            {
                _reportedBlocked = _wait.IsBlocked;
                (_reportedBlocked ? _blocked : _resumed).Publish(this);
            }
            if (!Mathf.Approximately(_reportedSpeedMultiplier, _wait.SpeedMultiplier))
            {
                _reportedSpeedMultiplier = _wait.SpeedMultiplier;
                _speedChanged.Publish(this, _wait.SpeedMultiplier);
            }
        }

        #endregion
    }
}
