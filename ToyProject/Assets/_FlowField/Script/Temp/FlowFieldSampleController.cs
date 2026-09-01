using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField.Samples
{
    /// <summary>
    /// 1,000개 물리 Agent를 생성하고 FlowField Goal을 무작위로 변경하는 샘플 컨트롤러입니다.
    /// </summary>
    [DefaultExecutionOrder(0)]
    public sealed class FlowFieldSampleController : MonoBehaviour
    {
        [Header("FlowField")]
        [SerializeField] private FlowFieldManager _manager;
        [SerializeField] private FlowFieldSampleAgent _agentPrefab;
        [SerializeField] private Transform _agentRoot;
        [SerializeField] private Transform _goalMarker;

        [Header("Agent Spawn")]
        [SerializeField] private int _agentCount = 1000;
        [SerializeField] private int _spawnColumns = 40;
        [SerializeField] private int _spawnRows = 25;
        [SerializeField] private float _spawnSpacing = 0.65f;
        [SerializeField] private Vector3 _spawnCenter = new Vector3(-35f, 0.4f, -40f);
        [SerializeField] private float _agentSpeed = 3f;
        [SerializeField] private float _agentAcceleration = 8f;

        [Header("Random Goal")]
        [SerializeField] private Vector3[] _goalPositions =
        {
            new Vector3(-35f, 0.4f, 35f),
            new Vector3(-5f, 0.4f, 35f),
            new Vector3(35f, 0.4f, 35f),
            new Vector3(35f, 0.4f, 5f),
            new Vector3(35f, 0.4f, -35f),
            new Vector3(5f, 0.4f, -35f),
            new Vector3(-35f, 0.4f, -5f),
            new Vector3(5f, 0.4f, 5f),
        };
        [SerializeField] private float _goalChangeInterval = 15f;
        [SerializeField] private float _goalInfluenceRadius = 10f;
        [SerializeField] private int _randomSeed = 20260831;

        [Header("Diagnostics")]
        [SerializeField] private float _diagnosticsInterval = 1f;
        [SerializeField] private float _deepOverlapDistance = 0.45f;
        [SerializeField] private bool _showOverlay = true;

        private readonly List<FlowFieldSampleAgent> _agents = new List<FlowFieldSampleAgent>();
        private readonly Dictionary<Vector2Int, List<int>> _overlapBuckets =
            new Dictionary<Vector2Int, List<int>>();
        private System.Random _random;
        private float _goalTimer;
        private float _diagnosticsTimer;
        private int _activeGoalIndex = -1;
        private int _goalChangeCount;
        private int _deepOverlapPairs;
        private bool _simulationReady;
        private bool _automaticGoalChanges = true;
        private bool _isInitialized;
        private bool _isFaulted;
        private Exception _fault;

        public int SpawnedAgentCount => _agents.Count;
        public int ActiveGoalIndex => _activeGoalIndex;
        public int GoalChangeCount => _goalChangeCount;
        public bool HasActiveGoal => _activeGoalIndex >= 0;
        public int ManagerRevision => _manager != null ? _manager.Revision : 0;
        public Vector3 ActiveGoalPosition => _activeGoalIndex >= 0 && _activeGoalIndex < _goalPositions.Length
            ? _goalPositions[_activeGoalIndex]
            : throw new InvalidOperationException("FlowField sample has no active goal.");
        public bool AutomaticGoalChangesEnabled => _automaticGoalChanges;
        public int DeepOverlapPairs => _deepOverlapPairs;
        public bool IsSimulationReady => _simulationReady;
        public bool IsInitialized => _isInitialized;
        public bool IsFaulted => _isFaulted;

        private void Awake()
        {
            if (Application.isPlaying)
                Init();
        }

        public void Init()
        {
            if (_isInitialized)
                throw new InvalidOperationException("FlowFieldSampleController is already initialized.");
            if (_isFaulted)
                throw new InvalidOperationException("FlowFieldSampleController is faulted; call Release before Init.", _fault);
            try
            {
                if (_manager == null)
                    throw new InvalidOperationException("FlowFieldSampleController requires a serialized FlowFieldManager.");
                if (!_manager.IsInitialized)
                    throw new InvalidOperationException("FlowFieldManager must be initialized before the sample controller.");
                if (_agentPrefab == null)
                    throw new InvalidOperationException("FlowFieldSampleController requires a serialized agent prefab.");
                if (_agentRoot == null)
                    throw new InvalidOperationException("FlowFieldSampleController requires a serialized agent root.");
                if (_goalMarker == null)
                    throw new InvalidOperationException("FlowFieldSampleController requires a serialized goal marker.");
                if (_agentCount != _spawnColumns * _spawnRows)
                    throw new ArgumentException("Agent count must equal spawn columns multiplied by spawn rows.", nameof(_agentCount));
                if (_agentCount <= 0 || _spawnColumns <= 0 || _spawnRows <= 0)
                    throw new ArgumentOutOfRangeException(nameof(_agentCount));
                if (!IsFinite(_spawnSpacing) || _spawnSpacing < 0.5f)
                    throw new ArgumentOutOfRangeException(nameof(_spawnSpacing));
                if (!IsFinite(_spawnCenter))
                    throw new ArgumentOutOfRangeException(nameof(_spawnCenter));
                if (!IsFinite(_agentSpeed) || _agentSpeed <= 0f)
                    throw new ArgumentOutOfRangeException(nameof(_agentSpeed));
                if (!IsFinite(_agentAcceleration) || _agentAcceleration <= 0f)
                    throw new ArgumentOutOfRangeException(nameof(_agentAcceleration));
                if (_goalPositions == null || _goalPositions.Length < 2)
                    throw new ArgumentException("At least two goal positions are required.", nameof(_goalPositions));
                for (int i = 0; i < _goalPositions.Length; i++)
                {
                    if (!IsFinite(_goalPositions[i]))
                        throw new ArgumentOutOfRangeException(nameof(_goalPositions));
                }
                if (!IsFinite(_goalChangeInterval) || _goalChangeInterval <= 0f)
                    throw new ArgumentOutOfRangeException(nameof(_goalChangeInterval));
                if (!IsFinite(_goalInfluenceRadius) || _goalInfluenceRadius < 0f)
                    throw new ArgumentOutOfRangeException(nameof(_goalInfluenceRadius));
                if (!IsFinite(_diagnosticsInterval) || _diagnosticsInterval <= 0f)
                    throw new ArgumentOutOfRangeException(nameof(_diagnosticsInterval));
                if (!IsFinite(_deepOverlapDistance) || _deepOverlapDistance <= 0f)
                    throw new ArgumentOutOfRangeException(nameof(_deepOverlapDistance));
                _random = new System.Random(_randomSeed);
                _automaticGoalChanges = true;
                _isInitialized = true;
            }
            catch (Exception exception)
            {
                _isInitialized = false;
                _isFaulted = true;
                if (_fault == null)
                    _fault = exception;
                throw;
            }
        }

        private void Start()
        {
            if (_isFaulted)
                throw new InvalidOperationException("FlowFieldSampleController is faulted; call Release before use.", _fault);
            if (!_isInitialized)
                throw new InvalidOperationException("FlowFieldSampleController is not initialized.");

            _agentRoot.gameObject.SetActive(false);
            try
            {
                SpawnAgents();
                _agentRoot.gameObject.SetActive(true);
                for (int i = 0; i < _agents.Count; i++)
                {
                    if (_agents[i] == null || !_agents[i].IsInitialized)
                        throw new InvalidOperationException($"FlowField agent {i} did not initialize when Agent Root was activated.");
                }

                ChooseRandomGoal();
                _simulationReady = _agents.Count == _agentCount;
            }
            catch (Exception exception)
            {
                ReleaseSpawnedAgents();
                _simulationReady = false;
                _isInitialized = false;
                _isFaulted = true;
                if (_fault == null)
                    _fault = exception;
                throw;
            }
        }

        private void Update()
        {
            if (_isFaulted)
                throw new InvalidOperationException("FlowFieldSampleController is faulted; call Release before use.", _fault);
            if (!_isInitialized)
                throw new InvalidOperationException("FlowFieldSampleController is not initialized.");
            if (Input.GetKeyDown(KeyCode.Space))
                ChooseRandomGoal();

            if (_automaticGoalChanges)
            {
                _goalTimer += Time.deltaTime;
                if (_goalTimer >= _goalChangeInterval)
                {
                    _goalTimer = 0f;
                    ChooseRandomGoal();
                }
            }
        }

        private void FixedUpdate()
        {
            if (_isFaulted)
                throw new InvalidOperationException("FlowFieldSampleController is faulted; call Release before use.", _fault);
            if (!_isInitialized)
                throw new InvalidOperationException("FlowFieldSampleController is not initialized.");
            if (!_simulationReady)
                return;

            float deltaTime = Time.fixedDeltaTime;
            for (int i = 0; i < _agents.Count; i++)
            {
                FlowFieldSampleAgent agent = _agents[i];
                if (agent == null || !agent.IsInitialized)
                    throw new InvalidOperationException($"FlowField agent {i} is missing or not initialized.");
                agent.Simulate(deltaTime);
            }

            _diagnosticsTimer += deltaTime;
            if (_diagnosticsTimer >= _diagnosticsInterval)
            {
                _diagnosticsTimer = 0f;
                UpdateOverlapDiagnostics();
            }
        }

        private void SpawnAgents()
        {
            _agents.Clear();
            if (_agentPrefab == null)
                throw new InvalidOperationException("FlowFieldSampleController requires a serialized agent prefab.");

            for (int i = 0; i < _agentCount; i++)
            {
                int column = i % _spawnColumns;
                int row = i / _spawnColumns;
                Vector3 position = _spawnCenter + new Vector3(
                    (column - (_spawnColumns - 1) * 0.5f) * _spawnSpacing,
                    0f,
                    (row - (_spawnRows - 1) * 0.5f) * _spawnSpacing);

                FlowFieldSampleAgent agent = Instantiate(
                    _agentPrefab,
                    position,
                    Quaternion.identity,
                    _agentRoot);
                agent.name = $"Agent_{i + 1:0000}";
                agent.Configure(_manager, _agentSpeed, _agentAcceleration);
                if (!agent.IsInitialized)
                    agent.Init();
                _agents.Add(agent);
            }
        }

        public void ChooseNextGoal()
        {
            ThrowIfUnavailable();
            ChooseRandomGoal();
        }

        /// <summary>
        /// 활성 Goal을 명시적으로 제거해 Goal 없는 Field 결과를 확인합니다.
        /// </summary>
        public void ClearGoal()
        {
            ThrowIfUnavailable();
            _manager.ClearGoal();
            _activeGoalIndex = -1;
            _goalTimer = 0f;
            _goalChangeCount++;
        }

        public void SetAutomaticGoalChanges(bool enabled)
        {
            ThrowIfUnavailable();
            _automaticGoalChanges = enabled;
            _goalTimer = 0f;
        }

        private void ChooseRandomGoal()
        {
            if (_isFaulted)
                throw new InvalidOperationException("FlowFieldSampleController is faulted; call Release before use.", _fault);
            if (!_isInitialized)
                throw new InvalidOperationException("FlowFieldSampleController is not initialized.");

            int nextIndex = _random.Next(_goalPositions.Length);
            if (_goalPositions.Length > 1)
            {
                while (nextIndex == _activeGoalIndex)
                    nextIndex = _random.Next(_goalPositions.Length);
            }

            _activeGoalIndex = nextIndex;
            Vector3 goalPosition = _goalPositions[nextIndex];
            _goalMarker.position = goalPosition;

            _manager.SetGoalPosition(goalPosition, _goalInfluenceRadius);
            _goalChangeCount++;
        }

        private void UpdateOverlapDiagnostics()
        {
            _deepOverlapPairs = 0;
            _overlapBuckets.Clear();

            float bucketSize = _deepOverlapDistance;
            for (int i = 0; i < _agents.Count; i++)
            {
                FlowFieldSampleAgent agent = _agents[i];
                if (agent == null)
                    throw new InvalidOperationException($"FlowField agent {i} is missing.");

                Vector3 position = agent.Position;
                Vector2Int bucket = new Vector2Int(
                    Mathf.FloorToInt(position.x / bucketSize),
                    Mathf.FloorToInt(position.z / bucketSize));
                if (!_overlapBuckets.TryGetValue(bucket, out List<int> indices))
                {
                    indices = new List<int>();
                    _overlapBuckets.Add(bucket, indices);
                }

                indices.Add(i);
            }

            float thresholdSqr = _deepOverlapDistance * _deepOverlapDistance;
            foreach (KeyValuePair<Vector2Int, List<int>> pair in _overlapBuckets)
            {
                Vector2Int bucket = pair.Key;
                List<int> currentIndices = pair.Value;
                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        Vector2Int neighborKey = bucket + new Vector2Int(dx, dz);
                        if (!_overlapBuckets.TryGetValue(neighborKey, out List<int> neighborIndices))
                            continue;

                        for (int i = 0; i < currentIndices.Count; i++)
                        {
                            int firstIndex = currentIndices[i];
                            FlowFieldSampleAgent first = _agents[firstIndex];
                            if (first == null)
                                throw new InvalidOperationException($"FlowField agent {firstIndex} is missing.");

                            for (int j = 0; j < neighborIndices.Count; j++)
                            {
                                int secondIndex = neighborIndices[j];
                                if (secondIndex <= firstIndex)
                                    continue;

                                FlowFieldSampleAgent second = _agents[secondIndex];
                                if (second == null)
                                    throw new InvalidOperationException($"FlowField agent {secondIndex} is missing.");

                                Vector3 delta = first.Position - second.Position;
                                delta.y = 0f;
                                if (delta.sqrMagnitude < thresholdSqr)
                                    _deepOverlapPairs++;
                            }
                        }
                    }
                }
            }
        }

        private void OnGUI()
        {
            if (!_showOverlay)
                return;

            GUI.Box(new Rect(16f, 16f, 430f, 170f), "FlowField 1,000 Agent Sample");
            GUI.Label(new Rect(30f, 44f, 400f, 22f), $"Ready: {_manager != null && _manager.IsReady}");
            GUI.Label(new Rect(30f, 66f, 400f, 22f), $"Agents: {_agents.Count}/{_agentCount}");
            GUI.Label(new Rect(30f, 88f, 400f, 22f), $"Goal: {_activeGoalIndex} / changes: {_goalChangeCount}");
            int revision = _manager != null ? _manager.Revision : 0;
            GUI.Label(new Rect(30f, 110f, 400f, 22f), $"Revision: {revision}");
            GUI.Label(new Rect(30f, 132f, 400f, 22f), $"Deep overlaps: {_deepOverlapPairs}");
            GUI.Label(new Rect(30f, 154f, 400f, 22f), "Space: choose another random Goal");
        }

        private void ThrowIfUnavailable()
        {
            if (_isFaulted)
                throw new InvalidOperationException("FlowFieldSampleController is faulted; call Release before use.", _fault);
            if (!_isInitialized)
                throw new InvalidOperationException("FlowFieldSampleController is not initialized.");
        }

        private void OnValidate()
        {
            // Validation is reported at Init so serialized values remain untouched in the editor.
        }

        private void OnDestroy()
        {
            if (_isInitialized || _isFaulted)
                Release();
        }

        public void Release()
        {
            if (!_isInitialized && !_isFaulted)
                throw new InvalidOperationException("FlowFieldSampleController has not been initialized.");

            _simulationReady = false;
            ReleaseSpawnedAgents();
            _activeGoalIndex = -1;
            _goalChangeCount = 0;
            _deepOverlapPairs = 0;
            _random = null;
            _isInitialized = false;
            _isFaulted = false;
            _fault = null;
        }

        private void ReleaseSpawnedAgents()
        {
            for (int i = 0; i < _agents.Count; i++)
            {
                FlowFieldSampleAgent agent = _agents[i];
                if (agent == null)
                    continue;

                if (agent.IsInitialized || agent.IsFaulted)
                    agent.Release();
                if (agent.gameObject != null)
                    Destroy(agent.gameObject);
            }

            _agents.Clear();
            _overlapBuckets.Clear();
        }

        private static bool IsFinite(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool IsFinite(Vector3 value)
            => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    }
}
