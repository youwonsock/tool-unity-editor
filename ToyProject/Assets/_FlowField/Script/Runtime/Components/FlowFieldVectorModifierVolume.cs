using System;
using UnityEngine;

namespace Common.FlowField
{
    [DefaultExecutionOrder(-100)]
    public abstract class FlowFieldVectorModifierVolume : MonoBehaviour, IFlowFieldVectorModifier
    {
        [Header("Flow Field Modifier")]
        [SerializeField] private FlowFieldManager _flowFieldManager;
        [SerializeField] private Collider _influenceCollider;
        [SerializeField] private int _priority;

        [System.NonSerialized] private FlowFieldManager _registeredManager;
        [System.NonSerialized] private Collider _lastValidatedCollider;
        [System.NonSerialized] private int _lastValidatedPriority;
        [System.NonSerialized] private int _lastValidatedValueHash;
        [System.NonSerialized] private int _revision;
        [System.NonSerialized] private bool _validationSnapshotInitialized;
        [System.NonSerialized] private bool _isInitialized;
        [System.NonSerialized] private Exception _fault;
        // The Influence Collider bounds last reported to the Manager.
        [System.NonSerialized] private Bounds _reportedBounds;
        [System.NonSerialized] private bool _reportedMissingCollider;

        public FlowFieldManager FlowFieldManager => _flowFieldManager;
        public Collider InfluenceCollider => _influenceCollider;
        public int Priority => _priority;
        public int Revision => _revision;
        public FlowFieldModifierObservation Observe()
            => new FlowFieldModifierObservation(_influenceCollider, _priority, _revision);
        public bool IsInitialized => _isInitialized;
        public bool IsFaulted => _fault != null;

        protected abstract int DefaultPriority { get; }
        protected abstract int ModifierValueHash { get; }

        protected virtual void Reset()
        {
            _priority = DefaultPriority;
            _influenceCollider = GetComponent<Collider>();
            if (_influenceCollider == null)
                return;

            if (_influenceCollider is MeshCollider meshCollider)
                meshCollider.convex = true;
            _influenceCollider.isTrigger = true;
        }

        private void Awake()
        {
            Track(this);
            if (Application.isPlaying)
                Init();
        }

        protected virtual void OnEnable()
        {
            // Also tracked here: an object that outlives a domain reload is
            // enabled again without another Awake.
            Track(this);
            if (Application.isPlaying && _isInitialized && _registeredManager == null)
                RegisterWithConfiguredManager();
        }

        protected virtual void OnDisable()
        {
            UnregisterFromCurrentManager();
        }

        // FlowField never polls a Modifier. The volume itself reports, at most
        // once a frame, that its Influence Collider moved or was destroyed;
        // value, priority and collider edits report through their setters.
        private void LateUpdate()
        {
            if (_registeredManager == null)
                return;
            if (_influenceCollider == null)
            {
                if (_reportedMissingCollider)
                    return;
                _reportedMissingCollider = true;
                ReportAreaChanged();
                return;
            }
            _reportedMissingCollider = false;
            FlowFieldPhysicsSync.SyncOncePerFrame();
            Bounds bounds = _influenceCollider.bounds;
            if (bounds == _reportedBounds)
                return;
            _reportedBounds = bounds;
            ReportAreaChanged();
        }

        private void ReportAreaChanged()
        {
            // A Manager that stopped or faulted has nothing to rebuild.
            if (!_registeredManager.IsInitialized || _registeredManager.IsFaulted)
                return;
            _registeredManager.NotifyModifierChanged(this, FlowFieldModifierChange.Area);
        }

        private void RememberReportedArea()
        {
            _reportedMissingCollider = _influenceCollider == null;
            _reportedBounds = _reportedMissingCollider
                ? default
                : FlowFieldPhysicsSync.SyncedBounds(_influenceCollider);
        }

        private void OnDestroy()
        {
            Live.Remove(this);
            if (_isInitialized || IsFaulted)
                Release();
        }

        // Every volume that has woken and not been destroyed. Only these can be
        // registered or reattached: registration needs an enabled component.
        // The Manager walks this list instead of every loaded object.
        private static readonly System.Collections.Generic.List<FlowFieldVectorModifierVolume> Live
            = new System.Collections.Generic.List<FlowFieldVectorModifierVolume>(8);

        private static void Track(FlowFieldVectorModifierVolume volume)
        {
            if (!Live.Contains(volume))
                Live.Add(volume);
        }

        /// <summary>
        /// Copies the live volumes into the caller's buffer, dropping destroyed
        /// ones. A copy, because reattaching may enable or destroy volumes.
        /// </summary>
        internal static void CopyLive(System.Collections.Generic.List<FlowFieldVectorModifierVolume> buffer)
        {
            buffer.Clear();
            for (int i = Live.Count - 1; i >= 0; i--)
            {
                if (Live[i] == null)
                    Live.RemoveAt(i);
            }
            buffer.AddRange(Live);
        }

        protected virtual void OnValidate()
        {
            bool areaChanged = !_validationSnapshotInitialized
                || _lastValidatedCollider != _influenceCollider;
            bool priorityChanged = !_validationSnapshotInitialized
                || _lastValidatedPriority != _priority;
            bool valueChanged = !_validationSnapshotInitialized
                || _lastValidatedValueHash != ModifierValueHash;
            if (valueChanged || priorityChanged || areaChanged)
                IncrementRevision();

            CacheValidationSnapshot();

            if (!Application.isPlaying)
                return;
            // The Inspector assigned another Manager: move the registration
            // there, as SetFlowFieldManager does. A faulted one takes none.
            if (_registeredManager != _flowFieldManager)
            {
                UnregisterFromCurrentManager();
                if (_flowFieldManager != null && !_flowFieldManager.IsFaulted)
                    ReattachToConfiguredManager();
                return;
            }
            // A Manager that stopped or faulted has nothing to rebuild.
            if ((valueChanged || priorityChanged || areaChanged)
                && _registeredManager != null
                && _registeredManager.IsInitialized
                && !_registeredManager.IsFaulted)
            {
                // Modifier edits only affect the final composition. Keep the
                // committed base field usable while the Session coalesces the
                // area/value change; a modifier must never invalidate or
                // rerun Surface, obstacle or Goal/BFS preparation.
                if (areaChanged)
                    _registeredManager.NotifyModifierChanged(this, FlowFieldModifierChange.Area);
                if (valueChanged || priorityChanged)
                    _registeredManager.NotifyModifierChanged(
                        this,
                        (valueChanged ? FlowFieldModifierChange.Value : FlowFieldModifierChange.None)
                        | (priorityChanged ? FlowFieldModifierChange.Priority : FlowFieldModifierChange.None));
            }
        }

        public void SetFlowFieldManager(FlowFieldManager manager)
        {
            ThrowIfFaulted();
            if (manager == null)
                throw new ArgumentNullException(nameof(manager));
            if (_flowFieldManager == manager && _registeredManager == manager)
                return;


            FlowFieldManager previousManager = _flowFieldManager;
            FlowFieldManager previousRegistered = _registeredManager;
            if (Application.isPlaying && isActiveAndEnabled && manager.IsFaulted)
                throw new InvalidOperationException("Cannot assign a faulted FlowFieldManager.");

            bool registeredWithCandidate = false;
            try
            {
                // Register the candidate first. A priority/collider/lifecycle
                // error therefore leaves the existing manager registration and
                // serialized reference untouched.
                if (Application.isPlaying && isActiveAndEnabled && manager.IsInitialized)
                {
                    registeredWithCandidate = manager.RegisterVectorModifier(this);
                    if (!registeredWithCandidate)
                        throw new InvalidOperationException("Modifier is already registered with the target FlowFieldManager.");
                }

                if (previousRegistered != null && previousRegistered != manager)
                    previousRegistered.UnregisterVectorModifier(this);

                _flowFieldManager = manager;
                _registeredManager = registeredWithCandidate ? manager : null;
                if (_registeredManager != null)
                    RememberReportedArea();
            }
            catch
            {
                // The old registration is still the source of truth if its
                // removal failed.  Roll back a successful candidate
                // registration as well, without replacing the original
                // exception with a rollback failure.
                if (registeredWithCandidate && manager != previousRegistered)
                {
                    try
                    {
                        if (manager.IsInitialized)
                            manager.UnregisterVectorModifier(this);
                    }
                    catch
                    {
                    }
                }
                _flowFieldManager = previousManager;
                _registeredManager = previousRegistered;
                throw;
            }
        }

        public void SetInfluenceCollider(Collider influenceCollider)
        {
            ThrowIfFaulted();
            if (influenceCollider == null)
                throw new ArgumentNullException(nameof(influenceCollider));
            if (_influenceCollider == influenceCollider)
                return;

            if (Application.isPlaying && isActiveAndEnabled
                && !CanChangeConfigurationWhilePlaying())
                throw new InvalidOperationException("Modifier must be registered before changing its collider.");

            FlowFieldModifierRegistry.ValidateCollider(influenceCollider);
            Collider previous = _influenceCollider;
            int previousRevision = _revision;
            try
            {
                _influenceCollider = influenceCollider;
                IncrementRevision();
                if (Application.isPlaying && isActiveAndEnabled && _registeredManager != null)
                {
                    _registeredManager.NotifyModifierChanged(this, FlowFieldModifierChange.Area);
                    RememberReportedArea();
                }
            }
            catch
            {
                _influenceCollider = previous;
                _revision = previousRevision;
                throw;
            }
        }

        public void SetPriority(int priority)
        {
            ThrowIfFaulted();
            if (_priority == priority)
                return;

            if (Application.isPlaying && isActiveAndEnabled
                && !CanChangeConfigurationWhilePlaying())
                throw new InvalidOperationException("Modifier must be registered before changing its priority.");

            int previous = _priority;
            int previousRevision = _revision;
            try
            {
                _priority = priority;
                IncrementRevision();
                if (Application.isPlaying && isActiveAndEnabled && _registeredManager != null)
                    _registeredManager.NotifyModifierChanged(this, FlowFieldModifierChange.Priority);
            }
            catch
            {
                _priority = previous;
                _revision = previousRevision;
                throw;
            }
        }

        /// <summary>
        /// Marks a value-only modifier change.  This is intentionally
        /// protected: callers use the controller's
        /// <see cref="IFlowFieldController.NotifyModifierChanged"/> contract,
        /// while concrete modifiers can expose their own atomic setters.
        /// </summary>
        protected void NotifyValueChanged()
        {
            ThrowIfFaulted();
            if (Application.isPlaying && isActiveAndEnabled
                && !CanChangeConfigurationWhilePlaying())
                throw new InvalidOperationException("Modifier is not registered with a FlowFieldManager.");

            int previousRevision = _revision;
            IncrementRevision();
            if (Application.isPlaying && isActiveAndEnabled && _registeredManager != null)
            {
                try
                {
                    _registeredManager.NotifyModifierChanged(this, FlowFieldModifierChange.Value);
                }
                catch
                {
                    _revision = previousRevision;
                    throw;
                }
            }
        }

        /// <summary>
        /// Captures an immutable value operation for one build.  The common
        /// validation lives here so every concrete modifier follows the same
        /// null/collider/settings contract; derived classes only create their
        /// small value snapshot.
        /// </summary>
        public IFlowFieldModifierSnapshot CaptureSnapshot()
        {
            ThrowIfFaulted();
            FlowFieldModifierRegistry.ValidateCollider(_influenceCollider);
            ValidateModifierSettings();
            IFlowFieldModifierSnapshot snapshot = CreateSnapshot();
            if (snapshot == null)
                throw new InvalidOperationException(
                    $"FlowField modifier '{GetType().Name}' returned a null snapshot.");
            return snapshot;
        }

        protected abstract IFlowFieldModifierSnapshot CreateSnapshot();

        protected virtual void ValidateModifierSettings()
        {
        }

        public void Init()
        {
            if (_isInitialized)
                throw new InvalidOperationException("FlowFieldVectorModifierVolume is already initialized.");
            if (IsFaulted)
                throw new InvalidOperationException("FlowFieldVectorModifierVolume is faulted; call Release before Init.", _fault);

            try
            {
                ValidateModifierSettings();
                CacheValidationSnapshot();
                _isInitialized = true;
                if (Application.isPlaying && isActiveAndEnabled)
                    RegisterWithConfiguredManager();
            }
            catch (Exception exception)
            {
                _isInitialized = false;
                if (_fault == null)
                    _fault = exception;
                throw;
            }
        }

        public void Release()
        {
            if (!_isInitialized && !IsFaulted)
                return;

            UnregisterFromCurrentManager();
            _isInitialized = false;
            _fault = null;
        }

        private void RegisterWithConfiguredManager()
        {
            if (!_isInitialized)
                throw new InvalidOperationException("FlowFieldVectorModifierVolume is not initialized.");
            if (_flowFieldManager == null)
                throw new InvalidOperationException("FlowField Vector Modifier requires a serialized FlowFieldManager.");

            // Manager/Modifier execution order is not a configuration error.
            // Keep the component initialized and wait for Manager reattach.
            // A faulted Manager is not initialized either; its retry reattaches.
            if (!_flowFieldManager.IsInitialized
                || _flowFieldManager.State == FlowFieldRuntimeState.Suspended)
                return;

            if (_registeredManager == _flowFieldManager)
                throw new InvalidOperationException("FlowField Vector Modifier is already registered with the configured manager.");

            if (_registeredManager != null && _registeredManager != _flowFieldManager)
            {
                _registeredManager.UnregisterVectorModifier(this);
                _registeredManager = null;
            }

            if (!_flowFieldManager.RegisterVectorModifier(this))
                throw new InvalidOperationException("FlowField Vector Modifier is already registered with the configured manager.");
            _registeredManager = _flowFieldManager;
            RememberReportedArea();
        }

        /// <summary>
        /// Release clears the Session registry, so an enabled modifier must
        /// forget the old registration as well.  The Manager calls this
        /// before releasing its Session; the next Init can then attach the
        /// already-enabled component again without requiring an OnEnable
        /// cycle.
        /// </summary>
        internal void DetachFromFlowFieldSession(FlowFieldManager manager)
        {
            if (_registeredManager == manager)
                _registeredManager = null;
        }

        /// <summary>
        /// Reattaches an initialized, enabled volume after a Manager
        /// Release/Init sequence.  This keeps the public component lifecycle
        /// independent from the Session's internal registry lifetime.
        /// </summary>
        internal void ReattachToConfiguredManager()
        {
            if (!_isInitialized
                || !isActiveAndEnabled
                || _flowFieldManager == null
                || _registeredManager != null)
                return;

            RegisterWithConfiguredManager();
        }

        private void UnregisterFromCurrentManager()
        {
            if (_registeredManager != null
                && (_registeredManager.IsInitialized || _registeredManager.IsFaulted))
                _registeredManager.UnregisterVectorModifier(this);
            _registeredManager = null;
        }

        private void ThrowIfFaulted()
        {
            if (IsFaulted)
                throw new InvalidOperationException("FlowFieldVectorModifierVolume is faulted. Call Release before use.", _fault);
        }

        private void IncrementRevision()
        {
            unchecked
            {
                _revision++;
            }
        }

        private void CacheValidationSnapshot()
        {
            _lastValidatedCollider = _influenceCollider;
            _lastValidatedPriority = _priority;
            _lastValidatedValueHash = ModifierValueHash;
            _validationSnapshotInitialized = true;
        }

        private bool CanChangeConfigurationWhilePlaying()
        {
            if (_registeredManager != null)
                return _registeredManager.IsInitialized && !_registeredManager.IsFaulted;

            // A modifier may be edited while its configured Manager is still
            // preparing, disabled, or suspended. The local value is then the
            // next registration snapshot; no live Session notification is
            // possible until the Manager reattaches it.
            if (_flowFieldManager == null)
                return true;
            if (_flowFieldManager.IsFaulted)
                return false;
            return !_flowFieldManager.IsInitialized
                || _flowFieldManager.State == FlowFieldRuntimeState.Suspended;
        }
    }
}
