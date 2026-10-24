using System;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>PathFollower adapter that applies one manager constraint per frame.</summary>
    [DefaultExecutionOrder(-110)]
    [RequireComponent(typeof(PathFollower))]
    public sealed class QueuedPathFollower : MonoBehaviour, IQueuedPathAgent, IPathFollower, IPathPlaybackIdentity, IPathRuntimeTickable, IPathRuntimeFaultHandler
    {
        #region Constants

        private const float DEFAULT_ACTOR_SPACING = 1.5f;
        private const float DEFAULT_RESUME_MOVEMENT_DELAY = 0.1f;
        private const float DEFAULT_UNBLOCK_HYSTERESIS = 0.3f;
        private const float DEFAULT_SPEED_SMOOTH_RATE = 5f;
        private const float CONSTRAINT_VIOLATION_EPSILON = 0.00001f;

        private enum EQueueParticipationMode
        {
            Detached,
            Queued,
        }

        #endregion


        #region Member Variables

        [Header("Components")]
        [SerializeField] private PathFollower _pathFollower;
        [SerializeField] private QueuedPathManager _manager;

        [Header("Queue")]
        [SerializeField, Min(0f)] private float _actorSpacing = DEFAULT_ACTOR_SPACING;
        [SerializeField] private bool _useManagerSpacing = true;
        [SerializeField] private bool _enableGradualSlowdown = true;
        [SerializeField] private bool _enableOvertakeProtection = true;
        [SerializeField] private PathFollowerAnimatorView _animatorView;
        [SerializeField, Min(0f)] private float _resumeMovementDelay = DEFAULT_RESUME_MOVEMENT_DELAY;
        [SerializeField, Min(0f)] private float _unblockHysteresis = DEFAULT_UNBLOCK_HYSTERESIS;
        [SerializeField, Min(0f)] private float _speedSmoothRate = DEFAULT_SPEED_SMOOTH_RATE;

        private bool _isInitialized;
        private readonly PathQueueSession _queue = new PathQueueSession();
        private QueuedPathManager _activeManager;
        private EQueueParticipationMode _queueParticipationMode;
        private float _lastReportedSpeedMultiplier = 1f;
        private PathQueueState? _managerState;
        private float _requestedMaxGlobalNormalizedTime = 1f;
        private bool _runtimeDriverRegistered;
        private bool _suppressDetachStop;
        // Queue commands can change notification meaning without changing the
        // underlying follower's PlaybackId or public StateRevision.
        private ulong _notificationRevision;
        private Action<QueuedPathFollower> _onBlocked;
        private Action<QueuedPathFollower> _onResumed;
        private Action<QueuedPathFollower> _onCompleted;
        private Action<QueuedPathFollower, float> _onSpeedChanged;
        private Delegate[] _onBlockedInvocationList;
        private Delegate[] _onResumedInvocationList;
        private Delegate[] _onCompletedInvocationList;
        private Delegate[] _onSpeedChangedInvocationList;
        private Action<EPathFollowerState> _stateChanged;
        private Action<int> _segmentChanged;
        private Action _completed;
        private Action<QueuedPathFollower, EPathQueueDetachReason> _queueInvalidated;
        private Delegate[] _stateChangedInvocationList;
        private Delegate[] _segmentChangedInvocationList;
        private Delegate[] _completedInvocationList;
        private Delegate[] _queueInvalidatedInvocationList;

        #endregion


        #region Properties

        private PathFollower Source => _pathFollower == null ? null : _pathFollower;

        public bool IsInitialized => _isInitialized;
        int IPathRuntimeTickable.RuntimeOrder => -110;
        public bool IsBlocked => _queue.IsBlocked;
        public bool IsMoving => Source?.IsMoving ?? false;
        public bool IsActuallyMoving => IsMoving && !_queue.IsBlocked;
        public bool IsRegistered => _queue.IsRegistered;
        public bool IsQueueHeld => _queueParticipationMode == EQueueParticipationMode.Queued
            && _queue.IsRegistered && _queue.ManualBlock;
        public IPathFollower Playback => this;
        public PathFollower UnderlyingFollower => _pathFollower;

        public float ActorSpacing
        {
            get => _actorSpacing;
            set
            {
                if (!PathValueUtility.IsNonNegativeFinite(value))
                    throw new ArgumentOutOfRangeException(nameof(value));
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

        public float CurrentSpeedMultiplier => _queue.SpeedMultiplier;
        public ulong PlaybackId => Source?.PlaybackId ?? 0UL;
        public IPathProvider CurrentProvider => Source?.CurrentProvider;
        public IPathSequenceProvider CurrentSequence => Source?.CurrentSequence;
        public EPathFollowerState State => Source?.State ?? EPathFollowerState.Uninitialized;
        public float NormalizedTime => Source?.NormalizedTime ?? 0f;
        public int CurrentSegmentIndex => Source?.CurrentSegmentIndex ?? -1;
        public EPathMoveType MoveType => Source?.MoveType ?? EPathMoveType.TimeBased;
        public float Speed => Source?.Speed ?? 0f;
        public float Duration => Source?.Duration ?? 0f;
        public ulong StateRevision => Source?.StateRevision ?? 0UL;
        ulong IPathPlaybackIdentity.PlaybackOwnerId => _pathFollower is IPathPlaybackIdentity identity
            ? identity.PlaybackOwnerId
            : 0UL;
        public PathQueueAgentSettings QueueSettings => new PathQueueAgentSettings(
            _actorSpacing,
            _useManagerSpacing,
            _enableGradualSlowdown,
            _enableOvertakeProtection);
        public float GlobalNormalizedTime => Source?.GlobalNormalizedTime ?? 0f;
        public QueuedPathManager Manager => _manager;
        public QueuedPathManager ActiveManager => _activeManager;
        public IPathProvider QueueProvider => CurrentProvider;
        public int SnapshotRevision => Source?.SnapshotRevision ?? -1;

        public event Action<QueuedPathFollower> OnBlocked
        {
            add
            {
                _onBlocked += value;
                _onBlockedInvocationList = _onBlocked?.GetInvocationList();
            }
            remove
            {
                _onBlocked -= value;
                _onBlockedInvocationList = _onBlocked?.GetInvocationList();
            }
        }

        public event Action<QueuedPathFollower> OnResumed
        {
            add
            {
                _onResumed += value;
                _onResumedInvocationList = _onResumed?.GetInvocationList();
            }
            remove
            {
                _onResumed -= value;
                _onResumedInvocationList = _onResumed?.GetInvocationList();
            }
        }

        public event Action<QueuedPathFollower> OnCompleted
        {
            add
            {
                _onCompleted += value;
                _onCompletedInvocationList = _onCompleted?.GetInvocationList();
            }
            remove
            {
                _onCompleted -= value;
                _onCompletedInvocationList = _onCompleted?.GetInvocationList();
            }
        }

        public event Action<QueuedPathFollower, float> OnSpeedChanged
        {
            add
            {
                _onSpeedChanged += value;
                _onSpeedChangedInvocationList = _onSpeedChanged?.GetInvocationList();
            }
            remove
            {
                _onSpeedChanged -= value;
                _onSpeedChangedInvocationList = _onSpeedChanged?.GetInvocationList();
            }
        }

        public event Action<EPathFollowerState> StateChanged
        {
            add
            {
                _stateChanged += value;
                _stateChangedInvocationList = _stateChanged?.GetInvocationList();
            }
            remove
            {
                _stateChanged -= value;
                _stateChangedInvocationList = _stateChanged?.GetInvocationList();
            }
        }

        public event Action<int> SegmentChanged
        {
            add
            {
                _segmentChanged += value;
                _segmentChangedInvocationList = _segmentChanged?.GetInvocationList();
            }
            remove
            {
                _segmentChanged -= value;
                _segmentChangedInvocationList = _segmentChanged?.GetInvocationList();
            }
        }

        public event Action Completed
        {
            add
            {
                _completed += value;
                _completedInvocationList = _completed?.GetInvocationList();
            }
            remove
            {
                _completed -= value;
                _completedInvocationList = _completed?.GetInvocationList();
            }
        }

        public event Action<QueuedPathFollower, EPathQueueDetachReason> QueueInvalidated
        {
            add
            {
                _queueInvalidated += value;
                _queueInvalidatedInvocationList = _queueInvalidated?.GetInvocationList();
            }
            remove
            {
                _queueInvalidated -= value;
                _queueInvalidatedInvocationList = _queueInvalidated?.GetInvocationList();
            }
        }

        #endregion


        #region Unity Events

        public PathCommandReceipt Init()
        {
            if (_isInitialized)
                return _pathFollower == null
                    ? default(PathCommandReceipt)
                    : _pathFollower.Init();

            if (_pathFollower == null)
                TryGetComponent(out _pathFollower);
            if (_pathFollower == null)
                return default(PathCommandReceipt);
            if (_animatorView == null)
                TryGetComponent(out _animatorView);
            if (_manager == null)
                _manager = GetComponentInParent<QueuedPathManager>();

            _pathFollower.StateChanged -= HandleUnderlyingStateChanged;
            _pathFollower.StateChanged += HandleUnderlyingStateChanged;
            _pathFollower.SegmentChanged -= HandleUnderlyingSegmentChanged;
            _pathFollower.SegmentChanged += HandleUnderlyingSegmentChanged;
            _pathFollower.Completed -= HandleUnderlyingCompleted;
            _pathFollower.Completed += HandleUnderlyingCompleted;
            _pathFollower.LoopBoundary -= HandleUnderlyingLoopBoundary;
            _pathFollower.LoopBoundary += HandleUnderlyingLoopBoundary;
            PathCommandReceipt receipt = _pathFollower.Init();
            _pathFollower.SetEventTarget(this);
            _isInitialized = true;
            return receipt;
        }

        public PathCommandReceipt Release()
        {
            if (!_isInitialized && !_queue.IsRegistered)
                return default(PathCommandReceipt);

            _notificationRevision++;
            // Registered releases already stop through the detach callback.
            // Stop explicitly so its receipt can protect a replacement before
            // unsubscribing the facade or releasing the underlying follower.
            if (_queue.IsRegistered && _pathFollower != null)
            {
                PathCommandReceipt stopReceipt = _pathFollower.StopMove();
                if (PlaybackId != stopReceipt.PlaybackId)
                    return stopReceipt;
            }
            UnregisterFromManager();
            if (_pathFollower != null)
            {
                _pathFollower.StateChanged -= HandleUnderlyingStateChanged;
                _pathFollower.SegmentChanged -= HandleUnderlyingSegmentChanged;
                _pathFollower.Completed -= HandleUnderlyingCompleted;
                _pathFollower.LoopBoundary -= HandleUnderlyingLoopBoundary;
            }
            _isInitialized = false;
            _managerState = null;
            _queue.IsBlocked = false;
            _queue.SpeedMultiplier = 1f;
            return _pathFollower == null
                ? default(PathCommandReceipt)
                : _pathFollower.Release();
        }

        private void Reset()
        {
            TryGetComponent(out _pathFollower);
        }

        private void Awake()
        {
            if (Application.isPlaying)
            {
                Init();
                PathRuntimeDriverBehaviour.Instance.Register(this);
                _runtimeDriverRegistered = true;
            }
        }

        private void OnEnable()
        {
            if (!Application.isPlaying)
                return;
            if (!_isInitialized)
                Init();
            if (!_runtimeDriverRegistered)
            {
                PathRuntimeDriverBehaviour.Instance.Register(this);
                _runtimeDriverRegistered = true;
            }
        }

        private void OnDisable()
        {
            if (_runtimeDriverRegistered && PathRuntimeDriverBehaviour.HasInstance)
            {
                PathRuntimeDriverBehaviour.Instance.Unregister(this);
                _runtimeDriverRegistered = false;
            }
            UnregisterFromManager();
        }

        private void OnDestroy()
        {
            if (_runtimeDriverRegistered && PathRuntimeDriverBehaviour.HasInstance)
            {
                PathRuntimeDriverBehaviour.Instance.Unregister(this);
                _runtimeDriverRegistered = false;
            }
            Release();
        }

        void IPathRuntimeTickable.Tick(
            float deltaTime,
            float unscaledDeltaTime,
            int frameId)
        {
            Tick(deltaTime, frameId);
        }

        private void Tick(float deltaTime, int frameId)
        {
            if (!_isInitialized || _pathFollower == null || !_pathFollower.IsMoving)
                return;
            if (_queueParticipationMode == EQueueParticipationMode.Queued && !TryRegisterWithManager())
                return;

            if (_queueParticipationMode != EQueueParticipationMode.Queued || !_queue.IsRegistered)
                return;

            float effectiveMaxProgress = _managerState.HasValue
                ? Mathf.Min(
                    _requestedMaxGlobalNormalizedTime,
                    _managerState.Value.MaxGlobalNormalizedTime)
                : _requestedMaxGlobalNormalizedTime;
            if (_managerState.HasValue
                && GlobalNormalizedTime > effectiveMaxProgress + CONSTRAINT_VIOLATION_EPSILON)
            {
                InvalidateRegistration(EPathQueueDetachReason.ConstraintViolation);
                return;
            }

            bool wasBlocked = _queue.IsBlocked;
            _queue.Step(deltaTime, _managerState.HasValue, _managerState.GetValueOrDefault(),
                GetSpacing(), _resumeMovementDelay, _unblockHysteresis, _speedSmoothRate);
            ApplyCurrentConstraint();
            UpdateAnimator();
            ReportStateChanges(wasBlocked, _queue.IsBlocked);
        }

        void IPathRuntimeFaultHandler.HandleRuntimeFault(
            ulong playbackId,
            System.Exception exception)
        {
            if (_pathFollower == null || _pathFollower.PlaybackId != playbackId)
                return;
            try
            {
                StopMove();
            }
            catch
            {
                UnregisterFromManager();
            }
        }

        #endregion


        #region Public Methods

        public PathCommandReceipt StartPlayback(PathPlaybackRequest request)
        {
            return StartQueuedPlayback(_manager, request, 1f);
        }

        public PathCommandReceipt StartQueuedPlayback(
            QueuedPathManager manager,
            PathPlaybackRequest request,
            float maxGlobalNormalizedTime = 1f)
        {
            PathPlaybackSession session = PrepareRequestForManager(manager, request);
            if (manager.RouteProvider == null)
                manager.ConfigureRoute(request.Provider);
            if (!ReferenceEquals(manager.RouteProvider, request.Provider))
                throw new InvalidOperationException(
                    "Queue follower and manager must use the same route provider instance.");

            UnregisterFromManager(EPathQueueDetachReason.Replaced);
            _activeManager = manager;
            _queueParticipationMode = EQueueParticipationMode.Queued;
            _requestedMaxGlobalNormalizedTime = Mathf.Clamp01(maxGlobalNormalizedTime);

            PathCommandReceipt receipt = _pathFollower.StartPreparedPlayback(request, session);
            if (!receipt.Changed)
                return receipt;
            // Replacing an already-moving playback does not emit StateChanged.
            // Use the same registration path, only if the requested participation
            // is still current after callbacks (Pause and Detach also win).
            if (PlaybackId == receipt.PlaybackId && IsMoving
                && _queueParticipationMode == EQueueParticipationMode.Queued
                && ReferenceEquals(_activeManager, manager)
                && !TryRegisterWithManager())
                throw new InvalidOperationException(
                    "The playback could not acquire its queue constraint ownership.");
            return receipt;
        }

        public PathCommandReceipt StartFreePlayback(PathPlaybackRequest request)
        {
            if (!_isInitialized)
                Init();
            if (!_isInitialized || _pathFollower == null)
                throw new InvalidOperationException("QueuedPathFollower is not initialized.");
            PathPlaybackSession session = _pathFollower.PreparePlayback(request);
            UnregisterFromManager(EPathQueueDetachReason.Replaced);
            _queueParticipationMode = EQueueParticipationMode.Detached;
            _activeManager = null;
            _requestedMaxGlobalNormalizedTime = 1f;
            // Prepare free-playback state before notifications can run a newer
            // command. There is no old Start postprocessing after the callback.
            ResetConstraintState();
            return _pathFollower.StartPreparedPlayback(request, session);
        }

        public PathCommandReceipt StopMove()
        {
            PathQueueRegistration registration = _queue.Registration;
            PathCommandReceipt receipt = _pathFollower == null
                ? default(PathCommandReceipt)
                : _pathFollower.StopMove();
            // Ready callbacks may start another playback and acquire a new
            // registration. The old command must not reset that execution.
            if (PlaybackId != receipt.PlaybackId
                || (_queue.IsRegistered && !registration.Matches(_queue.Registration)))
                return receipt;
            UnregisterFromManager(EPathQueueDetachReason.Stopped);
            _queueParticipationMode = EQueueParticipationMode.Detached;
            _activeManager = null;
            ResetConstraintState();
            return receipt;
        }

        public PathCommandReceipt PauseMove() => Source?.PauseMove() ?? default(PathCommandReceipt);

        public PathCommandReceipt ResumeMove()
        {
            if (_pathFollower == null)
                return default(PathCommandReceipt);

            if (_pathFollower.State != EPathFollowerState.Paused)
                return default(PathCommandReceipt);
            return _pathFollower.ResumeMove();
        }

        public PathCommandReceipt Seek(float normalizedTime) =>
            InvalidateQueueCalculationAfterSeek(Source?.Seek(normalizedTime) ?? default(PathCommandReceipt));

        public PathCommandReceipt SeekSegment(int segmentIndex, float localNormalizedTime) =>
            InvalidateQueueCalculationAfterSeek(Source?.SeekSegment(segmentIndex, localNormalizedTime) ?? default(PathCommandReceipt));

        public PathCommandReceipt SetSpeed(float speed) => Source?.SetSpeed(speed) ?? default(PathCommandReceipt);

        public PathCommandReceipt SetDuration(float duration) => Source?.SetDuration(duration) ?? default(PathCommandReceipt);

        public void ForceBlock()
        {
            _notificationRevision++;
            _queue.ManualBlock = true;
            _queue.ResumeDelayRemaining = _resumeMovementDelay;
            _queue.IsBlocked = true;
            _queue.SpeedMultiplier = 0f;
            ApplyCurrentConstraint();
            UpdateAnimator();
        }

        public void ForceUnblock()
        {
            _notificationRevision++;
            _queue.ManualBlock = false;
            _queue.ResumeDelayRemaining = _resumeMovementDelay;
            ApplyCurrentConstraint();
            UpdateAnimator();
        }

        /// <summary>
        /// Leaves the active queue session while preserving the underlying
        /// playback. Explicit detaches never report navigation invalidation;
        /// callers that want a lifecycle transition should use StopMove or a
        /// new StartFreePlayback request instead.
        /// </summary>
        public void DetachQueue()
        {
            _suppressDetachStop = true;
            try
            {
                UnregisterFromManager(EPathQueueDetachReason.Explicit);
            }
            finally
            {
                _suppressDetachStop = false;
            }
            _queueParticipationMode = EQueueParticipationMode.Detached;
            _activeManager = null;
            _requestedMaxGlobalNormalizedTime = 1f;
            ResetConstraintState();
        }

        public void ApplyQueueState(
            PathQueueRegistration registration,
            PathQueueState state)
        {
            if (!_queue.Registration.IsValid
                || !registration.Matches(_queue.Registration))
                return;
            if (state.PlaybackId != PlaybackId
                || CurrentProvider == null || state.RouteRevision != CurrentProvider.Revision
                || (_managerState.HasValue && state.FrameId < _managerState.Value.FrameId))
                return;
            _notificationRevision++;
            _managerState = state;
            ApplyCurrentConstraint();
        }

        public void OnQueueDetached(
            PathQueueRegistration registration,
            EPathQueueDetachReason reason)
        {
            if (!_queue.Registration.IsValid
                || !registration.Matches(_queue.Registration))
                return;

            _notificationRevision++;
            _queue.Forget(registration);
            if (_pathFollower is IPathConstraintTarget constraintTarget)
                constraintTarget.ReleaseQueueConstraint(registration);
            _managerState = null;
            _queue.IsBlocked = false;
            HandleDetachedPlayback(reason);
        }

        private void HandleDetachedPlayback(EPathQueueDetachReason reason)
        {
            bool notifyInvalidation = reason == EPathQueueDetachReason.ProviderInvalid
                || reason == EPathQueueDetachReason.ManagerReleased
                || reason == EPathQueueDetachReason.ConstraintViolation;
            // Pause intentionally detaches from the queue while keeping the
            // playback session alive. The manager's explicit detach callback
            // can arrive after the underlying follower has already entered
            // Paused, so preserve that state as well as the dedicated reason.
            if (!_suppressDetachStop
                && reason != EPathQueueDetachReason.Paused
                && reason != EPathQueueDetachReason.Replaced
                && _pathFollower != null
                && _pathFollower.State == EPathFollowerState.Moving)
            {
                ulong notificationRevision = _notificationRevision;
                PathCommandReceipt receipt = _pathFollower.StopMove();
                if (PlaybackId != receipt.PlaybackId || StateRevision != receipt.StateRevision
                    || _notificationRevision != notificationRevision)
                    return;
            }
            if (notifyInvalidation)
                InvokeCallbacks(_queueInvalidatedInvocationList,
                    callback => ((Action<QueuedPathFollower, EPathQueueDetachReason>)callback)(this, reason));
        }

        #endregion


        #region Private Methods

        private PathPlaybackSession PrepareRequestForManager(
            QueuedPathManager manager,
            PathPlaybackRequest request)
        {
            if (!_isInitialized)
                Init();
            if (!_isInitialized || _pathFollower == null)
                throw new InvalidOperationException("QueuedPathFollower is not initialized.");
            if (manager == null || !manager.isActiveAndEnabled || !manager.IsInitialized)
                throw new InvalidOperationException(
                    "QueuedPathManager must be initialized before starting a queued move.");
            if (request.Provider == null || !request.Provider.IsReady)
                throw new InvalidOperationException("Queue provider must be initialized and ready.");
            return _pathFollower.PreparePlayback(request);
        }

        private bool TryRegisterWithManager()
        {
            if (_queueParticipationMode != EQueueParticipationMode.Queued
                || _pathFollower == null
                || !_pathFollower.IsMoving)
                return false;
            if (_activeManager == null || !_activeManager.IsInitialized || !_activeManager.isActiveAndEnabled)
            {
                InvalidateQueueParticipation(EPathQueueDetachReason.ManagerReleased);
                return false;
            }
            IPathProvider provider = _pathFollower.CurrentProvider;
            if (provider == null || !provider.IsReady)
            {
                InvalidateQueueParticipation(EPathQueueDetachReason.ProviderInvalid);
                return false;
            }
            if (_activeManager.RouteProvider == null)
                _activeManager.ConfigureRoute(provider);
            if (!ReferenceEquals(_activeManager.RouteProvider, provider))
            {
                InvalidateQueueParticipation(EPathQueueDetachReason.ProviderInvalid);
                return false;
            }

            if (_queue.IsRegistered)
            {
                if (_queue.Registration.PlaybackId == PlaybackId)
                    return true;
                // A direct Start on the underlying follower can replace a
                // Moving playback without emitting StateChanged. Preserve its
                // requested hold while replacing only the old ownership token.
                UnregisterFromManager(EPathQueueDetachReason.Replaced);
            }

            if (!_queue.TryAcquire(_activeManager, this, _pathFollower))
                return false;
            AwaitFirstQueueResult();
            return true;
        }

        private void UnregisterFromManager(
            EPathQueueDetachReason reason = EPathQueueDetachReason.Explicit)
        {
            _queue.Release(reason);
        }

        private void HandleUnderlyingStateChanged(EPathFollowerState state)
        {
            ulong playbackId = PlaybackId;
            ulong stateRevision = StateRevision;
            if (state == EPathFollowerState.Moving)
            {
                if (_queueParticipationMode == EQueueParticipationMode.Queued)
                    TryRegisterWithManager();
            }
            else if (state == EPathFollowerState.Paused)
            {
                ApplyPausedState();
            }
            else if (state == EPathFollowerState.Ready
                || state == EPathFollowerState.Completed
                || state == EPathFollowerState.Uninitialized)
            {
                UnregisterFromManager(
                    state == EPathFollowerState.Completed
                        ? EPathQueueDetachReason.Completed
                        : EPathQueueDetachReason.Explicit);
            }

            if (PlaybackId != playbackId || StateRevision != stateRevision || State != state)
                return;
            InvokeCallbacks(
                _stateChangedInvocationList,
                callback => ((Action<EPathFollowerState>)callback)(state));
        }

        private void HandleUnderlyingSegmentChanged(int segmentIndex)
        {
            InvokeCallbacks(
                _segmentChangedInvocationList,
                callback => ((Action<int>)callback)(segmentIndex));
        }

        private void ApplyPausedState()
        {
            _notificationRevision++;
            if (_queueParticipationMode == EQueueParticipationMode.Queued)
                UnregisterFromManager(EPathQueueDetachReason.Paused);
            _queue.IsBlocked = true;
            _queue.SpeedMultiplier = 0f;
            UpdateAnimator();
        }

        private void HandleUnderlyingLoopBoundary()
        {
            if (_queueParticipationMode != EQueueParticipationMode.Queued
                || !_queue.IsRegistered || _pathFollower == null || !_pathFollower.IsMoving)
                return;

            _suppressDetachStop = true;
            try
            {
                UnregisterFromManager();
            }
            finally
            {
                _suppressDetachStop = false;
            }

            TryRegisterWithManager();
        }

        private void HandleUnderlyingCompleted()
        {
            UnregisterFromManager(EPathQueueDetachReason.Completed);
            ResetConstraintState();
            // Forward the underlying playback lifecycle event as well as the
            // queue-specific callback. CounterPathAgent subscribes to the
            // IPathFollower-style event to advance its sale/exit state.
            if (!InvokeCallbacks(
                _completedInvocationList,
                callback => ((Action)callback)()))
                return;
            InvokeCallbacks(
                _onCompletedInvocationList,
                callback => ((Action<QueuedPathFollower>)callback)(this));
        }

        private void ResetConstraintState()
        {
            _notificationRevision++;
            _queue.ManualBlock = false;
            _queue.IsBlocked = false;
            _managerState = null;
            _queue.SpeedMultiplier = 1f;
            _queue.ResumeDelayRemaining = 0f;
            if (_pathFollower != null)
            {
                if (_queue.Registration.IsValid
                    && _pathFollower is IPathConstraintTarget constraintTarget)
                    constraintTarget.ReleaseQueueConstraint(_queue.Registration);
            }
            UpdateAnimator();
        }

        private void ApplyCurrentConstraint()
        {
            if (!(_pathFollower is IPathConstraintTarget constraintTarget)
                || !_queue.Registration.IsValid)
                return;

            PathQueueConstraint constraint = new PathQueueConstraint(
                _queue.Registration,
                _queue.IsBlocked,
                _queue.SpeedMultiplier,
                _managerState.HasValue
                    ? Mathf.Min(
                        _requestedMaxGlobalNormalizedTime,
                        _managerState.Value.MaxGlobalNormalizedTime)
                    : _requestedMaxGlobalNormalizedTime,
                // The runtime driver owns the queue frame sequence. A
                // Time.frameCount fallback can be larger than the driver's
                // first frame and would make the first real manager result
                // look stale to PathFollower.
                _managerState.HasValue ? _managerState.Value.FrameId : -1,
                _managerState.HasValue ? _managerState.Value.RouteRevision : _queue.Registration.RouteRevision);
            constraintTarget.ApplyQueueConstraint(_queue.Registration, constraint);
        }

        private PathCommandReceipt InvalidateQueueCalculationAfterSeek(PathCommandReceipt receipt)
        {
            if (!receipt.Changed || !_queue.IsRegistered)
                return receipt;

            _notificationRevision++;
            _managerState = null;
            _queue.IsBlocked = true;
            _queue.SpeedMultiplier = 0f;
            ApplyCurrentConstraint();
            UpdateAnimator();
            return receipt;
        }

        private void AwaitFirstQueueResult()
        {
            _notificationRevision++;
            _managerState = null;
            _queue.IsBlocked = true;
            _queue.ResumeDelayRemaining = _resumeMovementDelay;
            _queue.SpeedMultiplier = 0f;
            ApplyCurrentConstraint();
            UpdateAnimator();
        }

        private void ReportStateChanges(bool wasBlocked, bool blocked)
        {
            if (blocked != wasBlocked)
            {
                if (blocked)
                {
                    if (!InvokeCallbacks(
                        _onBlockedInvocationList,
                        callback => ((Action<QueuedPathFollower>)callback)(this)))
                        return;
                }
                else
                {
                    if (!InvokeCallbacks(
                        _onResumedInvocationList,
                        callback => ((Action<QueuedPathFollower>)callback)(this)))
                        return;
                }
            }
            if (!Mathf.Approximately(
                    _lastReportedSpeedMultiplier,
                    _queue.SpeedMultiplier))
            {
                _lastReportedSpeedMultiplier = _queue.SpeedMultiplier;
                InvokeCallbacks(
                    _onSpeedChangedInvocationList,
                    callback => ((Action<QueuedPathFollower, float>)callback)(
                        this,
                        _queue.SpeedMultiplier));
            }
        }

        private bool InvokeCallbacks(
            Delegate[] callbacks,
            Action<Delegate> invoke)
        {
            if (callbacks == null)
                return true;

            ulong playbackId = PlaybackId;
            ulong stateRevision = StateRevision;
            ulong notificationRevision = _notificationRevision;
            for (int i = 0; i < callbacks.Length; i++)
            {
                try
                {
                    invoke(callbacks[i]);
                }
                catch
                {
                    if (PlaybackId == playbackId)
                        UnregisterFromManager();
                    throw;
                }
                if (PlaybackId != playbackId
                    || StateRevision != stateRevision
                    || _notificationRevision != notificationRevision)
                    return false;
            }
            return true;
        }

        private void UpdateAnimator()
        {
            _animatorView?.ApplyQueueMultiplier(_queue.SpeedMultiplier);
        }

        private float GetSpacing()
        {
            if (_useManagerSpacing && _activeManager != null)
                return Mathf.Max(0f, _activeManager.DefaultSpacing);
            return Mathf.Max(0f, _actorSpacing);
        }

        private void InvalidateRegistration(EPathQueueDetachReason reason)
        {
            if (!_queue.IsRegistered)
                return;
            UnregisterFromManager(reason);
        }

        private void InvalidateQueueParticipation(EPathQueueDetachReason reason)
        {
            if (_queue.IsRegistered)
                UnregisterFromManager(reason);
            else
            {
                _notificationRevision++;
                _managerState = null;
                _queue.IsBlocked = false;
                HandleDetachedPlayback(reason);
            }
        }

        #endregion


        #region Explicit Interface Implementation

        #endregion
    }
}
