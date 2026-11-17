using System;
using UnityEngine;

namespace Common.TransformPath
{
    public enum EPathQueueLeaveReason
    {
        /// <summary>The member left the queue itself and kept playing.</summary>
        Explicit,
        /// <summary>The follower's playback was stopped.</summary>
        Stopped,
        /// <summary>The follower reached the end of the route.</summary>
        Completed,
        /// <summary>The route is not ready, or the follower plays another route.</summary>
        ProviderInvalid,
        /// <summary>The manager was disabled, changed its route, or its route's segments changed.</summary>
        ManagerReleased,
    }

    /// <summary>
    /// Queue settings and waiting state of one <see cref="PathFollower"/>. Play
    /// through <see cref="StartQueuedPlayback(QueuedPathManager, PathPlaybackRequest, float)"/>
    /// and control the playback on the follower. The manager computes the
    /// spacing; this component eases its result and limits the follower.
    /// </summary>
    [RequireComponent(typeof(PathFollower))]
    public sealed class PathQueueMember : MonoBehaviour
    {
        [Header("Components")]
        [SerializeField] private PathFollower _pathFollower;
        [SerializeField] private QueuedPathManager _manager;

        [Header("Queue")]
        [SerializeField, Min(0f)] private float _actorSpacing = PathConstants.DEFAULT_QUEUE_SPACING;
        [SerializeField] private bool _useManagerSpacing = true;
        [SerializeField] private bool _enableGradualSlowdown = true;
        [SerializeField] private bool _enableOvertakeProtection = true;
        [SerializeField, Min(0f)] private float _resumeMovementDelay = PathConstants.DEFAULT_RESUME_DELAY;
        [SerializeField, Min(0f)] private float _unblockHysteresis = PathConstants.DEFAULT_UNBLOCK_HYSTERESIS;
        [SerializeField, Min(0f)] private float _speedSmoothRate = PathConstants.DEFAULT_SPEED_SMOOTH_RATE;

        private QueuedPathManager _activeManager;
        private PathQueueWait _wait = PathQueueWait.Free;
        private PathQueueStatus _status;
        private bool _hasStatus;
        private float _requestedMaxProgress = 1f;
        private ulong _playbackId;
        private int _seekCount;
        private bool _showingPause;
        private bool _reportedBlocked;
        private float _reportedSpeedMultiplier = 1f;
        private readonly PathListeners _blocked = new PathListeners();
        private readonly PathListeners _resumed = new PathListeners();
        private readonly PathListeners _speedChanged = new PathListeners();
        private readonly PathListeners _invalidated = new PathListeners();

        public PathFollower Follower
        {
            get
            {
                if (_pathFollower == null)
                    TryGetComponent(out _pathFollower);
                return _pathFollower;
            }
        }

        /// <summary>The manager <see cref="StartQueuedPlayback(PathPlaybackRequest)"/> uses.</summary>
        public QueuedPathManager Manager
        {
            get => _manager;
            set => _manager = value;
        }

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

        /// <summary>The member moves again, or left the queue while waiting.</summary>
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

        /// <summary>The queue dropped the member because its route or manager went away. The playback stopped.</summary>
        public event Action<PathQueueMember, EPathQueueLeaveReason> QueueInvalidated
        {
            add => _invalidated.Add(value);
            remove => _invalidated.Remove(value);
        }

        private void Reset()
        {
            TryGetComponent(out _pathFollower);
        }

        private void Awake()
        {
            if (_manager == null)
                _manager = GetComponentInParent<QueuedPathManager>();
        }

        // A disabled member leaves quietly; its follower stops on its own.
        private void OnDisable()
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
            PathValueUtility.ThrowIfNotFinite(maxProgress, nameof(maxProgress));
            PathFollower follower = RequireFollower();
            using var operation = PathExecutionBoundary.Enter();
            if (manager.RouteProvider == null)
                manager.ConfigureRoute(request.Provider);
            if (!ReferenceEquals(manager.RouteProvider, request.Provider))
                throw new InvalidOperationException("Queue member and manager must use the same route provider instance.");

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
        internal void ApplyQueueStatus(PathQueueStatus status, bool hasStatus, float deltaTime)
        {
            PathFollower follower = Follower;
            _status = status;
            _hasStatus = hasStatus;

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
                }
                return;
            }
            if (_showingPause || follower.PlaybackId != _playbackId)
                AwaitFirstResult();
            if (follower.SeekCount != _seekCount)
            {
                // Limits from before a Seek no longer fit the new position.
                _seekCount = follower.SeekCount;
                _wait.IsBlocked = true;
                _wait.SpeedMultiplier = 0f;
            }

            _wait.Step(deltaTime, _hasStatus, _status, GetSpacing(), _resumeMovementDelay, _unblockHysteresis, _speedSmoothRate);
            ApplyLimit();
            ReportChanges();
        }

        /// <summary>The manager dropped this member.</summary>
        internal void OnRemovedByManager(EPathQueueLeaveReason reason)
        {
            _activeManager = null;
            _hasStatus = false;
            bool invalidated = reason == EPathQueueLeaveReason.ProviderInvalid
                || reason == EPathQueueLeaveReason.ManagerReleased;
            // Waiting ends without a Resumed notification. Stop and Complete speak for themselves.
            ResetWait(reason == EPathQueueLeaveReason.Completed);
            _reportedBlocked = false;
            if (invalidated)
            {
                PathFollower follower = Follower;
                if (follower != null && follower.State == EPathFollowerState.Moving)
                    follower.Stop();
                PathExecutionBoundary.PublishMemberInvalidated(_invalidated, this, reason);
            }
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

        private void RemoveFromManager()
        {
            if (_activeManager != null)
                _activeManager.Remove(this);
            _activeManager = null;
            _hasStatus = false;
        }

        // A new or resumed playback waits for the manager and the restart delay.
        private void AwaitFirstResult()
        {
            PathFollower follower = Follower;
            _playbackId = follower.PlaybackId;
            _seekCount = follower.SeekCount;
            _showingPause = false;
            _hasStatus = false;
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
            float maxProgress = _hasStatus
                ? Mathf.Min(_requestedMaxProgress, _status.MaxProgress)
                : _requestedMaxProgress;
            Follower.SetMotionLimit(new PathMotionLimit(_wait.IsBlocked, _wait.SpeedMultiplier, maxProgress));
        }

        private void ReportChanges()
        {
            if (_wait.IsBlocked != _reportedBlocked)
            {
                _reportedBlocked = _wait.IsBlocked;
                PathExecutionBoundary.PublishMember(_reportedBlocked ? _blocked : _resumed, this);
            }
            if (!Mathf.Approximately(_reportedSpeedMultiplier, _wait.SpeedMultiplier))
            {
                _reportedSpeedMultiplier = _wait.SpeedMultiplier;
                PathExecutionBoundary.PublishMemberSpeed(_speedChanged, this, _wait.SpeedMultiplier);
            }
        }

        private float GetSpacing()
        {
            if (_useManagerSpacing && _activeManager != null)
                return Mathf.Max(0f, _activeManager.DefaultSpacing);
            return Mathf.Max(0f, _actorSpacing);
        }

        #endregion
    }
}
