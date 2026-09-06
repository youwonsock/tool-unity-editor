using System;
using UnityEngine;

namespace Common.FlowField.Samples
{
    /// <summary>
    /// Scene-only input and diagnostics for the Volume3D sample. The sample
    /// controller still owns agent creation and FixedUpdate simulation.
    /// </summary>
    [DefaultExecutionOrder(20)]
    public sealed class FlowFieldVolume3DTestSceneController : MonoBehaviour
    {
        [Header("Serialized References")]
        [SerializeField] private FlowFieldManager _manager;
        [SerializeField] private FlowFieldVolume3DSampleController _sampleController;
        [SerializeField] private Transform _goalMarker;
        [SerializeField] private Collider _dynamicGate;
        [SerializeField] private GameObject _dynamicGateObject;
        [SerializeField] private FlowFieldFreeCamera _freeCamera;

        [Header("Diagnostics")]
        [SerializeField] private Vector3 _probePosition = new Vector3(10f, 2f, 10f);

        private bool _dynamicGateRegistered;
        private bool _goalCleared;
        private string _lastAction = "Starting Volume3D test scene...";
        private Exception _lastException;
        private int _probeStatusFrame = -1;
        private string _probeStatus = "Probe: waiting for the first frame.";
        private bool _sceneValidated;
        private bool _validationReported;
        private bool _cameraFocused;

        private void Start()
        {
            TryValidateSceneConfiguration();
        }

        private void Update()
        {
            if (_manager == null || _sampleController == null)
                return;

            if (!_sceneValidated)
            {
                if (_manager.IsFaulted && _manager.BakeMode == FlowFieldBakeMode.StaticBaked)
                    _lastAction = "StaticBaked Asset 설정 불일치. Editor에서 ReBake가 필요합니다.";
                if (_manager.IsReady && _sampleController.IsInitialized)
                    TryValidateSceneConfiguration();
                if (!_sceneValidated)
                    return;
            }

            if (Input.GetKeyDown(KeyCode.Space))
            {
                if (_manager.BakeMode == FlowFieldBakeMode.StaticBaked)
                    _lastAction = "Space is disabled in StaticBaked mode; the baked Goal is fixed.";
                else
                    ExecuteAction(
                        () =>
                        {
                            _sampleController.SetNextGoal();
                            _goalCleared = false;
                            _goalMarker.gameObject.SetActive(true);
                        },
                        "Advanced to the next Goal.");
            }

            if (Input.GetKeyDown(KeyCode.O))
            {
                if (_manager.BakeMode == FlowFieldBakeMode.StaticBaked)
                    _lastAction = "O is disabled in StaticBaked mode; baked obstacles are fixed.";
                else
                    ExecuteAction(ToggleDynamicGate, "Toggled DynamicGate.");
            }

            if (Input.GetKeyDown(KeyCode.R))
                ExecuteAction(_manager.RequestRebuild,
                    _manager.BakeMode == FlowFieldBakeMode.StaticBaked
                        ? "Reloaded and recomposed the baked Volume3D field."
                        : "Requested an explicit rebuild.");

            if (Input.GetKeyDown(KeyCode.G))
            {
                if (_manager.BakeMode == FlowFieldBakeMode.StaticBaked)
                    _lastAction = "G is disabled in StaticBaked mode; the baked Goal is fixed.";
                else
                    ExecuteAction(
                        () =>
                        {
                            _manager.SetGoal(FlowFieldGoalRequest.None);
                            _manager.RequestRebuild();
                            _goalCleared = true;
                            _goalMarker.gameObject.SetActive(false);
                        },
                        "Cleared the Goal; default flow is active.");
            }

            if (Input.GetKeyDown(KeyCode.F))
                ExecuteAction(FocusCamera, "Focused the FlowField camera on the published Volume3D bounds.");
        }

        private void OnGUI()
        {
            if (_manager == null || _sampleController == null)
                return;

            RefreshProbeStatus();
            const float width = 500f;
            const float height = 270f;
            GUI.Box(new Rect(16f, 16f, width, height), GUIContent.none);
            GUILayout.BeginArea(new Rect(30f, 28f, width - 28f, height - 24f));
            GUILayout.Label("FlowField Volume3D Test");
            GUILayout.Label("Space: next Goal   O: DynamicGate   R: rebuild/reload   G: clear Goal   F: focus camera");

            string gridStatus = _manager.TryGetFieldInfo(out FlowFieldFieldInfo fieldInfo)
                ? $"Grid: {fieldInfo.Grid.Width} x {fieldInfo.Grid.Height} x {fieldInfo.Grid.Depth} ({fieldInfo.Grid.CellCount} cells)"
                : "Grid: unavailable";
            Vector3 displayedGoal = _goalCleared || !_sampleController.HasActiveGoal
                ? Vector3.zero
                : _sampleController.ActiveGoalPosition;
            GUILayout.Label(
                $"Mode: {_manager.BakeMode}   State: {_manager.State}   Ready: {_manager.IsReady}   Rebuilding: {_manager.IsRebuilding}\n"
                + $"Revision: {_manager.Revision}   {gridStatus}\n"
                + $"Agents: {_sampleController.AgentCount}   Goal: {(_goalCleared ? -1 : _sampleController.ActiveGoalIndex)}  Y: {(_goalCleared ? 0f : displayedGoal.y):0.00}\n"
                + $"DynamicGate: {(_dynamicGateObject != null && _dynamicGateObject.activeSelf ? "ON" : "OFF")}"
                + $" / registered: {_dynamicGateRegistered}");
            GUILayout.Label($"Sample: {_sampleController.LastStatus}");
            GUILayout.Label(_probeStatus);
            GUILayout.Label($"Last action: {_lastAction}");
            if (_lastException != null)
                GUILayout.Label($"Last error: {_lastException.Message}");
            if (!string.IsNullOrEmpty(_manager.LastError))
                GUILayout.Label($"Manager error: {_manager.LastError}");
            GUILayout.EndArea();
        }

        private void RefreshProbeStatus()
        {
            if (_probeStatusFrame == Time.frameCount)
                return;
            _probeStatusFrame = Time.frameCount;

            if (!_manager.TrySample(_probePosition, out FlowFieldSample sample))
            {
                _probeStatus = $"Probe: {_probePosition} (not ready or outside the Volume3D grid)";
                return;
            }

            _probeStatus =
                $"Probe: {_probePosition}  HasCell: {sample.HasCell}  HasSurface: {sample.HasSurface}\n"
                + $"Direction: {sample.Direction:F2}  Speed: {sample.SpeedMultiplier:F2}  Normal: {sample.SurfaceNormal:F2}";
        }

        private void ToggleDynamicGate()
        {
            bool enable = !_dynamicGateObject.activeSelf;
            if (enable)
            {
                _dynamicGateObject.SetActive(true);
                try
                {
                    Physics.SyncTransforms();
                    bool added = _manager.RegisterDynamicObstacle(_dynamicGate);
                    _dynamicGateRegistered = true;
                    if (added)
                        _manager.RequestRebuild();
                }
                catch
                {
                    _dynamicGateObject.SetActive(false);
                    throw;
                }
                return;
            }

            try
            {
                bool removed = false;
                if (_dynamicGateRegistered && _manager.IsInitialized && !_manager.IsFaulted)
                    removed = _manager.UnregisterDynamicObstacle(_dynamicGate);
                _dynamicGateRegistered = false;
                _dynamicGateObject.SetActive(false);
                if (removed)
                    _manager.RequestRebuild();
            }
            catch
            {
                _dynamicGateRegistered = false;
                _dynamicGateObject.SetActive(false);
                throw;
            }
        }

        private void ValidateSceneConfiguration()
        {
            if (_manager == null || _sampleController == null || _goalMarker == null)
                throw new InvalidOperationException(
                    "Volume3D test scene requires a FlowFieldManager, FlowFieldVolume3DSampleController and GoalMarker.");
            if (_manager.SpaceMode != FlowFieldSpaceMode.Volume3D)
                throw new InvalidOperationException("Volume3D test scene Manager must use Volume3D mode.");
            if (!_manager.IsInitialized)
                throw new InvalidOperationException("Volume3D test scene Manager must initialize before the test driver.");
            if (_freeCamera == null)
                throw new InvalidOperationException("Volume3D test scene requires a FlowFieldFreeCamera reference.");
            if (_manager.IsFaulted)
                throw new InvalidOperationException(
                    "Volume3D test scene Manager initialization failed.",
                    new InvalidOperationException(_manager.LastError));
            if (_sampleController.AgentCount != 64)
                throw new InvalidOperationException("Volume3D test scene requires exactly 64 spawned agents.");
            if (_dynamicGate == null || _dynamicGateObject == null || _dynamicGate.gameObject != _dynamicGateObject)
                throw new InvalidOperationException("Volume3D test scene requires a DynamicGate object and collider reference.");
            if (!(_dynamicGate is BoxCollider) || _dynamicGate.isTrigger)
                throw new InvalidOperationException("DynamicGate must use a non-trigger BoxCollider.");

            int obstacleLayer = LayerMask.NameToLayer("FlowFieldObstacle");
            if (obstacleLayer < 0 || _dynamicGate.gameObject.layer != obstacleLayer)
                throw new InvalidOperationException("DynamicGate must be on the FlowFieldObstacle layer.");
            if (!IsFinite(_probePosition))
                throw new ArgumentOutOfRangeException(nameof(_probePosition));
        }

        private void TryValidateSceneConfiguration()
        {
            try
            {
                if (!_manager.IsReady || !_sampleController.IsInitialized)
                {
                    _lastAction = "Waiting for a published Volume3D field and 64 agents...";
                    return;
                }
                ValidateSceneConfiguration();
                if (_dynamicGateObject.activeSelf)
                    _dynamicGateObject.SetActive(false);
                _dynamicGateRegistered = false;
                _sceneValidated = true;
                _lastException = null;
                if (!_cameraFocused)
                {
                    FocusCamera();
                    _cameraFocused = true;
                }
                _lastAction = "Ready. Space: next Goal, O: toggle gate, R: rebuild, G: clear Goal, F: focus camera.";
            }
            catch (Exception exception)
            {
                _lastException = exception;
                _lastAction = "Scene validation failed.";
                if (!_validationReported)
                {
                    _validationReported = true;
                    Debug.LogException(exception, this);
                }
            }
        }

        private void FocusCamera()
        {
            if (_freeCamera == null)
                throw new InvalidOperationException("FlowFieldFreeCamera is not assigned.");
            if (!_manager.TryGetFieldInfo(out FlowFieldFieldInfo info) || !info.IsValid)
                throw new InvalidOperationException("A published Volume3D field is required before focusing the camera.");
            _freeCamera.FocusOnBounds(info.WorldBounds);
            _cameraFocused = true;
        }

        private void ExecuteAction(Action action, string successMessage)
        {
            try
            {
                action();
                _lastException = null;
                _lastAction = successMessage;
            }
            catch (Exception exception)
            {
                _lastException = exception;
                _lastAction = "Action failed.";
                Debug.LogException(exception, this);
            }
        }

        private void OnDestroy()
        {
            if (!_dynamicGateRegistered || _manager == null || !_manager.IsInitialized)
                return;

            try
            {
                if (_manager.BakeMode == FlowFieldBakeMode.RuntimeDynamic && !_manager.IsFaulted)
                    _manager.UnregisterDynamicObstacle(_dynamicGate);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
            _dynamicGateRegistered = false;
        }

        private static bool IsFinite(Vector3 value)
            => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);

        private static bool IsFinite(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
