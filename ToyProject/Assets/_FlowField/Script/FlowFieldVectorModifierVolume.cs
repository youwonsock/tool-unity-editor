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

            if (Application.isPlaying
                && (valueChanged || priorityChanged || areaChanged)
                && _registeredManager != null)
            {
                _registeredManager.MarkConfigurationStale();
            }
        }

        public void SetFlowFieldManager(FlowFieldManager manager)
        {
            ThrowIfFaulted();
            if (manager == null)
                throw new ArgumentNullException(nameof(manager));
            if (_flowFieldManager == manager && _registeredManager == manager)
                return;

            if (Application.isPlaying && isActiveAndEnabled && !manager.IsInitialized)
                throw new InvalidOperationException("FlowFieldManager must be initialized before assigning an active modifier.");

            UnregisterFromCurrentManager();
            _flowFieldManager = manager;
            if (Application.isPlaying && isActiveAndEnabled)
                RegisterWithConfiguredManager();
        }

        public void SetInfluenceCollider(Collider influenceCollider)
        {
            ThrowIfFaulted();
            if (influenceCollider == null)
                throw new ArgumentNullException(nameof(influenceCollider));
            if (_influenceCollider == influenceCollider)
                return;

            if (Application.isPlaying && isActiveAndEnabled
                && (_registeredManager == null || !_registeredManager.IsInitialized))
                throw new InvalidOperationException("Modifier must be registered before changing its collider.");

            _influenceCollider = influenceCollider;
            if (Application.isPlaying && isActiveAndEnabled && _flowFieldManager != null)
            {
                _registeredManager.MarkVectorModifierAreaDirty(this);
            }
            MarkInfluenceAreaDirty();
        }

        public void SetPriority(int priority)
        {
            ThrowIfFaulted();
            if (_priority == priority)
                return;

            if (Application.isPlaying && isActiveAndEnabled
                && (_registeredManager == null || !_registeredManager.IsInitialized))
                throw new InvalidOperationException("Modifier must be registered before changing its priority.");

            _priority = priority;
            MarkModifierDirty();
        }

        public void MarkModifierDirty()
        {
            ThrowIfFaulted();
            if (Application.isPlaying && isActiveAndEnabled
                && (_registeredManager == null || !_registeredManager.IsInitialized))
                throw new InvalidOperationException("Modifier is not registered with a FlowFieldManager.");

            IncrementRevision();
            if (Application.isPlaying && isActiveAndEnabled)
            {
                _registeredManager.MarkVectorModifierDirty(this);
            }
        }

        public void MarkInfluenceAreaDirty()
        {
            ThrowIfFaulted();
            if (Application.isPlaying && isActiveAndEnabled)
            {
                if (_registeredManager == null)
                    throw new InvalidOperationException("Modifier is not registered with a FlowFieldManager.");
                _registeredManager.MarkVectorModifierAreaDirty(this);
            }
        }

        public abstract FlowFieldVectorState Modify(
            in FlowFieldVectorState current,
            in FlowFieldVectorModifierContext context);

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
                throw new InvalidOperationException("FlowFieldVectorModifierVolume has not been initialized.");

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

            if (_registeredManager == _flowFieldManager)
                throw new InvalidOperationException("FlowField Vector Modifier is already registered with the configured manager.");

            if (_registeredManager != null && _registeredManager != _flowFieldManager)
            {
                _registeredManager.UnregisterVectorModifier(this);
                _registeredManager = null;
            }

            _flowFieldManager.RegisterVectorModifier(this);
            _registeredManager = _flowFieldManager;
        }

        private void UnregisterFromCurrentManager()
        {
            if (_registeredManager != null && _registeredManager.IsInitialized)
                _registeredManager.UnregisterVectorModifier(this);
            _registeredManager = null;
        }

        private void ThrowIfFaulted()
        {
            if (_isFaulted)
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
    }
}
