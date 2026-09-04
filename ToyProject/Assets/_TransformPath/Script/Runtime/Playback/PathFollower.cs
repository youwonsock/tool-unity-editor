using System;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// Single Update-driven path playback. A playback session owns the current
    /// provider snapshot, event cursor, and queue constraint so no coroutine or
    /// per-frame temporary object is required.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [RequireComponent(typeof(PathEventHandler))]
    public sealed class PathFollower : MonoBehaviour, IPathFollower
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
        [SerializeField] private Animator _animator;

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
        private bool _queueBlocked;
        private float _queueSpeedMultiplier = 1f;
        private float _queueMaxGlobalNormalizedTime = 1f;
        private Action<int> _segmentChanged;
        private Delegate[] _segmentChangedInvocationList;

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

        public float Speed
        {
            get => _speed;
            set
            {
                ValidateMovementValue(value, nameof(value));
                _speed = value;
            }
        }

        public float Duration
        {
            get => _duration;
            set
            {
                ValidateMovementValue(value, nameof(value));
                _duration = value;
            }
        }

        public event Action<EPathFollowerState> StateChanged;
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
        public event Action Completed;

        #endregion


        #region Unity Events

        public void Init()
        {
            if (_isInitialized)
                return;

            if (_pathEventHandler == null)
                TryGetComponent(out _pathEventHandler);
            if (_pathEventHandler != null && !_pathEventHandler.IsInitialized)
                _pathEventHandler.Init();

            _isInitialized = true;
            _state = EPathFollowerState.Ready;
            _playbackRevision++;
        }

        public void Release()
        {
            if (!_isInitialized && _playbackSession == null)
                return;

            StopMove();
            _playbackSession = null;
            _isInitialized = false;
            SetState(EPathFollowerState.Uninitialized);
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

        private void Update()
        {
            if (!_isInitialized || !_isMoving || _playbackSession == null)
                return;
            if (!RefreshProviderSnapshotIfNeeded())
                return;

            float deltaTime = Time.deltaTime;
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

        private void OnDisable()
        {
            if (Application.isPlaying)
                StopMove();
        }

        private void OnDestroy()
        {
            Release();
        }

        #endregion


        #region Public Methods

        public void StartPlayback(PathPlaybackRequest request)
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
                        session.Provider as IPathEventSource,
                        session.MovementSettings.MoveType);
            BeginPlayback(session, request.Loop);
        }

        public void StopMove()
        {
            if (!_isInitialized && !_isMoving)
                return;
            StopPlaybackOnly();
            SetState(_isInitialized
                ? EPathFollowerState.Ready
                : EPathFollowerState.Uninitialized);
        }

        public void PauseMove()
        {
            if (!_isMoving)
                return;

            _isMoving = false;
            _pendingDeltaTime = 0f;
            _playbackRevision++;
            SetState(EPathFollowerState.Paused);
            ApplyAnimatorSpeed(0f);
        }

        public void ResumeMove()
        {
            if (!_isInitialized
                || _playbackSession == null
                || _state != EPathFollowerState.Paused)
                return;

            _isMoving = true;
            _playbackRevision++;
            SetState(EPathFollowerState.Moving);
            ApplyAnimatorSpeed(1f);
        }

        public void Seek(float normalizedTime)
        {
            EnsureInitialized();
            if (_playbackSession == null)
                throw new InvalidOperationException("PathFollower has no active provider.");
            ValidateFinite(normalizedTime, nameof(normalizedTime));

            _pendingDeltaTime = 0f;
            if (_playbackSession.Sequence == null)
            {
                _normalizedTime = Mathf.Clamp01(normalizedTime);
                _globalNormalizedTime = _normalizedTime;
                _segmentElapsed = _duration * _normalizedTime;
                _segmentDistance = _playbackSession.Provider.PathLength * _normalizedTime;
                _playbackSession.EventCursor.Reset(
                    _playbackSession.Provider as IPathEventSource,
                    _normalizedTime);
            }
            else
            {
                SetSequenceGlobalProgress(Mathf.Clamp01(normalizedTime));
            }

            ApplyCurrentPosition();
        }

        public void SeekSegment(int segmentIndex, float localNormalizedTime)
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

            SetSequenceSegmentProgress(segmentIndex, local);
            ApplyCurrentPosition();
        }

        /// <summary>
        /// Called once per frame by QueuedPathFollower before this follower's
        /// Update tick. The constraint is a non-destructive multiplier/clamp.
        /// </summary>
        internal void SetQueueConstraint(
            bool blocked,
            float speedMultiplier,
            float maxGlobalNormalizedTime)
        {
            _queueBlocked = blocked;
            _queueSpeedMultiplier = Mathf.Clamp01(speedMultiplier);
            _queueMaxGlobalNormalizedTime = Mathf.Clamp01(maxGlobalNormalizedTime);

            if (_isMoving && _globalNormalizedTime > _queueMaxGlobalNormalizedTime)
                Seek(_queueMaxGlobalNormalizedTime);
        }

        internal void ResetQueueConstraint()
        {
            _queueBlocked = false;
            _queueSpeedMultiplier = 1f;
            _queueMaxGlobalNormalizedTime = 1f;
        }

        #endregion


        #region Private Methods

        private void BeginPlayback(
            PathPlaybackSession session,
            bool loop)
        {
            StopPlaybackOnly();
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
                    session.Provider as IPathEventSource,
                    0f);
            }
            ResetQueueConstraint();
            _isMoving = true;
            _playbackRevision++;
            SetState(EPathFollowerState.Moving);
            ApplyCurrentPosition();
        }

        private float TickSingle(float availableTime, int tickRevision)
        {
            IPathProvider provider = _playbackSession.Provider;
            float multiplier = _queueSpeedMultiplier;
            if (_moveType == EPathMoveType.TimeBased)
            {
                float duration = Mathf.Max(_duration, PathMovementSettingsUtility.MIN_VALUE);
                float remainingToEnd = Mathf.Max(0f, duration - _segmentElapsed);
                float consume = Mathf.Min(
                    availableTime,
                    remainingToEnd / Mathf.Max(multiplier, PathMovementSettingsUtility.MIN_VALUE));
                _segmentElapsed += consume * multiplier;
                _normalizedTime = EvaluateTimeProgress(_segmentElapsed / duration, _timeCurve);
                _globalNormalizedTime = _normalizedTime;
                DispatchEvents(provider as IPathEventSource, _normalizedTime, tickRevision);
                if (_playbackRevision != tickRevision)
                    return consume;
                if (_segmentElapsed >= duration - EPSILON)
                {
                    _normalizedTime = 1f;
                    _globalNormalizedTime = 1f;
                    if (_loop)
                    {
                        ApplyCurrentPosition();
                        FlushEvents(provider as IPathEventSource, tickRevision);
                        if (_playbackRevision != tickRevision)
                            return consume;
                        _segmentElapsed = 0f;
                        _segmentDistance = 0f;
                        _normalizedTime = 0f;
                        _globalNormalizedTime = 0f;
                        _playbackSession.EventCursor.Reset(
                            provider as IPathEventSource,
                            0f);
                    }
                    else
                        CompletePlayback(provider as IPathEventSource);
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
            float consumeTime = Mathf.Min(
                availableTime,
                remainingDistance / Mathf.Max(effectiveSpeed, PathMovementSettingsUtility.MIN_VALUE));
            _segmentDistance += consumeTime * effectiveSpeed;
            _normalizedTime = Mathf.Clamp01(_segmentDistance / pathLength);
            _globalNormalizedTime = _normalizedTime;
            DispatchEvents(provider as IPathEventSource, _normalizedTime, tickRevision);
            if (_playbackRevision != tickRevision)
                return consumeTime;
            if (_segmentDistance >= pathLength - EPSILON)
            {
                _normalizedTime = 1f;
                _globalNormalizedTime = 1f;
                if (_loop)
                {
                    ApplyCurrentPosition();
                    FlushEvents(provider as IPathEventSource, tickRevision);
                    if (_playbackRevision != tickRevision)
                        return consumeTime;
                    _segmentDistance = 0f;
                    _segmentElapsed = 0f;
                    _normalizedTime = 0f;
                    _globalNormalizedTime = 0f;
                    _playbackSession.EventCursor.Reset(
                        provider as IPathEventSource,
                        0f);
                }
                else
                    CompletePlayback(provider as IPathEventSource);
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
            float multiplier = Mathf.Max(
                _queueSpeedMultiplier,
                PathMovementSettingsUtility.MIN_VALUE);
            float consume;

            if (movementSettings.MoveType == EPathMoveType.TimeBased)
            {
                float duration = Mathf.Max(_duration, PathMovementSettingsUtility.MIN_VALUE);
                float remaining = Mathf.Max(0f, duration - _segmentElapsed);
                consume = Mathf.Min(availableTime, remaining / multiplier);
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
                    AdvanceToSegment(0, tickRevision);
                    transitioned = true;
                }
                else
                    CompletePlayback(snapshot.GetEventSource(_currentSegmentIndex));
            }

            return Mathf.Max(consume, EPSILON);
        }

        private void AdvanceToSegment(int index, int tickRevision)
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
            ApplyCurrentPosition();
            FlushEvents(source, completionRevision);
            if (_playbackRevision != completionRevision)
                return;
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

            _playbackSession.ProviderRevision = _playbackSession.Provider.Revision;
            _normalizedTime = Mathf.Clamp01(_normalizedTime);
            _globalNormalizedTime = _normalizedTime;
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
                ? _duration * _normalizedTime
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
                PathEventEntry entry = source.GetEvent(
                    _playbackSession.EventCursor.NextIndex);
                if (entry.NormalizedTime > progress + EPSILON)
                    break;
                _playbackSession.EventCursor.NextIndex++;
                if (entry.EventSetting == null)
                    continue;
                _pathEventHandler?.HandleEvent(entry.EventSetting, this);
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

        private void SetState(EPathFollowerState state)
        {
            if (_state == state)
                return;
            _state = state;
            InvokeStateChanged(state);
        }

        private void ApplyAnimatorSpeed(float value)
        {
            if (_animator != null)
                _animator.speed = value;
        }

        private void InvokeStateChanged(EPathFollowerState value)
        {
            StateChanged?.Invoke(value);
        }

        private void InvokeSegmentChanged(int value, int tickRevision)
        {
            Delegate[] listeners = _segmentChangedInvocationList;
            if (listeners == null)
                return;

            for (int i = 0; i < listeners.Length; i++)
            {
                ((Action<int>)listeners[i])(value);
                if (_playbackRevision != tickRevision)
                    return;
            }
        }

        private void InvokeCompleted()
        {
            Completed?.Invoke();
        }

        #endregion
    }
}
