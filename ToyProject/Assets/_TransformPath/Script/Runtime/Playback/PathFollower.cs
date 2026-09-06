using System;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// Single driver-driven path playback. A playback session owns the current
    /// provider snapshot, event cursor, and queue constraint so no coroutine or
    /// per-frame temporary object is required.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [RequireComponent(typeof(PathEventHandler))]
    public sealed class PathFollower : MonoBehaviour, IPathFollower, IPathPlaybackIdentity, IPathRuntimeTickable, IPathConstraintTarget, IPathRuntimeFaultHandler
    {
        #region Constants

        private const float EPSILON = 0.00001f;
        private const int MAX_SEGMENT_TRANSITIONS_PER_TICK = 65;

        #endregion


        #region Member Variables

        [Header("Startup")]
        [SerializeField] private MonoBehaviour _startupProviderObject;
        [SerializeField] private bool _playOnStart;
        [SerializeField] private bool _startupLoop;

        [Header("Runtime")]
        [SerializeField] private PathEventHandler _pathEventHandler;
        [SerializeField] private PathFollowerAnimatorView _animatorView;

        private bool _isInitialized;
        private bool _isMoving;
        private EPathFollowerState _state = EPathFollowerState.Uninitialized;
        private PathPlaybackSession _playbackSession;
        private EPathMoveType _moveType;
        private float _speed;
        private float _duration;
        private AnimationCurve _timeCurve;
        private bool _loop;
        private int _currentSegmentIndex = -1;
        private float _normalizedTime;
        private float _globalNormalizedTime;
        private float _segmentElapsed;
        private float _segmentDistance;
        private float _pendingDeltaTime;
        private int _playbackRevision;
        private readonly PathPlaybackEngine _playbackEngine = new PathPlaybackEngine();
        private PathPlaybackScope _playbackScope;
        private IPathFollower _eventTarget;
        private bool _queueBlocked;
        private float _queueSpeedMultiplier = 1f;
        private float _queueMaxGlobalNormalizedTime = 1f;
        private PathQueueRegistration _queueConstraintRegistration;
        private int _queueConstraintFrameId = -1;
        private int _queueConstraintRouteRevision = -1;
        private bool _runtimeDriverRegistered;
        private Action<int> _segmentChanged;
        private Delegate[] _segmentChangedInvocationList;
        private Action _loopBoundary;
        private Delegate[] _loopBoundaryInvocationList;
        private Action<EPathFollowerState> _stateChanged;
        private Delegate[] _stateChangedInvocationList;
        private Action _completed;
        private Delegate[] _completedInvocationList;

        #endregion


        #region Properties

        public bool IsInitialized => _isInitialized;
        public bool IsMoving => _isMoving;
        public IPathProvider CurrentProvider => _playbackSession?.Provider;
        public IPathSequenceProvider CurrentSequence => _playbackSession?.Sequence;
        public EPathFollowerState State => _state;
        public float NormalizedTime => _normalizedTime;
        public float GlobalNormalizedTime => _globalNormalizedTime;
        public int CurrentSegmentIndex => _currentSegmentIndex;
        public int SnapshotRevision => _playbackSession?.ProviderRevision ?? -1;
        public EPathMoveType MoveType => _moveType;
        public ulong PlaybackId => _playbackEngine.PlaybackId;
        public ulong StateRevision => _playbackEngine.StateRevision;
        ulong IPathPlaybackIdentity.PlaybackOwnerId => _playbackEngine.OwnerId;

        public float Speed
        {
            get => _speed;
        }

        public float Duration
        {
            get => _duration;
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
        internal event Action LoopBoundary
        {
            add
            {
                _loopBoundary += value;
                _loopBoundaryInvocationList = _loopBoundary?.GetInvocationList();
            }
            remove
            {
                _loopBoundary -= value;
                _loopBoundaryInvocationList = _loopBoundary?.GetInvocationList();
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
                return CreateReceipt(false);

            if (_pathEventHandler == null)
                TryGetComponent(out _pathEventHandler);
            if (_animatorView == null)
                TryGetComponent(out _animatorView);
            if (_pathEventHandler != null && !_pathEventHandler.IsInitialized)
                _pathEventHandler.Init();
            _pathEventHandler?.Bind(this);
            _eventTarget = this;

            _isInitialized = true;
            _state = EPathFollowerState.Ready;
            _playbackRevision++;
            _playbackEngine.Touch();
            PathCommandReceipt receipt = CreateReceipt(true);
            InvokeStateChanged(_state);
            return receipt;
        }

        public PathCommandReceipt Release()
        {
            if (!_isInitialized && _playbackSession == null)
                return CreateReceipt(false);

            PathCommandReceipt stopReceipt = StopMove();
            if (PlaybackId != 0)
            {
                // A StateChanged callback may have started a new playback.
                // The release that ended the old scope must not tear it down.
                return stopReceipt;
            }
            _playbackSession = null;
            _isInitialized = false;
            _playbackEngine.Touch();
            PathCommandReceipt receipt = CreateReceipt(true);
            SetState(EPathFollowerState.Uninitialized);
            return receipt;
        }

        private void Reset()
        {
            TryGetComponent(out _pathEventHandler);
        }

        private void Awake()
        {
            if (!Application.isPlaying)
                return;
            Init();
            PathRuntimeDriverBehaviour.Instance.Register(this);
            _runtimeDriverRegistered = true;
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

        private void Start()
        {
            if (!_playOnStart || _startupProviderObject == null)
                return;

            IPathProvider provider = _startupProviderObject as IPathProvider;
            if (provider == null)
            {
                Debug.LogError(
                    $"PathFollower '{name}' startup provider does not implement IPathProvider.",
                    this);
                return;
            }

            IPathSequenceProvider sequenceProvider = provider as IPathSequenceProvider;
            if (sequenceProvider != null)
            {
                StartPlayback(PathPlaybackRequest.Sequence(sequenceProvider, _startupLoop));
                return;
            }

            IPathMovementProvider movementProvider = provider as IPathMovementProvider;
            if (movementProvider != null)
            {
                StartPlayback(PathPlaybackRequest.Single(movementProvider, _startupLoop));
                return;
            }

            Debug.LogError(
                $"PathFollower '{name}' startup provider does not expose movement settings. "
                + "Use the explicit Aggregate playback request.",
                this);
        }

        void IPathRuntimeTickable.Tick(
            float deltaTime,
            float unscaledDeltaTime,
            int frameId)
        {
            Tick(deltaTime);
        }

        internal void Tick(float deltaTime)
        {
            if (!_isInitialized || !_isMoving || _playbackSession == null)
                return;
            if (!RefreshProviderSnapshotIfNeeded())
                return;

            if (deltaTime <= 0f)
                return;

            int tickRevision = _playbackRevision;
            if (_queueBlocked)
            {
                _pendingDeltaTime = 0f;
                ApplyCurrentPosition();
                return;
            }

            float remaining = deltaTime + _pendingDeltaTime;
            _pendingDeltaTime = 0f;
            if (remaining <= 0f)
                return;

            int transitionCount = 0;
            while (remaining > EPSILON
                && _isMoving
                && transitionCount < MAX_SEGMENT_TRANSITIONS_PER_TICK)
            {
                bool transitioned = false;
                float consumed = _playbackSession.Sequence == null
                    ? TickSingle(remaining, tickRevision)
                    : TickSequence(remaining, tickRevision, out transitioned);

                if (_playbackSession.Sequence != null && transitioned)
                    transitionCount++;
                if (!_isMoving || _playbackRevision != tickRevision)
                    return;
                if (consumed <= EPSILON)
                    break;
                remaining -= consumed;
            }

            if (_isMoving
                && remaining > EPSILON
                && transitionCount >= MAX_SEGMENT_TRANSITIONS_PER_TICK)
                _pendingDeltaTime = remaining;

            ApplyCurrentPosition();
        }

        void IPathRuntimeFaultHandler.HandleRuntimeFault(
            ulong playbackId,
            System.Exception exception)
        {
            if (playbackId == 0 || PlaybackId != playbackId)
                return;
            try
            {
                StopMove();
            }
            catch
            {
                if (PlaybackId != playbackId)
                    return;
                _pathEventHandler?.CancelPlaybackEffects(playbackId);
                StopPlaybackOnly();
                _playbackEngine.InvalidatePlayback();
                DisposePlaybackScope();
                SetState(EPathFollowerState.Ready);
            }
        }

        private void OnDisable()
        {
            if (Application.isPlaying)
            {
                if (_runtimeDriverRegistered && PathRuntimeDriverBehaviour.HasInstance)
                {
                    PathRuntimeDriverBehaviour.Instance.Unregister(this);
                    _runtimeDriverRegistered = false;
                }
                StopMove();
            }
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

        #endregion


        #region Public Methods

        public PathCommandReceipt StartPlayback(PathPlaybackRequest request)
        {
            EnsureInitialized();
            PathPlaybackSession session = PathPlaybackSession.CreateOrReuse(
                request,
                _playbackSession);
            if (session.Kind == EPathPlaybackKind.Sequence)
            {
                _pathEventHandler?.PrepareForSequence(this, session.Snapshot);
            }
                else
                    _pathEventHandler?.PrepareForPlayback(
                        this,
                        session.EventSource,
                        session.MovementSettings.MoveType);
            return BeginPlayback(session, request.Loop);
        }

        internal void ValidatePlaybackRequest(PathPlaybackRequest request)
        {
            EnsureInitialized();
            PathPlaybackSession session = PathPlaybackSession.CreateOrReuse(
                request,
                _playbackSession);
            if (_pathEventHandler == null)
                return;
            if (session.Kind == EPathPlaybackKind.Sequence)
                _pathEventHandler.ValidateForSequence(this, session.Snapshot);
            else
                _pathEventHandler.ValidateForPlayback(
                    this,
                    session.EventSource,
                    session.MovementSettings.MoveType);
        }

        public PathCommandReceipt StopMove()
        {
            if (_state == EPathFollowerState.Uninitialized
                || _state == EPathFollowerState.Ready)
                return CreateReceipt(false);
            ulong stoppedPlaybackId = PlaybackId;
            _pathEventHandler?.CancelPlaybackEffects(stoppedPlaybackId);
            StopPlaybackOnly();
            _playbackEngine.InvalidatePlayback();
            DisposePlaybackScope();
            PathCommandReceipt receipt = CreateReceipt(true);
            SetState(_isInitialized
                ? EPathFollowerState.Ready
                : EPathFollowerState.Uninitialized);
            return receipt;
        }

        public PathCommandReceipt PauseMove()
        {
            if (!_isMoving)
                return CreateReceipt(false);

            _isMoving = false;
            _pendingDeltaTime = 0f;
            _playbackRevision++;
            _playbackEngine.Touch();
            _pathEventHandler?.CancelMovementAdjustments();
            PathCommandReceipt receipt = CreateReceipt(true);
            SetState(EPathFollowerState.Paused);
            ApplyAnimatorSpeed(0f);
            return receipt;
        }

        public PathCommandReceipt ResumeMove()
        {
            if (!_isInitialized
                || _playbackSession == null
                || _state != EPathFollowerState.Paused)
                return CreateReceipt(false);

            _isMoving = true;
            _playbackRevision++;
            _playbackEngine.Touch();
            PathCommandReceipt receipt = CreateReceipt(true);
            SetState(EPathFollowerState.Moving);
            ApplyAnimatorSpeed(1f);
            return receipt;
        }

        public PathCommandReceipt Seek(float normalizedTime)
        {
            EnsureInitialized();
            if (_playbackSession == null)
                throw new InvalidOperationException("PathFollower has no active provider.");
            ValidateFinite(normalizedTime, nameof(normalizedTime));

            float targetProgress = Mathf.Clamp01(normalizedTime);
            if (_playbackSession.Sequence == null)
            {
                if (Mathf.Abs(_normalizedTime - targetProgress) <= EPSILON)
                    return CreateReceipt(false);
            }
            else
            {
                int targetSegment = _playbackSession.Snapshot.FindSegment(targetProgress);
                float targetLocal = _playbackSession.Snapshot.GetLocalProgress(
                    targetSegment,
                    targetProgress);
                if (_currentSegmentIndex == targetSegment
                    && Mathf.Abs(_normalizedTime - targetLocal) <= EPSILON)
                    return CreateReceipt(false);
            }

            _pendingDeltaTime = 0f;
            _queueConstraintFrameId = -1;
            if (_queueConstraintRegistration.IsValid)
                SetConstraintState(true, 0f, 0f);
            if (_playbackSession.Sequence == null)
            {
                _normalizedTime = targetProgress;
                _globalNormalizedTime = _normalizedTime;
                _segmentElapsed = InverseTimeProgress(_normalizedTime, _timeCurve) * _duration;
                _segmentDistance = _playbackSession.Provider.PathLength * _normalizedTime;
                _playbackSession.EventCursor.Reset(
                    _playbackSession.EventSource,
                    _normalizedTime);
            }
            else
            {
                SetSequenceGlobalProgress(targetProgress);
            }

            ApplyCurrentPosition();
            _playbackEngine.Touch();
            return CreateReceipt(true);
        }

        public PathCommandReceipt SeekSegment(int segmentIndex, float localNormalizedTime)
        {
            EnsureInitialized();
            if (_playbackSession?.Sequence == null || _playbackSession.Snapshot == null)
                throw new InvalidOperationException("SeekSegment requires an active sequence.");
            if (segmentIndex < 0 || segmentIndex >= _playbackSession.Snapshot.Count)
                throw new ArgumentOutOfRangeException(nameof(segmentIndex));
            ValidateFinite(localNormalizedTime, nameof(localNormalizedTime));

            float local = Mathf.Clamp01(localNormalizedTime);
            if (local >= 1f && segmentIndex < _playbackSession.Snapshot.Count - 1)
            {
                segmentIndex++;
                local = 0f;
            }

            if (_currentSegmentIndex == segmentIndex
                && Mathf.Abs(_normalizedTime - local) <= EPSILON)
                return CreateReceipt(false);

            _queueConstraintFrameId = -1;
            if (_queueConstraintRegistration.IsValid)
                SetConstraintState(true, 0f, 0f);

            SetSequenceSegmentProgress(segmentIndex, local);
            ApplyCurrentPosition();
            _playbackEngine.Touch();
            return CreateReceipt(true);
        }

        public PathCommandReceipt SetSpeed(float speed)
        {
            EnsureInitialized();
            ValidateMovementValue(speed, nameof(speed));
            if (_playbackSession == null)
                throw new InvalidOperationException("Speed can only be changed during playback.");
            if (_moveType != EPathMoveType.SpeedBased)
                throw new InvalidOperationException("Speed can only be changed in SpeedBased mode.");
            if (Mathf.Approximately(_speed, speed))
                return CreateReceipt(false);
            _speed = speed;
            _playbackEngine.Touch();
            return CreateReceipt(true);
        }

        public PathCommandReceipt SetDuration(float duration)
        {
            EnsureInitialized();
            ValidateMovementValue(duration, nameof(duration));
            if (_playbackSession == null)
                throw new InvalidOperationException("Duration can only be changed during playback.");
            if (_moveType != EPathMoveType.TimeBased)
                throw new InvalidOperationException("Duration can only be changed in TimeBased mode.");
            if (Mathf.Approximately(_duration, duration))
                return CreateReceipt(false);

            float normalized = _duration > PathMovementSettingsUtility.MIN_VALUE
                ? EvaluateTimeProgress(_segmentElapsed / _duration, _timeCurve)
                : 0f;
            _duration = duration;
            _segmentElapsed = InverseTimeProgress(normalized, _timeCurve) * duration;
            _playbackEngine.Touch();
            return CreateReceipt(true);
        }

        /// <summary>
        /// Called once per frame by QueuedPathFollower before this follower's
        /// Update tick. The constraint is a non-destructive multiplier/clamp.
        /// </summary>
        private void SetConstraintState(
            bool blocked,
            float speedMultiplier,
            float maxGlobalNormalizedTime)
        {
            _queueBlocked = blocked;
            _queueSpeedMultiplier = Mathf.Clamp01(speedMultiplier);
            _queueMaxGlobalNormalizedTime = Mathf.Clamp01(maxGlobalNormalizedTime);

            // The bound is enforced while calculating the next advance. The
            // current pose is never rewound when the queue becomes tighter.
        }

        public bool AttachQueueConstraint(PathQueueRegistration registration)
        {
            if (!registration.IsValid || registration.PlaybackId == 0
                || registration.PlaybackId != PlaybackId)
                return false;

            _queueConstraintRegistration = registration;
            _queueConstraintFrameId = -1;
            _queueConstraintRouteRevision = registration.RouteRevision;
            SetConstraintState(true, 0f, 0f);
            return true;
        }

        public void ApplyQueueConstraint(
            PathQueueRegistration registration,
            PathQueueConstraint constraint)
        {
            if (!_queueConstraintRegistration.IsValid
                || !SameQueueRegistration(
                    registration,
                    _queueConstraintRegistration)
                || registration.PlaybackId != PlaybackId
                || !SameQueueRegistration(constraint.Registration, registration))
                return;
            if (constraint.RouteRevision != registration.RouteRevision
                || constraint.FrameId < _queueConstraintFrameId)
                return;

            _queueConstraintFrameId = constraint.FrameId;
            _queueConstraintRouteRevision = constraint.RouteRevision;
            SetConstraintState(
                constraint.IsBlocked,
                constraint.SpeedMultiplier,
                constraint.MaxGlobalNormalizedTime);
        }

        public void ReleaseQueueConstraint(PathQueueRegistration registration)
        {
            if (!_queueConstraintRegistration.IsValid
                || !SameQueueRegistration(
                    registration,
                    _queueConstraintRegistration))
                return;

            _queueConstraintRegistration = default(PathQueueRegistration);
            _queueConstraintFrameId = -1;
            _queueConstraintRouteRevision = -1;
            ClearConstraintState();
        }

        internal void SetEventTarget(IPathFollower target)
        {
            _eventTarget = target ?? this;
            _pathEventHandler?.Bind(_eventTarget);
        }

        private void ClearConstraintState()
        {
            _queueBlocked = false;
            _queueSpeedMultiplier = 1f;
            _queueMaxGlobalNormalizedTime = 1f;
        }

        #endregion


        #region Private Methods

        private PathCommandReceipt BeginPlayback(
            PathPlaybackSession session,
            bool loop)
        {
            ulong previousPlaybackId = PlaybackId;
            if (previousPlaybackId != 0)
            {
                _pathEventHandler?.CancelPlaybackEffects(previousPlaybackId);
                _playbackEngine.InvalidatePlayback();
            }
            StopPlaybackOnly();
            DisposePlaybackScope();
            _playbackSession = session;
            _loop = loop;
            _currentSegmentIndex = 0;
            _normalizedTime = 0f;
            _globalNormalizedTime = 0f;
            _segmentElapsed = 0f;
            _segmentDistance = 0f;
            if (session.Kind == EPathPlaybackKind.Sequence)
            {
                ConfigureSequenceSegment(0, 0f, false);
                session.EventCursor.Reset(
                    session.Snapshot.GetEventSource(0),
                    0f);
            }
            else
            {
                _moveType = session.MovementSettings.MoveType;
                if (_moveType == EPathMoveType.SpeedBased)
                    _speed = session.MovementSettings.Value;
                else
                    _duration = session.MovementSettings.Value;
                _timeCurve = session.MovementSettings.TimeCurve;
                session.EventCursor.Reset(
                    session.EventSource,
                    0f);
            }
            ClearConstraintState();
            _isMoving = true;
            _playbackRevision++;
            _playbackEngine.BeginPlayback();
            _playbackScope = new PathPlaybackScope(PlaybackId);
            PathCommandReceipt receipt = CreateReceipt(true);
            int beginRevision = _playbackRevision;
            ulong beginPlaybackId = PlaybackId;
            ulong beginStateRevision = StateRevision;
            SetState(EPathFollowerState.Moving);
            if (_playbackRevision != beginRevision
                || PlaybackId != beginPlaybackId
                || StateRevision != beginStateRevision)
                return receipt;
            ApplyCurrentPosition();
            return receipt;
        }

        private float TickSingle(float availableTime, int tickRevision)
        {
            IPathProvider provider = _playbackSession.Provider;
            float multiplier = _queueSpeedMultiplier;
            if (_moveType == EPathMoveType.TimeBased)
            {
                float duration = Mathf.Max(_duration, PathMovementSettingsUtility.MIN_VALUE);
                if (multiplier <= EPSILON)
                    return availableTime;
                float currentProgress = EvaluateTimeProgress(_segmentElapsed / duration, _timeCurve);
                float maxProgress = Mathf.Clamp01(_queueMaxGlobalNormalizedTime);
                if (maxProgress <= currentProgress + EPSILON)
                    return 0f;
                float maximumElapsed = InverseTimeProgress(maxProgress, _timeCurve) * duration;
                float targetElapsed = Mathf.Min(
                    _segmentElapsed + availableTime * multiplier,
                    maximumElapsed);
                float consume = Mathf.Min(
                    availableTime,
                    Mathf.Max(0f, targetElapsed - _segmentElapsed) / multiplier);
                _segmentElapsed += consume * multiplier;
                _normalizedTime = EvaluateTimeProgress(_segmentElapsed / duration, _timeCurve);
                _globalNormalizedTime = _normalizedTime;
                DispatchEvents(_playbackSession.EventSource, _normalizedTime, tickRevision);
                if (_playbackRevision != tickRevision)
                    return consume;
                if (_segmentElapsed >= duration - EPSILON)
                {
                    _normalizedTime = 1f;
                    _globalNormalizedTime = 1f;
                    if (_loop)
                    {
                        ApplyCurrentPosition();
                        FlushEvents(_playbackSession.EventSource, tickRevision);
                        if (_playbackRevision != tickRevision)
                            return consume;
                        InvokeLoopBoundary(tickRevision);
                        if (_playbackRevision != tickRevision)
                            return consume;
                        _segmentElapsed = 0f;
                        _segmentDistance = 0f;
                        _normalizedTime = 0f;
                        _globalNormalizedTime = 0f;
                        _playbackSession.EventCursor.Reset(
                            _playbackSession.EventSource,
                            0f);
                    }
                    else
                        CompletePlayback(_playbackSession.EventSource);
                }

                return Mathf.Max(consume, EPSILON);
            }

            float pathLength = Mathf.Max(
                provider.PathLength,
                PathMovementSettingsUtility.MIN_VALUE);
            float remainingDistance = Mathf.Max(0f, pathLength - _segmentDistance);
            float effectiveSpeed = Mathf.Max(
                _speed,
                PathMovementSettingsUtility.MIN_VALUE) * multiplier;
            if (multiplier <= EPSILON)
                return availableTime;
            float consumeTime = Mathf.Min(
                availableTime,
                remainingDistance / Mathf.Max(effectiveSpeed, PathMovementSettingsUtility.MIN_VALUE));
            float maximumDistance = Mathf.Clamp01(_queueMaxGlobalNormalizedTime) * pathLength;
            if (maximumDistance <= _segmentDistance + EPSILON)
                return 0f;
            consumeTime = Mathf.Min(
                consumeTime,
                Mathf.Max(0f, maximumDistance - _segmentDistance)
                    / Mathf.Max(effectiveSpeed, EPSILON));
            _segmentDistance += consumeTime * effectiveSpeed;
            _normalizedTime = Mathf.Clamp01(_segmentDistance / pathLength);
            _globalNormalizedTime = _normalizedTime;
            DispatchEvents(_playbackSession.EventSource, _normalizedTime, tickRevision);
            if (_playbackRevision != tickRevision)
                return consumeTime;
            if (_segmentDistance >= pathLength - EPSILON)
            {
                _normalizedTime = 1f;
                _globalNormalizedTime = 1f;
                if (_loop)
                {
                    ApplyCurrentPosition();
                    FlushEvents(_playbackSession.EventSource, tickRevision);
                    if (_playbackRevision != tickRevision)
                        return consumeTime;
                    InvokeLoopBoundary(tickRevision);
                    if (_playbackRevision != tickRevision)
                        return consumeTime;
                    _segmentDistance = 0f;
                    _segmentElapsed = 0f;
                    _normalizedTime = 0f;
                    _globalNormalizedTime = 0f;
                    _playbackSession.EventCursor.Reset(
                        _playbackSession.EventSource,
                        0f);
                }
                else
                    CompletePlayback(_playbackSession.EventSource);
            }

            return Mathf.Max(consumeTime, EPSILON);
        }

        private float TickSequence(
            float availableTime,
            int tickRevision,
            out bool transitioned)
        {
            transitioned = false;
            PathSequenceSnapshot snapshot = _playbackSession.Snapshot;
            PathSegmentDescriptor descriptor = snapshot.GetDescriptor(_currentSegmentIndex);
            PathMovementSettings movementSettings = descriptor.MovementSettings;
            float multiplier = _queueSpeedMultiplier;
            if (multiplier <= EPSILON)
                return availableTime;
            float consume;

            if (movementSettings.MoveType == EPathMoveType.TimeBased)
            {
                float duration = Mathf.Max(_duration, PathMovementSettingsUtility.MIN_VALUE);
                float currentProgress = EvaluateTimeProgress(
                    _segmentElapsed / duration,
                    movementSettings.TimeCurve);
                float maxLocalProgress = snapshot.GetLocalProgress(
                    _currentSegmentIndex,
                    _queueMaxGlobalNormalizedTime);
                if (maxLocalProgress <= currentProgress + EPSILON)
                    return 0f;
                float maximumElapsed = InverseTimeProgress(
                    maxLocalProgress,
                    movementSettings.TimeCurve) * duration;
                float targetElapsed = Mathf.Min(
                    _segmentElapsed + availableTime * multiplier,
                    maximumElapsed);
                consume = Mathf.Min(
                    availableTime,
                    Mathf.Max(0f, targetElapsed - _segmentElapsed) / multiplier);
                _segmentElapsed += consume * multiplier;
                _normalizedTime = EvaluateTimeProgress(
                    _segmentElapsed / duration,
                    movementSettings.TimeCurve);
            }
            else
            {
                float length = Mathf.Max(
                    snapshot.GetLength(_currentSegmentIndex),
                    PathMovementSettingsUtility.MIN_VALUE);
                float speed = Mathf.Max(
                    _speed,
                    PathMovementSettingsUtility.MIN_VALUE) * multiplier;
                float remaining = Mathf.Max(0f, length - _segmentDistance);
                consume = Mathf.Min(
                    availableTime,
                    remaining / Mathf.Max(speed, PathMovementSettingsUtility.MIN_VALUE));
                float maxLocalProgress = snapshot.GetLocalProgress(
                    _currentSegmentIndex,
                    _queueMaxGlobalNormalizedTime);
                if (maxLocalProgress <= _normalizedTime + EPSILON)
                    return 0f;
                float maximumDistance = maxLocalProgress * length;
                consume = Mathf.Min(
                    consume,
                    Mathf.Max(0f, maximumDistance - _segmentDistance)
                        / Mathf.Max(speed, EPSILON));
                _segmentDistance += consume * speed;
                _normalizedTime = Mathf.Clamp01(_segmentDistance / length);
            }

            _globalNormalizedTime = snapshot.GetGlobalProgress(
                _currentSegmentIndex,
                _normalizedTime);
            DispatchEvents(
                snapshot.GetEventSource(_currentSegmentIndex),
                _normalizedTime,
                tickRevision);
            if (_playbackRevision != tickRevision)
                return Mathf.Max(consume, EPSILON);

            bool atEnd = movementSettings.MoveType == EPathMoveType.TimeBased
                ? _segmentElapsed >= _duration - EPSILON
                : _segmentDistance >= snapshot.GetLength(_currentSegmentIndex) - EPSILON;
            if (atEnd)
            {
                _normalizedTime = 1f;
                _globalNormalizedTime = snapshot.GetGlobalProgress(
                    _currentSegmentIndex,
                    1f);
                if (_currentSegmentIndex + 1 < snapshot.Count)
                {
                    ApplyCurrentPosition();
                    FlushEvents(
                        snapshot.GetEventSource(_currentSegmentIndex),
                        tickRevision);
                    if (_playbackRevision != tickRevision)
                        return Mathf.Max(consume, EPSILON);
                    AdvanceToSegment(_currentSegmentIndex + 1, tickRevision);
                    transitioned = true;
                }
                else if (_loop)
                {
                    ApplyCurrentPosition();
                    FlushEvents(
                        snapshot.GetEventSource(_currentSegmentIndex),
                        tickRevision);
                    if (_playbackRevision != tickRevision)
                        return Mathf.Max(consume, EPSILON);
                    AdvanceToSegment(0, tickRevision, true);
                    transitioned = true;
                }
                else
                    CompletePlayback(snapshot.GetEventSource(_currentSegmentIndex));
            }

            return Mathf.Max(consume, EPSILON);
        }

        private void AdvanceToSegment(int index, int tickRevision)
        {
            AdvanceToSegment(index, tickRevision, false);
        }

        private void AdvanceToSegment(
            int index,
            int tickRevision,
            bool loopBoundary)
        {
            float previousNominalSpeed = GetCurrentNominalSpeed();
            _currentSegmentIndex = index;
            _segmentElapsed = 0f;
            _segmentDistance = 0f;
            _normalizedTime = 0f;
            _globalNormalizedTime = _playbackSession.Snapshot.GetGlobalProgress(index, 0f);
            PathSegmentDescriptor descriptor = _playbackSession.Snapshot.GetDescriptor(index);
            ConfigureSequenceSegment(index, previousNominalSpeed, descriptor.PreservePreviousSpeed);
            _playbackSession.EventCursor.Reset(
                _playbackSession.Snapshot.GetEventSource(index),
                0f);
            if (loopBoundary)
            {
                InvokeLoopBoundary(tickRevision);
                if (_playbackRevision != tickRevision)
                    return;
            }
            InvokeSegmentChanged(index, tickRevision);
        }

        private void ConfigureSequenceSegment(
            int index,
            float previousNominalSpeed,
            bool preserveSpeed)
        {
            PathSegmentDescriptor descriptor = _playbackSession.Snapshot.GetDescriptor(index);
            PathMovementSettings settings = descriptor.MovementSettings;
            _moveType = settings.MoveType;
            _timeCurve = settings.TimeCurve;
            if (preserveSpeed
                && previousNominalSpeed >= PathMovementSettingsUtility.MIN_VALUE
                && PathValueUtility.IsFinite(previousNominalSpeed))
            {
                if (settings.MoveType == EPathMoveType.SpeedBased)
                    _speed = previousNominalSpeed;
                else
                {
                    _duration = Mathf.Clamp(
                        _playbackSession.Snapshot.GetLength(index) / previousNominalSpeed,
                        PathMovementSettingsUtility.MIN_VALUE,
                        PathMovementSettingsUtility.MAX_VALUE);
                }
            }
            else if (settings.MoveType == EPathMoveType.SpeedBased)
                _speed = settings.Value;
            else
                _duration = settings.Value;
        }

        private float GetCurrentNominalSpeed()
        {
            if (_playbackSession?.Sequence != null
                && _playbackSession.Snapshot != null
                && _currentSegmentIndex >= 0)
            {
                if (_moveType == EPathMoveType.SpeedBased)
                    return _speed;
                return _duration > PathMovementSettingsUtility.MIN_VALUE
                    ? _playbackSession.Snapshot.GetLength(_currentSegmentIndex) / _duration
                    : 0f;
            }

            if (_moveType == EPathMoveType.SpeedBased)
                return _speed;
            return _duration > PathMovementSettingsUtility.MIN_VALUE
                && _playbackSession != null
                ? _playbackSession.Provider.PathLength / _duration
                : 0f;
        }

        private void CompletePlayback(
            IPathEventSource source)
        {
            _isMoving = false;
            _pendingDeltaTime = 0f;
            _normalizedTime = 1f;
            _globalNormalizedTime = 1f;
            _playbackRevision++;
            int completionRevision = _playbackRevision;
            ulong completedPlaybackId = PlaybackId;
            ApplyCurrentPosition();
            FlushEvents(source, completionRevision);
            if (_playbackRevision != completionRevision)
                return;
            _pathEventHandler?.CancelPlaybackEffects(completedPlaybackId);
            _playbackEngine.InvalidatePlayback();
            DisposePlaybackScope();
            SetState(EPathFollowerState.Completed);
            if (_playbackRevision != completionRevision)
                return;
            InvokeCompleted();
        }

        private bool RefreshProviderSnapshotIfNeeded()
        {
            if (_playbackSession == null || !_playbackSession.Provider.IsReady)
            {
                StopMove();
                return false;
            }
            if (_playbackSession.Provider.Revision == _playbackSession.ProviderRevision)
                return true;

            if (_playbackSession.Sequence != null)
            {
                if (_playbackSession.Sequence.SegmentCount
                    != _playbackSession.Snapshot.Count)
                {
                    StopMove();
                    return false;
                }

                if (!PathSequenceSnapshot.TryCreate(
                        _playbackSession.Sequence,
                        out PathSequenceSnapshot next,
                        out _)
                    || !next.HasSameStructure(_playbackSession.Snapshot))
                {
                    StopMove();
                    return false;
                }

                _playbackSession.Snapshot = next;
                _playbackSession.ProviderRevision = _playbackSession.Provider.Revision;
                if (_moveType == EPathMoveType.SpeedBased)
                    _segmentDistance = _playbackSession.Snapshot.GetLength(
                        _currentSegmentIndex) * _normalizedTime;
                else
                    _segmentElapsed = InverseTimeProgress(
                        _normalizedTime,
                        _timeCurve) * _duration;
                _globalNormalizedTime = _playbackSession.Snapshot.GetGlobalProgress(
                    _currentSegmentIndex,
                    _normalizedTime);
                ApplyCurrentPosition();
                return true;
            }

            if (_playbackSession.Provider is IPathMovementProvider movementProvider)
            {
                PathMovementSettings current = movementProvider.MovementSettings;
                if (!_playbackSession.ProviderMovementSettings.HasValue
                    || !PathMovementSettingsUtility.AreSame(
                        _playbackSession.ProviderMovementSettings.Value,
                        current))
                {
                    StopMove();
                    return false;
                }
            }

            IPathEventSource currentEventSource =
                _playbackSession.Provider as IPathEventSource;
            if (!_playbackSession.EventSource.Matches(currentEventSource))
            {
                StopMove();
                return false;
            }

            _playbackSession.ProviderRevision = _playbackSession.Provider.Revision;
            _playbackSession.EventSource = PathEventSourceSnapshot.Create(
                currentEventSource);
            _normalizedTime = Mathf.Clamp01(_normalizedTime);
            _globalNormalizedTime = _normalizedTime;
            if (_moveType == EPathMoveType.SpeedBased)
                _segmentDistance = _playbackSession.Provider.PathLength * _normalizedTime;
            else
                _segmentElapsed = InverseTimeProgress(
                    _normalizedTime,
                    _timeCurve) * _duration;
            ApplyCurrentPosition();
            return true;
        }

        private void SetSequenceGlobalProgress(float progress)
        {
            int index = _playbackSession.Snapshot.FindSegment(progress);
            float local = _playbackSession.Snapshot.GetLocalProgress(index, progress);
            SetSequenceSegmentProgress(index, local);
        }

        private void SetSequenceSegmentProgress(int index, float local)
        {
            if (index != _currentSegmentIndex)
                ConfigureSequenceSegment(index, 0f, false);
            _currentSegmentIndex = index;
            _normalizedTime = Mathf.Clamp01(local);
            _segmentElapsed = _moveType == EPathMoveType.TimeBased
                ? InverseTimeProgress(_normalizedTime, _timeCurve) * _duration
                : 0f;
            _segmentDistance = _moveType == EPathMoveType.SpeedBased
                ? _playbackSession.Snapshot.GetLength(index) * _normalizedTime
                : 0f;
            _globalNormalizedTime = _playbackSession.Snapshot.GetGlobalProgress(
                index,
                _normalizedTime);
            _playbackSession.EventCursor.Reset(
                _playbackSession.Snapshot.GetEventSource(index),
                _normalizedTime);
        }

        private void ApplyCurrentPosition()
        {
            if (_playbackSession == null || !_playbackSession.Provider.IsReady)
                return;

            Vector3 position = _playbackSession.Sequence == null
                ? _playbackSession.Provider.Sample(_normalizedTime)
                : _playbackSession.Snapshot.Sample(_currentSegmentIndex, _normalizedTime);
            transform.position = position;
            Vector3 ahead = _playbackSession.Sequence == null
                ? _playbackSession.Provider.Sample(Mathf.Min(1f, _normalizedTime + 0.001f))
                : _playbackSession.Snapshot.Sample(
                    _currentSegmentIndex,
                    Mathf.Min(1f, _normalizedTime + 0.001f));
            Vector3 direction = ahead - position;
            if (direction.sqrMagnitude > EPSILON)
                transform.rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
        }

        private void DispatchEvents(
            IPathEventSource source,
            float progress,
            int tickRevision)
        {
            if (_playbackSession == null
                || _playbackSession.EventCursor == null
                || source == null)
                return;

            while (_playbackSession.EventCursor.HasNext(source))
            {
                PathRuntimeEvent entry = source.GetEvent(
                    _playbackSession.EventCursor.NextIndex);
                if (entry.NormalizedTime > progress + EPSILON)
                    break;
                _playbackSession.EventCursor.NextIndex++;
                if (entry.Definition == null)
                    continue;
                _pathEventHandler?.HandleEvent(
                    entry.Definition,
                    _eventTarget ?? this);
                if (_playbackRevision != tickRevision)
                    return;
            }
        }

        private void FlushEvents(IPathEventSource source, int tickRevision)
        {
            DispatchEvents(source, 1f, tickRevision);
        }

        private void EnsureInitialized()
        {
            if (!_isInitialized)
                throw new InvalidOperationException("PathFollower is not initialized.");
        }

        private static float EvaluateTimeProgress(
            float rawProgress,
            AnimationCurve curve)
        {
            return Mathf.Clamp01(
                curve == null
                    ? rawProgress
                    : curve.Evaluate(Mathf.Clamp01(rawProgress)));
        }

        private static void ValidateMovementValue(float value, string parameterName)
        {
            if (!PathValueUtility.IsInRange(
                    value,
                    PathMovementSettingsUtility.MIN_VALUE,
                    PathMovementSettingsUtility.MAX_VALUE))
                throw new ArgumentOutOfRangeException(parameterName);
        }

        private static void ValidateFinite(float value, string parameterName)
        {
            if (!PathValueUtility.IsFinite(value))
                throw new ArgumentOutOfRangeException(parameterName);
        }

        private void StopPlaybackOnly()
        {
            _isMoving = false;
            _pendingDeltaTime = 0f;
            _playbackRevision++;
            ApplyAnimatorSpeed(0f);
        }

        private void DisposePlaybackScope()
        {
            if (_playbackScope == null)
                return;
            _playbackScope.Dispose();
            _playbackScope = null;
        }

        private static float InverseTimeProgress(
            float progress,
            AnimationCurve curve)
        {
            float target = Mathf.Clamp01(progress);
            if (target <= 0f || target >= 1f || curve == null)
                return target;

            float low = 0f;
            float high = 1f;
            for (int i = 0; i < 32; i++)
            {
                float middle = (low + high) * 0.5f;
                float value = Mathf.Clamp01(curve.Evaluate(middle));
                if (value >= target)
                    high = middle;
                else
                    low = middle;
            }
            return high;
        }

        private static bool SameQueueRegistration(
            PathQueueRegistration left,
            PathQueueRegistration right)
        {
            return left.IsValid
                && right.IsValid
                && left.BelongsTo(right.Owner)
                && left.RegistrationId == right.RegistrationId
                && left.PlaybackId == right.PlaybackId;
        }

        private PathCommandReceipt CreateReceipt(bool changed)
        {
            return _playbackEngine.CreateReceipt(changed);
        }

        private void SetState(EPathFollowerState state)
        {
            if (_state == state)
                return;
            _state = state;
            InvokeStateChanged(state);
        }

        private void ApplyAnimatorSpeed(float value)
        {
            _animatorView?.ApplyPlaybackSpeed(value);
        }

        private void InvokeStateChanged(EPathFollowerState value)
        {
            Delegate[] listeners = _stateChangedInvocationList;
            if (listeners == null)
                return;

            int revision = _playbackRevision;
            ulong playbackId = PlaybackId;
            ulong stateRevision = StateRevision;
            for (int i = 0; i < listeners.Length; i++)
            {
                try
                {
                    ((Action<EPathFollowerState>)listeners[i])(value);
                }
                catch
                {
                    CleanupAfterCallbackFailure(playbackId);
                    throw;
                }
                if (_playbackRevision != revision
                    || PlaybackId != playbackId
                    || StateRevision != stateRevision)
                    return;
            }
        }

        private void InvokeSegmentChanged(int value, int tickRevision)
        {
            Delegate[] listeners = _segmentChangedInvocationList;
            if (listeners == null)
                return;

            ulong playbackId = PlaybackId;
            ulong stateRevision = StateRevision;
            for (int i = 0; i < listeners.Length; i++)
            {
                try
                {
                    ((Action<int>)listeners[i])(value);
                }
                catch
                {
                    CleanupAfterCallbackFailure(playbackId);
                    throw;
                }
                if (_playbackRevision != tickRevision
                    || PlaybackId != playbackId
                    || StateRevision != stateRevision)
                    return;
            }
        }

        private void InvokeLoopBoundary(int tickRevision)
        {
            Delegate[] listeners = _loopBoundaryInvocationList;
            if (listeners == null)
                return;

            ulong playbackId = PlaybackId;
            ulong stateRevision = StateRevision;
            for (int i = 0; i < listeners.Length; i++)
            {
                try
                {
                    ((Action)listeners[i])();
                }
                catch
                {
                    CleanupAfterCallbackFailure(playbackId);
                    throw;
                }
                if (_playbackRevision != tickRevision
                    || PlaybackId != playbackId
                    || StateRevision != stateRevision)
                    return;
            }
        }

        private void InvokeCompleted()
        {
            Delegate[] listeners = _completedInvocationList;
            if (listeners == null)
                return;

            int revision = _playbackRevision;
            ulong playbackId = PlaybackId;
            ulong stateRevision = StateRevision;
            for (int i = 0; i < listeners.Length; i++)
            {
                try
                {
                    ((Action)listeners[i])();
                }
                catch
                {
                    CleanupAfterCallbackFailure(playbackId);
                    throw;
                }
                if (_playbackRevision != revision
                    || PlaybackId != playbackId
                    || StateRevision != stateRevision)
                    return;
            }
        }

        private void CleanupAfterCallbackFailure(ulong failedPlaybackId)
        {
            if (failedPlaybackId == 0 || PlaybackId != failedPlaybackId)
                return;

            _pathEventHandler?.CancelPlaybackEffects(failedPlaybackId);
            StopPlaybackOnly();
            _playbackEngine.InvalidatePlayback();
            DisposePlaybackScope();
            _state = _isInitialized
                ? EPathFollowerState.Ready
                : EPathFollowerState.Uninitialized;
        }

        #endregion
    }
}
