using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// Concrete queue coordinator. The entry list and index dictionary are the
    /// only source of truth; constraints are calculated once before followers
    /// tick for the frame.
    /// </summary>
    [DefaultExecutionOrder(-180)]
    public sealed class QueuedPathManager : MonoBehaviour
    {
        private const float DEFAULT_SPACING = 1.5f;
        private const float DEFAULT_SLOWDOWN_START_DISTANCE = 3f;
        private const float DEFAULT_MIN_SPEED_MULTIPLIER = 0.1f;

        [Header("Route")]
        [SerializeField] private MonoBehaviour _routeProviderObject;

        [Header("Spacing")]
        [SerializeField, Min(0f)] private float _defaultSpacing = DEFAULT_SPACING;

        [Header("Slowdown")]
        [SerializeField] private bool _enableGradualSlowdown = true;
        [SerializeField, Min(0f)] private float _slowdownStartDistance = DEFAULT_SLOWDOWN_START_DISTANCE;
        [SerializeField, Range(0f, 1f)] private float _minSpeedMultiplier = DEFAULT_MIN_SPEED_MULTIPLIER;
        [SerializeField] private AnimationCurve _slowdownCurve = null;

        private readonly List<QueueEntry> _entries = new List<QueueEntry>(100);
        private readonly Dictionary<IQueuedPathAgent, int> _indices = new Dictionary<IQueuedPathAgent, int>(100);
        private readonly Dictionary<IQueuedPathAgent, PathQueueState> _states = new Dictionary<IQueuedPathAgent, PathQueueState>(100);
        private readonly List<IPathProvider> _routeStructureProviders = new List<IPathProvider>();
        private readonly List<EPathMoveType> _routeStructureMoveTypes = new List<EPathMoveType>();
        private readonly List<float> _routeStructureValues = new List<float>();
        private readonly List<AnimationCurve> _routeStructureCurves = new List<AnimationCurve>();

        private IPathProvider _routeProvider;
        private bool _isInitialized;
        private bool _awaitingFollowerSnapshots;
        private int _observedRouteRevision = -1;
        private int _routeRevision;
        private int _registrationSequence;

        public bool IsInitialized => _isInitialized;
        public IPathProvider RouteProvider => _routeProvider;
        public int RouteRevision => _routeRevision;
        public int AgentCount => _entries.Count;
        public float DefaultSpacing
        {
            get => _defaultSpacing;
            set
            {
                ValidateNonNegative(value, nameof(value));
                _defaultSpacing = value;
            }
        }
        public bool EnableGradualSlowdown
        {
            get => _enableGradualSlowdown;
            set => _enableGradualSlowdown = value;
        }
        public float SlowdownStartDistance
        {
            get => _slowdownStartDistance;
            set
            {
                ValidateNonNegative(value, nameof(value));
                _slowdownStartDistance = value;
            }
        }
        public float MinSpeedMultiplier
        {
            get => _minSpeedMultiplier;
            set
            {
                if (!IsFinite(value) || value < 0f || value > 1f)
                    throw new ArgumentOutOfRangeException(nameof(value));
                _minSpeedMultiplier = value;
            }
        }

        private void Awake()
        {
            if (Application.isPlaying)
                Init();
        }

        private void Update()
        {
            if (!_isInitialized || _routeProvider == null)
                return;
            if (!_routeProvider.IsReady)
            {
                StopAllAgents();
                return;
            }

            if (_routeProvider.Revision != _observedRouteRevision)
                RefreshRouteRevision();

            for (int i = 0; i < _entries.Count; i++)
                _entries[i].Progress = Mathf.Clamp01(_entries[i].Agent.GlobalNormalizedTime);
            _entries.Sort(QueueEntryComparer.Instance);
            _indices.Clear();
            for (int i = 0; i < _entries.Count; i++)
                _indices[_entries[i].Agent] = i;

            float routeLength = Mathf.Max(_routeProvider.PathLength, 0.001f);
            for (int i = 0; i < _entries.Count; i++)
            {
                IQueuedPathAgent agent = _entries[i].Agent;
                IQueuedPathAgent ahead = i == 0 ? null : _entries[i - 1].Agent;
                float progress = _entries[i].Progress;
                float? distance = ahead == null
                    ? (float?)null
                    : Mathf.Max(0f, ( _entries[i - 1].Progress - progress) * routeLength);
                float spacing = GetSpacing(agent);
                bool revisionBlocked = _awaitingFollowerSnapshots && agent.SnapshotRevision != _routeRevision;
                bool spacingBlocked = distance.HasValue && distance.Value <= spacing;
                bool blocked = revisionBlocked || spacingBlocked;
                float multiplier = CalculateSpeedMultiplier(agent, distance, spacing);
                float maxProgress = ahead == null
                    ? 1f
                    : Mathf.Clamp01(_entries[i - 1].Progress - spacing / routeLength);

                PathQueueState state = new PathQueueState(
                    ahead,
                    distance,
                    blocked,
                    multiplier,
                    maxProgress,
                    _routeRevision);
                _states[agent] = state;
                agent.ApplyQueueState(state);
            }

            if (_awaitingFollowerSnapshots && AllSnapshotsCurrent())
                _awaitingFollowerSnapshots = false;
        }

        private void OnDestroy()
        {
            Release();
        }

        public void Init()
        {
            if (_isInitialized)
                return;
            ValidateSettings();
            if (_slowdownCurve == null)
                _slowdownCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f);

            _isInitialized = true;
            if (_routeProviderObject != null)
            {
                IPathProvider provider = _routeProviderObject as IPathProvider;
                if (provider == null)
                {
                    Debug.LogError($"QueuedPathManager '{name}' route object does not implement IPathProvider.", this);
                    return;
                }
                if (provider.IsInitialized && provider.IsReady)
                    ConfigureRoute(provider);
            }
        }

        public void Release()
        {
            StopAllAgents();
            if (_routeProvider != null)
                _routeProvider.PathChanged -= HandleRouteChanged;
            _routeProvider = null;
            _routeStructureProviders.Clear();
            _routeStructureMoveTypes.Clear();
            _routeStructureValues.Clear();
            _routeStructureCurves.Clear();
            _isInitialized = false;
            _observedRouteRevision = -1;
            _routeRevision = 0;
            _awaitingFollowerSnapshots = false;
        }

        public void ConfigureRoute(IPathProvider provider)
        {
            if (provider == null)
                throw new ArgumentNullException(nameof(provider));
            if (!provider.IsInitialized || !provider.IsReady)
                throw new InvalidOperationException("Queue route provider must be initialized and ready.");

            if (ReferenceEquals(_routeProvider, provider))
                return;

            if (_routeProvider != null)
            {
                _routeProvider.PathChanged -= HandleRouteChanged;
                StopAllAgents();
            }
            _routeProvider = provider;
            _routeProvider.PathChanged += HandleRouteChanged;
            _routeRevision = provider.Revision;
            _observedRouteRevision = provider.Revision;
            CaptureRouteStructure();
            _awaitingFollowerSnapshots = false;
        }

        public IQueuedPathAgent GetAgent(int orderedIndex)
        {
            if (orderedIndex < 0 || orderedIndex >= _entries.Count)
                throw new ArgumentOutOfRangeException(nameof(orderedIndex));
            return _entries[orderedIndex].Agent;
        }

        public bool Register(IQueuedPathAgent agent)
        {
            if (!_isInitialized)
                throw new InvalidOperationException("QueuedPathManager is not initialized.");
            if (agent == null)
                throw new ArgumentNullException(nameof(agent));
            if (_routeProvider == null || !ReferenceEquals(agent.QueueProvider, _routeProvider))
                throw new InvalidOperationException("Queue agent and manager must use the same route provider instance.");
            if (_indices.ContainsKey(agent))
                return false;

            _entries.Add(new QueueEntry(agent, _registrationSequence++));
            _indices[agent] = _entries.Count - 1;
            return true;
        }

        public bool Unregister(IQueuedPathAgent agent)
        {
            if (agent == null)
                return false;
            if (!_indices.TryGetValue(agent, out int index))
                return false;

            int last = _entries.Count - 1;
            if (index != last)
                _entries[index] = _entries[last];
            _entries.RemoveAt(last);
            _indices.Remove(agent);
            _states.Remove(agent);
            if (index != last)
                _indices[_entries[index].Agent] = index;
            return true;
        }

        public bool TryGetState(IQueuedPathAgent agent, out PathQueueState state)
        {
            if (agent != null && _states.TryGetValue(agent, out state))
                return true;
            state = default(PathQueueState);
            return false;
        }

        private void HandleRouteChanged()
        {
            // The revision is consumed in Update so followers are constrained
            // before they tick in the next frame.
            _observedRouteRevision = -1;
        }

        private void RefreshRouteRevision()
        {
            bool sameStructure = IsSameRouteStructure();
            _routeRevision = _routeProvider.Revision;
            _observedRouteRevision = _routeProvider.Revision;
            if (!sameStructure)
            {
                StopAllAgents();
                CaptureRouteStructure();
                _awaitingFollowerSnapshots = false;
            }
            else
            {
                CaptureRouteStructure();
                _awaitingFollowerSnapshots = true;
            }
        }

        private bool IsSameRouteStructure()
        {
            int count = GetRouteSegmentCount(_routeProvider);
            if (count != _routeStructureProviders.Count)
                return false;
            for (int i = 0; i < count; i++)
            {
                PathSegmentDescriptor descriptor = GetRouteSegment(_routeProvider, i);
                if (!ReferenceEquals(descriptor.Provider, _routeStructureProviders[i])
                    || descriptor.MoveType != _routeStructureMoveTypes[i]
                    || !Mathf.Approximately(descriptor.Value, _routeStructureValues[i])
                    || descriptor.TimeCurve != _routeStructureCurves[i])
                    return false;
            }
            return true;
        }

        private void CaptureRouteStructure()
        {
            _routeStructureProviders.Clear();
            _routeStructureMoveTypes.Clear();
            _routeStructureValues.Clear();
            _routeStructureCurves.Clear();
            int count = GetRouteSegmentCount(_routeProvider);
            for (int i = 0; i < count; i++)
            {
                PathSegmentDescriptor descriptor = GetRouteSegment(_routeProvider, i);
                _routeStructureProviders.Add(descriptor.Provider);
                _routeStructureMoveTypes.Add(descriptor.MoveType);
                _routeStructureValues.Add(descriptor.Value);
                _routeStructureCurves.Add(descriptor.TimeCurve);
            }
        }

        private bool AllSnapshotsCurrent()
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Agent.SnapshotRevision != _routeRevision)
                    return false;
            }
            return true;
        }

        private float CalculateSpeedMultiplier(IQueuedPathAgent agent, float? distance, float spacing)
        {
            QueuedPathFollower concrete = agent as QueuedPathFollower;
            if (!_enableGradualSlowdown || (concrete != null && !concrete.EnableGradualSlowdown) || !distance.HasValue)
                return 1f;
            if (distance.Value <= spacing)
                return 0f;
            if (distance.Value >= _slowdownStartDistance || _slowdownStartDistance <= spacing)
                return 1f;
            float t = Mathf.Clamp01((distance.Value - spacing) / (_slowdownStartDistance - spacing));
            float curveValue = _slowdownCurve == null ? t : Mathf.Clamp01(_slowdownCurve.Evaluate(t));
            return Mathf.Lerp(_minSpeedMultiplier, 1f, curveValue);
        }

        private float GetSpacing(IQueuedPathAgent agent)
        {
            QueuedPathFollower concrete = agent as QueuedPathFollower;
            return concrete == null || concrete.UseManagerSpacing ? _defaultSpacing : concrete.ActorSpacing;
        }

        private static int GetRouteSegmentCount(IPathProvider provider)
        {
            IPathSequenceProvider sequence = provider as IPathSequenceProvider;
            return sequence == null ? 1 : sequence.SegmentCount;
        }

        private static PathSegmentDescriptor GetRouteSegment(IPathProvider provider, int index)
        {
            IPathSequenceProvider sequence = provider as IPathSequenceProvider;
            if (sequence != null)
                return sequence.GetSegment(index);
            return new PathSegmentDescriptor(provider, EPathMoveType.SpeedBased, 1f, null);
        }

        private void StopAllAgents()
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                IQueuedPathAgent agent = _entries[i].Agent;
                agent.PathFollower?.StopMove();
                QueuedPathFollower concrete = agent as QueuedPathFollower;
                if (concrete != null)
                    concrete.MarkUnregisteredByManager();
            }
            _entries.Clear();
            _indices.Clear();
            _states.Clear();
        }

        private void ValidateSettings()
        {
            ValidateNonNegative(_defaultSpacing, nameof(_defaultSpacing));
            ValidateNonNegative(_slowdownStartDistance, nameof(_slowdownStartDistance));
            if (!IsFinite(_minSpeedMultiplier) || _minSpeedMultiplier < 0f || _minSpeedMultiplier > 1f)
                throw new ArgumentOutOfRangeException(nameof(_minSpeedMultiplier));
        }

        private static void ValidateNonNegative(float value, string parameterName)
        {
            if (!IsFinite(value) || value < 0f)
                throw new ArgumentOutOfRangeException(parameterName);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private sealed class QueueEntry
        {
            public readonly IQueuedPathAgent Agent;
            public readonly int RegistrationSequence;
            public float Progress;

            public QueueEntry(IQueuedPathAgent agent, int registrationSequence)
            {
                Agent = agent;
                RegistrationSequence = registrationSequence;
            }
        }

        private sealed class QueueEntryComparer : IComparer<QueueEntry>
        {
            public static readonly QueueEntryComparer Instance = new QueueEntryComparer();

            public int Compare(QueueEntry left, QueueEntry right)
            {
                int progress = right.Progress.CompareTo(left.Progress);
                return progress != 0 ? progress : left.RegistrationSequence.CompareTo(right.RegistrationSequence);
            }
        }
    }
}
