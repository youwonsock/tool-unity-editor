using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace Supercent.Common.FlowField
{
    [MovedFrom(true, "Supercent.XpHero.Actor.Enemy.FlowField", "Assembly-CSharp", "FlowFieldVectorModifierVolume")]
    [ExecuteAlways]
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
        [System.NonSerialized] private bool _missingManagerWarningIssued;

        public FlowFieldManager FlowFieldManager => _flowFieldManager;
        public Collider InfluenceCollider => _influenceCollider;
        public int Priority => _priority;
        public int Revision => _revision;

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

        protected virtual void OnEnable()
        {
            SanitizeSettings();
            CacheValidationSnapshot();
            RegisterWithConfiguredManager();
        }

        protected virtual void OnDisable()
        {
            UnregisterFromCurrentManager();
        }

        protected virtual void OnValidate()
        {
            SanitizeSettings();
            bool areaChanged = !_validationSnapshotInitialized
                || _lastValidatedCollider != _influenceCollider;
            bool priorityChanged = !_validationSnapshotInitialized
                || _lastValidatedPriority != _priority;
            bool valueChanged = !_validationSnapshotInitialized
                || _lastValidatedValueHash != ModifierValueHash;
            if (valueChanged)
                IncrementRevision();

            if (_registeredManager != _flowFieldManager)
            {
                UnregisterFromCurrentManager();
                if (isActiveAndEnabled)
                    RegisterWithConfiguredManager();
            }
            else if (isActiveAndEnabled && _flowFieldManager != null)
            {
                // 구성 오류가 수정된 경우 다시 등록을 시도합니다.
                _flowFieldManager.RegisterVectorModifier(this);
            }

            if (valueChanged || priorityChanged)
                _registeredManager?.MarkVectorModifierDirty(this);
            if (areaChanged)
                _registeredManager?.MarkVectorModifierAreaDirty(this);

            CacheValidationSnapshot();
        }

        public void SetFlowFieldManager(FlowFieldManager manager)
        {
            if (_flowFieldManager == manager && _registeredManager == manager)
                return;

            UnregisterFromCurrentManager();
            _flowFieldManager = manager;
            _missingManagerWarningIssued = false;
            if (isActiveAndEnabled)
                RegisterWithConfiguredManager();
        }

        public void SetInfluenceCollider(Collider influenceCollider)
        {
            if (_influenceCollider == influenceCollider)
                return;

            _influenceCollider = influenceCollider;
            if (isActiveAndEnabled && _flowFieldManager != null)
            {
                _registeredManager = _flowFieldManager;
                _registeredManager.RegisterVectorModifier(this);
            }
            MarkInfluenceAreaDirty();
        }

        public void SetPriority(int priority)
        {
            if (_priority == priority)
                return;

            _priority = priority;
            MarkModifierDirty();
        }

        public void MarkModifierDirty()
        {
            IncrementRevision();
            _registeredManager?.MarkVectorModifierDirty(this);
        }

        public void MarkInfluenceAreaDirty()
        {
            if (_registeredManager == null && isActiveAndEnabled && _flowFieldManager != null)
            {
                _registeredManager = _flowFieldManager;
                _registeredManager.RegisterVectorModifier(this);
            }
            _registeredManager?.MarkVectorModifierAreaDirty(this);
        }

        public abstract FlowFieldVectorState Modify(
            in FlowFieldVectorState current,
            in FlowFieldVectorModifierContext context);

        protected virtual void SanitizeSettings()
        {
        }

        private void RegisterWithConfiguredManager()
        {
            if (_flowFieldManager == null)
            {
                if (!_missingManagerWarningIssued)
                {
                    _missingManagerWarningIssued = true;
                    Debug.LogError(
                        $"[{nameof(FlowFieldVectorModifierVolume)}] FlowField Manager가 지정되지 않았습니다: {name}",
                        this);
                }
                return;
            }

            if (_registeredManager != null && _registeredManager != _flowFieldManager)
                _registeredManager.UnregisterVectorModifier(this);

            _registeredManager = _flowFieldManager;
            _registeredManager.RegisterVectorModifier(this);
        }

        private void UnregisterFromCurrentManager()
        {
            if (_registeredManager != null)
                _registeredManager.UnregisterVectorModifier(this);
            _registeredManager = null;
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
