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
    public sealed class QueuedPathManager : MonoBehaviour, IPathRuntimeTickable, IPathRuntimeFaultHandler
    {
        #region Constants

        private const float DEFAULT_SPACING = 1.5f;
        private const float DEFAULT_SLOWDOWN_START_DISTANCE = 3f;
        private const float DEFAULT_MIN_SPEED_MULTIPLIER = 0.1f;

        #endregion


        #region Inner Classes / Structs

        private sealed class QueueEntry
        {
            public IQueuedPathAgent Agent;
            public int RegistrationSequence;
            public float Progress;
            public PathQueueRegistration Registration;
            public PathQueueState? State;

            public void Set(
                IQueuedPathAgent agent,
                int registrationSequence,
                PathQueueRegistration registration)
            {
                Agent = agent;
                RegistrationSequence = registrationSequence;
                Progress = 0f;
                Registration = registration;
                State = null;
            }

            public void Clear()
            {
                Agent = null;
                RegistrationSequence = 0;
                Progress = 0f;
                Registration = default(PathQueueRegistration);
                State = null;
            }
        }

        #endregion


        #region Member Variables

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
        private readonly List<QueueEntry> _entryPool = new List<QueueEntry>(100);
        private readonly List<QueueEntry> _detachedEntries = new List<QueueEntry>(100);
        private readonly Dictionary<IQueuedPathAgent, int> _indices = new Dictionary<IQueuedPathAgent, int>(100);
        private readonly List<IPathProvider> _routeStructure = new List<IPathProvider>();

        private IPathProvider _routeProvider;
        private bool _isInitialized;
        private bool _configurationErrorReported;
        private int _observedRouteRevision = -1;
        private int _routeRevision;
        private int _registrationSequence;
        private ulong _registrationId;
        private bool _runtimeDriverRegistered;

        #endregion


        #region Properties

        public bool IsInitialized => _isInitialized;
        int IPathRuntimeTickable.RuntimeOrder => -180;
        ulong IPathRuntimeTickable.PlaybackId => 0UL;
        public IPathProvider RouteProvider => _routeProvider;
        public int RouteRevision => _routeRevision;
        public int AgentCount => _entries.Count;
        public float DefaultSpacing
        {
            get => _defaultSpacing;
            set
            {
                ValidateNonNegativeFinite(value, nameof(value));
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
                ValidateNonNegativeFinite(value, nameof(value));
                _slowdownStartDistance = value;
            }
        }
        public float MinSpeedMultiplier
        {
            get => _minSpeedMultiplier;
            set
            {
                if (!PathValueUtility.IsInRange(value, 0f, 1f))
                    throw new ArgumentOutOfRangeException(nameof(value));
                _minSpeedMultiplier = value;
            }
        }

        #endregion


        #region Unity Events

        public void Init()
        {
            if (_isInitialized)
                return;
            if (!TryValidateSettings(out string settingsError))
            {
                MarkConfigurationError(settingsError);
                return;
            }
            if (_slowdownCurve == null)
                _slowdownCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f);

            _isInitialized = true;
            _configurationErrorReported = false;
            if (_routeProviderObject != null)
            {
                IPathProvider provider = _routeProviderObject as IPathProvider;
                if (provider == null)
                {
                    MarkConfigurationError(
                        $"QueuedPathManager '{name}' route object does not implement IPathProvider.");
                    return;
                }
                if (provider.IsInitialized && provider.IsReady)
                    ConfigureRoute(provider);
            }
        }

        public void Release()
        {
            using var operation = PathExecutionBoundary.Enter();
            Exception firstException = null;
            try
            {
                StopAllAgents();
            }
            catch (Exception exception)
            {
                firstException = exception;
            }
            if (_routeProvider != null)
                _routeProvider.PathChanged -= HandleRouteChanged;
            _routeProvider = null;
            _routeStructure.Clear();
            _isInitialized = false;
            _observedRouteRevision = -1;
            _routeRevision = 0;
            if (firstException != null)
                throw firstException;
        }

        private void Awake()
        {
            if (Application.isPlaying)
            {
                Init();
                PathRuntimeDriverBehaviour.Instance.Register(this);
                _runtimeDriverRegistered = true;
            }
        }

        private void OnEnable()
        {
            if (!Application.isPlaying)
                return;
            if (!_isInitialized)
                Init();
            if (!_runtimeDriverRegistered)
            {
                PathRuntimeDriverBehaviour.Instance.Register(this);
                _runtimeDriverRegistered = true;
            }
        }

        private void OnDisable()
        {
            if (!Application.isPlaying)
                return;
            if (_runtimeDriverRegistered && PathRuntimeDriverBehaviour.HasInstance)
            {
                PathRuntimeDriverBehaviour.Instance.Unregister(this);
                _runtimeDriverRegistered = false;
            }
            Release();
        }

        void IPathRuntimeTickable.Tick(
            float deltaTime,
            float unscaledDeltaTime,
            int frameId)
        {
            Tick(frameId);
        }

        private void Tick(int frameId)
        {
            if (!_isInitialized || _routeProvider == null)
                return;
            using var operation = PathExecutionBoundary.Enter();
            if (!_routeProvider.IsReady)
            {
                StopAllAgents(EPathQueueDetachReason.ProviderInvalid);
                return;
            }

            if (_routeProvider.Revision != _observedRouteRevision)
                RefreshRouteRevision();

            for (int i = 0; i < _entries.Count; i++)
                _entries[i].Progress = Mathf.Clamp01(_entries[i].Agent.GlobalNormalizedTime);
            SortEntries();
            _indices.Clear();
            for (int i = 0; i < _entries.Count; i++)
                _indices[_entries[i].Agent] = i;

            float routeLength = Mathf.Max(_routeProvider.PathLength, 0.001f);
            QueueEntry ahead = null;
            for (int i = 0; i < _entries.Count; i++)
            {
                QueueEntry entry = _entries[i];
                // A registration whose playback already ended is no longer on
                // the route. It stays listed until its agent replaces or
                // releases it, but no other agent may wait behind it.
                if (entry.Agent.PlaybackId != entry.Registration.PlaybackId)
                    continue;
                float? distance = ahead == null
                    ? (float?)null
                    : Mathf.Max(0f, (ahead.Progress - entry.Progress) * routeLength);
                PathQueueAgentSettings settings = entry.Agent.QueueSettings;
                float spacing = PathQueueCoordinator.GetSpacing(settings, _defaultSpacing);
                bool revisionBlocked = entry.Agent.SnapshotRevision != _routeRevision;
                bool spacingBlocked = distance.HasValue && distance.Value <= spacing;
                float multiplier = PathQueueCoordinator.CalculateSpeedMultiplier(
                    _enableGradualSlowdown && settings.EnableGradualSlowdown,
                    distance,
                    spacing,
                    _slowdownStartDistance,
                    _minSpeedMultiplier,
                    _slowdownCurve);
                float maxProgress = ahead == null || !settings.EnableOvertakeProtection
                    ? 1f
                    : Mathf.Clamp01(ahead.Progress - spacing / routeLength);

                PathQueueState state = new PathQueueState(
                    ahead?.Agent,
                    distance,
                    revisionBlocked || spacingBlocked,
                    multiplier,
                    maxProgress,
                    _routeRevision,
                    entry.Registration.PlaybackId,
                    frameId);
                entry.State = state;
                entry.Agent.ApplyQueueState(entry.Registration, state);
                ahead = entry;
            }
        }

        void IPathRuntimeFaultHandler.HandleRuntimeFault(
            ulong playbackId,
            System.Exception exception)
        {
            StopAllAgents(EPathQueueDetachReason.ManagerReleased);
        }

        private void OnDestroy()
        {
            if (_runtimeDriverRegistered && PathRuntimeDriverBehaviour.HasInstance)
            {
                PathRuntimeDriverBehaviour.Instance.Unregister(this);
                _runtimeDriverRegistered = false;
            }
            Release();
        }

        #endregion


        #region Public Methods

        public void ConfigureRoute(IPathProvider provider)
        {
            if (provider == null)
                throw new ArgumentNullException(nameof(provider));
            if (!PathProviderUtility.TryValidateReady(provider, out string error))
                throw new InvalidOperationException(error);

            if (ReferenceEquals(_routeProvider, provider))
                return;

            using var operation = PathExecutionBoundary.Enter();
            Exception firstException = null;
            if (_routeProvider != null)
            {
                _routeProvider.PathChanged -= HandleRouteChanged;
                try
                {
                    StopAllAgents();
                }
                catch (Exception exception)
                {
                    firstException = exception;
                }
            }
            _routeProvider = provider;
            _routeProvider.PathChanged += HandleRouteChanged;
            _routeRevision = provider.Revision;
            _observedRouteRevision = provider.Revision;
            CaptureRouteStructure();
            if (firstException != null)
                throw firstException;
        }

        public IQueuedPathAgent GetAgent(int orderedIndex)
        {
            if (orderedIndex < 0 || orderedIndex >= _entries.Count)
                throw new ArgumentOutOfRangeException(nameof(orderedIndex));
            return _entries[orderedIndex].Agent;
        }

        public PathQueueRegistration Register(IQueuedPathAgent agent)
        {
            using var operation = PathExecutionBoundary.Enter();
            ValidateRegistration(agent);
            // A registration can arrive after the provider raised PathChanged but
            // before this manager's next driver frame. Consume that revision first
            // so the token and the next calculation carry the same route identity.
            if (_routeProvider.Revision != _observedRouteRevision)
                RefreshRouteRevision();
            if (_indices.TryGetValue(agent, out int existingIndex))
            {
                PathQueueRegistration existing =
                    _entries[existingIndex].Registration;
                if (existing.PlaybackId == agent.PlaybackId)
                    return existing;

                RemoveEntryAt(existingIndex);
                agent.OnQueueDetached(
                    existing,
                    EPathQueueDetachReason.Replaced);
            }

            PathQueueRegistration registration = new PathQueueRegistration(
                this,
                ++_registrationId,
                agent.PlaybackId,
                _routeRevision);
            QueueEntry entry = AcquireEntry(agent, _registrationSequence++, registration);
            _entries.Add(entry);
            _indices[agent] = _entries.Count - 1;
            return registration;
        }

        public bool Unregister(PathQueueRegistration registration)
        {
            return Unregister(registration, EPathQueueDetachReason.Explicit);
        }

        internal bool Unregister(
            PathQueueRegistration registration,
            EPathQueueDetachReason reason)
        {
            if (!registration.IsValid)
                return false;
            using var operation = PathExecutionBoundary.Enter();
            int index = FindRegistrationIndex(registration);
            if (index < 0)
                return false;
            QueueEntry removedEntry = _entries[index];
            IQueuedPathAgent removedAgent = removedEntry.Agent;
            PathQueueRegistration removedRegistration = removedEntry.Registration;
            RemoveEntryAt(index);
            removedAgent.OnQueueDetached(
                removedRegistration,
                reason);
            return true;
        }

        public bool Unregister(IQueuedPathAgent agent)
        {
            if (agent == null || !_indices.TryGetValue(agent, out int index))
                return false;
            return Unregister(_entries[index].Registration);
        }

        public bool TryGetState(PathQueueRegistration registration, out PathQueueState state)
        {
            return TryGetStateAt(FindRegistrationIndex(registration), out state);
        }

        public bool TryGetState(IQueuedPathAgent agent, out PathQueueState state)
        {
            return TryGetStateAt(
                agent != null && _indices.TryGetValue(agent, out int index) ? index : -1,
                out state);
        }

        #endregion


        #region Private Methods

        private void ValidateRegistration(IQueuedPathAgent agent)
        {
            if (!_isInitialized)
                throw new InvalidOperationException("QueuedPathManager is not initialized.");
            if (agent == null)
                throw new ArgumentNullException(nameof(agent));
            if (_routeProvider == null || !ReferenceEquals(agent.QueueProvider, _routeProvider))
                throw new InvalidOperationException("Queue agent and manager must use the same route provider instance.");
        }

        private bool TryGetStateAt(int index, out PathQueueState state)
        {
            PathQueueState? current = index >= 0 ? _entries[index].State : null;
            state = current.GetValueOrDefault();
            return current.HasValue;
        }

        private void HandleRouteChanged()
        {
            // The revision is consumed in the runtime driver so followers are constrained
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
            }
        }

        private bool IsSameRouteStructure()
        {
            if (!PathProviderUtility.TryGetRouteSegmentCount(
                    _routeProvider,
                    out int count,
                    out _))
                return false;
            if (count != _routeStructure.Count)
                return false;
            for (int i = 0; i < count; i++)
            {
                if (!PathProviderUtility.TryGetDescriptor(
                        _routeProvider,
                        i,
                    out PathSegmentDescriptor descriptor,
                    out _)
                    || !ReferenceEquals(
                        descriptor.Provider,
                        _routeStructure[i]))
                    return false;
            }
            return true;
        }

        private void CaptureRouteStructure()
        {
            _routeStructure.Clear();
            if (!PathProviderUtility.TryGetRouteSegmentCount(
                    _routeProvider,
                    out int count,
                    out string countError))
                throw new InvalidOperationException(countError);

            for (int i = 0; i < count; i++)
            {
                if (!PathProviderUtility.TryGetDescriptor(
                        _routeProvider,
                        i,
                        out PathSegmentDescriptor descriptor,
                        out string descriptorError))
                    throw new InvalidOperationException(descriptorError);
                _routeStructure.Add(descriptor.Provider);
            }
        }

        private void StopAllAgents(
            EPathQueueDetachReason reason = EPathQueueDetachReason.ManagerReleased)
        {
            _detachedEntries.Clear();
            _detachedEntries.AddRange(_entries);
            _entries.Clear();
            _indices.Clear();
            Exception firstException = null;
            for (int i = 0; i < _detachedEntries.Count; i++)
            {
                QueueEntry entry = _detachedEntries[i];
                try
                {
                    entry.Agent.OnQueueDetached(entry.Registration, reason);
                }
                catch (Exception exception)
                {
                    firstException ??= exception;
                }
                RecycleEntry(entry);
            }
            _detachedEntries.Clear();
            if (firstException != null)
                throw firstException;
        }

        private QueueEntry AcquireEntry(
            IQueuedPathAgent agent,
            int registrationSequence,
            PathQueueRegistration registration)
        {
            QueueEntry entry;
            int lastIndex = _entryPool.Count - 1;
            if (lastIndex >= 0)
            {
                entry = _entryPool[lastIndex];
                _entryPool.RemoveAt(lastIndex);
            }
            else
                entry = new QueueEntry();

            entry.Set(agent, registrationSequence, registration);
            return entry;
        }

        // Leaders first. List.Sort allocates on every call here, and the order
        // barely changes between frames, which insertion sort handles in one pass.
        private void SortEntries()
        {
            for (int i = 1; i < _entries.Count; i++)
            {
                QueueEntry entry = _entries[i];
                int j = i - 1;
                while (j >= 0 && IsAhead(entry, _entries[j]))
                {
                    _entries[j + 1] = _entries[j];
                    j--;
                }
                _entries[j + 1] = entry;
            }
        }

        private static bool IsAhead(QueueEntry left, QueueEntry right)
        {
            int progress = right.Progress.CompareTo(left.Progress);
            return progress != 0
                ? progress < 0
                : left.RegistrationSequence < right.RegistrationSequence;
        }

        private int FindRegistrationIndex(PathQueueRegistration registration)
        {
            if (!registration.BelongsTo(this))
                return -1;
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Registration.Matches(registration))
                    return i;
            }
            return -1;
        }

        private void RemoveEntryAt(int index)
        {
            QueueEntry removedEntry = _entries[index];
            IQueuedPathAgent agent = removedEntry.Agent;
            // GetAgent exposes queue order between driver frames as well. Keep the
            // remaining order stable when the head leaves.
            _entries.RemoveAt(index);
            _indices.Remove(agent);
            for (int i = index; i < _entries.Count; i++)
                _indices[_entries[i].Agent] = i;
            RecycleEntry(removedEntry);
        }

        private void RecycleEntry(QueueEntry entry)
        {
            if (entry == null)
                return;

            entry.Clear();
            _entryPool.Add(entry);
        }

        private bool TryValidateSettings(out string error)
        {
            if (!PathValueUtility.IsNonNegativeFinite(_defaultSpacing))
            {
                error = "Default spacing must be finite and non-negative.";
                return false;
            }
            if (!PathValueUtility.IsNonNegativeFinite(_slowdownStartDistance))
            {
                error = "Slowdown start distance must be finite and non-negative.";
                return false;
            }
            if (!PathValueUtility.IsInRange(_minSpeedMultiplier, 0f, 1f))
            {
                error = "Minimum speed multiplier must be within 0..1.";
                return false;
            }

            error = null;
            return true;
        }

        private static void ValidateNonNegativeFinite(float value, string parameterName)
        {
            if (!PathValueUtility.IsNonNegativeFinite(value))
                throw new ArgumentOutOfRangeException(parameterName);
        }

        private void MarkConfigurationError(string message)
        {
            _isInitialized = false;
            if (_configurationErrorReported)
                return;

            Debug.LogError(message, this);
            _configurationErrorReported = true;
        }

        #endregion
    }
}
