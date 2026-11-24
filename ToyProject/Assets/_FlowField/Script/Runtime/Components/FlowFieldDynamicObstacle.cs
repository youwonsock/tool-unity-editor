using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// A moving obstacle. While enabled it registers its Collider with the
    /// Manager and, at most once a frame, reports that the Collider moved.
    /// FlowField never polls the scene, so a registered obstacle without this
    /// component must call <see cref="IFlowFieldController.NotifyDynamicObstacleMoved"/>.
    /// StaticBaked Managers take no runtime obstacles; it does nothing there.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FlowFieldDynamicObstacle : MonoBehaviour
    {
        [SerializeField] private FlowFieldManager _manager;
        [SerializeField] private Collider _collider;

        [NonSerialized] private FlowFieldManager _registered;
        [NonSerialized] private Collider _registeredCollider;
        [NonSerialized] private Bounds _reportedBounds;

        public FlowFieldManager Manager => _manager;
        public Collider Collider => _collider;

        private void Reset() => _collider = GetComponent<Collider>();

        private void OnDisable() => Unregister();

        private void OnDestroy() => Unregister();

        // Registers on the first frame the Manager can take it, and again
        // whenever its registration is gone: the Manager's Init order and a
        // later Release and Init, in one frame or two, need no care. A new
        // Manager or Collider replaces the registration, and a destroyed
        // Collider ends it, so the cells it blocked are rebuilt.
        private void LateUpdate()
        {
            if (_registered != null
                && (_registered != _manager || _registeredCollider == null || _registeredCollider != _collider))
                Unregister();
            if (_manager == null || _collider == null || !CanTakeInput(_manager))
                return;
            if (_registered == null || !_manager.IsDynamicObstacleRegistered(_collider))
            {
                _manager.RegisterDynamicObstacle(_collider);
                _registered = _manager;
                _registeredCollider = _collider;
                // Registration synced the physics scene, so these are current.
                _reportedBounds = _collider.bounds;
                return;
            }

            FlowFieldPhysicsSync.SyncOncePerFrame();
            Bounds bounds = _collider.bounds;
            if (bounds == _reportedBounds)
                return;
            _reportedBounds = bounds;
            _manager.NotifyDynamicObstacleMoved(_collider);
        }

        private static bool CanTakeInput(FlowFieldManager manager)
            => manager.IsInitialized
                && !manager.IsFaulted
                && manager.BakeMode != FlowFieldBakeMode.StaticBaked;

        private void Unregister()
        {
            FlowFieldManager registered = _registered;
            Collider collider = _registeredCollider;
            _registered = null;
            _registeredCollider = null;
            // A faulted Manager takes the removal too, so its retry leaves it out.
            if (registered != null && (registered.IsInitialized || registered.IsFaulted) && collider is not null)
                registered.UnregisterDynamicObstacle(collider);
        }
    }
}
