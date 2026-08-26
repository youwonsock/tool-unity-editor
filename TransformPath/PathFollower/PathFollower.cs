using System;
using System.Collections.Generic;
using UnityEngine;

namespace Supercent.Common.TransformPath
{
    /// <summary>
    /// PathData를 사용하여 시간 기반 또는 속도 기반으로 경로를 따라 이동하는 애니메이터입니다.
    /// 공개 API와 직렬화 상태는 partial 파일에 책임별로 나뉘어 있습니다.
    /// </summary>
    public partial class PathFollower : MonoBehaviour
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
            _pathEventHandler = PathComponentUtility.EnsureComponent<PathEventHandler>(gameObject);
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
    }
}
