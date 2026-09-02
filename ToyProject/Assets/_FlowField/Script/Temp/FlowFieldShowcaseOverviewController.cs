using System;
using UnityEngine;
using Common.TransformPath.Samples;

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
        [SerializeField] private TransformPathFreeCamera _freeCamera;
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
        private Exception _fault;
        private FlowFieldSample _lastSample;
        private FlowFieldClampResult _lastClamp;
        private bool _hasSample;

        public bool IsInitialized => _isInitialized;
        public bool IsFaulted => _isFaulted;
        public string CurrentMode => _mode.ToString();
        public bool DynamicObstacleEnabled => _dynamicObstacleEnabled;
        public bool DynamicObstacleRegistered => _dynamicObstacleRegistered;
        public bool HasSample => _hasSample;
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
                if (_freeCamera == null)
                    throw new InvalidOperationException("FlowField overview requires a serialized free camera.");
                if (_mapBoundsRoot == null)
                    throw new InvalidOperationException("FlowField overview requires a serialized map bounds root.");
                if (_agentRoot == null)
                    throw new InvalidOperationException("FlowField overview requires a serialized agent root.");
                if (!IsFinite(_sampleProbe))
                    throw new ArgumentOutOfRangeException(nameof(_sampleProbe));

                _sampleController.SetAutomaticGoalChanges(false);
                _dynamicObstacleEnabled = false;
                _dynamicObstacleRegistered = false;
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
            if (!_waitingForManager)
                BeginShowcase();
        }

        private void Update()
        {
            ThrowIfUnavailable();

            if (_waitingForManager)
            {
                if (!_manager.IsReady)
                    return;

                _waitingForManager = false;
                BeginShowcase();
            }

            if (!_showcaseStarted)
                return;

            if (Input.GetKeyDown(KeyCode.Space))
                AdvanceGoal();
            if (Input.GetKeyDown(KeyCode.G))
                _sampleController.ClearGoal();
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
                _manager.Rebuild();
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

            ApplyMode(ShowcaseMode.Baseline);
            SetDynamicObstacle(_dynamicObstacleStartsEnabled);
            RefreshDiagnostics();
            FocusCamera();
            _showcaseStarted = true;
            RenderBoard();
        }

        public void AdvanceGoal()
        {
            ThrowIfUnavailable();
            _sampleController.AdvanceToNextGoal();
            RenderBoard();
        }

        public void ToggleDynamicObstacle()
        {
            SetDynamicObstacle(!_dynamicObstacleEnabled);
        }

        public void SetDynamicObstacle(bool enabled)
        {
            ThrowIfUnavailable();

            if (enabled == _dynamicObstacleEnabled)
            {
                _dynamicObstacleObject.SetActive(enabled);
                if (enabled && !_dynamicObstacleRegistered && _manager.IsReady)
                    RegisterDynamicObstacle();
                return;
            }

            if (enabled)
            {
                _dynamicObstacleObject.SetActive(true);
                _dynamicObstacleEnabled = true;
                if (_manager.IsReady)
                    RegisterDynamicObstacle();
            }
            else
            {
                if (_dynamicObstacleRegistered)
                    UnregisterDynamicObstacleSafely();

                _dynamicObstacleEnabled = false;
                _dynamicObstacleObject.SetActive(false);
            }

            RenderBoard();
        }

        private void RegisterDynamicObstacle()
        {
            if (_dynamicObstacleRegistered)
                return;

            _dynamicObstacleObject.SetActive(true);
            _manager.RegisterDynamicObstacle(_dynamicObstacle);
            _dynamicObstacleRegistered = true;
        }

        private void UnregisterDynamicObstacleSafely()
        {
            if (!_dynamicObstacleRegistered)
                return;

            try
            {
                _manager.UnregisterDynamicObstacle(_dynamicObstacle);
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
        }

        private void ApplyMode(ShowcaseMode mode)
        {
            ThrowIfUnavailable();
            _speedModifier.gameObject.SetActive(mode == ShowcaseMode.SpeedModifier);
            _noiseModifier.gameObject.SetActive(mode == ShowcaseMode.NoiseModifier);
            _mode = mode;

            if (mode == ShowcaseMode.SampleAndClamp)
                RefreshDiagnostics();
        }

        private void RefreshDiagnostics()
        {
            FlowFieldClampResult clamp = _manager.ClampPositionToGrid(_sampleProbe);
            FlowFieldSample sample = _manager.Sample(clamp.Position);
            _lastClamp = clamp;
            _lastSample = sample;
            _hasSample = true;
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
                + $"Ready: {_manager.IsReady}  Revision: {_manager.Revision}\n"
                + $"Agents: {_sampleController.SpawnedAgentCount}/1000  Mode: {_mode}\n"
                + $"{goalText}\n"
                + $"West Ramp Gate: {(_dynamicObstacleEnabled ? "ON" : "OFF")}  Registered: {_dynamicObstacleRegistered}\n"
                + "Ramps: WEST / EAST  Y=0.0 -> Y=2.0 | ON => EAST bypass\n"
                + $"{sampleText}  {clampText}\n"
                + "Space: next Goal | M: Gate | F: Focus | RMB+WASD/QE: Camera");
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

        private static bool IsFinite(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool IsFinite(Vector3 value)
            => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    }
}
