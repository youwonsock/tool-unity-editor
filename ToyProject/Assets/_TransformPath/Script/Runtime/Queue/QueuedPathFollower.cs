using System;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>PathFollower adapter that applies one manager constraint per frame.</summary>
    [DefaultExecutionOrder(-110)]
    [RequireComponent(typeof(PathFollower))]
    public sealed class QueuedPathFollower : MonoBehaviour, IQueuedPathAgent
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
        [SerializeField] private Animator _animator;
        [SerializeField] private string _speedParamName = "Speed";

        private bool _isInitialized;
        private bool _isRegistered;
        private bool _manualBlock;
        private bool _externalPause;
        private bool _effectiveBlocked;
        private float _currentSpeedMultiplier = 1f;
        private float _lastReportedSpeedMultiplier = 1f;
        private int _speedParamHash;
        private PathQueueState _managerState;
        private bool _hasManagerState;
        private Action _completedSubscription;

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
        public float GlobalNormalizedTime => _pathFollower == null
            ? 0f
            : _pathFollower.GlobalNormalizedTime;
        public PathFollower PathFollower => _pathFollower;
        public QueuedPathManager Manager => _manager;
        public IPathProvider QueueProvider => _pathFollower == null
            ? null
            : _pathFollower.CurrentProvider;
        public int SnapshotRevision => _pathFollower == null
            ? -1
            : _pathFollower.SnapshotRevision;

        public event Action<QueuedPathFollower> OnBlocked;
        public event Action<QueuedPathFollower> OnResumed;
        public event Action<QueuedPathFollower> OnCompleted;
        public event Action<QueuedPathFollower, float> OnSpeedChanged;

        #endregion


        #region Unity Events

        public void Init()
        {
            if (_isInitialized)
                return;

            if (_pathFollower == null)
                TryGetComponent(out _pathFollower);
            if (_pathFollower == null)
                return;
            if (!_pathFollower.IsInitialized)
                _pathFollower.Init();
            if (_manager == null)
                _manager = GetComponentInParent<QueuedPathManager>();

            _speedParamHash = string.IsNullOrEmpty(_speedParamName)
                ? 0
                : Animator.StringToHash(_speedParamName);
            _completedSubscription = HandleUnderlyingCompleted;
            _pathFollower.Completed -= _completedSubscription;
            _pathFollower.Completed += _completedSubscription;
            _isInitialized = true;
        }

        public void Release()
        {
            if (!_isInitialized && !_isRegistered)
                return;

            UnregisterFromManager();
            if (_pathFollower != null && _completedSubscription != null)
                _pathFollower.Completed -= _completedSubscription;
            _completedSubscription = null;
            _isInitialized = false;
            _hasManagerState = false;
            _effectiveBlocked = false;
            _currentSpeedMultiplier = 1f;
        }

        private void Reset()
        {
            TryGetComponent(out _pathFollower);
        }

        private void Awake()
        {
            if (Application.isPlaying)
                Init();
        }

        private void OnDisable()
        {
            UnregisterFromManager();
        }

        private void OnDestroy()
        {
            Release();
        }

        private void Update()
        {
            if (!_isInitialized || _pathFollower == null || !_pathFollower.IsMoving)
                return;
            if (!_isRegistered)
                TryRegisterWithManager();

            PathQueueState state = default(PathQueueState);
            bool hasState = _manager != null
                && _manager.TryGetState(this, out state);
            if (hasState)
            {
                _managerState = state;
                _hasManagerState = true;
            }

            bool blocked = _externalPause
                || _manualBlock
                || (_hasManagerState && _managerState.IsBlocked);
            float multiplier = _hasManagerState
                ? _managerState.SpeedMultiplier
                : 1f;
            if (_manualBlock || _externalPause)
                multiplier = 0f;

            if (_enableOvertakeProtection
                && _hasManagerState
                && !_manualBlock
                && !_externalPause
                && GlobalNormalizedTime > _managerState.MaxGlobalNormalizedTime)
                _pathFollower.Seek(_managerState.MaxGlobalNormalizedTime);

            bool wasBlocked = _effectiveBlocked;
            _effectiveBlocked = blocked;
            _currentSpeedMultiplier = Mathf.Clamp01(multiplier);
            _pathFollower.SetQueueConstraint(
                _effectiveBlocked,
                _currentSpeedMultiplier,
                _hasManagerState ? _managerState.MaxGlobalNormalizedTime : 1f);
            UpdateAnimator();
            ReportStateChanges(wasBlocked, blocked);
        }

        #endregion


        #region Public Methods

        public void StartPlayback(PathPlaybackRequest request)
        {
            EnsureQueueReady(request.Provider);
            _pathFollower.StartPlayback(request);
            if (!ReferenceEquals(_pathFollower.CurrentProvider, _manager.RouteProvider))
            {
                _pathFollower.StopMove();
                throw new InvalidOperationException(
                    "Queue follower and manager must use the same route provider instance.");
            }
            RegisterWithManager();
        }

        public void StopMove()
        {
            _pathFollower?.StopMove();
            UnregisterFromManager();
            ResetConstraintState();
        }

        public void PauseMove()
        {
            if (_pathFollower == null)
                return;

            _externalPause = true;
            _pathFollower.PauseMove();
            UnregisterFromManager();
            _effectiveBlocked = true;
        }

        public void ResumeMove()
        {
            if (_pathFollower == null)
                return;

            if (_pathFollower.State != EPathFollowerState.Paused)
                return;
            EnsureQueueReady(_pathFollower.CurrentProvider);
            _externalPause = false;
            RegisterWithManager();
            _pathFollower.ResumeMove();
        }

        public void ForceBlock()
        {
            _manualBlock = true;
        }

        public void ForceUnblock()
        {
            _manualBlock = false;
        }

        public void ApplyQueueState(PathQueueState state)
        {
            _managerState = state;
            _hasManagerState = true;
        }

        #endregion


        #region Private Methods

        internal void MarkUnregisteredByManager()
        {
            _isRegistered = false;
            _hasManagerState = false;
            _effectiveBlocked = false;
            if (_pathFollower != null)
                _pathFollower.ResetQueueConstraint();
        }

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
            _pathFollower.ResetQueueConstraint();
            if (!_isRegistered)
            {
                _manager.Register(this);
                _isRegistered = true;
            }
            _hasManagerState = false;
            _effectiveBlocked = false;
            _currentSpeedMultiplier = 1f;
            UpdateAnimator();
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

            _manager.Register(this);
            _isRegistered = true;
        }

        private void UnregisterFromManager()
        {
            if (!_isRegistered)
                return;
            if (_manager != null)
                _manager.Unregister(this);
            _isRegistered = false;
        }

        private void HandleUnderlyingCompleted()
        {
            UnregisterFromManager();
            ResetConstraintState();
            OnCompleted?.Invoke(this);
        }

        private void ResetConstraintState()
        {
            _externalPause = false;
            _manualBlock = false;
            _effectiveBlocked = false;
            _hasManagerState = false;
            _currentSpeedMultiplier = 1f;
            if (_pathFollower != null)
                _pathFollower.ResetQueueConstraint();
            UpdateAnimator();
        }

        private void ReportStateChanges(bool wasBlocked, bool blocked)
        {
            if (blocked != wasBlocked)
            {
                if (blocked)
                    OnBlocked?.Invoke(this);
                else
                    OnResumed?.Invoke(this);
            }
            if (!Mathf.Approximately(
                    _lastReportedSpeedMultiplier,
                    _currentSpeedMultiplier))
            {
                _lastReportedSpeedMultiplier = _currentSpeedMultiplier;
                OnSpeedChanged?.Invoke(this, _currentSpeedMultiplier);
            }
        }

        private void UpdateAnimator()
        {
            if (_animator == null)
                return;
            if (_speedParamHash != 0)
                _animator.SetFloat(_speedParamHash, _currentSpeedMultiplier);
        }

        #endregion


        #region Explicit Interface Implementation

        IPathFollower IQueuedPathAgent.PathFollower => _pathFollower;

        #endregion
    }
}
