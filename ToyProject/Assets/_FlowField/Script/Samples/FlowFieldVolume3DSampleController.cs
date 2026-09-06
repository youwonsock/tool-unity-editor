using System;
using UnityEngine;

namespace Common.FlowField.Samples
{
    /// <summary>
    /// Minimal free-flight sample for a Volume3D manager. Configure the
    /// referenced manager in Volume3D mode with a 20x12x20, cell-size 1
    /// volume, then use the public goal request contract to verify Y movement and
    /// diagonal obstacle detours with 64 Rigidbody agents.
    /// </summary>
    [DefaultExecutionOrder(10)]
    public sealed class FlowFieldVolume3DSampleController : FlowFieldSampleControllerBase
    {
        [Header("FlowField")]
        [SerializeField] private FlowFieldManager _manager;
        [SerializeField] private FlowFieldSampleAgent _agentPrefab;
        [SerializeField] private Transform _agentRoot;
        [SerializeField] private Transform _goalMarker;

        [Header("20 x 12 x 20 sample")]
        [SerializeField] private int _agentCount = 64;
        [SerializeField] private Vector3 _spawnCenter = new Vector3(10f, 2f, 10f);
        [SerializeField] private Vector3 _spawnSpacing = new Vector3(0.75f, 0.75f, 0.75f);
        [SerializeField] private float _agentSpeed = 3f;
        [SerializeField] private float _agentAcceleration = 8f;
        [SerializeField] private Vector3[] _goalPositions =
        {
            new Vector3(19.5f, 11.5f, 19.5f),
            new Vector3(0.5f, 0.5f, 19.5f),
            new Vector3(19.5f, 0.5f, 0.5f),
            new Vector3(0.5f, 11.5f, 0.5f),
        };

        private int _goalIndex = -1;
        private Vector3 _activeGoalPosition;
        private bool _hasActiveGoal;
        private bool _initialized;
        private bool _waitingForManager;
        private bool _initializationReported;

        public int AgentCount => Agents.Count;
        public int ActiveGoalIndex => _goalIndex;
        public bool HasActiveGoal => _hasActiveGoal;
        public Vector3 ActiveGoalPosition => _hasActiveGoal
            ? _activeGoalPosition
            : throw new InvalidOperationException("Volume3D sample has no active goal.");
        public bool IsInitialized => _initialized;
        public bool IsWaitingForManager => _waitingForManager;
        public string LastStatus { get; private set; } = "Waiting for Volume3D field.";

        private void Awake()
        {
            if (Application.isPlaying)
                TryInitializeWhenReady();
        }

        private void Update()
        {
            if (Application.isPlaying && _waitingForManager && !_initialized)
                TryInitializeWhenReady();
        }

        public void Init()
        {
            if (_initialized)
                throw new InvalidOperationException("Volume3D sample is already initialized.");
            if (_manager == null || _agentPrefab == null || _agentRoot == null || _goalMarker == null)
                throw new InvalidOperationException("Volume3D sample requires Manager, Agent Prefab, Agent Root and Goal Marker.");
            if (_manager.SpaceMode != FlowFieldSpaceMode.Volume3D)
                throw new InvalidOperationException("The sample manager must be configured for Volume3D before Init.");
            if (!_manager.IsInitialized)
            {
                if (_manager.IsFaulted)
                    throw new InvalidOperationException(
                        "The FlowFieldManager failed before the Volume3D sample could start.",
                        new InvalidOperationException(_manager.LastError));
                _waitingForManager = true;
                return;
            }
            if (_manager.IsFaulted)
                throw new InvalidOperationException(
                    "The FlowFieldManager failed before the Volume3D sample could start.",
                    new InvalidOperationException(_manager.LastError));
            if (!_manager.IsReady)
            {
                _waitingForManager = true;
                return;
            }
            if (!_manager.TryGetFieldInfo(out FlowFieldFieldInfo fieldInfo)
                || !fieldInfo.IsValid)
            {
                _waitingForManager = true;
                return;
            }
            FlowFieldGridSpace grid = fieldInfo.Grid;
            if (grid.SpaceMode != FlowFieldSpaceMode.Volume3D
                || grid.Width != 20
                || grid.Height != 12
                || grid.Depth != 20
                || !Mathf.Approximately(grid.CellSize, 1f))
                throw new InvalidOperationException("The Volume3D sample requires a 20x12x20, CellSize 1 grid.");
            if (_agentCount != 64 || _goalPositions == null || _goalPositions.Length < 2)
                throw new ArgumentException("The Volume3D sample uses exactly 64 agents and at least two Goals.");
            if (_spawnSpacing.x <= 0f || _spawnSpacing.y <= 0f || _spawnSpacing.z <= 0f)
                throw new ArgumentOutOfRangeException(nameof(_spawnSpacing));
            for (int index = 0; index < _goalPositions.Length; index++)
                if (!IsFinite(_goalPositions[index]))
                    throw new ArgumentException("Volume3D sample Goals must be finite.", nameof(_goalPositions));

            _waitingForManager = false;
            _agentRoot.gameObject.SetActive(false);
            try
            {
                for (int index = 0; index < _agentCount; index++)
                {
                    int x = index % 4;
                    int y = (index / 4) % 4;
                    int z = index / 16;
                    FlowFieldSampleAgent agent = Instantiate(
                        _agentPrefab,
                        _spawnCenter + Vector3.Scale(new Vector3(x, y, z), _spawnSpacing),
                        Quaternion.identity,
                        _agentRoot);
                    // Take ownership before Configure/Init so a partial
                    // spawn is always released if either step fails.
                    OwnAgent(agent);
                    agent.name = $"VolumeAgent_{index + 1:00}";
                    ConfigureAndInitializeAgent(agent, _manager, _agentSpeed, _agentAcceleration);
                }
                _agentRoot.gameObject.SetActive(true);
                if (_manager.BakeMode == FlowFieldBakeMode.StaticBaked)
                {
                    _goalIndex = fieldInfo.HasRequestedGoal
                        ? FindGoalIndex(fieldInfo.RequestedGoalWorld)
                        : -1;
                    _hasActiveGoal = fieldInfo.HasRequestedGoal;
                    _activeGoalPosition = fieldInfo.RequestedGoalWorld;
                    _goalMarker.position = fieldInfo.RequestedGoalWorld;
                    _goalMarker.gameObject.SetActive(fieldInfo.HasRequestedGoal);
                    LastStatus = "StaticBaked field loaded; runtime Goal input is disabled.";
                }
                else
                {
                    ApplyInitialRuntimeGoal(fieldInfo);
                    LastStatus = "RuntimeDynamic field is ready.";
                }
                _initialized = true;
            }
            catch
            {
                Release();
                throw;
            }
        }

        private void TryInitializeWhenReady()
        {
            try
            {
                Init();
                _initializationReported = false;
            }
            catch (Exception exception)
            {
                _waitingForManager = false;
                if (_initializationReported)
                    return;
                _initializationReported = true;
                Debug.LogException(exception, this);
            }
        }

        public bool SetNextGoal()
        {
            if (!_initialized)
            {
                LastStatus = "Volume3D sample is not initialized.";
                return false;
            }
            if (_manager.BakeMode == FlowFieldBakeMode.StaticBaked)
            {
                LastStatus = "StaticBaked mode uses the Goal stored in the bake asset.";
                return false;
            }
            return SetGoal((_goalIndex + 1) % _goalPositions.Length);
        }

        public void Release()
        {
            ReleaseOwnedAgents();
            _initialized = false;
            _waitingForManager = false;
            _goalIndex = -1;
            _hasActiveGoal = false;
            _activeGoalPosition = default;
            LastStatus = "Released.";
        }

        private void FixedUpdate()
        {
            if (!_initialized || _manager == null)
                return;
            SimulateOwnedAgents(Time.fixedDeltaTime);
        }

        private bool SetGoal(int index)
        {
            if (_manager == null
                || !_manager.IsInitialized
                || _manager.IsFaulted
                || _manager.BakeMode == FlowFieldBakeMode.StaticBaked)
                return false;

            Vector3 goal = _goalPositions[index];
            _manager.SetGoal(FlowFieldGoalRequest.Position(goal));
            _manager.RequestRebuild();
            _goalIndex = index;
            _activeGoalPosition = goal;
            _hasActiveGoal = true;
            _goalMarker.position = goal;
            _goalMarker.gameObject.SetActive(true);
            LastStatus = $"Requested runtime Goal {index + 1}.";
            return true;
        }

        private void ApplyInitialRuntimeGoal(FlowFieldFieldInfo fieldInfo)
        {
            Vector3 initialGoal = _goalPositions[0];
            bool sameGoal = fieldInfo.HasRequestedGoal
                && Approximately(fieldInfo.RequestedGoalWorld, initialGoal);
            if (!sameGoal)
            {
                _manager.SetGoal(FlowFieldGoalRequest.Position(initialGoal));
                _manager.RequestRebuild();
            }

            _goalIndex = 0;
            _activeGoalPosition = initialGoal;
            _hasActiveGoal = true;
            _goalMarker.position = initialGoal;
            _goalMarker.gameObject.SetActive(true);
        }

        private int FindGoalIndex(Vector3 position)
        {
            for (int index = 0; index < _goalPositions.Length; index++)
                if (Approximately(_goalPositions[index], position))
                    return index;
            return -1;
        }

        private static bool Approximately(Vector3 left, Vector3 right)
            => (left - right).sqrMagnitude <= 0.000001f;

        private static bool IsFinite(Vector3 value)
            => !float.IsNaN(value.x) && !float.IsInfinity(value.x)
                && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
                && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
    }
}
