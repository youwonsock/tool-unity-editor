using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// PathData를 사용하여 시간 기반 또는 속도 기반으로 경로를 따라 이동하는 애니메이터입니다.
    /// 공개 API와 직렬화 상태는 partial 파일에 책임별로 나뉘어 있습니다.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [RequireComponent(typeof(PathEventHandler))]
    public partial class PathFollower : MonoBehaviour, IPathFollower
    {
        #region Constants

        private const float TIME_BASED_MIN_DURATION = 0.001f;
        private const float TIME_BASED_MAX_DURATION = 9999f;
        private const float SPEED_BASED_MIN_SPEED = 0.001f;
        private const float SPEED_BASED_MAX_SPEED = 9999f;
        private const float MIN_SPEED_MULTIPLIER = 0.001f;
        private const float PATH_EVENT_TIME_EPSILON = 0.0001f;

        #endregion

        #region Inner Classes / Structs

        public enum EMoveType
        {
            TimeBased,
            SpeedBased
        }

        #endregion

        #region Serialized State

        [SerializeField, HideInInspector] private PathData _pathData = null;
        [SerializeField, HideInInspector] private MultiPathData _multiPathData = null;
        [SerializeField] private PathEventHandler _pathEventHandler = null;
        [SerializeField] private EMoveType _moveType = EMoveType.TimeBased;
        [SerializeField] private AnimationCurve _timeCurve = AnimationCurve.Linear(0, 0, 1, 1);
        [SerializeField] private float _duration = 5f;
        [SerializeField] private float _speed = 3f;
        [SerializeField] private bool _autoStart = false;
        [SerializeField] private bool _useContinuousSpeedOnPathChange = false;
        [SerializeField] private Animator _animator = null;

        [Header("Path Start Override")]
        [SerializeField]
        [Tooltip("시작 시 PathFollower의 현재 위치를 제어 폴리라인 첫 점으로 치환해 PathData를 재초기화합니다. 멀티 경로에서는 첫 세그먼트에만 적용됩니다.")]
        private bool _replacePathStartWithFollowerPosition = false;

        private float _normalizedTime = 0f;
        private bool _loop = false;
        private bool _isMoving = false;
        private Coroutine _moveCoroutine = null;
        private Coroutine _restoreSpeedCoroutine = null;
        private Action _onComplete = null;
        private Action _onMultiComplete = null;
        private Action<int> _onPathChanged = null;
        private float _defaultSpeed = 0f;
        private float _defaultDuration = 0f;
        private float _defaultAnimatorSpeed = 1f;
        private bool _isAnimatorPaused = false;
        private float _pausedAnimatorSpeed = 1f;
        private float _previousNormalizedTime = 0f;
        private float _durationChangeBaseNormalizedTime = 0f;
        private bool _needsElapsedTimeReset = false;
        private bool _needsTravelDistanceReset = false;
        private bool _hasPathEvents = false;
        private bool _useMultiPaths = false;
        private int _currentPathIndex = 0;
        private int _moveRevision = 0;
        private int _activeMoveCoroutineId = 0;
        private int _nextPathEventIndex = 0;
        private bool _isInitialized;
        private bool _isFaulted;
        private Exception _fault;

        private readonly List<Vector3> _controlPointScratch = new List<Vector3>();
        private readonly PathFollowerRuntimeProvider _runtimePathProvider = new PathFollowerRuntimeProvider();
        private bool _pendingReplacePathStart = false;

        #endregion

        #region Unity Events

        private void Reset()
        {
            _pathEventHandler = GetComponent<PathEventHandler>();
        }

        private void Awake()
        {
            if (Application.isPlaying)
                Init();
        }

        public bool IsInitialized => _isInitialized;
        public bool IsFaulted => _isFaulted;

        public void Init()
        {
            if (_isInitialized)
                throw new InvalidOperationException("PathFollower is already initialized.");
            if (_isFaulted)
                throw new InvalidOperationException("PathFollower is faulted. Call Release before Init.", _fault);
            try
            {
                if (_pathEventHandler == null)
                    throw new InvalidOperationException("PathFollower requires a serialized PathEventHandler.");
                if (!_pathEventHandler.IsInitialized)
                    throw new InvalidOperationException("PathEventHandler must be initialized before PathFollower.");
                if (!IsFinite(_duration) || _duration <= 0f || _duration > TIME_BASED_MAX_DURATION)
                    throw new ArgumentOutOfRangeException(nameof(_duration));
                if (!IsFinite(_speed) || _speed < SPEED_BASED_MIN_SPEED || _speed > SPEED_BASED_MAX_SPEED)
                    throw new ArgumentOutOfRangeException(nameof(_speed));
                if (!Enum.IsDefined(typeof(EMoveType), _moveType))
                    throw new ArgumentOutOfRangeException(nameof(_moveType));
                if (_moveType == EMoveType.TimeBased)
                {
                    if (_timeCurve == null)
                        throw new ArgumentNullException(nameof(_timeCurve));
                    if (_timeCurve.length == 0)
                        throw new ArgumentException("TimeCurve is required for time-based movement.", nameof(_timeCurve));
                }
                _isInitialized = true;
            }
            catch (Exception exception)
            {
                if (_fault == null)
                    _fault = exception;
                _isFaulted = true;
                _isInitialized = false;
                throw;
            }
        }

        private void OnValidate()
        {
            // Configuration is checked at Init and movement boundaries.
        }

        private void OnDisable()
        {
            if (_isInitialized)
                StopMove();
        }

        private void OnDestroy()
        {
            if (_isInitialized || _isFaulted)
                Release();
        }

        public void Release()
        {
            if (!_isInitialized && !_isFaulted)
                throw new InvalidOperationException("PathFollower has not been initialized.");
            if (_isInitialized)
                StopMove();
            else
            {
                StopMoveCoroutineIfRunning();
                StopRestoreSpeed();
                UnsubscribeFromActiveProvider();
                _activePathProvider = null;
                if (_runtimePathProvider.IsInitialized || _runtimePathProvider.IsFaulted)
                    _runtimePathProvider.Release();
            }
            _isInitialized = false;
            _isFaulted = false;
            _fault = null;
        }

        private void Start()
        {
            if (!_autoStart)
                return;
            ThrowIfFaulted();
            if (!_isInitialized)
                throw new InvalidOperationException("PathFollower is not initialized.");

            if (_multiPathData != null && _multiPathData.PathCount > 0)
                StartMove(MultiPathData, null, null);
            else
            {
                _pendingReplacePathStart = _replacePathStartWithFollowerPosition;
                StartSinglePath(null);
            }
        }

        #endregion

        #region Follower Properties

        public AnimationCurve TimeCurve
        {
            get => _timeCurve;
            set
            {
                ThrowIfFaulted();
                if (value == null || value.length == 0)
                    throw new ArgumentException("TimeCurve must contain at least one key.", nameof(value));
                _timeCurve = value;
            }
        }

        public PathData PathData
        {
            get => _pathData;
            set
            {
                ThrowIfFaulted();
                if (value == null)
                    throw new ArgumentNullException(nameof(value));
                if (_pathData == value)
                    return;

                _pathData = value;
                _normalizedTime = 0f;
            }
        }

        public float NormalizedTime
        {
            get => _normalizedTime;
            set
            {
                ThrowIfFaulted();
                if (!IsFinite(value) || value < 0f || value > 1f)
                    throw new ArgumentOutOfRangeException(nameof(value));
                _normalizedTime = value;
                UpdatePosition(_normalizedTime);
            }
        }

        public bool IsMoving => _isMoving;
        public bool HasActiveMoveCoroutine => _moveCoroutine != null;

        public bool ReplacePathStartWithFollowerPosition
        {
            get => _replacePathStartWithFollowerPosition;
            set
            {
                ThrowIfFaulted();
                _replacePathStartWithFollowerPosition = value;
            }
        }

        public EMoveType CurrentMoveType
        {
            get => _moveType;
            set
            {
                ThrowIfFaulted();
                if (!Enum.IsDefined(typeof(EMoveType), value))
                    throw new ArgumentOutOfRangeException(nameof(value));
                _moveType = value;
            }
        }

        public float Duration
        {
            get => _duration;
            set
            {
                ThrowIfFaulted();
                if (!IsFinite(value) || value < TIME_BASED_MIN_DURATION || value > TIME_BASED_MAX_DURATION)
                    throw new ArgumentOutOfRangeException(nameof(value));
                if (_isMoving
                    && _moveType == EMoveType.TimeBased
                    && !Mathf.Approximately(_duration, value))
                {
                    _durationChangeBaseNormalizedTime = _normalizedTime;
                    _needsElapsedTimeReset = true;
                }

                _duration = value;
            }
        }

        public float Speed
        {
            get => _speed;
            set
            {
                ThrowIfFaulted();
                if (!IsFinite(value) || value < SPEED_BASED_MIN_SPEED || value > SPEED_BASED_MAX_SPEED)
                    throw new ArgumentOutOfRangeException(nameof(value));
                _speed = value;
            }
        }

        public bool Loop
        {
            get => _loop;
            set
            {
                ThrowIfFaulted();
                _loop = value;
            }
        }

        public float DefaultSpeed => _defaultSpeed;
        public float DefaultDuration => _defaultDuration;

        public Animator Animator
        {
            get => _animator;
            set
            {
                ThrowIfFaulted();
                _animator = value;
            }
        }

        public MultiPathData MultiPathData
        {
            get => _multiPathData;
            set
            {
                ThrowIfFaulted();
                if (value == null)
                    throw new ArgumentNullException(nameof(value));
                _multiPathData = value;
            }
        }

        public int CurrentPathIndex => _currentPathIndex;

        public float GlobalNormalizedTime
        {
            get
            {
                ThrowIfFaulted();
                if (!_isInitialized)
                    throw new InvalidOperationException("PathFollower is not initialized.");
                if (_useMultiPaths && _multiPathData != null && _multiPathData.PathCount > 0)
                {
                    float pathStartNormalized = _multiPathData.GetPathStartNormalizedValue(_currentPathIndex);
                    float pathEndNormalized = _multiPathData.GetPathEndNormalizedValue(_currentPathIndex);
                    float pathRange = pathEndNormalized - pathStartNormalized;
                    return pathStartNormalized + pathRange * _normalizedTime;
                }

                return _normalizedTime;
            }
        }

        #endregion

        #region Provider Contract State

        private IPathProvider _activePathProvider = null;
        private bool _providerChangePending = false;
        private EPathFollowerState _pathState = EPathFollowerState.Stopped;
        private int _stateRevision = 0;

        private event Action _stateChanged;
        private event Action _segmentChanged;
        private event Action _completed;

        #endregion


        #region IPathFollower Contract

        IPathProvider IPathFollower.CurrentProvider
        {
            get
            {
                ThrowIfFaulted();
                if (!_isInitialized)
                    throw new InvalidOperationException("PathFollower is not initialized.");
                if (_activePathProvider != null)
                    return _activePathProvider;

                if (_useMultiPaths)
                    return _multiPathData;

                return _pathData;
            }
        }
        EPathFollowerState IPathFollower.State => _pathState;
        int IPathFollower.StateRevision => _stateRevision;
        int IPathFollower.CurrentSegmentIndex => _currentPathIndex;
        EPathMoveType IPathFollower.CurrentMoveType => PathTypeConversion.ToPublic(_moveType);

        event Action IPathFollower.StateChanged
        {
            add => _stateChanged += value;
            remove => _stateChanged -= value;
        }

        event Action IPathFollower.SegmentChanged
        {
            add => _segmentChanged += value;
            remove => _segmentChanged -= value;
        }

        event Action IPathFollower.Completed
        {
            add => _completed += value;
            remove => _completed -= value;
        }

        #endregion


        #region Provider API

        public void StartMove(IPathProvider provider, PathMoveSettings settings)
        {
            ThrowIfFaulted();
            if (!_isInitialized)
                throw new InvalidOperationException("PathFollower is not initialized.");
            if (!IsAlive(provider))
                throw new ArgumentNullException(nameof(provider));
            if (!provider.IsInitialized || !provider.IsReady)
                throw new InvalidOperationException("Path provider must be initialized and ready.");
            if (!Enum.IsDefined(typeof(EPathMoveType), settings.MoveType))
                throw new ArgumentOutOfRangeException(nameof(settings.MoveType));
            if (!IsFinite(settings.Value) || settings.Value <= 0f)
                throw new ArgumentOutOfRangeException(nameof(settings));
            if (settings.MoveType == EPathMoveType.TimeBased && settings.TimeCurve == null)
                throw new ArgumentNullException(nameof(settings.TimeCurve));
            if (settings.MoveType == EPathMoveType.TimeBased && settings.TimeCurve.length == 0)
                throw new ArgumentException("Time-based movement requires a non-empty TimeCurve.", nameof(settings.TimeCurve));

            if (_replacePathStartWithFollowerPosition && !(provider is PathData))
            {
                throw new InvalidOperationException("ReplacePathStartWithFollowerPosition requires a PathData provider.");
            }

            if (provider is PathData pathData)
            {
                StartMove(
                    pathData,
                    PathTypeConversion.ToInternal(settings.MoveType),
                    settings.Value,
                    settings.TimeCurve,
                    null);
                Loop = settings.Loop;
                return;
            }

            StopMove();
            _useMultiPaths = false;
            _activePathProvider = provider;
            _moveType = PathTypeConversion.ToInternal(settings.MoveType);
            _timeCurve = settings.TimeCurve;
            _loop = settings.Loop;

            if (_moveType == EMoveType.TimeBased)
                Duration = settings.Value;
            else
                Speed = settings.Value;

            _normalizedTime = 0f;
            _onComplete = null;
            _onMultiComplete = null;
            _hasPathEvents = provider is IPathEventSource eventSource && eventSource.PathEvents.Count > 0;
            _nextPathEventIndex = 0;
            _providerChangePending = false;
            SubscribeToActiveProvider();
            AbortMoveCoroutineAndBumpRevision();

            _defaultSpeed = _speed;
            _defaultDuration = _duration;
            _defaultAnimatorSpeed = _animator != null ? _animator.speed : 1f;
            _isMoving = true;
            PublishState(EPathFollowerState.Moving);

            if (_hasPathEvents)
            {
                InvokePendingPathEventsUpTo(0f);
                if (!_isMoving)
                    throw new InvalidOperationException("Path event cancelled the move at the starting point.");
            }

            StartMoveCoroutine(_moveRevision);
            return;
        }

        public void Seek(float normalizedTime)
        {
            ThrowIfFaulted();
            if (!_isInitialized)
                throw new InvalidOperationException("PathFollower is not initialized.");
            if (!IsFinite(normalizedTime) || normalizedTime < 0f || normalizedTime > 1f)
                throw new ArgumentOutOfRangeException(nameof(normalizedTime));
            if (!IsPathValid())
                throw new InvalidOperationException("Path provider is not ready.");

            SetNormalizedTime(normalizedTime);
            PublishState(_isMoving ? EPathFollowerState.Moving : EPathFollowerState.Paused);
        }

        #endregion


        #region Provider Helpers

        private void SubscribeToActiveProvider()
        {
            UnsubscribeFromActiveProvider();

            if (_activePathProvider != null)
                _activePathProvider.PathChanged += HandleActiveProviderChanged;
        }

        private void UnsubscribeFromActiveProvider()
        {
            if (_activePathProvider != null)
                _activePathProvider.PathChanged -= HandleActiveProviderChanged;
        }

        private void HandleActiveProviderChanged()
        {
            _providerChangePending = true;
        }

        private bool ConsumeProviderChange()
        {
            if (!_providerChangePending)
                return true;

            _providerChangePending = false;

            if (!IsAlive(_activePathProvider)
                || !_activePathProvider.IsInitialized
                || !_activePathProvider.IsReady)
                throw new InvalidOperationException("Active path provider became unavailable.");

            UpdatePosition(_normalizedTime);
            return true;
        }

        private void PublishState(EPathFollowerState state)
        {
            if (_pathState == state)
                return;

            _pathState = state;
            _stateRevision++;
            _stateChanged?.Invoke();
        }

        private void PublishSegmentChanged()
        {
            _stateRevision++;
            _segmentChanged?.Invoke();
        }

        private void PublishCompleted()
        {
            _completed?.Invoke();
        }

        private float GetActivePathLength()
        {
            if (_activePathProvider != null)
                return _activePathProvider.PathLength;
            if (_pathData == null)
                throw new InvalidOperationException("PathFollower has no active path provider.");
            return _pathData.PathLength;
        }

        private IReadOnlyList<PathEventEntry> GetActivePathEvents()
        {
            if (_activePathProvider == null)
                throw new InvalidOperationException("PathFollower has no active path provider.");
            if (_activePathProvider is IPathEventSource providerEventSource)
                return providerEventSource.PathEvents;
            throw new InvalidOperationException("Active path provider does not expose path events.");
        }

        private static bool IsAlive(IPathProvider provider)
        {
            if (provider == null)
                return false;

            if (provider is UnityEngine.Object unityObject && unityObject == null)
                return false;

            return true;
        }

        private static bool IsFinite(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value);

        #endregion

        #region Lifecycle Helpers

        private void StartSinglePath(Action onComplete, bool preserveMultiPathState = false)
        {
            if (_pathData == null)
                throw new InvalidOperationException("PathFollower requires a PathData reference.");
            if (!_pathData.IsInitialized || !_pathData.IsReady)
                throw new InvalidOperationException("PathData must be initialized and ready before movement.");

            bool requestReplace = _pendingReplacePathStart;
            bool wasMultiPath = preserveMultiPathState && _useMultiPaths;
            MultiPathData multiPath = wasMultiPath ? _multiPathData : null;
            Action multiComplete = wasMultiPath ? _onMultiComplete : null;
            Action<int> pathChanged = wasMultiPath ? _onPathChanged : null;
            _pendingReplacePathStart = false;
            StopMove();

            _useMultiPaths = wasMultiPath;
            if (wasMultiPath)
            {
                _multiPathData = multiPath;
                _onMultiComplete = multiComplete;
                _onPathChanged = pathChanged;
            }
            _activePathProvider = _pathData;
            SubscribeToActiveProvider();
            if (requestReplace && _replacePathStartWithFollowerPosition)
            {
                _runtimePathProvider.Init(_pathData, transform.position);
                _activePathProvider = _runtimePathProvider;
                SubscribeToActiveProvider();
            }

            AbortMoveCoroutineAndBumpRevision();
            _isMoving = false;
            StopRestoreSpeed();
            int moveRevision = _moveRevision;
            _defaultSpeed = _speed;
            _defaultDuration = _duration;
            _defaultAnimatorSpeed = _animator != null ? _animator.speed : 1f;
            _pausedAnimatorSpeed = _defaultAnimatorSpeed;
            _isAnimatorPaused = false;
            _hasPathEvents = _pathData.HasPathEvents;
            _onComplete = onComplete;
            _isMoving = true;
            PublishState(EPathFollowerState.Moving);
            ResetMoveProgressState(resetNormalizedTime: true, needsTravelDistanceReset: false);

            if (_hasPathEvents)
            {
                InvokePendingPathEventsUpTo(0f);
                if (moveRevision != _moveRevision)
                    return;
            }

            StartMoveCoroutine(moveRevision);
        }

        private bool IsPathValid()
        {
            if (_activePathProvider != null)
                return _activePathProvider.IsInitialized && _activePathProvider.IsReady;

            if (_pathData == null)
                return false;

            return _pathData.IsInitialized && _pathData.IsReady;
        }

        private void ResetMoveProgressState(
            bool resetNormalizedTime,
            bool needsTravelDistanceReset,
            bool needsElapsedTimeReset = false)
        {
            if (resetNormalizedTime)
            {
                _normalizedTime = 0f;
                _previousNormalizedTime = 0f;
                _durationChangeBaseNormalizedTime = 0f;
                _nextPathEventIndex = 0;
            }

            _needsElapsedTimeReset = needsElapsedTimeReset;
            _needsTravelDistanceReset = needsTravelDistanceReset;
        }

        private void ThrowIfFaulted()
        {
            if (_isFaulted)
                throw new InvalidOperationException("PathFollower is faulted. Call Release before using it.", _fault);
        }

        #endregion

#if UNITY_EDITOR
        [ContextMenu("Bind Serialized Field")]
        private void BindSerializedField()
        {
            UnityEditor.Undo.RecordObject(this, "Bind Serialized Field");
            _pathEventHandler = GetComponent<PathEventHandler>();
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
}
}
