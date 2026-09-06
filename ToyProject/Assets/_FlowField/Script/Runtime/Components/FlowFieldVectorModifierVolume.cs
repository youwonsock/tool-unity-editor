using System;
using UnityEngine;

namespace Common.FlowField
{
    [ExecuteAlways]
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
        [System.NonSerialized] private bool _isFaulted;
        [System.NonSerialized] private bool _isChanging;
        [System.NonSerialized] private Exception _fault;

        public FlowFieldManager FlowFieldManager => _flowFieldManager;
        public Collider InfluenceCollider => _influenceCollider;
        public int Priority => _priority;
        public int Revision => _revision;
        public bool IsInitialized => _isInitialized;
        public bool IsFaulted => _isFaulted;

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
            if (Application.isPlaying)
                Init();
        }

        protected virtual void OnEnable()
        {
            if (Application.isPlaying && _isInitialized && _registeredManager == null)
                RegisterWithConfiguredManager();
        }

        protected virtual void OnDisable()
        {
            UnregisterFromCurrentManager();
        }

        private void OnDestroy()
        {
            if (_isInitialized || _isFaulted)
                Release();
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

#if UNITY_EDITOR
            if (!Application.isPlaying
                && (valueChanged || priorityChanged || areaChanged)
                && _flowFieldManager != null)
            {
                // Editor Preview is event-invalidated just like the runtime
                // Session.  A modifier's own OnValidate runs after the
                // manager's callback, so notify the manager directly instead
                // of waiting for the next repaint to discover stale data.
                _flowFieldManager.InvalidateEditorPreview();
            }
#endif

            if (Application.isPlaying
                && (valueChanged || priorityChanged || areaChanged)
                && _registeredManager != null)
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

            if (_isChanging)
                throw new InvalidOperationException("Modifier configuration cannot be changed reentrantly.");

            FlowFieldManager previousManager = _flowFieldManager;
            FlowFieldManager previousRegistered = _registeredManager;
            if (Application.isPlaying && isActiveAndEnabled && manager.IsFaulted)
                throw new InvalidOperationException("Cannot assign a faulted FlowFieldManager.");

            _isChanging = true;
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
            finally
            {
                _isChanging = false;
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

            ValidateInfluenceCollider(influenceCollider);
            if (_isChanging)
                throw new InvalidOperationException("Modifier configuration cannot be changed reentrantly.");
            Collider previous = _influenceCollider;
            int previousRevision = _revision;
            _isChanging = true;
            try
            {
                _influenceCollider = influenceCollider;
                IncrementRevision();
                if (Application.isPlaying && isActiveAndEnabled && _registeredManager != null)
                    _registeredManager.NotifyModifierChanged(this, FlowFieldModifierChange.Area);
            }
            catch
            {
                _influenceCollider = previous;
                _revision = previousRevision;
                throw;
            }
            finally
            {
                _isChanging = false;
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

            if (_isChanging)
                throw new InvalidOperationException("Modifier configuration cannot be changed reentrantly.");
            int previous = _priority;
            int previousRevision = _revision;
            _isChanging = true;
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
            finally
            {
                _isChanging = false;
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

            if (_isChanging)
                throw new InvalidOperationException("Modifier configuration cannot be changed reentrantly.");
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
        /// Marks an influence-area change without exposing the old public
        /// dirty-method API to consumers.
        /// </summary>
        protected void NotifyInfluenceAreaChanged()
        {
            ThrowIfFaulted();
            if (Application.isPlaying && isActiveAndEnabled)
            {
                if (_registeredManager == null && !CanChangeConfigurationWhilePlaying())
                    throw new InvalidOperationException("Modifier is not registered with a FlowFieldManager.");
                _registeredManager?.NotifyModifierChanged(this, FlowFieldModifierChange.Area);
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
            ValidateInfluenceCollider(_influenceCollider);
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
            if (_isFaulted)
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
                _isFaulted = true;
                if (_fault == null)
                    _fault = exception;
                throw;
            }
        }

        public void Release()
        {
            if (!_isInitialized && !_isFaulted)
                return;

            UnregisterFromCurrentManager();
            _isInitialized = false;
            _isFaulted = false;
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
            if (!_flowFieldManager.IsInitialized
                || _flowFieldManager.State == FlowFieldRuntimeState.Suspended)
                return;
            if (_flowFieldManager.IsFaulted)
                throw new InvalidOperationException("Configured FlowFieldManager is faulted.",
                    new InvalidOperationException(_flowFieldManager.LastError));

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
            if (_registeredManager != null && _registeredManager.IsInitialized)
            {
                if (_registeredManager.IsFaulted)
                {
                    // A Faulted Manager rejects public input calls, but a
                    // component must still be able to disable/destroy itself
                    // without producing a second exception. Remove the
                    // registry entry directly; the failed field is already
                    // discarded by the Session.
                    try { _registeredManager.UnregisterVectorModifier(this); }
                    catch { }
                }
                else
                {
                    _registeredManager.UnregisterVectorModifier(this);
                }
            }
            _registeredManager = null;
        }

        private void ThrowIfFaulted()
        {
            if (_isFaulted)
                throw new InvalidOperationException("FlowFieldVectorModifierVolume is faulted. Call Release before use.", _fault);
        }

        private static void ValidateInfluenceCollider(Collider collider)
        {
            if (collider == null)
                throw new ArgumentNullException(nameof(collider));
            if (!collider.isTrigger)
                throw new ArgumentException(FlowFieldModifierMaskBuilder.TriggerRequiredMessage, nameof(collider));
            if (collider is MeshCollider meshCollider && !meshCollider.convex)
                throw new ArgumentException(FlowFieldModifierMaskBuilder.ConvexMeshRequiredMessage, nameof(collider));
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
