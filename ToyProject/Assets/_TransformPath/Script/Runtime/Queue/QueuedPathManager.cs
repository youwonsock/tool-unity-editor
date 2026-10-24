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

        private sealed class QueueEntryComparer : IComparer<QueueEntry>
        {
            public static readonly QueueEntryComparer INSTANCE = new QueueEntryComparer();

            public int Compare(QueueEntry left, QueueEntry right)
            {
                int progress = right.Progress.CompareTo(left.Progress);
                return progress != 0
                    ? progress
                    : left.RegistrationSequence.CompareTo(right.RegistrationSequence);
            }
        }

        private struct QueueFrameEntry
        {
            public IQueuedPathAgent Agent;
            public PathQueueRegistration Registration;
            public float Progress;
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
        private readonly List<QueueFrameEntry> _frameEntries = new List<QueueFrameEntry>(100);
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
        private bool _isStopping;

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
            if (!_routeProvider.IsReady)
            {
                StopAllAgents(EPathQueueDetachReason.ProviderInvalid);
                return;
            }

            if (_routeProvider.Revision != _observedRouteRevision)
                RefreshRouteRevision();
            if (!_isInitialized || _routeProvider == null)
                return;

            for (int i = 0; i < _entries.Count; i++)
                _entries[i].Progress = Mathf.Clamp01(_entries[i].Agent.GlobalNormalizedTime);
            _entries.Sort(QueueEntryComparer.INSTANCE);
            _indices.Clear();
            for (int i = 0; i < _entries.Count; i++)
                _indices[_entries[i].Agent] = i;

            _frameEntries.Clear();
            for (int i = 0; i < _entries.Count; i++)
            {
                QueueEntry entry = _entries[i];
                _frameEntries.Add(new QueueFrameEntry
                {
                    Agent = entry.Agent,
                    Registration = entry.Registration,
                    Progress = entry.Progress,
                });
            }

            float routeLength = Mathf.Max(_routeProvider.PathLength, 0.001f);
            // Keep one frame's settings stable when an agent callback edits
            // the manager. The next tick consumes those changes.
            float defaultSpacing = _defaultSpacing;
            bool enableGradualSlowdown = _enableGradualSlowdown;
            float slowdownStartDistance = _slowdownStartDistance;
            float minSpeedMultiplier = _minSpeedMultiplier;
            AnimationCurve slowdownCurve = _slowdownCurve;
            for (int i = 0; i < _frameEntries.Count; i++)
            {
                QueueFrameEntry frameEntry = _frameEntries[i];
                if (!TryGetCurrentEntry(frameEntry, out _))
                    continue;
                IQueuedPathAgent agent = frameEntry.Agent;
                IQueuedPathAgent ahead = i == 0 ? null : _frameEntries[i - 1].Agent;
                float progress = frameEntry.Progress;
                float? distance = ahead == null
                    ? (float?)null
                    : Mathf.Max(0f, (_frameEntries[i - 1].Progress - progress) * routeLength);
                PathQueueAgentSettings settings = agent.QueueSettings;
                float spacing = PathQueueCoordinator.GetSpacing(settings, defaultSpacing);
                bool revisionBlocked = agent.SnapshotRevision != _routeRevision;
                bool spacingBlocked = distance.HasValue && distance.Value <= spacing;
                bool blocked = revisionBlocked || spacingBlocked;
                float multiplier = PathQueueCoordinator.CalculateSpeedMultiplier(
                    enableGradualSlowdown && settings.EnableGradualSlowdown,
                    distance,
                    spacing,
                    slowdownStartDistance,
                    minSpeedMultiplier,
                    slowdownCurve);
                float maxProgress = ahead == null
                    || !settings.EnableOvertakeProtection
                    ? 1f
                    : Mathf.Clamp01(_frameEntries[i - 1].Progress - spacing / routeLength);

                PathQueueState state = new PathQueueState(
                    ahead,
                    distance,
                    blocked,
                    multiplier,
                    maxProgress,
                    _routeRevision,
                    frameEntry.Registration.PlaybackId,
                    frameId);
                // An earlier agent callback may detach or replace this
                // registration. Contract getters can also invoke user code.
                if (!TryGetCurrentEntry(frameEntry, out QueueEntry current))
                    continue;
                current.State = state;
                agent.ApplyQueueState(frameEntry.Registration, state);
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

            Exception firstException = null;
            IPathProvider previousProvider = _routeProvider;
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
            // A detach callback can release or reconfigure the manager. Do not
            // overwrite that newer command with this pending route change.
            if (!ReferenceEquals(_routeProvider, previousProvider))
            {
                if (firstException != null)
                    throw firstException;
                return;
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
            ValidateRegistration(agent);
            // A registration created from a callback can arrive after the
            // provider raised PathChanged but before this manager's next
            // driver frame.  Consume that revision first so the token and
            // the next calculation carry the same route identity.
            if (_routeProvider.Revision != _observedRouteRevision)
            {
                RefreshRouteRevision();
                ValidateRegistration(agent);
            }
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
                ValidateRegistration(agent);
                if (_indices.TryGetValue(agent, out int reentrantIndex))
                    return _entries[reentrantIndex].Registration;
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
            int index = FindRegistrationIndex(registration);
            if (index < 0)
                return false;
            QueueEntry removedEntry = _entries[index];
            IQueuedPathAgent removedAgent = removedEntry.Agent;
            PathQueueRegistration removedRegistration = removedEntry.Registration;
            RemoveEntryAt(index);
            // Remove the entry before notifying the agent. A callback may
            // register a new execution, and that new token must not be
            // mistaken for the registration being detached.
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
            if (_isStopping)
                throw new InvalidOperationException("QueuedPathManager is releasing its registrations.");
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
            IPathProvider provider = _routeProvider;
            bool sameStructure = IsSameRouteStructure();
            _routeRevision = _routeProvider.Revision;
            _observedRouteRevision = _routeProvider.Revision;
            if (!sameStructure)
            {
                StopAllAgents();
                if (!ReferenceEquals(_routeProvider, provider))
                    return;
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
            // The outer batch already owns every removed registration. A nested
            // Release/ConfigureRoute must not clear its pending notifications.
            if (_isStopping)
                return;
            _detachedEntries.Clear();
            _detachedEntries.AddRange(_entries);
            _entries.Clear();
            _indices.Clear();
            Exception firstException = null;
            _isStopping = true;
            try
            {
                for (int i = 0; i < _detachedEntries.Count; i++)
                {
                    try
                    {
                        _detachedEntries[i].Agent.OnQueueDetached(
                            _detachedEntries[i].Registration,
                            reason);
                    }
                    catch (Exception exception)
                    {
                        firstException ??= exception;
                    }
                    RecycleEntry(_detachedEntries[i]);
                }
            }
            finally
            {
                _isStopping = false;
                _detachedEntries.Clear();
            }
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

        private bool TryGetCurrentEntry(QueueFrameEntry frameEntry, out QueueEntry entry)
        {
            if (_indices.TryGetValue(frameEntry.Agent, out int index))
            {
                QueueEntry current = _entries[index];
                if (current.Registration.Matches(frameEntry.Registration)
                    && frameEntry.Agent.PlaybackId == frameEntry.Registration.PlaybackId)
                {
                    entry = current;
                    return true;
                }
            }
            entry = null;
            return false;
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
            // remaining order stable when a sale transition removes the head.
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
