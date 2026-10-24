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

        [Header("Movement Collision")]
        [SerializeField] private CharacterController _characterController;
        [SerializeField] private bool _suppressCharacterControllerDuringPlayback = true;

        // 재생 중 경로 진행 방향으로 회전을 추종할지 여부.
        // 에스컬레이터처럼 진입 시 한 번만 정렬하고 이후 회전을 고정해야 하는 연출에서 런타임에 끈다.
        private bool _rotateToPathDirection = true;

        private bool _characterControllerDisabledByFollower;
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
        private static ulong _nextPlaybackOwnerId;
        private readonly ulong _playbackOwnerId = ++_nextPlaybackOwnerId;
        [NonSerialized] private ulong _nextPlaybackId;
        [NonSerialized] private ulong _playbackId;
        [NonSerialized] private ulong _stateRevision;
        [NonSerialized] private ulong _resourcePlaybackId;
        private IPathFollower _eventTarget;
        private bool _queueBlocked;
        private float _queueSpeedMultiplier = 1f;
        private float _queueMaxGlobalNormalizedTime = 1f;
        private PathQueueRegistration _queueConstraintRegistration;
        private int _queueConstraintFrameId = -1;
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
        int IPathRuntimeTickable.RuntimeOrder => -100;
        public bool IsMoving => _isMoving;
        public IPathProvider CurrentProvider => _playbackSession?.Provider;
        public IPathSequenceProvider CurrentSequence => _playbackSession?.Sequence;
        public EPathFollowerState State => _state;
        public float NormalizedTime => _normalizedTime;
        public float GlobalNormalizedTime => _globalNormalizedTime;
        public int CurrentSegmentIndex => _currentSegmentIndex;
        public int SnapshotRevision => _playbackSession?.ProviderRevision ?? -1;
        public EPathMoveType MoveType => _moveType;
        public ulong PlaybackId => _playbackId;
        public ulong StateRevision => _stateRevision;
        ulong IPathPlaybackIdentity.PlaybackOwnerId => _playbackOwnerId;

        public float Speed
        {
            get => _speed;
        }

        public float Duration
        {
            get => _duration;
        }

        /// <summary>
        /// 재생 중 경로 진행 방향으로 회전을 추종하는지 여부.
        /// </summary>
        public bool RotateToPathDirection => _rotateToPathDirection;

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
            if (_characterController == null)
                TryGetComponent(out _characterController);
            if (_pathEventHandler != null && !_pathEventHandler.IsInitialized)
                _pathEventHandler.Init();
            _pathEventHandler?.Bind(this);
            _eventTarget = this;

            _isInitialized = true;
            _state = EPathFollowerState.Ready;
            _playbackRevision++;
            _stateRevision++;
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
                // The release that ended the previous playback must not tear it down.
                return stopReceipt;
            }
            _playbackSession = null;
            _isInitialized = false;
            _stateRevision++;
            PathCommandReceipt receipt = CreateReceipt(true);
            SetState(EPathFollowerState.Uninitialized);
            return receipt;
        }

        private void Reset()
        {
            TryGetComponent(out _pathEventHandler);
            TryGetComponent(out _characterController);
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

                // Events and completion callbacks can release the session or
                // replace playback. Check the execution before touching it again.
                if (!_isMoving || _playbackRevision != tickRevision)
                    return;
                if (transitioned)
                    transitionCount++;
                if (consumed <= EPSILON && !transitioned)
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
                StopPlaybackOnly();
                EndPlayback();
                SetState(EPathFollowerState.Ready);
                TryRestoreCharacterController();
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
                TryRestoreCharacterController();
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

        /// <summary>
        /// 재생 중 경로 진행 방향으로 회전을 추종할지 설정합니다.
        /// false로 끄면 위치만 갱신되고 transform.rotation은 유지됩니다.
        /// </summary>
        public void SetRotateToPathDirection(bool rotate)
        {
            _rotateToPathDirection = rotate;
        }

        /// <summary>
        /// 현재 재생 위치의 경로 진행 방향으로 회전을 1회 정렬합니다.
        /// 추종을 끈 뒤(<see cref="SetRotateToPathDirection"/>) 진입 시점 정렬용으로 사용합니다.
        /// <paramref name="axes"/>에 포함된 euler 축만 경로 방향을 반영하고, 나머지 축은 현재값을 유지합니다.
        /// </summary>
        /// <param name="axes">경로 방향을 적용할 회전 축. 기본값은 전 축(<see cref="EPathAlignRotationAxes.All"/>).</param>
        /// <returns>정렬에 성공하면 true.</returns>
        public bool AlignRotationToPathDirection(
            EPathAlignRotationAxes axes = EPathAlignRotationAxes.All)
        {
            if (axes == EPathAlignRotationAxes.None)
                return false;

            if (!TryEvaluatePathDirection(out Vector3 direction))
                return false;

            Quaternion pathRotation = Quaternion.LookRotation(direction, Vector3.up);

            if (axes == EPathAlignRotationAxes.All)
            {
                transform.rotation = pathRotation;
                return true;
            }

            Vector3 pathEuler = pathRotation.eulerAngles;
            Vector3 currentEuler = transform.rotation.eulerAngles;

            if (!axes.HasFlag(EPathAlignRotationAxes.X))
                pathEuler.x = currentEuler.x;
            if (!axes.HasFlag(EPathAlignRotationAxes.Y))
                pathEuler.y = currentEuler.y;
            if (!axes.HasFlag(EPathAlignRotationAxes.Z))
                pathEuler.z = currentEuler.z;

            transform.rotation = Quaternion.Euler(pathEuler);
            return true;
        }

        public PathCommandReceipt StartPlayback(PathPlaybackRequest request)
        {
            return StartPreparedPlayback(request, PreparePlayback(request));
        }

        internal PathPlaybackSession PreparePlayback(PathPlaybackRequest request)
        {
            EnsureInitialized();
            PathPlaybackSession session = PathPlaybackSession.CreateOrReuse(
                request,
                _playbackSession);
            if (session.Kind == EPathPlaybackKind.Sequence)
                _pathEventHandler?.PrepareForSequence(this, session.Snapshot);
            else
                _pathEventHandler?.PrepareForPlayback(
                    this,
                    session.EventSource,
                    session.MovementSettings.MoveType);
            return session;
        }

        internal PathCommandReceipt StartPreparedPlayback(
            PathPlaybackRequest request,
            PathPlaybackSession session)
        {
            EnsureInitialized();
            // Preparation is local to this command. If a provider changed while
            // the queue reconciled ownership, validate a fresh candidate before
            // replacing the current playback. Normal starts reuse the candidate.
            if (!session.Provider.IsReady || session.Provider.Revision != session.ProviderRevision)
                session = PreparePlayback(request);
            return BeginPlayback(session, request.Loop);
        }

        public PathCommandReceipt StopMove()
        {
            if (_state == EPathFollowerState.Uninitialized
                || _state == EPathFollowerState.Ready)
                return CreateReceipt(false);
            StopPlaybackOnly();
            EndPlayback();
            PathCommandReceipt receipt = CreateReceipt(true);
            TryRestoreCharacterController();
            ApplyAnimatorSpeed(1f);
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
            _stateRevision++;
            _pathEventHandler?.CancelMovementAdjustments();
            PathCommandReceipt receipt = CreateReceipt(true);
            ApplyAnimatorSpeed(0f);
            SetState(EPathFollowerState.Paused);
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
            _stateRevision++;
            PathCommandReceipt receipt = CreateReceipt(true);
            ApplyAnimatorSpeed(1f);
            SetState(EPathFollowerState.Moving);
            return receipt;
        }

        public PathCommandReceipt Seek(float normalizedTime)
        {
            EnsureInitialized();
            if (_playbackSession == null)
                throw new InvalidOperationException("PathFollower has no active provider.");
            ValidateFinite(normalizedTime, nameof(normalizedTime));

            float targetProgress = Mathf.Clamp01(normalizedTime);
            if (_playbackSession.Sequence != null)
            {
                int targetSegment = _playbackSession.Snapshot.FindSegment(targetProgress);
                float targetLocal = _playbackSession.Snapshot.GetLocalProgress(
                    targetSegment,
                    targetProgress);
                return SeekTo(targetSegment, targetLocal);
            }
            return SeekTo(_currentSegmentIndex, targetProgress);
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

            return SeekTo(segmentIndex, local);
        }

        private PathCommandReceipt SeekTo(int segmentIndex, float local)
        {
            if (_currentSegmentIndex == segmentIndex
                && Mathf.Abs(_normalizedTime - local) <= EPSILON)
                return CreateReceipt(false);

            // A changed position owns a new tick boundary, but keeps PlaybackId.
            // Both seek APIs discard the old frame's remaining movement budget.
            _pendingDeltaTime = 0f;
            _playbackRevision++;
            _queueConstraintFrameId = -1;
            if (_queueConstraintRegistration.IsValid)
                SetConstraintState(true, 0f, 0f);

            if (_playbackSession.Sequence != null)
                SetSequenceSegmentProgress(segmentIndex, local);
            else
            {
                _normalizedTime = local;
                _globalNormalizedTime = local;
                _segmentElapsed = PathProgressCalculator.InverseTimeProgress(local, _timeCurve) * _duration;
                _segmentDistance = _playbackSession.Provider.PathLength * local;
                _playbackSession.ResetEventCursor(_playbackSession.EventSource, local);
            }
            ApplyCurrentPosition();
            _stateRevision++;
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
            _stateRevision++;
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

            float normalized = _duration >= PathMovementSettingsUtility.MIN_VALUE
                ? PathProgressCalculator.EvaluateTimeProgress(_segmentElapsed / _duration, _timeCurve)
                : 0f;
            _duration = duration;
            _segmentElapsed = PathProgressCalculator.InverseTimeProgress(normalized, _timeCurve) * duration;
            _stateRevision++;
            return CreateReceipt(true);
        }

        /// <summary>
        /// Called once per frame by the queue runtime before this follower's
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
        }

        public bool AttachQueueConstraint(PathQueueRegistration registration)
        {
            if (!registration.IsValid || registration.PlaybackId == 0
                || registration.PlaybackId != PlaybackId)
                return false;

            _queueConstraintRegistration = registration;
            _queueConstraintFrameId = -1;
            SetConstraintState(true, 0f, 0f);
            return true;
        }

        public void ApplyQueueConstraint(
            PathQueueRegistration registration,
            PathQueueConstraint constraint)
        {
            if (!_queueConstraintRegistration.IsValid
                || !registration.Matches(_queueConstraintRegistration)
                || registration.PlaybackId != PlaybackId
                || !constraint.Registration.Matches(registration))
                return;
            // Registration identifies ownership for the whole playback. Geometry
            // revisions keep that ownership but must use freshly calculated limits,
            // including the blocked result sent before our snapshot refreshes.
            if (_playbackSession == null
                || constraint.RouteRevision != _playbackSession.Provider.Revision
                || constraint.FrameId < _queueConstraintFrameId)
                return;

            _queueConstraintFrameId = constraint.FrameId;
            SetConstraintState(
                constraint.IsBlocked,
                constraint.SpeedMultiplier,
                constraint.MaxGlobalNormalizedTime);
        }

        public void ReleaseQueueConstraint(PathQueueRegistration registration)
        {
            if (!_queueConstraintRegistration.IsValid
                || !registration.Matches(_queueConstraintRegistration))
                return;

            _queueConstraintRegistration = default(PathQueueRegistration);
            _queueConstraintFrameId = -1;
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
            if (PlaybackId != 0)
            {
                _playbackId = 0;
                _stateRevision++;
            }
            StopPlaybackOnly();
            ClearPlaybackResources();
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
                session.ResetEventCursor(
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
                session.ResetEventCursor(
                    session.EventSource,
                    0f);
            }
            ClearConstraintState();
            _isMoving = true;
            _playbackRevision++;
            _playbackId = ++_nextPlaybackId;
            _stateRevision++;
            _resourcePlaybackId = _playbackId;
            PathCommandReceipt receipt = CreateReceipt(true);
            int beginRevision = _playbackRevision;
            TrySuppressCharacterController();
            // Commit presentation before notifying. Nested commands own any
            // subsequent change, even when a callback only adjusts movement.
            ApplyAnimatorSpeed(1f);
            SetState(EPathFollowerState.Moving);
            // Value/rotation settings may change without replacing this start.
            // Stop, Pause, Seek and replacement playback end its tick boundary.
            if (_playbackRevision != beginRevision || PlaybackId != receipt.PlaybackId)
            {
                TryRestoreCharacterController();
                return receipt;
            }
            ApplyCurrentPosition();
            return receipt;
        }

        private float TickSingle(float availableTime, int tickRevision)
        {
            float multiplier = _queueSpeedMultiplier;
            bool timeBased = _moveType == EPathMoveType.TimeBased;
            float boundaryPosition;
            PathProgressStep step;
            if (timeBased)
            {
                boundaryPosition = Mathf.Max(_duration, PathMovementSettingsUtility.MIN_VALUE);
                if (multiplier <= EPSILON)
                    return availableTime;
                float currentProgress = PathProgressCalculator.EvaluateTimeProgress(
                    _segmentElapsed / boundaryPosition, _timeCurve);
                float maxProgress = Mathf.Clamp01(_queueMaxGlobalNormalizedTime);
                if (maxProgress < 1f && maxProgress <= currentProgress + EPSILON)
                    return 0f;
                step = PathProgressCalculator.AdvanceTime(
                    _segmentElapsed, boundaryPosition, _timeCurve, availableTime, multiplier, maxProgress);
                _segmentElapsed = step.Position;
            }
            else
            {
                boundaryPosition = Mathf.Max(
                    _playbackSession.Provider.PathLength, PathMovementSettingsUtility.MIN_VALUE);
                if (multiplier <= EPSILON)
                    return availableTime;
                float maximumDistance = Mathf.Clamp01(_queueMaxGlobalNormalizedTime) * boundaryPosition;
                if (_queueMaxGlobalNormalizedTime < 1f && maximumDistance <= _segmentDistance + EPSILON)
                    return 0f;
                step = PathProgressCalculator.AdvanceDistance(
                    _segmentDistance, boundaryPosition, _speed, availableTime, multiplier, _queueMaxGlobalNormalizedTime);
                _segmentDistance = step.Position;
            }

            _normalizedTime = step.Progress;
            _globalNormalizedTime = _normalizedTime;
            DispatchEvents(_playbackSession.EventSource, _normalizedTime, tickRevision);
            if (_playbackRevision != tickRevision)
                return step.ConsumedTime;

            if (_queueMaxGlobalNormalizedTime >= 1f && HasReachedSegmentEnd(boundaryPosition)
                && !TryProcessPlaybackBoundary(tickRevision, out _))
                return step.ConsumedTime;
            return Mathf.Max(step.ConsumedTime, EPSILON);
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
                float currentProgress = PathProgressCalculator.EvaluateTimeProgress(
                    _segmentElapsed / duration,
                    movementSettings.TimeCurve);
                float maxLocalProgress = snapshot.GetLocalProgress(
                    _currentSegmentIndex,
                    _queueMaxGlobalNormalizedTime);
                if (snapshot.GetGlobalProgress(_currentSegmentIndex, 1f) > _queueMaxGlobalNormalizedTime
                    && maxLocalProgress <= currentProgress + EPSILON)
                    return 0f;
                PathProgressStep step = PathProgressCalculator.AdvanceTime(
                    _segmentElapsed, duration, movementSettings.TimeCurve,
                    availableTime, multiplier, maxLocalProgress);
                consume = step.ConsumedTime;
                _segmentElapsed = step.Position;
                _normalizedTime = step.Progress;
            }
            else
            {
                float length = Mathf.Max(
                    snapshot.GetLength(_currentSegmentIndex),
                    PathMovementSettingsUtility.MIN_VALUE);
                float maxLocalProgress = snapshot.GetLocalProgress(
                    _currentSegmentIndex, _queueMaxGlobalNormalizedTime);
                if (snapshot.GetGlobalProgress(_currentSegmentIndex, 1f) > _queueMaxGlobalNormalizedTime
                    && maxLocalProgress <= _normalizedTime + EPSILON)
                    return 0f;
                PathProgressStep step = PathProgressCalculator.AdvanceDistance(
                    _segmentDistance, length, _speed, availableTime, multiplier, maxLocalProgress);
                consume = step.ConsumedTime;
                _segmentDistance = step.Position;
                _normalizedTime = step.Progress;
            }

            _normalizedTime = Mathf.Min(_normalizedTime, snapshot.GetLocalProgress(
                _currentSegmentIndex, _queueMaxGlobalNormalizedTime));
            _globalNormalizedTime = Mathf.Min(_queueMaxGlobalNormalizedTime, snapshot.GetGlobalProgress(
                _currentSegmentIndex,
                _normalizedTime));
            DispatchEvents(
                snapshot.GetEventSource(_currentSegmentIndex),
                _normalizedTime,
                tickRevision);
            if (_playbackRevision != tickRevision)
                return Mathf.Max(consume, EPSILON);

            float boundaryProgress = snapshot.GetGlobalProgress(_currentSegmentIndex, 1f);
            bool boundaryAllowed = boundaryProgress <= _queueMaxGlobalNormalizedTime;
            if (boundaryAllowed && HasReachedSegmentEnd(snapshot.GetLength(_currentSegmentIndex)))
                TryProcessPlaybackBoundary(tickRevision, out transitioned);

            return Mathf.Max(consume, EPSILON);
        }

        private bool HasReachedSegmentEnd(float segmentLength)
        {
            // SetDuration preserves progress by rescaling elapsed time. Compare
            // that elapsed time with the current duration, after event callbacks.
            return _moveType == EPathMoveType.TimeBased
                ? _segmentElapsed >= _duration - EPSILON
                : _segmentDistance >= segmentLength - EPSILON;
        }

        private bool TryProcessPlaybackBoundary(int tickRevision, out bool transitioned)
        {
            transitioned = false;
            bool sequence = _playbackSession.Sequence != null;
            PathSequenceSnapshot snapshot = _playbackSession.Snapshot;
            IPathEventSource source = sequence
                ? snapshot.GetEventSource(_currentSegmentIndex)
                : _playbackSession.EventSource;
            _normalizedTime = 1f;
            _globalNormalizedTime = sequence
                ? snapshot.GetGlobalProgress(_currentSegmentIndex, 1f)
                : 1f;
            bool hasNextSegment = sequence && _currentSegmentIndex + 1 < snapshot.Count;
            if (!hasNextSegment && !_loop)
            {
                CompletePlayback(source);
                return true;
            }

            if (!ApplyBoundaryPositionAndFlushEvents(source, tickRevision))
                return false;
            if (sequence)
            {
                AdvanceToSegment(hasNextSegment ? _currentSegmentIndex + 1 : 0,
                    tickRevision, !hasNextSegment);
                transitioned = true;
                return true;
            }

            // Single/Aggregate notify at the endpoint. Sequence resets its
            // segment before notifying; preserve that observable distinction.
            InvokeLoopBoundary(tickRevision);
            if (_playbackRevision != tickRevision)
                return false;
            _segmentElapsed = 0f;
            _segmentDistance = 0f;
            _normalizedTime = 0f;
            _globalNormalizedTime = 0f;
            _playbackSession.ResetEventCursor(source, 0f);
            return true;
        }

        private bool ApplyBoundaryPositionAndFlushEvents(IPathEventSource source, int tickRevision)
        {
            ApplyCurrentPosition();
            FlushEvents(source, tickRevision);
            return _playbackRevision == tickRevision;
        }

        private void AdvanceToSegment(int index, int tickRevision, bool loopBoundary)
        {
            float previousNominalSpeed = GetCurrentNominalSpeed();
            _currentSegmentIndex = index;
            _segmentElapsed = 0f;
            _segmentDistance = 0f;
            _normalizedTime = 0f;
            _globalNormalizedTime = _playbackSession.Snapshot.GetGlobalProgress(index, 0f);
            PathSegmentDescriptor descriptor = _playbackSession.Snapshot.GetDescriptor(index);
            ConfigureSequenceSegment(index, previousNominalSpeed, descriptor.PreservePreviousSpeed);
            _playbackSession.ResetEventCursor(
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
                return _duration >= PathMovementSettingsUtility.MIN_VALUE
                    ? _playbackSession.Snapshot.GetLength(_currentSegmentIndex) / _duration
                    : 0f;
            }

            if (_moveType == EPathMoveType.SpeedBased)
                return _speed;
            return _duration >= PathMovementSettingsUtility.MIN_VALUE
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
            if (!ApplyBoundaryPositionAndFlushEvents(source, completionRevision))
            {
                TryRestoreCharacterController();
                return;
            }
            EndPlayback();
            TryRestoreCharacterController();
            SetState(EPathFollowerState.Completed);
            if (_playbackRevision != completionRevision)
                return;
            InvokeCompleted();
        }

        private bool RefreshProviderSnapshotIfNeeded()
        {
            PathSequenceSnapshot nextSequence = null;
            PathEventSourceSnapshot nextEvents = null;
            EPathSnapshotRefreshDecision decision = _playbackSession == null
                ? EPathSnapshotRefreshDecision.StopInvalidProvider
                : _playbackSession.EvaluateRefresh(out nextSequence, out nextEvents);
            if (decision == EPathSnapshotRefreshDecision.Unchanged)
                return true;
            if (decision != EPathSnapshotRefreshDecision.Refresh)
            {
                StopMove();
                return false;
            }

            _playbackSession.ProviderRevision = _playbackSession.Provider.Revision;
            if (nextSequence != null)
            {
                _playbackSession.Snapshot = nextSequence;
                _globalNormalizedTime = nextSequence.GetGlobalProgress(
                    _currentSegmentIndex, _normalizedTime);
            }
            else
            {
                _playbackSession.EventSource = nextEvents;
                _normalizedTime = Mathf.Clamp01(_normalizedTime);
                _globalNormalizedTime = _normalizedTime;
            }

            float length = nextSequence != null
                ? nextSequence.GetLength(_currentSegmentIndex)
                : _playbackSession.Provider.PathLength;
            if (_moveType == EPathMoveType.SpeedBased)
                _segmentDistance = length * _normalizedTime;
            else
                _segmentElapsed = PathProgressCalculator.InverseTimeProgress(_normalizedTime, _timeCurve) * _duration;
            ApplyCurrentPosition();
            return true;
        }

        private void SetSequenceSegmentProgress(int index, float local)
        {
            if (index != _currentSegmentIndex)
                ConfigureSequenceSegment(index, 0f, false);
            _currentSegmentIndex = index;
            _normalizedTime = Mathf.Clamp01(local);
            _segmentElapsed = _moveType == EPathMoveType.TimeBased
                ? PathProgressCalculator.InverseTimeProgress(_normalizedTime, _timeCurve) * _duration
                : 0f;
            _segmentDistance = _moveType == EPathMoveType.SpeedBased
                ? _playbackSession.Snapshot.GetLength(index) * _normalizedTime
                : 0f;
            _globalNormalizedTime = _playbackSession.Snapshot.GetGlobalProgress(
                index,
                _normalizedTime);
            _playbackSession.ResetEventCursor(
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

            if (!_rotateToPathDirection)
                return;

            if (TryEvaluatePathDirection(out Vector3 direction))
                transform.rotation = Quaternion.LookRotation(direction, Vector3.up);
        }

        /// <summary>
        /// 현재 재생 위치에서의 경로 진행 방향(정규화)을 계산합니다.
        /// </summary>
        private bool TryEvaluatePathDirection(out Vector3 direction)
        {
            direction = Vector3.zero;

            if (_playbackSession == null || !_playbackSession.Provider.IsReady)
                return false;

            float aheadNormalizedTime = Mathf.Min(1f, _normalizedTime + 0.001f);
            Vector3 position = _playbackSession.Sequence == null
                ? _playbackSession.Provider.Sample(_normalizedTime)
                : _playbackSession.Snapshot.Sample(_currentSegmentIndex, _normalizedTime);
            Vector3 ahead = _playbackSession.Sequence == null
                ? _playbackSession.Provider.Sample(aheadNormalizedTime)
                : _playbackSession.Snapshot.Sample(_currentSegmentIndex, aheadNormalizedTime);
            Vector3 delta = ahead - position;

            if (delta.sqrMagnitude <= EPSILON)
                return false;

            direction = delta.normalized;
            return true;
        }

        private void DispatchEvents(
            IPathEventSource source,
            float progress,
            int tickRevision)
        {
            if (_playbackSession == null
                || source == null)
                return;

            while (_playbackSession.NextEventIndex < source.EventCount)
            {
                PathRuntimeEvent entry = source.GetEvent(
                    _playbackSession.NextEventIndex);
                if (entry.NormalizedTime > progress + EPSILON)
                    break;
                _playbackSession.NextEventIndex++;
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

        private void EndPlayback()
        {
            _playbackId = 0;
            _stateRevision++;
            ClearPlaybackResources();
        }

        private void ClearPlaybackResources()
        {
            ulong playbackId = _resourcePlaybackId;
            _resourcePlaybackId = 0;
            if (playbackId == 0)
                return;
            try
            {
                ReleasePlaybackResources(playbackId);
            }
            catch
            {
                // Preserve best-effort cleanup: lifecycle notification must
                // still run if one of the owned resources fails to release.
            }
        }

        private void ReleasePlaybackResources(ulong playbackId)
        {
            // An older playback must not clear effects or constraints installed by
            // a replacement playback during a callback.
            if (PlaybackId != 0 && PlaybackId != playbackId)
                return;

            try
            {
                _pathEventHandler?.CancelPlaybackEffects(playbackId);
            }
            finally
            {
                if (_queueConstraintRegistration.PlaybackId == playbackId)
                    ReleaseQueueConstraint(_queueConstraintRegistration);
            }
        }

        private PathCommandReceipt CreateReceipt(bool changed)
        {
            return new PathCommandReceipt(_playbackOwnerId, PlaybackId, StateRevision, changed);
        }

        private void SetState(EPathFollowerState state)
        {
            if (_state == state)
                return;
            _state = state;
            InvokeStateChanged(state);
        }

        // Path playback uses 0 for pause and 1 for moving/ready; no stored speed restore.
        private void ApplyAnimatorSpeed(float value)
        {
            _animatorView?.ApplyPlaybackSpeed(value);
        }

        private void InvokeStateChanged(EPathFollowerState value)
        {
            InvokeCallbacks(_stateChangedInvocationList, _playbackRevision, value,
                (listener, state) => ((Action<EPathFollowerState>)listener)(state));
        }

        private void InvokeSegmentChanged(int value, int tickRevision)
        {
            InvokeCallbacks(_segmentChangedInvocationList, tickRevision, value,
                (listener, segment) => ((Action<int>)listener)(segment));
        }

        private void InvokeLoopBoundary(int tickRevision)
        {
            InvokeCallbacks(_loopBoundaryInvocationList, tickRevision, 0,
                (listener, _) => ((Action)listener)());
        }

        private void InvokeCompleted()
        {
            InvokeCallbacks(_completedInvocationList, _playbackRevision, 0,
                (listener, _) => ((Action)listener)());
        }

        private void InvokeCallbacks<T>(
            Delegate[] listeners, int revision, T value, Action<Delegate, T> invoke)
        {
            if (listeners == null)
                return;

            ulong playbackId = PlaybackId;
            ulong stateRevision = StateRevision;
            for (int i = 0; i < listeners.Length; i++)
            {
                try
                {
                    invoke(listeners[i], value);
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

            StopPlaybackOnly();
            EndPlayback();
            _state = _isInitialized
                ? EPathFollowerState.Ready
                : EPathFollowerState.Uninitialized;
            TryRestoreCharacterController();
        }

        private void TrySuppressCharacterController()
        {
            if (!_suppressCharacterControllerDuringPlayback
                || _characterController == null
                || !_characterController.enabled)
                return;

            _characterController.enabled = false;
            _characterControllerDisabledByFollower = true;
        }

        private void TryRestoreCharacterController()
        {
            if (!_characterControllerDisabledByFollower
                || _characterController == null)
                return;

            // A callback can replace or pause playback before an older command
            // finishes cleanup. The active session still owns suppression.
            if (isActiveAndEnabled && PlaybackId != 0
                && (_isMoving || _state == EPathFollowerState.Paused))
                return;

            _characterController.enabled = true;
            _characterControllerDisabledByFollower = false;
        }

        #endregion
    }
}
