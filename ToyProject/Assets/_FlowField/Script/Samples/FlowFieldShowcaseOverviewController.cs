using System;
using UnityEngine;

namespace Common.FlowField.Samples
{
    /// <summary>
    /// FlowField 공개 기능을 한 씬에서 수동으로 전환해 확인하는 샘플 전용 컨트롤러입니다.
    /// </summary>
    [DefaultExecutionOrder(10)]
    public sealed class FlowFieldShowcaseOverviewController : MonoBehaviour
    {
        private enum ShowcaseMode
        {
            Baseline,
            SpeedModifier,
            NoiseModifier,
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
        [SerializeField] private FlowFieldFreeCamera _freeCamera;
        [SerializeField] private Transform _mapBoundsRoot;
        [SerializeField] private Transform _agentRoot;

        [Header("Showcase Defaults")]
        [SerializeField] private bool _dynamicObstacleStartsEnabled = true;
        [SerializeField] private Vector3 _sampleProbe = new Vector3(-35f, 1f, 5f);

        private ShowcaseMode _mode;
        private bool _dynamicObstacleEnabled;
        private bool _dynamicObstacleRegistered;
        private bool _isInitialized;
        private bool _waitingForManager;
        private bool _showcaseStarted;
        private bool _isFaulted;
        private bool _initializationReported;
        private Exception _fault;
        private string _lastAction = "Waiting for FlowField and sample agents.";
        private FlowFieldSample _lastSample;
        private FlowFieldClampResult _lastClamp;
        private bool _hasSample;

        public bool IsInitialized => _isInitialized;
        public bool IsFaulted => _isFaulted;
        public string CurrentMode => _mode.ToString();
        public bool DynamicObstacleEnabled => _dynamicObstacleEnabled;
        public bool DynamicObstacleRegistered => _dynamicObstacleRegistered;
        public bool HasSample => _hasSample;
        public string LastAction => _lastAction;
        public int ActiveGoalIndex => _sampleController != null ? _sampleController.ActiveGoalIndex : -1;
        public int GoalCount => _sampleController != null ? _sampleController.GoalCount : 0;
        public Vector3 ActiveGoalPosition => _sampleController != null && _sampleController.HasActiveGoal
            ? _sampleController.ActiveGoalPosition
            : throw new InvalidOperationException("FlowField sample has no active goal.");
        public FlowFieldSample LastSample => _hasSample
            ? _lastSample
            : throw new InvalidOperationException("FlowField overview has not sampled a probe yet.");
        public FlowFieldClampResult LastClamp => _hasSample
            ? _lastClamp
            : throw new InvalidOperationException("FlowField overview has not clamped a probe yet.");

        private void Awake()
        {
            if (Application.isPlaying)
                TryInitializeWhenReady();
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
                if (_speedModifier == null || _noiseModifier == null)
                    throw new InvalidOperationException("FlowField overview requires serialized Speed and Noise modifiers.");
                if (_dynamicObstacle == null || _dynamicObstacleObject == null)
                    throw new InvalidOperationException("FlowField overview requires a serialized dynamic obstacle collider and object.");
                if (_freeCamera == null)
                    throw new InvalidOperationException("FlowField overview requires a serialized free camera.");
                if (_mapBoundsRoot == null)
                    throw new InvalidOperationException("FlowField overview requires a serialized map bounds root.");
                if (_agentRoot == null)
                    throw new InvalidOperationException("FlowField overview requires a serialized agent root.");
                if (!IsFinite(_sampleProbe))
                    throw new ArgumentOutOfRangeException(nameof(_sampleProbe));

                _dynamicObstacleEnabled = false;
                _dynamicObstacleRegistered = false;
                _waitingForManager = true;
                _isInitialized = true;
                _lastAction = "Waiting for a published FlowField and initialized sample agents.";
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
            TryBeginShowcaseWhenReady();
        }

        private void Update()
        {
            if (_isFaulted || !_isInitialized)
                return;

            if (_waitingForManager)
            {
                if (_manager == null || _manager.IsFaulted)
                {
                    if (_manager != null && _manager.BakeMode == FlowFieldBakeMode.StaticBaked)
                        _lastAction = "StaticBaked Asset 설정 불일치. Editor에서 ReBake가 필요합니다.";
                    else
                        _lastAction = _manager != null && !string.IsNullOrEmpty(_manager.LastError)
                            ? $"Manager error: {_manager.LastError}"
                            : "Waiting for Manager recovery.";
                    RenderBoard();
                    return;
                }
                if (!_manager.IsReady || !_sampleController.IsSimulationReady)
                {
                    RenderBoard();
                    return;
                }

                _waitingForManager = false;
                BeginShowcase();
            }

            if (!_showcaseStarted)
                return;

            if (Input.GetKeyDown(KeyCode.Space))
                AdvanceGoal();
            if (Input.GetKeyDown(KeyCode.G))
            {
                if (_manager.BakeMode == FlowFieldBakeMode.StaticBaked)
                    _lastAction = "Goal changes are disabled in StaticBaked mode.";
                else if (_sampleController.ClearGoal())
                    _lastAction = "Cleared the runtime Goal and requested a rebuild.";
            }
            if (Input.GetKeyDown(KeyCode.Alpha1))
                ApplyMode(ShowcaseMode.Baseline);
            if (Input.GetKeyDown(KeyCode.Alpha2))
                ApplyMode(ShowcaseMode.SpeedModifier);
            if (Input.GetKeyDown(KeyCode.Alpha3))
                ApplyMode(ShowcaseMode.NoiseModifier);
            if (Input.GetKeyDown(KeyCode.M))
                ToggleDynamicObstacle();
            if (Input.GetKeyDown(KeyCode.O))
                ApplyMode(ShowcaseMode.SampleAndClamp);
            if (Input.GetKeyDown(KeyCode.R))
                ExecuteAction(
                    _manager.RequestRebuild,
                    _manager.BakeMode == FlowFieldBakeMode.StaticBaked
                        ? "Reloaded and recomposed the baked Surface2D field."
                        : "Requested an explicit RuntimeDynamic rebuild.");
            if (Input.GetKeyDown(KeyCode.C))
                RefreshDiagnostics();
            if (Input.GetKeyDown(KeyCode.F))
                FocusCamera();

            if (_manager.IsReady)
                RefreshDiagnostics();
            RenderBoard();
        }

        private void BeginShowcase()
        {
            if (_showcaseStarted)
                return;

            _sampleController.SetAutomaticGoalChanges(false);
            ApplyMode(ShowcaseMode.Baseline);
            if (_manager.BakeMode == FlowFieldBakeMode.StaticBaked)
            {
                SetDynamicObstacle(false);
                _lastAction = "StaticBaked ready. Runtime Goal and obstacle inputs are disabled.";
            }
            else
            {
                SetDynamicObstacle(_dynamicObstacleStartsEnabled);
            }
            RefreshDiagnostics();
            FocusCamera();
            _showcaseStarted = true;
            RenderBoard();
        }

        public void AdvanceGoal()
        {
            ThrowIfUnavailable();
            if (_manager.BakeMode == FlowFieldBakeMode.StaticBaked)
            {
                _lastAction = "Goal changes are disabled in StaticBaked mode.";
                return;
            }
            if (_sampleController.AdvanceToNextGoal())
                _lastAction = "Advanced to the next runtime Goal and requested a rebuild.";
            RenderBoard();
        }

        public void ToggleDynamicObstacle()
        {
            SetDynamicObstacle(!_dynamicObstacleEnabled);
        }

        public void SetDynamicObstacle(bool enabled)
        {
            ThrowIfUnavailable();

            if (_manager.BakeMode == FlowFieldBakeMode.StaticBaked)
            {
                if (_dynamicObstacleRegistered)
                    UnregisterDynamicObstacleSafely();
                _dynamicObstacleEnabled = false;
                _dynamicObstacleObject.SetActive(false);
                _lastAction = "Dynamic obstacle input is disabled in StaticBaked mode.";
                RenderBoard();
                return;
            }

            bool changed = false;

            if (enabled == _dynamicObstacleEnabled)
            {
                _dynamicObstacleObject.SetActive(enabled);
                if (enabled && !_dynamicObstacleRegistered && _manager.IsReady)
                    changed = RegisterDynamicObstacle();
                if (changed)
                    _manager.RequestRebuild();
                return;
            }

            if (enabled)
            {
                _dynamicObstacleObject.SetActive(true);
                _dynamicObstacleEnabled = true;
                if (_manager.IsReady)
                    changed = RegisterDynamicObstacle();
            }
            else
            {
                if (_dynamicObstacleRegistered)
                    UnregisterDynamicObstacleSafely();

                _dynamicObstacleEnabled = false;
                _dynamicObstacleObject.SetActive(false);
                changed = true;
            }

            if (changed)
            {
                _manager.RequestRebuild();
                _lastAction = enabled
                    ? "Enabled the dynamic obstacle and requested a rebuild."
                    : "Disabled the dynamic obstacle and requested a rebuild.";
            }

            RenderBoard();
        }

        private bool RegisterDynamicObstacle()
        {
            if (_dynamicObstacleRegistered)
                return false;

            _dynamicObstacleObject.SetActive(true);
            bool added = _manager.RegisterDynamicObstacle(_dynamicObstacle);
            _dynamicObstacleRegistered = true;
            return added;
        }

        private bool UnregisterDynamicObstacleSafely()
        {
            if (!_dynamicObstacleRegistered)
                return false;

            bool removed = false;
            try
            {
                removed = _manager.UnregisterDynamicObstacle(_dynamicObstacle);
            }
            catch (InvalidOperationException)
            {
                // A manager rebuild or teardown can already have cleared the
                // pipeline. Treat that state as successfully unregistered.
            }
            finally
            {
                _dynamicObstacleRegistered = false;
            }
            return removed;
        }

        private void ApplyMode(ShowcaseMode mode)
        {
            ThrowIfUnavailable();
            _speedModifier.gameObject.SetActive(mode == ShowcaseMode.SpeedModifier);
            _noiseModifier.gameObject.SetActive(mode == ShowcaseMode.NoiseModifier);
            bool changed = _mode != mode;
            _mode = mode;

            if (changed && _manager.IsInitialized && !_manager.IsFaulted)
            {
                _manager.RequestRebuild();
                _lastAction = "Updated Modifier mode and requested a rebuild.";
            }

            if (mode == ShowcaseMode.SampleAndClamp)
                RefreshDiagnostics();
        }

        private void RefreshDiagnostics()
        {
            if (_manager == null || !_manager.IsReady)
            {
                _hasSample = false;
                return;
            }

            try
            {
                FlowFieldClampResult clamp = _manager.ClampPositionToGrid(_sampleProbe);
                if (!_manager.TrySample(clamp.Position, out FlowFieldSample sample))
                {
                    _hasSample = false;
                    return;
                }
                _lastClamp = clamp;
                _lastSample = sample;
                _hasSample = true;
            }
            catch (InvalidOperationException)
            {
                _hasSample = false;
            }
        }

        public void FocusCamera()
        {
            ThrowIfUnavailable();
            Renderer[] renderers = _mapBoundsRoot.GetComponentsInChildren<Renderer>(true);
            Bounds bounds = new Bounds(_mapBoundsRoot.position, Vector3.one);
            bool hasBounds = false;

            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || renderer.transform == _agentRoot || renderer.transform.IsChildOf(_agentRoot))
                    continue;

                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            if (hasBounds)
                _freeCamera.FocusOnBounds(bounds);
        }

        private void RenderBoard()
        {
            if (_board == null || !_board.IsInitialized)
                return;

            string goalText = _sampleController.HasActiveGoal
                ? $"Goal: {_sampleController.ActiveGoalIndex + 1}/{_sampleController.GoalCount} "
                    + $"({_sampleController.ActiveGoalPosition.x:F0}, {_sampleController.ActiveGoalPosition.y:F1}, {_sampleController.ActiveGoalPosition.z:F0})"
                : "Goal: none";
            string sampleText = _hasSample
                ? $"Probe dir.y: {_lastSample.Direction.y:F2}  surface: {_lastSample.HasSurface}"
                : "Probe: pending";
            string clampText = _hasSample
                ? $"Probe clamp: {(_lastClamp.ClampedX || _lastClamp.ClampedZ ? "clamped" : "inside")}"
                : "Probe clamp: pending";

            _board.Render(
                "2.5D FLOWFIELD\n"
                + $"Mode: {_manager.BakeMode}  Ready: {_manager.IsReady}  Revision: {_manager.Revision}\n"
                + $"Sample ready: {_sampleController.IsSimulationReady}  {_sampleController.LastStatus}\n"
                + $"Agents: {_sampleController.SpawnedAgentCount}/1000  Mode: {_mode}\n"
                + $"{goalText}\n"
                + $"West Ramp Gate: {(_dynamicObstacleEnabled ? "ON" : "OFF")}  Registered: {_dynamicObstacleRegistered}\n"
                + "Ramps: WEST / EAST  Y=0.0 -> Y=2.0 | ON => EAST bypass\n"
                + $"{sampleText}  {clampText}\n"
                + $"{_lastAction}\n"
                + "Space: next Goal | G: clear Goal | M: Gate | R: rebuild | F: Focus | RMB+WASD/QE: Camera");
        }

        public void Release()
        {
            if (!_isInitialized && !_isFaulted)
                throw new InvalidOperationException("FlowFieldShowcaseOverviewController has not been initialized.");

            if (_dynamicObstacleRegistered && _manager != null && _manager.IsInitialized)
                UnregisterDynamicObstacleSafely();
            else
                _dynamicObstacleRegistered = false;
            _dynamicObstacleEnabled = false;
            _showcaseStarted = false;
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

        private void ExecuteAction(Action action, string successMessage)
        {
            try
            {
                action();
                _lastAction = successMessage;
                _fault = null;
            }
            catch (Exception exception)
            {
                _fault = exception;
                _lastAction = _manager != null && _manager.BakeMode == FlowFieldBakeMode.StaticBaked
                    ? $"StaticBaked Asset을 다시 Bake해야 합니다. {exception.Message}"
                    : exception.Message;
                Debug.LogException(exception, this);
            }
            RenderBoard();
        }

        private void TryInitializeWhenReady()
        {
            if (_isFaulted)
                return;

            try
            {
                if (!_isInitialized)
                    Init();
                _initializationReported = false;
            }
            catch (Exception exception)
            {
                if (_initializationReported)
                    return;
                _initializationReported = true;
                _isInitialized = false;
                _isFaulted = true;
                _fault = exception;
                _lastAction = exception.Message;
                Debug.LogException(exception, this);
            }
        }

        private void TryBeginShowcaseWhenReady()
        {
            if (_isFaulted || !_isInitialized)
                return;
            if (_manager == null || _sampleController == null)
                return;
            if (!_manager.IsReady || !_sampleController.IsSimulationReady)
            {
                _waitingForManager = true;
                return;
            }
            _waitingForManager = false;
            BeginShowcase();
        }

        private static bool IsFinite(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool IsFinite(Vector3 value)
            => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    }
}
