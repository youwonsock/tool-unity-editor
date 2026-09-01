using System;
using UnityEngine;

namespace Common.FlowField.Samples
{
    /// <summary>
    /// FlowField 공개 기능을 한 씬에서 전환해 확인하는 샘플 전용 컨트롤러입니다.
    /// </summary>
    [DefaultExecutionOrder(10)]
    public sealed class FlowFieldShowcaseOverviewController : MonoBehaviour
    {
        private enum ShowcaseMode
        {
            Baseline,
            SpeedModifier,
            NoiseModifier,
            DynamicObstacle,
            SampleAndClamp,
        }

        [Header("Serialized References")]
        [SerializeField] private FlowFieldManager _manager;
        [SerializeField] private FlowFieldSampleController _sampleController;
        [SerializeField] private FlowFieldSpeedModifier _speedModifier;
        [SerializeField] private FlowFieldNoiseModifier _noiseModifier;
        [SerializeField] private Collider _dynamicObstacle;
        [SerializeField] private GameObject _dynamicObstacleObject;
        [SerializeField] private FlowFieldOverviewBoard _board;

        [Header("Showcase Timing")]
        [SerializeField] private float _modeInterval = 8f;
        [SerializeField] private float _goalInterval = 15f;
        [SerializeField] private float _obstacleMoveInterval = 0.5f;
        [SerializeField] private Vector3 _sampleProbe = new Vector3(0f, 0.4f, 0f);

        private ShowcaseMode _mode;
        private float _modeTimer;
        private float _goalTimer;
        private float _obstacleTimer;
        private bool _dynamicObstacleRegistered;
        private bool _isInitialized;
        private bool _waitingForManager;
        private bool _isFaulted;
        private Exception _fault;
        private FlowFieldSample _lastSample;
        private FlowFieldClampResult _lastClamp;
        private bool _hasSample;

        public bool IsInitialized => _isInitialized;
        public bool IsFaulted => _isFaulted;
        public string CurrentMode => _mode.ToString();
        public bool DynamicObstacleRegistered => _dynamicObstacleRegistered;
        public bool HasSample => _hasSample;
        public FlowFieldSample LastSample => _hasSample
            ? _lastSample
            : throw new InvalidOperationException("FlowField overview has not sampled a probe yet.");
        public FlowFieldClampResult LastClamp => _hasSample
            ? _lastClamp
            : throw new InvalidOperationException("FlowField overview has not clamped a probe yet.");

        private void Awake()
        {
            if (Application.isPlaying)
                Init();
        }

        public void Init()
        {
            if (_isInitialized)
                throw new InvalidOperationException("FlowFieldShowcaseOverviewController is already initialized.");
            if (_isFaulted)
                throw new InvalidOperationException("FlowFieldShowcaseOverviewController is faulted; call Release before Init.", _fault);

            try
            {
                if (_manager == null || _sampleController == null || _board == null)
                    throw new InvalidOperationException("FlowField overview requires serialized Manager, Sample Controller, and Board references.");
                if (!_manager.IsInitialized)
                    throw new InvalidOperationException("FlowFieldManager must be initialized before the overview controller.");
                if (!_sampleController.IsInitialized)
                    throw new InvalidOperationException("FlowFieldSampleController must be initialized before the overview controller.");
                if (_speedModifier == null || _noiseModifier == null)
                    throw new InvalidOperationException("FlowField overview requires serialized Speed and Noise modifiers.");
                if (_dynamicObstacle == null || _dynamicObstacleObject == null)
                    throw new InvalidOperationException("FlowField overview requires a serialized dynamic obstacle collider and object.");
                ValidatePositive(_modeInterval, nameof(_modeInterval));
                ValidatePositive(_goalInterval, nameof(_goalInterval));
                ValidatePositive(_obstacleMoveInterval, nameof(_obstacleMoveInterval));
                if (!IsFinite(_sampleProbe))
                    throw new ArgumentOutOfRangeException(nameof(_sampleProbe));

                _sampleController.SetAutomaticGoalChanges(false);
                _waitingForManager = !_manager.IsReady;
                _isInitialized = true;
            }
            catch (Exception exception)
            {
                _fault = exception;
                _isFaulted = true;
                throw;
            }
        }

        private void Start()
        {
            ThrowIfUnavailable();
            if (_waitingForManager)
                return;
            ApplyMode(ShowcaseMode.Baseline);
            RefreshDiagnostics();
            RenderBoard();
        }

        private void Update()
        {
            ThrowIfUnavailable();

            if (_waitingForManager)
            {
                if (!_manager.IsReady)
                    return;
                _waitingForManager = false;
                ApplyMode(ShowcaseMode.Baseline);
                RefreshDiagnostics();
                RenderBoard();
            }

            if (Input.GetKeyDown(KeyCode.Space))
                _sampleController.ChooseNextGoal();
            if (Input.GetKeyDown(KeyCode.G))
                _sampleController.ClearGoal();
            if (Input.GetKeyDown(KeyCode.Alpha1))
                ApplyMode(ShowcaseMode.Baseline);
            if (Input.GetKeyDown(KeyCode.Alpha2))
                ApplyMode(ShowcaseMode.SpeedModifier);
            if (Input.GetKeyDown(KeyCode.Alpha3))
                ApplyMode(ShowcaseMode.NoiseModifier);
            if (Input.GetKeyDown(KeyCode.M))
                ApplyMode(ShowcaseMode.DynamicObstacle);
            if (Input.GetKeyDown(KeyCode.O))
                ApplyMode(ShowcaseMode.SampleAndClamp);
            if (Input.GetKeyDown(KeyCode.R))
                _manager.Rebuild();
            if (Input.GetKeyDown(KeyCode.C))
                RefreshDiagnostics();

            _modeTimer += Time.deltaTime;
            _goalTimer += Time.deltaTime;
            if (_modeTimer >= _modeInterval)
            {
                _modeTimer = 0f;
                ApplyMode((ShowcaseMode)(((int)_mode + 1) % Enum.GetValues(typeof(ShowcaseMode)).Length));
            }

            if (_goalTimer >= _goalInterval)
            {
                _goalTimer = 0f;
                _sampleController.ChooseNextGoal();
            }

            if (_mode == ShowcaseMode.DynamicObstacle)
                MoveDynamicObstacle();

            RenderBoard();
        }

        private void ApplyMode(ShowcaseMode mode)
        {
            ThrowIfUnavailable();
            if (_dynamicObstacleRegistered)
            {
                _manager.UnregisterDynamicObstacle(_dynamicObstacle);
                _dynamicObstacleRegistered = false;
            }

            _speedModifier.gameObject.SetActive(mode == ShowcaseMode.SpeedModifier);
            _noiseModifier.gameObject.SetActive(mode == ShowcaseMode.NoiseModifier);
            _dynamicObstacleObject.SetActive(mode == ShowcaseMode.DynamicObstacle);

            if (mode == ShowcaseMode.DynamicObstacle)
            {
                _manager.RegisterDynamicObstacle(_dynamicObstacle);
                _dynamicObstacleRegistered = true;
            }

            _mode = mode;
            _modeTimer = 0f;
            if (mode == ShowcaseMode.SampleAndClamp)
                RefreshDiagnostics();
        }

        private void MoveDynamicObstacle()
        {
            _obstacleTimer += Time.deltaTime;
            if (_obstacleTimer < _obstacleMoveInterval)
                return;
            _obstacleTimer = 0f;

            Bounds previous = _dynamicObstacle.bounds;
            Vector3 position = _dynamicObstacleObject.transform.position;
            position.x = Mathf.Sin(Time.time * 0.7f) * 12f;
            _dynamicObstacleObject.transform.position = position;
            Bounds current = _dynamicObstacle.bounds;
            previous.Encapsulate(current);
            _manager.NotifyObstacleRegionDirty(previous);
        }

        private void RefreshDiagnostics()
        {
            FlowFieldClampResult clamp = _manager.ClampPositionToGrid(_sampleProbe);
            FlowFieldSample sample = _manager.Sample(clamp.Position);
            _lastClamp = clamp;
            _lastSample = sample;
            _hasSample = true;
        }

        private void RenderBoard()
        {
            if (!_board.IsInitialized)
                throw new InvalidOperationException("FlowFieldOverviewBoard must be initialized before rendering.");

            string sampleText = _hasSample
                ? $"Sample surface={_lastSample.HasSurface} dir={_lastSample.Direction} speed={_lastSample.SpeedMultiplier:F2}"
                : "Sample: press C or O";
            string clampText = _hasSample
                ? $"Clamp={_lastClamp.Position} ({((_lastClamp.ClampedX || _lastClamp.ClampedZ) ? "clamped" : "inside")})"
                : "Clamp: pending";
            _board.Render(
                "FLOWFIELD SHOWCASE\n"
                + $"Ready: {_manager.IsReady}  Revision: {_manager.Revision}\n"
                + $"Agents: {_sampleController.SpawnedAgentCount}/1000  Goal changes: {_sampleController.GoalChangeCount}\n"
                + $"Goal active: {_sampleController.HasActiveGoal}\n"
                + $"Mode: {_mode}  Dynamic obstacle: {_dynamicObstacleRegistered}\n"
                + $"Deep overlaps: {_sampleController.DeepOverlapPairs}\n"
                + $"{sampleText}\n{clampText}\n"
                + "Space Goal | G clear Goal | 1/2/3 modifiers | M obstacle | O sample | R rebuild | C diagnostics");
        }

        public void Release()
        {
            if (!_isInitialized && !_isFaulted)
                throw new InvalidOperationException("FlowFieldShowcaseOverviewController has not been initialized.");
            if (_dynamicObstacleRegistered && _manager != null && _manager.IsInitialized)
                _manager.UnregisterDynamicObstacle(_dynamicObstacle);
            _dynamicObstacleRegistered = false;
            _isInitialized = false;
            _isFaulted = false;
            _fault = null;
        }

        private void OnDestroy()
        {
            if (_isInitialized || _isFaulted)
                Release();
        }

        private void ThrowIfUnavailable()
        {
            if (_isFaulted)
                throw new InvalidOperationException("FlowFieldShowcaseOverviewController is faulted; call Release before use.", _fault);
            if (!_isInitialized)
                throw new InvalidOperationException("FlowFieldShowcaseOverviewController is not initialized.");
        }

        private static void ValidatePositive(float value, string name)
        {
            if (!IsFinite(value) || value <= 0f)
                throw new ArgumentOutOfRangeException(name);
        }

        private static bool IsFinite(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool IsFinite(Vector3 value)
            => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    }
}
