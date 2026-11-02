using System;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// Single driver-driven path playback. A playback session owns the current
    /// provider snapshot and event cursor, the cursor owns the position on the
    /// route, so no coroutine or per-frame temporary object is required.
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
        private readonly PathPlaybackCursor _cursor = new PathPlaybackCursor();
        private readonly PathQueueConstraintState _constraint = new PathQueueConstraintState();
        private static ulong _nextPlaybackOwnerId;
        private readonly ulong _playbackOwnerId = ++_nextPlaybackOwnerId;
        [NonSerialized] private ulong _nextPlaybackId;
        [NonSerialized] private ulong _playbackId;
        [NonSerialized] private ulong _stateRevision;
        private IPathFollower _eventTarget;
        private readonly PathListeners _segmentChanged = new PathListeners();
        private readonly PathListeners _stateChanged = new PathListeners();
        private readonly PathListeners _completed = new PathListeners();

        #endregion


        #region Properties

        public bool IsInitialized => _isInitialized;
        int IPathRuntimeTickable.RuntimeOrder => -100;
        public bool IsMoving => _isMoving;
        public IPathProvider CurrentProvider => _playbackSession?.Provider;
        public IPathSequenceProvider CurrentSequence => _playbackSession?.Sequence;
        public EPathFollowerState State => _state;
        public float NormalizedTime => _cursor.Normalized;
        public float GlobalNormalizedTime => _cursor.Global;
        public int CurrentSegmentIndex => _cursor.Segment;
        public int SnapshotRevision => _playbackSession?.ProviderRevision ?? -1;
        public EPathMoveType MoveType => _cursor.MoveType;
        public ulong PlaybackId => _playbackId;
        public ulong StateRevision => _stateRevision;
        ulong IPathPlaybackIdentity.PlaybackOwnerId => _playbackOwnerId;
        public float Speed => _cursor.Speed;
        public float Duration => _cursor.Duration;

        public event Action<EPathFollowerState> StateChanged
        {
            add => _stateChanged.Add(value);
            remove => _stateChanged.Remove(value);
        }
        public event Action<int> SegmentChanged
        {
            add => _segmentChanged.Add(value);
            remove => _segmentChanged.Remove(value);
        }
        // The queue adapter reacts inside the operation that changed the state.
        // Public listeners wait until that operation has returned.
        internal event Action<EPathFollowerState> ObservedStateChanged;
        internal event Action<int> ObservedSegmentChanged;
        internal event Action ObservedCompleted;
        internal event Action LoopBoundary;
        public event Action Completed
        {
            add => _completed.Add(value);
            remove => _completed.Remove(value);
        }

        #endregion


        #region Unity Events

        public PathCommandReceipt Init()
        {
            if (_isInitialized)
                return CreateReceipt(false);

            using var operation = PathExecutionBoundary.Enter();
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
            _stateRevision++;
            PathCommandReceipt receipt = CreateReceipt(true);
            NotifyStateChanged(_state);
            return receipt;
        }

        public PathCommandReceipt Release()
        {
            if (!_isInitialized && _playbackSession == null)
                return CreateReceipt(false);

            using var operation = PathExecutionBoundary.Enter();
            StopMove();
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
        }

        private void OnEnable()
        {
            if (!Application.isPlaying)
                return;
            if (!_isInitialized)
                Init();
            PathRuntimeDriverBehaviour.Instance.Register(this);
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
            using var operation = PathExecutionBoundary.Enter();
            if (!RefreshProviderSnapshotIfNeeded())
                return;

            if (deltaTime <= 0f)
                return;

            if (_constraint.IsBlocked)
            {
                _cursor.PendingTime = 0f;
                ApplyCurrentPosition();
                return;
            }

            float remaining = deltaTime + _cursor.PendingTime;
            _cursor.PendingTime = 0f;
            if (remaining <= 0f)
                return;

            int transitionCount = 0;
            while (remaining > EPSILON
                && transitionCount < MAX_SEGMENT_TRANSITIONS_PER_TICK)
            {
                float consumed = AdvanceOnce(remaining, out bool transitioned);

                // A Pause event or the end of the path stops the tick here. The end
                // has already applied its position.
                if (!_isMoving)
                {
                    if (_state == EPathFollowerState.Paused)
                        ApplyCurrentPosition();
                    return;
                }
                if (transitioned)
                    transitionCount++;
                if (consumed <= EPSILON && !transitioned)
                    break;
                remaining -= consumed;
            }

            if (remaining > EPSILON
                && transitionCount >= MAX_SEGMENT_TRANSITIONS_PER_TICK)
                _cursor.PendingTime = remaining;

            ApplyCurrentPosition();
        }

        void IPathRuntimeFaultHandler.HandleRuntimeFault(
            ulong playbackId,
            System.Exception exception)
        {
            if (playbackId == 0 || PlaybackId != playbackId)
                return;
            using var operation = PathExecutionBoundary.Enter();
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
                ApplyAnimatorSpeed(1f);
                SetState(EPathFollowerState.Ready);
                TryRestoreCharacterController();
            }
        }

        private void OnDisable()
        {
            if (Application.isPlaying)
            {
                if (PathRuntimeDriverBehaviour.HasInstance)
                    PathRuntimeDriverBehaviour.Instance.Unregister(this);
                StopMove();
                TryRestoreCharacterController();
            }
        }

        private void OnDestroy()
        {
            if (PathRuntimeDriverBehaviour.HasInstance)
                PathRuntimeDriverBehaviour.Instance.Unregister(this);
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
            return StartPreparedPlayback(PreparePlayback(request), request.Loop);
        }

        internal PathPlaybackSession PreparePlayback(PathPlaybackRequest request)
        {
            EnsureInitialized();
            PathPlaybackSession session = PathPlaybackSession.CreateOrReuse(
                request,
                _playbackSession);
            if (session.IsSequence)
                _pathEventHandler?.PrepareForSequence(this, session.Snapshot);
            else
                _pathEventHandler?.PrepareForPlayback(
                    this,
                    session.EventSource,
                    session.MovementSettings.MoveType);
            return session;
        }

        internal PathCommandReceipt StartPreparedPlayback(PathPlaybackSession session, bool loop)
        {
            EnsureInitialized();
            return BeginPlayback(session, loop);
        }

        public PathCommandReceipt StopMove()
        {
            if (_state == EPathFollowerState.Uninitialized
                || _state == EPathFollowerState.Ready)
                return CreateReceipt(false);
            using var operation = PathExecutionBoundary.Enter();
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

            using var operation = PathExecutionBoundary.Enter();
            _isMoving = false;
            _cursor.PendingTime = 0f;
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

            using var operation = PathExecutionBoundary.Enter();
            _isMoving = true;
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
            int targetSegment = _playbackSession.FindSegment(targetProgress);
            return SeekTo(targetSegment, _playbackSession.GetLocalProgress(targetSegment, targetProgress));
        }

        public PathCommandReceipt SeekSegment(int segmentIndex, float localNormalizedTime)
        {
            EnsureInitialized();
            if (_playbackSession == null || !_playbackSession.IsSequence)
                throw new InvalidOperationException("SeekSegment requires an active sequence.");
            if (segmentIndex < 0 || segmentIndex >= _playbackSession.SegmentCount)
                throw new ArgumentOutOfRangeException(nameof(segmentIndex));
            ValidateFinite(localNormalizedTime, nameof(localNormalizedTime));

            float local = Mathf.Clamp01(localNormalizedTime);
            if (local >= 1f && segmentIndex < _playbackSession.SegmentCount - 1)
            {
                segmentIndex++;
                local = 0f;
            }

            return SeekTo(segmentIndex, local);
        }

        private PathCommandReceipt SeekTo(int segmentIndex, float local)
        {
            if (!_playbackSession.Provider.IsReady)
                throw new InvalidOperationException("Seek requires a ready path provider.");
            if (_cursor.Segment == segmentIndex
                && Mathf.Abs(_cursor.Normalized - local) <= EPSILON)
                return CreateReceipt(false);

            // A changed position keeps PlaybackId. Both seek APIs discard the old
            // frame's remaining movement budget.
            _cursor.PendingTime = 0f;
            if (_constraint.HasRegistration)
                _constraint.WaitForNextResult();

            if (segmentIndex != _cursor.Segment)
                ConfigureSegment(segmentIndex, 0f, false);
            _cursor.MoveTo(_playbackSession, segmentIndex, local);
            _playbackSession.ResetEventCursor(
                _playbackSession.GetEventSource(segmentIndex),
                _cursor.Normalized);
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
            if (_cursor.MoveType != EPathMoveType.SpeedBased)
                throw new InvalidOperationException("Speed can only be changed in SpeedBased mode.");
            if (Mathf.Approximately(_cursor.Speed, speed))
                return CreateReceipt(false);
            _cursor.SetSpeed(speed);
            _stateRevision++;
            return CreateReceipt(true);
        }

        public PathCommandReceipt SetDuration(float duration)
        {
            EnsureInitialized();
            ValidateMovementValue(duration, nameof(duration));
            if (_playbackSession == null)
                throw new InvalidOperationException("Duration can only be changed during playback.");
            if (_cursor.MoveType != EPathMoveType.TimeBased)
                throw new InvalidOperationException("Duration can only be changed in TimeBased mode.");
            if (Mathf.Approximately(_cursor.Duration, duration))
                return CreateReceipt(false);

            _cursor.SetDuration(duration);
            _stateRevision++;
            return CreateReceipt(true);
        }

        public bool AttachQueueConstraint(PathQueueRegistration registration)
        {
            if (!registration.IsValid || registration.PlaybackId == 0
                || registration.PlaybackId != PlaybackId)
                return false;

            _constraint.Attach(registration);
            return true;
        }

        public void ApplyQueueConstraint(
            PathQueueRegistration registration,
            PathQueueConstraint constraint)
        {
            if (registration.PlaybackId == PlaybackId && _playbackSession != null)
                _constraint.Apply(registration, constraint, _playbackSession.Provider.Revision);
        }

        public void ReleaseQueueConstraint(PathQueueRegistration registration)
        {
            _constraint.Release(registration);
        }

        internal void SetEventTarget(IPathFollower target)
        {
            _eventTarget = target ?? this;
            _pathEventHandler?.Bind(_eventTarget);
        }

        #endregion


        #region Private Methods

        private PathCommandReceipt BeginPlayback(
            PathPlaybackSession session,
            bool loop)
        {
            using var operation = PathExecutionBoundary.Enter();
            StopPlaybackOnly();
            if (PlaybackId != 0)
                EndPlayback();
            _playbackSession = session;
            _cursor.Begin(session, loop);
            session.RestartEventCursor();
            _constraint.Clear();
            _isMoving = true;
            _playbackId = ++_nextPlaybackId;
            _stateRevision++;
            PathCommandReceipt receipt = CreateReceipt(true);
            TrySuppressCharacterController();
            ApplyAnimatorSpeed(1f);
            SetState(EPathFollowerState.Moving);
            // The queue adapter can refuse the start inside SetState.
            if (_isMoving)
                ApplyCurrentPosition();
            else
                TryRestoreCharacterController();
            return receipt;
        }

        private float AdvanceOnce(float availableTime, out bool transitioned)
        {
            transitioned = false;
            float cap = _constraint.MaxGlobalProgress;
            float consumed = _cursor.Advance(
                _playbackSession,
                availableTime,
                _constraint.SpeedMultiplier,
                cap,
                out bool moved);
            if (!moved)
                return consumed;

            DispatchEvents(_playbackSession.GetEventSource(_cursor.Segment), _cursor.Normalized);
            if (_state == EPathFollowerState.Moving
                && _playbackSession.GetGlobalProgress(_cursor.Segment, 1f) <= cap
                && _cursor.HasReachedSegmentEnd(_playbackSession))
                ProcessSegmentEnd(out transitioned);
            return Mathf.Max(consumed, EPSILON);
        }

        private void ProcessSegmentEnd(out bool transitioned)
        {
            transitioned = false;
            IPathEventSource source = _playbackSession.GetEventSource(_cursor.Segment);
            _cursor.MoveToSegmentEnd(_playbackSession);
            bool hasNextSegment = _cursor.Segment + 1 < _playbackSession.SegmentCount;
            if (!hasNextSegment && !_cursor.Loop)
            {
                CompletePlayback(source);
                return;
            }

            if (!ApplyBoundaryPositionAndFlushEvents(source))
                return;
            EnterSegment(hasNextSegment ? _cursor.Segment + 1 : 0, !hasNextSegment);
            transitioned = true;
        }

        private bool ApplyBoundaryPositionAndFlushEvents(IPathEventSource source)
        {
            ApplyCurrentPosition();
            DispatchEvents(source, 1f);
            return _state == EPathFollowerState.Moving;
        }

        private void EnterSegment(int index, bool loopBoundary)
        {
            // A Single or Aggregate lap keeps the values set during the last one.
            if (_playbackSession.IsSequence)
                ConfigureSegment(
                    index,
                    _cursor.GetNominalSpeed(_playbackSession),
                    _playbackSession.PreservesPreviousSpeed(index));
            _cursor.EnterSegment(_playbackSession, index);
            _playbackSession.RestartEventCursor();
            if (loopBoundary)
                LoopBoundary?.Invoke();
            if (_playbackSession.IsSequence)
            {
                ObservedSegmentChanged?.Invoke(index);
                PathExecutionBoundary.PublishSegment(_segmentChanged, index);
            }
        }

        private void ConfigureSegment(int index, float previousNominalSpeed, bool preserveSpeed)
        {
            // An adjustment drives the channel of the segment that started it.
            if (_cursor.ConfigureSegment(_playbackSession, index, previousNominalSpeed, preserveSpeed))
                _pathEventHandler?.CancelMovementAdjustments();
        }

        private void CompletePlayback(IPathEventSource source)
        {
            _isMoving = false;
            _cursor.PendingTime = 0f;
            if (!ApplyBoundaryPositionAndFlushEvents(source))
            {
                TryRestoreCharacterController();
                return;
            }
            EndPlayback();
            TryRestoreCharacterController();
            SetState(EPathFollowerState.Completed);
            ObservedCompleted?.Invoke();
            PathExecutionBoundary.PublishPlain(_completed);
        }

        private bool RefreshProviderSnapshotIfNeeded()
        {
            EPathSnapshotRefreshDecision decision = _playbackSession.EvaluateRefresh(
                out PathSequenceSnapshot nextSequence,
                out PathEventSourceSnapshot nextEvents);
            if (decision == EPathSnapshotRefreshDecision.Unchanged)
                return true;
            if (decision != EPathSnapshotRefreshDecision.Refresh)
            {
                StopMove();
                return false;
            }

            _playbackSession.ApplyRefresh(nextSequence, nextEvents);
            _cursor.SyncToProvider(_playbackSession);
            ApplyCurrentPosition();
            return true;
        }

        private void ApplyCurrentPosition()
        {
            if (_playbackSession == null || !_playbackSession.Provider.IsReady)
                return;

            transform.position = _cursor.SamplePosition(_playbackSession);

            if (_rotateToPathDirection && TryEvaluatePathDirection(out Vector3 direction))
                transform.rotation = Quaternion.LookRotation(direction, Vector3.up);
        }

        private bool TryEvaluatePathDirection(out Vector3 direction)
        {
            direction = Vector3.zero;
            return _playbackSession != null
                && _playbackSession.Provider.IsReady
                && _cursor.TryEvaluateDirection(_playbackSession, out direction);
        }

        private void DispatchEvents(
            IPathEventSource source,
            float progress)
        {
            if (source == null)
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
                if (_state != EPathFollowerState.Moving)
                    return;
            }
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
            _cursor.PendingTime = 0f;
            ApplyAnimatorSpeed(0f);
        }

        private void EndPlayback()
        {
            ulong playbackId = _playbackId;
            _playbackId = 0;
            _stateRevision++;
            if (playbackId != 0)
                ReleasePlaybackResources(playbackId);
        }

        private void ReleasePlaybackResources(ulong playbackId)
        {
            try
            {
                _pathEventHandler?.CancelPlaybackEffects(playbackId);
            }
            catch
            {
                // Preserve best-effort cleanup: lifecycle notification must
                // still run if one of the owned resources fails to release.
            }

            if (_constraint.Registration.PlaybackId == playbackId)
                _constraint.Clear();
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
            NotifyStateChanged(state);
        }

        private void NotifyStateChanged(EPathFollowerState state)
        {
            ObservedStateChanged?.Invoke(state);
            // The queue adapter can stop a start it refuses. Listeners then hear
            // only the state that stands.
            if (_state == state)
                PathExecutionBoundary.PublishState(_stateChanged, state);
        }

        // Path playback uses 0 for pause and 1 for moving/ready; no stored speed restore.
        private void ApplyAnimatorSpeed(float value)
        {
            _animatorView?.ApplyPlaybackSpeed(value);
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

            // A paused playback still owns the suppression.
            if (isActiveAndEnabled && PlaybackId != 0
                && (_isMoving || _state == EPathFollowerState.Paused))
                return;

            _characterController.enabled = true;
            _characterControllerDisabledByFollower = false;
        }

        #endregion
    }
}
