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

        private bool _isInitialized;
        private bool _isRegistered;
        private bool _manualBlock;
        private bool _externalPause;
        private bool _effectiveBlocked;
        private float _currentSpeedMultiplier = 1f;
        private float _lastReportedSpeedMultiplier = 1f;
        private PathQueueState _managerState;
        private bool _hasManagerState;
        private PathQueueRegistration _registration;
        private Action _completedSubscription;
        private Action<EPathFollowerState> _stateSubscription;
        private Action<int> _segmentSubscription;
        private Action _loopSubscription;
        private bool _runtimeDriverRegistered;
        private bool _suppressDetachStop;
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
        private Delegate[] _stateChangedInvocationList;
        private Delegate[] _segmentChangedInvocationList;
        private Delegate[] _completedInvocationList;

        #endregion


        #region Properties

        public bool IsInitialized => _isInitialized;
        public bool IsBlocked => _effectiveBlocked;
        public bool IsMoving => _pathFollower != null && _pathFollower.IsMoving;
        public bool IsActuallyMoving => IsMoving && !_effectiveBlocked;
        public bool IsRegistered => _isRegistered;

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

        public float CurrentSpeedMultiplier => _currentSpeedMultiplier;
        public ulong PlaybackId => _pathFollower == null ? 0UL : _pathFollower.PlaybackId;
        public IPathProvider CurrentProvider => _pathFollower == null
            ? null
            : _pathFollower.CurrentProvider;
        public IPathSequenceProvider CurrentSequence => _pathFollower == null
            ? null
            : _pathFollower.CurrentSequence;
        public EPathFollowerState State => _pathFollower == null
            ? EPathFollowerState.Uninitialized
            : _pathFollower.State;
        public float NormalizedTime => _pathFollower == null
            ? 0f
            : _pathFollower.NormalizedTime;
        public int CurrentSegmentIndex => _pathFollower == null
            ? -1
            : _pathFollower.CurrentSegmentIndex;
        public EPathMoveType MoveType => _pathFollower == null
            ? EPathMoveType.TimeBased
            : _pathFollower.MoveType;
        public float Speed => _pathFollower == null ? 0f : _pathFollower.Speed;
        public float Duration => _pathFollower == null ? 0f : _pathFollower.Duration;
        public ulong StateRevision => _pathFollower == null
            ? 0UL
            : _pathFollower.StateRevision;
        ulong IPathPlaybackIdentity.PlaybackOwnerId => _pathFollower is IPathPlaybackIdentity identity
            ? identity.PlaybackOwnerId
            : 0UL;
        public PathQueueAgentSettings QueueSettings => new PathQueueAgentSettings(
            _actorSpacing,
            _useManagerSpacing,
            _enableGradualSlowdown,
            _enableOvertakeProtection);
        public float GlobalNormalizedTime => _pathFollower == null
            ? 0f
            : _pathFollower.GlobalNormalizedTime;
        public QueuedPathManager Manager => _manager;
        public IPathProvider QueueProvider => _pathFollower == null
            ? null
            : _pathFollower.CurrentProvider;
        public int SnapshotRevision => _pathFollower == null
            ? -1
            : _pathFollower.SnapshotRevision;

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

            _stateSubscription = HandleUnderlyingStateChanged;
            _segmentSubscription = HandleUnderlyingSegmentChanged;
            _completedSubscription = HandleUnderlyingCompleted;
            _loopSubscription = HandleUnderlyingLoopBoundary;
            _pathFollower.StateChanged -= _stateSubscription;
            _pathFollower.StateChanged += _stateSubscription;
            _pathFollower.SegmentChanged -= _segmentSubscription;
            _pathFollower.SegmentChanged += _segmentSubscription;
            _pathFollower.Completed -= _completedSubscription;
            _pathFollower.Completed += _completedSubscription;
            _pathFollower.LoopBoundary -= _loopSubscription;
            _pathFollower.LoopBoundary += _loopSubscription;
            PathCommandReceipt receipt = _pathFollower.Init();
            _pathFollower.SetEventTarget(this);
            _isInitialized = true;
            return receipt;
        }

        public PathCommandReceipt Release()
        {
            if (!_isInitialized && !_isRegistered)
                return default(PathCommandReceipt);

            UnregisterFromManager();
            if (_pathFollower != null)
            {
                if (_stateSubscription != null)
                    _pathFollower.StateChanged -= _stateSubscription;
                if (_segmentSubscription != null)
                    _pathFollower.SegmentChanged -= _segmentSubscription;
                if (_completedSubscription != null)
                    _pathFollower.Completed -= _completedSubscription;
                if (_loopSubscription != null)
                    _pathFollower.LoopBoundary -= _loopSubscription;
            }
            _stateSubscription = null;
            _segmentSubscription = null;
            _completedSubscription = null;
            _isInitialized = false;
            _hasManagerState = false;
            _effectiveBlocked = false;
            _currentSpeedMultiplier = 1f;
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
            Tick(frameId);
        }

        private void Tick(int frameId)
        {
            if (!_isInitialized || _pathFollower == null || !_pathFollower.IsMoving)
                return;
            if (!_isRegistered)
                TryRegisterWithManager();

            bool blocked = _externalPause
                || _manualBlock
                || !_hasManagerState
                || (_hasManagerState && _managerState.IsBlocked);
            float multiplier = _hasManagerState
                ? _managerState.SpeedMultiplier
                : 1f;
            if (_manualBlock || _externalPause)
                multiplier = 0f;

            bool wasBlocked = _effectiveBlocked;
            _effectiveBlocked = blocked;
            _currentSpeedMultiplier = Mathf.Clamp01(multiplier);
            ApplyCurrentConstraint();
            UpdateAnimator();
            ReportStateChanges(wasBlocked, blocked);
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
            EnsureQueueReady(request.Provider);
            // Perform all provider, movement, and event validation while the
            // existing queue registration is still live. A rejected request
            // must leave the active playback and its constraint untouched.
            _pathFollower.ValidatePlaybackRequest(request);
            if (_isRegistered)
            {
                _suppressDetachStop = true;
                try
                {
                    UnregisterFromManager();
                }
                finally
                {
                    _suppressDetachStop = false;
                }
            }
            PathCommandReceipt receipt = _pathFollower.StartPlayback(request);
            if (!ReferenceEquals(_pathFollower.CurrentProvider, _manager.RouteProvider))
            {
                _pathFollower.StopMove();
                throw new InvalidOperationException(
                    "Queue follower and manager must use the same route provider instance.");
            }
            RegisterWithManager();
            return receipt;
        }

        public PathCommandReceipt StopMove()
        {
            PathCommandReceipt receipt = _pathFollower == null
                ? default(PathCommandReceipt)
                : _pathFollower.StopMove();
            UnregisterFromManager();
            ResetConstraintState();
            return receipt;
        }

        public PathCommandReceipt PauseMove()
        {
            if (_pathFollower == null)
                return default(PathCommandReceipt);

            PathCommandReceipt receipt = _pathFollower.PauseMove();
            if (!receipt.Changed)
                return receipt;
            _externalPause = true;
            UnregisterFromManager();
            _effectiveBlocked = true;
            _currentSpeedMultiplier = 0f;
            UpdateAnimator();
            return receipt;
        }

        public PathCommandReceipt ResumeMove()
        {
            if (_pathFollower == null)
                return default(PathCommandReceipt);

            if (_pathFollower.State != EPathFollowerState.Paused)
                return default(PathCommandReceipt);
            EnsureQueueReady(_pathFollower.CurrentProvider);
            _externalPause = false;
            RegisterWithManager();
            return _pathFollower.ResumeMove();
        }

        public PathCommandReceipt Seek(float normalizedTime)
        {
            PathCommandReceipt receipt = _pathFollower == null
                ? default(PathCommandReceipt)
                : _pathFollower.Seek(normalizedTime);
            if (receipt.Changed)
                InvalidateQueueCalculationAfterSeek();
            return receipt;
        }

        public PathCommandReceipt SeekSegment(int segmentIndex, float localNormalizedTime)
        {
            PathCommandReceipt receipt = _pathFollower == null
                ? default(PathCommandReceipt)
                : _pathFollower.SeekSegment(segmentIndex, localNormalizedTime);
            if (receipt.Changed)
                InvalidateQueueCalculationAfterSeek();
            return receipt;
        }

        public PathCommandReceipt SetSpeed(float speed)
        {
            return _pathFollower == null
                ? default(PathCommandReceipt)
                : _pathFollower.SetSpeed(speed);
        }

        public PathCommandReceipt SetDuration(float duration)
        {
            return _pathFollower == null
                ? default(PathCommandReceipt)
                : _pathFollower.SetDuration(duration);
        }

        public void ForceBlock()
        {
            _manualBlock = true;
            _effectiveBlocked = true;
            _currentSpeedMultiplier = 0f;
            ApplyCurrentConstraint();
            UpdateAnimator();
        }

        public void ForceUnblock()
        {
            _manualBlock = false;
            ApplyCurrentConstraint();
            UpdateAnimator();
        }

        public void ApplyQueueState(
            PathQueueRegistration registration,
            PathQueueState state)
        {
            if (!_registration.IsValid
                || !SameRegistration(registration, _registration))
                return;
            _managerState = state;
            _hasManagerState = true;
            ApplyCurrentConstraint();
        }

        public void OnQueueDetached(
            PathQueueRegistration registration,
            EPathQueueDetachReason reason)
        {
            if (!_registration.IsValid
                || !SameRegistration(registration, _registration))
                return;

            _isRegistered = false;
            if (_pathFollower is IPathConstraintTarget constraintTarget)
                constraintTarget.ReleaseQueueConstraint(registration);
            _registration = default(PathQueueRegistration);
            _hasManagerState = false;
            _effectiveBlocked = false;
            // Pause intentionally detaches from the queue while keeping the
            // playback session alive. The manager's explicit detach callback
            // can arrive after the underlying follower has already entered
            // Paused, so preserve that state as well as the dedicated reason.
            if (!_suppressDetachStop
                && reason != EPathQueueDetachReason.Paused
                && reason != EPathQueueDetachReason.Replaced
                && _pathFollower != null
                && _pathFollower.State == EPathFollowerState.Moving)
                _pathFollower?.StopMove();
        }

        #endregion


        #region Private Methods

        private void EnsureQueueReady(IPathProvider provider)
        {
            if (!_isInitialized)
                Init();
            if (!_isInitialized || _pathFollower == null)
                throw new InvalidOperationException("QueuedPathFollower is not initialized.");
            if (_manager == null || !_manager.IsInitialized)
                throw new InvalidOperationException(
                    "QueuedPathManager must be initialized before starting a queued move.");
            if (provider == null || !provider.IsReady)
                throw new InvalidOperationException("Queue provider must be initialized and ready.");
            if (_manager.RouteProvider == null)
                _manager.ConfigureRoute(provider);
            if (!ReferenceEquals(_manager.RouteProvider, provider))
                throw new InvalidOperationException(
                    "Queue follower and manager must use the same route provider instance.");
        }

        private void RegisterWithManager()
        {
            if (_manager == null)
                return;
            if (!_isRegistered)
            {
                _registration = _manager.Register(this);
                _isRegistered = true;
                if (!(_pathFollower is IPathConstraintTarget constraintTarget)
                    || !constraintTarget.AttachQueueConstraint(_registration))
                {
                    _manager.Unregister(_registration);
                    _registration = default(PathQueueRegistration);
                    _isRegistered = false;
                    throw new InvalidOperationException(
                        "The playback could not acquire its queue constraint ownership.");
                }
                AwaitFirstQueueResult();
            }
            else if (!_hasManagerState)
                AwaitFirstQueueResult();
        }

        private void TryRegisterWithManager()
        {
            if (_manager == null
                || !_manager.IsInitialized
                || _pathFollower == null
                || !_pathFollower.IsMoving)
                return;
            IPathProvider provider = _pathFollower.CurrentProvider;
            if (provider == null || !provider.IsReady)
                return;
            if (_manager.RouteProvider == null)
                _manager.ConfigureRoute(provider);
            if (!ReferenceEquals(_manager.RouteProvider, provider))
                return;

            _registration = _manager.Register(this);
            _isRegistered = true;
            if (!(_pathFollower is IPathConstraintTarget constraintTarget)
                    || !constraintTarget.AttachQueueConstraint(_registration))
            {
                _manager.Unregister(_registration);
                _registration = default(PathQueueRegistration);
                _isRegistered = false;
                return;
            }
            AwaitFirstQueueResult();
        }

        private void UnregisterFromManager()
        {
            if (!_isRegistered)
                return;
            if (_manager != null && _registration.IsValid)
                _manager.Unregister(_registration);
            if (_pathFollower is IPathConstraintTarget constraintTarget)
                constraintTarget.ReleaseQueueConstraint(_registration);
            _isRegistered = false;
            _registration = default(PathQueueRegistration);
        }

        private void HandleUnderlyingStateChanged(EPathFollowerState state)
        {
            if (state == EPathFollowerState.Moving)
            {
                if (!_isRegistered)
                    TryRegisterWithManager();
            }
            else if (state == EPathFollowerState.Paused)
            {
                _externalPause = true;
                UnregisterFromManager();
                _effectiveBlocked = true;
                _currentSpeedMultiplier = 0f;
                UpdateAnimator();
            }
            else if (state == EPathFollowerState.Ready
                || state == EPathFollowerState.Completed
                || state == EPathFollowerState.Uninitialized)
            {
                UnregisterFromManager();
            }

            InvokePlaybackCallbacks(
                _stateChangedInvocationList,
                callback => ((Action<EPathFollowerState>)callback)(state));
        }

        private void HandleUnderlyingSegmentChanged(int segmentIndex)
        {
            InvokePlaybackCallbacks(
                _segmentChangedInvocationList,
                callback => ((Action<int>)callback)(segmentIndex));
        }

        private void HandleUnderlyingLoopBoundary()
        {
            if (!_isRegistered || _pathFollower == null || !_pathFollower.IsMoving)
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

            if (_pathFollower.IsMoving)
                RegisterWithManager();
        }

        private void HandleUnderlyingCompleted()
        {
            UnregisterFromManager();
            ResetConstraintState();
            InvokeQueueCallbacks(
                _onCompletedInvocationList,
                callback => ((Action<QueuedPathFollower>)callback)(this));
        }

        private void ResetConstraintState()
        {
            _externalPause = false;
            _manualBlock = false;
            _effectiveBlocked = false;
            _hasManagerState = false;
            _currentSpeedMultiplier = 1f;
            if (_pathFollower != null)
            {
                if (_registration.IsValid
                    && _pathFollower is IPathConstraintTarget constraintTarget)
                    constraintTarget.ReleaseQueueConstraint(_registration);
            }
            UpdateAnimator();
        }

        private void ApplyCurrentConstraint()
        {
            if (!(_pathFollower is IPathConstraintTarget constraintTarget)
                || !_registration.IsValid)
                return;

            PathQueueConstraint constraint = new PathQueueConstraint(
                _registration,
                _effectiveBlocked,
                _currentSpeedMultiplier,
                _hasManagerState ? _managerState.MaxGlobalNormalizedTime : 1f,
                _hasManagerState ? _managerState.FrameId : Time.frameCount,
                _hasManagerState ? _managerState.RouteRevision : _registration.RouteRevision);
            constraintTarget.ApplyQueueConstraint(_registration, constraint);
        }

        private void InvalidateQueueCalculationAfterSeek()
        {
            if (!_isRegistered)
                return;

            _hasManagerState = false;
            _effectiveBlocked = true;
            _currentSpeedMultiplier = 0f;
            ApplyCurrentConstraint();
            UpdateAnimator();
        }

        private void AwaitFirstQueueResult()
        {
            _hasManagerState = false;
            _effectiveBlocked = true;
            _currentSpeedMultiplier = 0f;
            ApplyCurrentConstraint();
            UpdateAnimator();
        }

        private void ReportStateChanges(bool wasBlocked, bool blocked)
        {
            if (blocked != wasBlocked)
            {
                if (blocked)
                    InvokeQueueCallbacks(
                        _onBlockedInvocationList,
                        callback => ((Action<QueuedPathFollower>)callback)(this));
                else
                    InvokeQueueCallbacks(
                        _onResumedInvocationList,
                        callback => ((Action<QueuedPathFollower>)callback)(this));
            }
            if (!Mathf.Approximately(
                    _lastReportedSpeedMultiplier,
                    _currentSpeedMultiplier))
            {
                _lastReportedSpeedMultiplier = _currentSpeedMultiplier;
                InvokeQueueCallbacks(
                    _onSpeedChangedInvocationList,
                    callback => ((Action<QueuedPathFollower, float>)callback)(
                        this,
                        _currentSpeedMultiplier));
            }
        }

        private void InvokeQueueCallbacks(
            Delegate[] callbacks,
            Action<Delegate> invoke)
        {
            if (callbacks == null)
                return;

            ulong playbackId = PlaybackId;
            ulong stateRevision = StateRevision;
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
                    || StateRevision != stateRevision)
                    return;
            }
        }

        private void InvokePlaybackCallbacks(
            Delegate[] callbacks,
            Action<Delegate> invoke)
        {
            if (callbacks == null)
                return;

            ulong playbackId = PlaybackId;
            ulong stateRevision = StateRevision;
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
                    || StateRevision != stateRevision)
                    return;
            }
        }

        private void UpdateAnimator()
        {
            _animatorView?.ApplyQueueMultiplier(_currentSpeedMultiplier);
        }

        private static bool SameRegistration(
            PathQueueRegistration left,
            PathQueueRegistration right)
        {
            return left.IsValid
                && right.IsValid
                && left.BelongsTo(right.Owner)
                && left.RegistrationId == right.RegistrationId
                && left.PlaybackId == right.PlaybackId;
        }

        #endregion


        #region Explicit Interface Implementation

        #endregion
    }
}
