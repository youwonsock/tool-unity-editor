using System;
using System.Collections.Generic;
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
        private const float MIN_VALUE = 0.001f;
        private const float MAX_VALUE = 9999f;
        private const float EPSILON = 0.00001f;
        private const int MAX_SEGMENT_TRANSITIONS_PER_TICK = 65;

        [Header("Startup")]
        [SerializeField] private MonoBehaviour _startupProviderObject;
        [SerializeField] private bool _startupAsSequence;
        [SerializeField] private bool _playOnStart;
        [SerializeField] private EPathMoveType _startupMoveType = EPathMoveType.TimeBased;
        [SerializeField, Min(0.001f)] private float _startupValue = 5f;
        [SerializeField] private AnimationCurve _startupTimeCurve = null;
        [SerializeField] private bool _startupLoop;
        [SerializeField] private bool _startupPreserveSpeedBetweenSegments;

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
        private bool _preserveSpeedBetweenSegments;
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
        private int _queueRouteRevision = -1;

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
                ValidatePositive(value, nameof(value));
                _speed = value;
            }
        }

        public float Duration
        {
            get => _duration;
            set
            {
                ValidatePositive(value, nameof(value));
                _duration = value;
            }
        }

        public event Action<EPathFollowerState> StateChanged;
        public event Action<int> SegmentChanged;
        public event Action Completed;

        private void Reset()
        {
            _pathEventHandler = GetComponent<PathEventHandler>();
            if (_startupTimeCurve == null)
                _startupTimeCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f);
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
                Debug.LogError($"PathFollower '{name}' startup provider does not implement IPathProvider.", this);
                return;
            }

            if (_startupAsSequence)
            {
                IPathSequenceProvider sequence = provider as IPathSequenceProvider;
                if (sequence == null)
                {
                    Debug.LogError($"PathFollower '{name}' startup provider is not a sequence.", this);
                    return;
                }
                StartSequence(sequence, new PathSequenceSettings(_startupLoop, _startupPreserveSpeedBetweenSegments));
            }
            else
            {
                StartMove(provider, new PathMoveSettings(
                    _startupMoveType,
                    _startupValue,
                    _startupTimeCurve,
                    _startupLoop,
                    _startupPreserveSpeedBetweenSegments));
            }
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
            while (remaining > EPSILON && _isMoving && transitionCount < MAX_SEGMENT_TRANSITIONS_PER_TICK)
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

            if (_isMoving && remaining > EPSILON && transitionCount >= MAX_SEGMENT_TRANSITIONS_PER_TICK)
                _pendingDeltaTime = remaining;

            ApplyCurrentPosition();
        }

        public void Init()
        {
            if (_isInitialized)
                return;

            if (_pathEventHandler == null)
                _pathEventHandler = GetComponent<PathEventHandler>();
            if (_pathEventHandler != null && !_pathEventHandler.IsInitialized)
                _pathEventHandler.Init();
            if (_startupTimeCurve == null)
                _startupTimeCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f);

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

        private void OnDisable()
        {
            if (Application.isPlaying)
                StopMove();
        }

        private void OnDestroy()
        {
            Release();
        }

        public void StartMove(IPathProvider provider, PathMoveSettings settings)
        {
            EnsureInitialized();
            ValidateProvider(provider);
            ValidateMoveSettings(settings);

            StopPlaybackOnly();
            _playbackSession = new PathPlaybackSession(provider, null, null);
            _moveType = settings.MoveType;
            _speed = settings.MoveType == EPathMoveType.SpeedBased ? settings.Value : _speed;
            _duration = settings.MoveType == EPathMoveType.TimeBased ? settings.Value : _duration;
            _timeCurve = settings.TimeCurve;
            _loop = settings.Loop;
            _preserveSpeedBetweenSegments = settings.PreserveSpeedBetweenSegments;
            _currentSegmentIndex = 0;
            _normalizedTime = 0f;
            _globalNormalizedTime = 0f;
            _segmentElapsed = 0f;
            _segmentDistance = 0f;
            _playbackSession.EventCursor.Reset(provider as IPathEventSource, 0f);
            ResetQueueConstraint();
            _isMoving = true;
            _playbackRevision++;
            SetState(EPathFollowerState.Moving);
            ApplyCurrentPosition();
        }

        public void StartSequence(IPathSequenceProvider provider, PathSequenceSettings settings)
        {
            EnsureInitialized();
            ValidateProvider(provider);
            PathSequenceSnapshot snapshot = PathSequenceSnapshot.Create(provider);
            if (snapshot.Count == 0)
                throw new ArgumentException("A sequence requires at least one segment.", nameof(provider));

            StopPlaybackOnly();
            _playbackSession = new PathPlaybackSession(provider, provider, snapshot);
            _loop = settings.Loop;
            _preserveSpeedBetweenSegments = settings.PreserveSpeedBetweenSegments;
            _currentSegmentIndex = 0;
            _normalizedTime = 0f;
            _globalNormalizedTime = 0f;
            _segmentElapsed = 0f;
            _segmentDistance = 0f;
            ConfigureSequenceSegment(0, 0f, false);
            _playbackSession.EventCursor.Reset(snapshot.GetEventSource(0), 0f);
            ResetQueueConstraint();
            _isMoving = true;
            _playbackRevision++;
            SetState(EPathFollowerState.Moving);
            ApplyCurrentPosition();
        }

        public void StopMove()
        {
            if (!_isInitialized && !_isMoving)
                return;
            StopPlaybackOnly();
            SetState(_isInitialized ? EPathFollowerState.Ready : EPathFollowerState.Uninitialized);
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
            if (!_isInitialized || _playbackSession == null || _state != EPathFollowerState.Paused)
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
            if (float.IsNaN(normalizedTime) || float.IsInfinity(normalizedTime))
                throw new ArgumentOutOfRangeException(nameof(normalizedTime));

            _pendingDeltaTime = 0f;

            if (_playbackSession.Sequence == null)
            {
                _normalizedTime = Mathf.Clamp01(normalizedTime);
                _globalNormalizedTime = _normalizedTime;
                _segmentElapsed = _duration * _normalizedTime;
                _segmentDistance = _playbackSession.Provider.PathLength * _normalizedTime;
                _playbackSession.EventCursor.Reset(_playbackSession.Provider as IPathEventSource, _normalizedTime);
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
            if (float.IsNaN(localNormalizedTime) || float.IsInfinity(localNormalizedTime))
                throw new ArgumentOutOfRangeException(nameof(localNormalizedTime));

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
        internal void SetQueueConstraint(bool blocked, float speedMultiplier, float maxGlobalNormalizedTime, int routeRevision)
        {
            _queueBlocked = blocked;
            _queueSpeedMultiplier = Mathf.Clamp01(speedMultiplier);
            _queueMaxGlobalNormalizedTime = Mathf.Clamp01(maxGlobalNormalizedTime);
            _queueRouteRevision = routeRevision;

            if (_isMoving && _globalNormalizedTime > _queueMaxGlobalNormalizedTime)
                Seek(_queueMaxGlobalNormalizedTime);
        }

        internal void ResetQueueConstraint()
        {
            _queueBlocked = false;
            _queueSpeedMultiplier = 1f;
            _queueMaxGlobalNormalizedTime = 1f;
            _queueRouteRevision = -1;
        }

        private float TickSingle(float availableTime, int tickRevision)
        {
            IPathProvider provider = _playbackSession.Provider;
            float multiplier = _queueSpeedMultiplier;
            if (_moveType == EPathMoveType.TimeBased)
            {
                float duration = Mathf.Max(_duration, MIN_VALUE);
                float remainingToEnd = Mathf.Max(0f, duration - _segmentElapsed);
                float consume = Mathf.Min(availableTime, remainingToEnd / Mathf.Max(multiplier, MIN_VALUE));
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
                    ApplyCurrentPosition();
                    FlushEvents(provider as IPathEventSource, tickRevision);
                    if (_playbackRevision != tickRevision)
                        return consume;
                    if (_loop)
                    {
                        _segmentElapsed = 0f;
                        _segmentDistance = 0f;
                        _normalizedTime = 0f;
                        _globalNormalizedTime = 0f;
                        _playbackSession.EventCursor.Reset(provider as IPathEventSource, 0f);
                    }
                    else
                    {
                        CompletePlayback(tickRevision);
                    }
                }
                return Mathf.Max(consume, EPSILON);
            }

            float pathLength = Mathf.Max(provider.PathLength, MIN_VALUE);
            float remainingDistance = Mathf.Max(0f, pathLength - _segmentDistance);
            float effectiveSpeed = Mathf.Max(_speed, MIN_VALUE) * multiplier;
            float consumeTime = Mathf.Min(availableTime, remainingDistance / effectiveSpeed);
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
                ApplyCurrentPosition();
                FlushEvents(provider as IPathEventSource, tickRevision);
                if (_playbackRevision != tickRevision)
                    return consumeTime;
                if (_loop)
                {
                    _segmentDistance = 0f;
                    _segmentElapsed = 0f;
                    _normalizedTime = 0f;
                    _globalNormalizedTime = 0f;
                    _playbackSession.EventCursor.Reset(provider as IPathEventSource, 0f);
                }
                else
                {
                    CompletePlayback(tickRevision);
                }
            }
            return Mathf.Max(consumeTime, EPSILON);
        }

        private float TickSequence(float availableTime, int tickRevision, out bool transitioned)
        {
            transitioned = false;
            PathSequenceSnapshot snapshot = _playbackSession.Snapshot;
            PathSegmentDescriptor descriptor = snapshot.GetDescriptor(_currentSegmentIndex);
            float multiplier = Mathf.Max(_queueSpeedMultiplier, MIN_VALUE);
            float consume;

            if (descriptor.MoveType == EPathMoveType.TimeBased)
            {
                float duration = Mathf.Max(_duration, MIN_VALUE);
                float remaining = Mathf.Max(0f, duration - _segmentElapsed);
                consume = Mathf.Min(availableTime, remaining / multiplier);
                _segmentElapsed += consume * multiplier;
                _normalizedTime = EvaluateTimeProgress(_segmentElapsed / duration, descriptor.TimeCurve);
            }
            else
            {
                float length = Mathf.Max(snapshot.GetLength(_currentSegmentIndex), MIN_VALUE);
                float speed = Mathf.Max(_speed, MIN_VALUE) * multiplier;
                float remaining = Mathf.Max(0f, length - _segmentDistance);
                consume = Mathf.Min(availableTime, remaining / speed);
                _segmentDistance += consume * speed;
                _normalizedTime = Mathf.Clamp01(_segmentDistance / length);
            }

            _globalNormalizedTime = snapshot.GetGlobalProgress(_currentSegmentIndex, _normalizedTime);
            DispatchEvents(snapshot.GetEventSource(_currentSegmentIndex), _normalizedTime, tickRevision);
            if (_playbackRevision != tickRevision)
                return Mathf.Max(consume, EPSILON);

            bool atEnd = descriptor.MoveType == EPathMoveType.TimeBased
                ? _segmentElapsed >= _duration - EPSILON
                : _segmentDistance >= snapshot.GetLength(_currentSegmentIndex) - EPSILON;
            if (atEnd)
            {
                _normalizedTime = 1f;
                _globalNormalizedTime = snapshot.GetGlobalProgress(_currentSegmentIndex, 1f);
                ApplyCurrentPosition();
                FlushEvents(snapshot.GetEventSource(_currentSegmentIndex), tickRevision);
                if (_playbackRevision != tickRevision)
                    return Mathf.Max(consume, EPSILON);

                if (_currentSegmentIndex + 1 < snapshot.Count)
                {
                    AdvanceToSegment(_currentSegmentIndex + 1, tickRevision);
                    transitioned = true;
                }
                else if (_loop)
                {
                    AdvanceToSegment(0, tickRevision);
                    transitioned = true;
                }
                else
                {
                    CompletePlayback(tickRevision);
                }
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
            ConfigureSequenceSegment(index, previousNominalSpeed, _preserveSpeedBetweenSegments);
            _playbackSession.EventCursor.Reset(_playbackSession.Snapshot.GetEventSource(index), 0f);
            InvokeSegmentChanged(index, tickRevision);
        }

        private void ConfigureSequenceSegment(int index, float previousNominalSpeed, bool preserveSpeed)
        {
            PathSegmentDescriptor descriptor = _playbackSession.Snapshot.GetDescriptor(index);
            _moveType = descriptor.MoveType;
            if (preserveSpeed && previousNominalSpeed >= MIN_VALUE && IsFinite(previousNominalSpeed))
            {
                if (descriptor.MoveType == EPathMoveType.SpeedBased)
                    _speed = previousNominalSpeed;
                else
                    _duration = Mathf.Clamp(
                        _playbackSession.Snapshot.GetLength(index) / previousNominalSpeed,
                        MIN_VALUE,
                        MAX_VALUE);
            }
            else if (descriptor.MoveType == EPathMoveType.SpeedBased)
            {
                _speed = descriptor.Value;
            }
            else
            {
                _duration = descriptor.Value;
            }
        }

        private float GetCurrentNominalSpeed()
        {
            if (_playbackSession?.Sequence != null
                && _playbackSession.Snapshot != null
                && _currentSegmentIndex >= 0)
            {
                if (_moveType == EPathMoveType.SpeedBased)
                    return _speed;
                return _duration > MIN_VALUE
                    ? _playbackSession.Snapshot.GetLength(_currentSegmentIndex) / _duration
                    : 0f;
            }

            if (_moveType == EPathMoveType.SpeedBased)
                return _speed;
            return _duration > MIN_VALUE && _playbackSession != null
                ? _playbackSession.Provider.PathLength / _duration
                : 0f;
        }

        private void CompletePlayback(int tickRevision)
        {
            _isMoving = false;
            _pendingDeltaTime = 0f;
            _normalizedTime = 1f;
            _globalNormalizedTime = 1f;
            ApplyCurrentPosition();
            _playbackRevision++;
            if (_playbackRevision != tickRevision + 1)
                return;
            SetState(EPathFollowerState.Completed);
            if (_playbackRevision != tickRevision + 1)
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
                if (_playbackSession.Sequence.SegmentCount != _playbackSession.Snapshot.Count)
                {
                    StopMove();
                    return false;
                }
                PathSequenceSnapshot next = PathSequenceSnapshot.Create(_playbackSession.Sequence);
                if (!next.HasSameStructure(_playbackSession.Snapshot))
                {
                    StopMove();
                    return false;
                }
                _playbackSession.Snapshot = next;
                _playbackSession.ProviderRevision = _playbackSession.Provider.Revision;
                _globalNormalizedTime = _playbackSession.Snapshot.GetGlobalProgress(_currentSegmentIndex, _normalizedTime);
                ApplyCurrentPosition();
                return true;
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
            _globalNormalizedTime = _playbackSession.Snapshot.GetGlobalProgress(index, _normalizedTime);
            _playbackSession.EventCursor.Reset(_playbackSession.Snapshot.GetEventSource(index), _normalizedTime);
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
                : _playbackSession.Snapshot.Sample(_currentSegmentIndex, Mathf.Min(1f, _normalizedTime + 0.001f));
            Vector3 direction = ahead - position;
            if (direction.sqrMagnitude > EPSILON)
                transform.rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
        }

        private void DispatchEvents(IPathEventSource source, float progress, int tickRevision)
        {
            if (_playbackSession == null || _playbackSession.EventCursor == null || source == null)
                return;
            while (_playbackSession.EventCursor.HasNext(source))
            {
                PathEventEntry entry = source.GetEvent(_playbackSession.EventCursor.NextIndex);
                if (entry.NormalizedTime > progress + EPSILON)
                    break;
                _playbackSession.EventCursor.NextIndex++;
                if (entry.EventSetting == null)
                    continue;
                try
                {
                    _pathEventHandler?.HandleEvent(entry.EventSetting, this);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, this);
                }
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

        private static void ValidateProvider(IPathProvider provider)
        {
            if (provider == null)
                throw new ArgumentNullException(nameof(provider));
            if (!provider.IsInitialized || !provider.IsReady)
                throw new InvalidOperationException("Path provider must be initialized and ready.");
        }

        private static void ValidateMoveSettings(PathMoveSettings settings)
        {
            if (!Enum.IsDefined(typeof(EPathMoveType), settings.MoveType))
                throw new ArgumentOutOfRangeException(nameof(settings));
            ValidatePositive(settings.Value, nameof(settings));
            if (settings.MoveType == EPathMoveType.TimeBased)
                ValidateCurve(settings.TimeCurve);
        }

        private static void ValidateCurve(AnimationCurve curve)
        {
            if (curve == null || curve.length == 0)
                throw new ArgumentException("TimeCurve must contain at least one key.");

            Keyframe[] keys = curve.keys;
            for (int i = 0; i < keys.Length; i++)
            {
                if (!IsFinite(keys[i].time) || !IsFinite(keys[i].value))
                    throw new ArgumentException("TimeCurve keys must be finite.");
            }

            float previous = curve.Evaluate(0f);
            if (!IsFinite(previous) || Mathf.Abs(previous) > 0.001f)
                throw new ArgumentException("TimeCurve must start at 0.");
            for (int i = 1; i <= 64; i++)
            {
                float value = curve.Evaluate(i / 64f);
                if (!IsFinite(value) || value + 0.001f < previous)
                    throw new ArgumentException("TimeCurve must be finite and non-decreasing.");
                previous = value;
            }
            if (Mathf.Abs(curve.Evaluate(1f) - 1f) > 0.001f)
                throw new ArgumentException("TimeCurve must end at 1.");
        }

        private static float EvaluateTimeProgress(float rawProgress, AnimationCurve curve)
        {
            return Mathf.Clamp01(curve == null ? rawProgress : curve.Evaluate(Mathf.Clamp01(rawProgress)));
        }

        private static void ValidatePositive(float value, string name)
        {
            if (!IsFinite(value) || value < MIN_VALUE || value > MAX_VALUE)
                throw new ArgumentOutOfRangeException(name);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private void StopPlaybackOnly()
        {
            if (_isMoving)
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
            Delegate[] listeners = StateChanged?.GetInvocationList();
            if (listeners == null)
                return;
            for (int i = 0; i < listeners.Length; i++)
            {
                try { ((Action<EPathFollowerState>)listeners[i])(value); }
                catch (Exception exception) { Debug.LogException(exception, this); }
            }
        }

        private void InvokeSegmentChanged(int value, int tickRevision)
        {
            Delegate[] listeners = SegmentChanged?.GetInvocationList();
            if (listeners == null)
                return;
            for (int i = 0; i < listeners.Length; i++)
            {
                try { ((Action<int>)listeners[i])(value); }
                catch (Exception exception) { Debug.LogException(exception, this); }
                if (_playbackRevision != tickRevision)
                    return;
            }
        }

        private void InvokeCompleted()
        {
            Delegate[] listeners = Completed?.GetInvocationList();
            if (listeners == null)
                return;
            for (int i = 0; i < listeners.Length; i++)
            {
                try { ((Action)listeners[i])(); }
                catch (Exception exception) { Debug.LogException(exception, this); }
            }
        }

        private sealed class PathPlaybackSession
        {
            public readonly IPathProvider Provider;
            public readonly IPathSequenceProvider Sequence;
            public PathSequenceSnapshot Snapshot;
            public readonly PathEventCursor EventCursor;
            public int ProviderRevision;

            public PathPlaybackSession(
                IPathProvider provider,
                IPathSequenceProvider sequence,
                PathSequenceSnapshot snapshot)
            {
                Provider = provider;
                Sequence = sequence;
                Snapshot = snapshot;
                EventCursor = new PathEventCursor();
                ProviderRevision = provider.Revision;
            }
        }

        private sealed class PathEventCursor
        {
            public int NextIndex;

            public void Reset(IPathEventSource source, float progress)
            {
                NextIndex = 0;
                if (source == null)
                    return;
                while (NextIndex < source.EventCount && source.GetEvent(NextIndex).NormalizedTime <= progress + EPSILON)
                    NextIndex++;
            }

            public bool HasNext(IPathEventSource source)
            {
                return source != null && NextIndex < source.EventCount;
            }
        }

        private sealed class PathSequenceSnapshot
        {
            private readonly PathSegmentDescriptor[] _descriptors;
            private readonly float[] _lengths;
            private readonly float[] _starts;
            private readonly float _totalLength;

            public int Count => _descriptors.Length;

            private PathSequenceSnapshot(
                PathSegmentDescriptor[] descriptors,
                float[] lengths,
                float[] starts,
                float totalLength)
            {
                _descriptors = descriptors;
                _lengths = lengths;
                _starts = starts;
                _totalLength = totalLength;
            }

            public static PathSequenceSnapshot Create(IPathSequenceProvider provider)
            {
                int count = provider.SegmentCount;
                PathSegmentDescriptor[] descriptors = new PathSegmentDescriptor[count];
                float[] lengths = new float[count];
                float[] starts = new float[count];
                float total = 0f;
                for (int i = 0; i < count; i++)
                {
                    descriptors[i] = provider.GetSegment(i);
                    if (descriptors[i].Provider == null
                        || !descriptors[i].Provider.IsInitialized
                        || !descriptors[i].Provider.IsReady)
                        throw new InvalidOperationException($"Sequence segment {i} provider is not ready.");
                    if (!Enum.IsDefined(typeof(EPathMoveType), descriptors[i].MoveType)
                        || !IsFinite(descriptors[i].Value)
                        || descriptors[i].Value < MIN_VALUE)
                        throw new InvalidOperationException($"Sequence segment {i} has invalid movement settings.");
                    lengths[i] = provider.GetSegmentLength(i);
                    starts[i] = total;
                    total += lengths[i];
                    if (!IsFinite(lengths[i]) || !IsFinite(total) || lengths[i] <= 0f)
                        throw new InvalidOperationException($"Sequence segment {i} has an invalid length.");
                    if (!Mathf.Approximately(lengths[i], descriptors[i].Provider.PathLength))
                        throw new InvalidOperationException($"Sequence segment {i} length does not match its provider.");
                    if (descriptors[i].MoveType == EPathMoveType.TimeBased)
                        ValidateCurve(descriptors[i].TimeCurve);
                }
                if (total <= 0f)
                    throw new InvalidOperationException("Sequence total length must be positive.");
                return new PathSequenceSnapshot(descriptors, lengths, starts, total);
            }

            public PathSegmentDescriptor GetDescriptor(int index) => _descriptors[index];
            public float GetLength(int index) => _lengths[index];
            public IPathEventSource GetEventSource(int index) => _descriptors[index].Provider as IPathEventSource;

            public Vector3 Sample(int index, float localProgress)
            {
                return _descriptors[index].Provider.Sample(Mathf.Clamp01(localProgress));
            }

            public float GetGlobalProgress(int index, float localProgress)
            {
                return Mathf.Clamp01((_starts[index] + _lengths[index] * Mathf.Clamp01(localProgress)) / _totalLength);
            }

            public int FindSegment(float globalProgress)
            {
                if (globalProgress >= 1f)
                    return _descriptors.Length - 1;
                float distance = Mathf.Clamp01(globalProgress) * _totalLength;
                int low = 0;
                int high = _starts.Length - 1;
                while (low < high)
                {
                    int middle = (low + high + 1) / 2;
                    if (_starts[middle] <= distance)
                        low = middle;
                    else
                        high = middle - 1;
                }
                return low;
            }

            public float GetLocalProgress(int index, float globalProgress)
            {
                float distance = Mathf.Clamp01(globalProgress) * _totalLength;
                return _lengths[index] <= Mathf.Epsilon
                    ? 0f
                    : Mathf.Clamp01((distance - _starts[index]) / _lengths[index]);
            }

            public bool HasSameStructure(PathSequenceSnapshot other)
            {
                if (other == null || other.Count != Count)
                    return false;
                for (int i = 0; i < Count; i++)
                {
                    PathSegmentDescriptor left = _descriptors[i];
                    PathSegmentDescriptor right = other._descriptors[i];
                    if (!ReferenceEquals(left.Provider, right.Provider)
                        || left.MoveType != right.MoveType
                        || !Mathf.Approximately(left.Value, right.Value)
                        || left.TimeCurve != right.TimeCurve)
                        return false;
                }
                return true;
            }
        }
    }
}
