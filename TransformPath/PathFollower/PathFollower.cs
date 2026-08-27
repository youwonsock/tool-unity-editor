using System;
using System.Collections.Generic;
using UnityEngine;

namespace Supercent.Common.TransformPath
{
    /// <summary>
    /// PathData를 사용하여 시간 기반 또는 속도 기반으로 경로를 따라 이동하는 애니메이터입니다.
    /// 공개 API와 직렬화 상태는 partial 파일에 책임별로 나뉘어 있습니다.
    /// </summary>
    public partial class PathFollower : MonoBehaviour, IPathFollower
    {
        #region Constants

        private const float TIME_BASED_MIN_DURATION = 0.001f;
        private const float TIME_BASED_MAX_DURATION = 9999f;
        private const float SPEED_BASED_MIN_SPEED = 0f;
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
        private float _pendingDefaultSpeed = 0f;
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

        private readonly List<Vector3> _controlPointScratch = new List<Vector3>();
        private bool _pendingReplacePathStart = false;
        private PathData _pathDataWithStartOverrideCache = null;

        #endregion

        #region Unity Events

        private void Reset()
        {
            if (!TryGetComponent(out _pathEventHandler))
                _pathEventHandler = gameObject.AddComponent<PathEventHandler>();
        }

        private void Awake()
        {
            CacheRuntimeReferences();
        }

        private void OnValidate()
        {
            _duration = Mathf.Clamp(_duration, TIME_BASED_MIN_DURATION, TIME_BASED_MAX_DURATION);
            _speed = Mathf.Clamp(_speed, SPEED_BASED_MIN_SPEED, SPEED_BASED_MAX_SPEED);
            CacheRuntimeReferences();
        }

        private void OnDisable()
        {
            StopMove();
        }

        private void Start()
        {
            if (!_autoStart)
                return;

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
            set => _timeCurve = value;
        }

        public PathData PathData
        {
            get => _pathData;
            set
            {
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
                _normalizedTime = Mathf.Clamp01(value);
                UpdatePosition(_normalizedTime);
            }
        }

        public bool IsMoving => _isMoving;
        public bool HasActiveMoveCoroutine => _moveCoroutine != null;

        public bool ReplacePathStartWithFollowerPosition
        {
            get => _replacePathStartWithFollowerPosition;
            set => _replacePathStartWithFollowerPosition = value;
        }

        public EMoveType CurrentMoveType
        {
            get => _moveType;
            set => _moveType = value;
        }

        public float Duration
        {
            get => _duration;
            set
            {
                float clampedValue = Mathf.Clamp(value, TIME_BASED_MIN_DURATION, TIME_BASED_MAX_DURATION);
                if (_isMoving
                    && _moveType == EMoveType.TimeBased
                    && !Mathf.Approximately(_duration, clampedValue))
                {
                    _durationChangeBaseNormalizedTime = _normalizedTime;
                    _needsElapsedTimeReset = true;
                }

                _duration = clampedValue;
            }
        }

        public float Speed
        {
            get => _speed;
            set => _speed = Mathf.Clamp(value, SPEED_BASED_MIN_SPEED, SPEED_BASED_MAX_SPEED);
        }

        public bool Loop
        {
            get => _loop;
            set => _loop = value;
        }

        public float DefaultSpeed => _defaultSpeed;
        public float DefaultDuration => _defaultDuration;

        public Animator Animator
        {
            get => _animator;
            set => _animator = value;
        }

        public MultiPathData MultiPathData
        {
            get => _multiPathData;
            set => _multiPathData = value;
        }

        public int CurrentPathIndex => _currentPathIndex;

        public float GlobalNormalizedTime
        {
            get
            {
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

        public bool TryStartMove(IPathProvider provider, PathMoveSettings settings)
        {
            if (!IsAlive(provider))
                return false;

            if (!provider.IsReady && provider is IPathController controller)
                controller.TryRebuild();

            if (!provider.IsReady)
                return false;

            if (_replacePathStartWithFollowerPosition && !(provider is PathData))
            {
                Debug.LogWarning("PathFollower: ReplacePathStartWithFollowerPosition은 PathData Provider에서만 지원됩니다.");
                return false;
            }

            if (provider is PathData pathData)
            {
                StartMove(
                    pathData,
                    PathTypeConversion.ToLegacy(settings.MoveType),
                    settings.Value,
                    settings.TimeCurve,
                    null);
                Loop = settings.Loop;
                return IsMoving;
            }

            StopMove();
            _useMultiPaths = false;
            _activePathProvider = provider;
            _moveType = PathTypeConversion.ToLegacy(settings.MoveType);
            _timeCurve = settings.TimeCurve ?? AnimationCurve.Linear(0, 0, 1, 1);
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
                    return false;
            }

            StartMoveCoroutine(_moveRevision);
            return true;
        }

        public bool TrySeek(float normalizedTime)
        {
            if (!IsFinite(normalizedTime) || !IsPathValid())
                return false;

            SetNormalizedTime(normalizedTime);
            PublishState(_isMoving ? EPathFollowerState.Moving : EPathFollowerState.Paused);
            return true;
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

            if (!IsAlive(_activePathProvider) || !_activePathProvider.IsReady)
            {
                _isMoving = false;
                PublishState(EPathFollowerState.Stopped);
                return false;
            }

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
            => _activePathProvider != null ? _activePathProvider.PathLength : (_pathData != null ? _pathData.PathLength : 0f);

        private IReadOnlyList<PathEventEntry> GetActivePathEvents()
        {
            if (_activePathProvider is IPathEventSource providerEventSource)
                return providerEventSource.PathEvents;

            return _pathData != null ? _pathData.PathEvents : null;
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

        private void StartSinglePath(Action onComplete)
        {
            if (_pathData == null)
            {
                Debug.LogWarning("PathFollower: PathData가 설정되지 않았습니다!");
                return;
            }

            _activePathProvider = _pathData;
            SubscribeToActiveProvider();
            RestorePathDataCacheFromSerializedTransformsIfNeeded();

            bool requestReplace = _pendingReplacePathStart;
            _pendingReplacePathStart = false;

            if (requestReplace && _replacePathStartWithFollowerPosition)
            {
                if (_pathData.TryCopyWorldControlPoints(_controlPointScratch))
                {
                    _controlPointScratch[0] = transform.position;
                    _pathData?.Init(_controlPointScratch, forceReinit: true);
                    _pathDataWithStartOverrideCache = _pathData;
                }
                else
                {
                    Debug.LogWarning("PathFollower: 시작점 치환에 필요한 제어점이 부족합니다. Transform 기준으로 강제 재초기화합니다.");
                    _pathData?.Init(forceReinit: true);
                }
            }
            else
            {
                _pathData?.Init();
            }

            AbortMoveCoroutineAndBumpRevision();
            _isMoving = false;
            StopRestoreSpeed();
            int moveRevision = _moveRevision;
            _defaultSpeed = _speed > 0f ? _speed : _pendingDefaultSpeed;
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
                return _activePathProvider.IsReady;

            if (_pathData == null)
                return false;

            return _pathData.PathPoints != null && _pathData.PathPoints.Length > 0;
        }

        private void RestorePathDataCacheFromSerializedTransformsIfNeeded()
        {
            if (_pathDataWithStartOverrideCache == null)
                return;

            _pathDataWithStartOverrideCache?.Init(forceReinit: true);
            _pathDataWithStartOverrideCache = null;
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

        private void CacheRuntimeReferences()
        {
            if (_pathEventHandler == null)
                TryGetComponent(out _pathEventHandler);

            if (_animator == null)
            {
                if (!TryGetComponent(out _animator))
                    _animator = GetComponentInChildren<Animator>();
            }
        }

        #endregion

#if UNITY_EDITOR
        [ContextMenu("Bind Serialized Field")]
        private void BindSerializedField()
        {
            UnityEditor.Undo.RecordObject(this, "Bind Serialized Field");
            TryGetComponent(out _pathEventHandler);
            if (!TryGetComponent(out _animator))
                _animator = GetComponentInChildren<Animator>();
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
}
}
