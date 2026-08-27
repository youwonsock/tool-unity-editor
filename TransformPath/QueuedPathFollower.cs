using System;
using UnityEngine;

namespace Supercent.Common.TransformPath
{
    /// <summary>
    /// PathFollower를 사용하여 경로를 따라 이동하며,
    /// 앞에 다른 객체가 있으면 자동으로 멈추는 기능을 제공합니다.
    /// </summary>
    public class QueuedPathFollower : MonoBehaviour, IQueuedPathAgent
    {
        #region Constants

        private const float MIN_SPACING = 0f;
        private const float MIN_RESUME_DELAY = 0f;
        private const float MIN_HYSTERESIS = 0f;
        private const float MIN_SPEED_SMOOTH_RATE = 0f;
        private const float DEFAULT_ACTOR_SPACING = 1.5f;
        private const float DEFAULT_RESUME_MOVEMENT_DELAY = 0.1f;
        private const float DEFAULT_UNBLOCK_HYSTERESIS = 0.3f;
        private const float DEFAULT_SPEED_SMOOTH_RATE = 5f;

        #endregion


        #region Queue Follower State

        [Header("컴포넌트")]
        [SerializeField] private PathFollower _pathFollower;
        [SerializeField] private QueuedPathManager _manager;

        [Header("블로킹 설정")]
        [SerializeField] private float _actorSpacing = DEFAULT_ACTOR_SPACING;
        [SerializeField] private float _resumeMovementDelay = DEFAULT_RESUME_MOVEMENT_DELAY;
        [SerializeField] private bool _useManagerSpacing = true;
        [SerializeField] private float _unblockHysteresis = DEFAULT_UNBLOCK_HYSTERESIS;

        [Header("속도 설정")]
        [SerializeField] private bool _enableGradualSlowdown = true;
        [SerializeField] private bool _enableOvertakeProtection = true;
        [SerializeField] private float _speedSmoothRate = DEFAULT_SPEED_SMOOTH_RATE;

        [Header("애니메이션 설정")]
        [SerializeField] private Animator _animator;
        [SerializeField] private string _speedParamName = "Speed";

        private bool _isBlocked = false;
        private float _blockTimer = 0f;
        private bool _isMoving = false;
        private Action _onComplete;
        private int _speedParamHash;
        private float _currentSpeedMultiplier = 1f;
        private float _lastCheckedNormalizedTime = -1f;
        private bool _speedSmoothSuspended = false;

        #endregion


        #region Queue Follower Properties

        public bool IsBlocked => _isBlocked;
        public bool IsMoving => _isMoving;

        /// <summary>
        /// 실제 이동 중인지 여부 (이동 중이면서 블로킹되지 않은 상태)
        /// </summary>
        public bool IsActuallyMoving => _isMoving && !_isBlocked;

        public float ActorSpacing
        {
            get => _useManagerSpacing && _manager != null ? _manager.DefaultSpacing : _actorSpacing;
            set => _actorSpacing = value;
        }

        public bool UseManagerSpacing { get => _useManagerSpacing; set => _useManagerSpacing = value; }
        public bool EnableGradualSlowdown { get => _enableGradualSlowdown; set => _enableGradualSlowdown = value; }
        public bool EnableOvertakeProtection { get => _enableOvertakeProtection; set => _enableOvertakeProtection = value; }
        public float CurrentSpeedMultiplier => _currentSpeedMultiplier;

        /// <summary>
        /// 전체 경로 기준 현재 위치 (0~1)
        /// </summary>
        public float GlobalNormalizedTime => _pathFollower != null
            ? _pathFollower.GlobalNormalizedTime
            : 0f;

        public PathFollower PathFollower => _pathFollower;
        public QueuedPathManager Manager => _manager;

        public event Action<QueuedPathFollower> OnBlocked;
        public event Action<QueuedPathFollower> OnResumed;
        public event Action<QueuedPathFollower> OnCompleted;
        public event Action<QueuedPathFollower, float> OnSpeedChanged;

        #endregion


        #region Unity Events

        private void Reset()
        {
            if (!TryGetComponent(out _pathFollower))
                _pathFollower = gameObject.AddComponent<PathFollower>();
        }

        private void OnValidate()
        {
            _actorSpacing = Mathf.Max(MIN_SPACING, _actorSpacing);
            _resumeMovementDelay = Mathf.Max(MIN_RESUME_DELAY, _resumeMovementDelay);
            _unblockHysteresis = Mathf.Max(MIN_HYSTERESIS, _unblockHysteresis);
            _speedSmoothRate = Mathf.Max(MIN_SPEED_SMOOTH_RATE, _speedSmoothRate);
        }

        private void Awake()
        {
            if (!EnsurePathFollowerReady())
                return;

            if (_animator != null && !string.IsNullOrEmpty(_speedParamName))
                _speedParamHash = Animator.StringToHash(_speedParamName);
        }

        private void OnEnable()
        {
            _manager?.Register(this);
        }

        private void OnDisable()
        {
            _manager?.Unregister(this);
            StopMove();
        }

        private void Update()
        {
            if (!_isMoving)
                return;

            float currentNormalized = GlobalNormalizedTime;
            bool shouldProcess = _isBlocked || _blockTimer > 0f;

            if (!shouldProcess && Mathf.Approximately(currentNormalized, _lastCheckedNormalizedTime))
                return;

            _lastCheckedNormalizedTime = currentNormalized;

            UpdateMoveModifiers();
        }

        #endregion


        #region Queue Follower API

        /// <summary>
        /// Manager를 설정합니다.
        /// </summary>
        public void SetManager(QueuedPathManager manager)
        {
            if (_manager != null)
                _manager.Unregister(this);

            _manager = manager;

            if (_manager != null && enabled)
                _manager.Register(this);
        }

        /// <summary>
        /// 경로 이동을 시작합니다.
        /// </summary>
        public void StartMove(Action onComplete = null)
        {
            if (!EnsurePathFollowerReady())
                return;

            _onComplete = onComplete;
            PrepareMoveStart();

            _pathFollower.StartMove(OnPathComplete);
        }

        /// <summary>
        /// 외부에서 MultiPathData를 주입하여 경로 이동을 시작합니다.
        /// </summary>
        public void StartMove(MultiPathData multiPathData, Action onComplete = null)
        {
            if (!EnsurePathFollowerReady())
                return;

            _onComplete = onComplete;
            PrepareMoveStart();

            _pathFollower.StartMove(multiPathData, OnPathComplete);
        }

        /// <summary>
        /// 경로 이동을 중지합니다.
        /// </summary>
        public void StopMove()
        {
            ResetMoveState();
            _onComplete = null;

            _pathFollower?.StopMove();
        }

        /// <summary>
        /// 경로 이동을 일시정지합니다.
        /// </summary>
        public void PauseMove(bool pauseAnimation = false)
        {
            _pathFollower?.PauseMove(pauseAnimation);
        }

        /// <summary>
        /// 일시정지된 경로 이동을 재개합니다.
        /// </summary>
        public void ResumeMove(bool resumeAnimation = true)
        {
            _pathFollower?.ResumeMove(resumeAnimation);
        }

        /// <summary>
        /// 외부에서 블로킹 상태를 강제로 설정합니다.
        /// </summary>
        public void ForceBlock(float duration = -1f)
        {
            if (!_isMoving)
                return;

            bool wasBlocked = _isBlocked;
            _isBlocked = true;
            _blockTimer = duration > 0f ? duration : _resumeMovementDelay;

            _pathFollower?.PauseMove();

            if (!wasBlocked)
                OnBlocked?.Invoke(this);
        }

        /// <summary>
        /// 블로킹 상태를 강제로 해제합니다.
        /// </summary>
        public void ForceUnblock()
        {
            if (!_isBlocked)
                return;

            ResumeFromBlock();
        }

        /// <summary>
        /// 스무딩에 의한 속도 제어를 일시정지합니다.
        /// 다음 블로킹/해제 사이클 또는 StartMove/StopMove 시 자동 해제됩니다.
        /// </summary>
        public void SuspendSpeedSmooth()
        {
            _speedSmoothSuspended = true;
        }

        #endregion


        #region Queue Follower Helpers

        private void UpdateMoveModifiers()
        {
            UpdateBlockingState();
            UpdateSpeedMultiplier();
            UpdateOvertakeProtection();
        }

        /// <summary>
        /// 매 프레임 블로킹 상태를 업데이트합니다.
        /// </summary>
        private void UpdateBlockingState()
        {
            if (_manager == null)
                return;

            // 블로킹 타이머 처리
            if (_isBlocked)
                _blockTimer -= Time.deltaTime;

            float distance = _manager.GetDistanceToAhead(this);

            // 앞에 객체가 없으면 블로킹 해제
            if (distance < 0f)
            {
                if (_isBlocked)
                    ResumeFromBlock();
                return;
            }

            float spacing = ActorSpacing;
            bool shouldBlock = ShouldStartBlocking(distance, spacing);
            bool shouldUnblock = ShouldEndBlocking(distance, spacing, _unblockHysteresis, _blockTimer);

            if (shouldBlock)
            {
                if (!_isBlocked)
                {
                    // 블로킹 시작
                    _isBlocked = true;
                    _blockTimer = _resumeMovementDelay;
                    _pathFollower?.PauseMove();
                    OnBlocked?.Invoke(this);
                }
                else
                {
                    // 계속 블로킹 상태 - 타이머 리셋
                    _blockTimer = _resumeMovementDelay;
                }
            }
            else if (_isBlocked && _blockTimer <= 0f && shouldUnblock)
            {
                ResumeFromBlock();
            }
        }

        /// <summary>
        /// 앞 객체와의 거리에 따라 속도를 조절합니다.
        /// </summary>
        private void UpdateSpeedMultiplier()
        {
            if (!_enableGradualSlowdown || _manager == null || _isBlocked)
                return;

            if (_speedSmoothSuspended)
                return;

            float targetMultiplier = _manager.GetSpeedMultiplier(this);

            if (!Mathf.Approximately(_currentSpeedMultiplier, targetMultiplier))
            {
                _currentSpeedMultiplier = Mathf.Lerp(_currentSpeedMultiplier, targetMultiplier, Time.deltaTime * _speedSmoothRate);

                if (Mathf.Approximately(_currentSpeedMultiplier, targetMultiplier))
                    _currentSpeedMultiplier = targetMultiplier;

                _pathFollower?.SetSpeedMultiplier(_currentSpeedMultiplier, true);

                UpdateAnimatorSpeed();
                OnSpeedChanged?.Invoke(this, _currentSpeedMultiplier);
            }
        }

        /// <summary>
        /// 추월 방지 처리를 수행합니다.
        /// </summary>
        private void UpdateOvertakeProtection()
        {
            // 블로킹 상태에서는 추월 방지 처리 스킵 (떨림 방지)
            if (!_enableOvertakeProtection || _manager == null || _isBlocked)
                return;

            if (_pathFollower == null)
                return;

            float currentNormalized = GlobalNormalizedTime;
            float clampedNormalized = _manager.GetClampedNormalizedTime(this, currentNormalized);

            if (currentNormalized > clampedNormalized)
            {
                // 앞 객체를 추월하려고 함 - 위치 클램핑
                _pathFollower.SetGlobalNormalizedTime(clampedNormalized);
            }
        }

        /// <summary>
        /// Animator 속도를 업데이트합니다.
        /// </summary>
        private void UpdateAnimatorSpeed()
        {
            if (_animator == null)
                return;

            if (_speedParamHash != 0)
                _animator.SetFloat(_speedParamHash, _currentSpeedMultiplier);
        }

        /// <summary>
        /// 경로 완료 시 호출됩니다.
        /// </summary>
        private void OnPathComplete()
        {
            ResetMoveState();

            _manager?.Unregister(this);

            OnCompleted?.Invoke(this);
            _onComplete?.Invoke();
        }

        /// <summary>
        /// 이동 시작 시 <see cref="ResetMoveState"/>와 대칭으로 호출됩니다.
        /// </summary>
        private void PrepareMoveStart()
        {
            _manager?.Register(this);
            ResetMoveState();

            _isMoving = true;
        }

        /// <summary>
        /// 이동/정지 시 내부 상태를 초기화합니다. <see cref="PrepareMoveStart"/>와 대칭입니다.
        /// </summary>
        private void ResetMoveState()
        {
            _isMoving = false;
            _isBlocked = false;
            _blockTimer = 0f;
            _speedSmoothSuspended = false;
            _currentSpeedMultiplier = 1f;
            _lastCheckedNormalizedTime = -1f;
        }

        private void ResumeFromBlock()
        {
            _isBlocked = false;
            _blockTimer = 0f;
            _speedSmoothSuspended = false;

            if (_manager != null)
                _currentSpeedMultiplier = _manager.MinSpeedMultiplier;

            _pathFollower?.ResumeMove();
            OnResumed?.Invoke(this);
        }

        private bool EnsurePathFollowerReady()
        {
            if (_pathFollower != null)
                return true;

            TryGetComponent(out _pathFollower);
            if (_pathFollower != null)
                return true;

            Debug.LogWarning("QueuedPathFollower: PathFollower가 설정되지 않았습니다!");
            return false;
        }

        private static bool ShouldStartBlocking(float distance, float spacing)
            => distance >= 0f && distance <= spacing;

        private static bool ShouldEndBlocking(float distance, float spacing, float hysteresis, float blockTimer)
            => blockTimer <= 0f && distance > spacing + hysteresis;

        #endregion


#if UNITY_EDITOR
        [ContextMenu("Bind Serialized Field")]
        private void BindSerializedField()
        {
            UnityEditor.Undo.RecordObject(this, "Bind Serialized Field");
            TryGetComponent(out _pathFollower);
            TryGetComponent(out _animator);
            if (_animator == null)
                _animator = GetComponentInChildren<Animator>();
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif

        #region IQueuedPathAgent Contract

        IPathFollower IQueuedPathAgent.PathFollower => _pathFollower;
        UnityEngine.Object IQueuedPathAgent.UnityOwner => this;

        #endregion


        #region Provider Start API

        public bool TryStartMove(IPathProvider provider, PathMoveSettings settings, Action onComplete = null)
        {
            if (!EnsurePathFollowerReady())
                return false;

            _onComplete = onComplete;
            PrepareMoveStart();

            if (!(_pathFollower is IPathFollower follower) || !follower.TryStartMove(provider, settings))
            {
                ResetMoveState();
                _onComplete = null;
                return false;
            }

            _isMoving = true;
            return true;
        }

        #endregion
    }
}
