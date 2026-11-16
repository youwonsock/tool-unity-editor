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
        [NonSerialized] private Bounds _reportedBounds;

        public FlowFieldManager Manager => _manager;
        public Collider Collider => _collider;

        private void Reset() => _collider = GetComponent<Collider>();

        private void OnDisable() => Unregister();

        private void OnDestroy() => Unregister();

        // Registers on the first frame the Manager can take it, so the
        // Manager's Init order and a later Release and Init need no care.
        private void LateUpdate()
        {
            if (_manager == null || _collider == null || !CanTakeInput(_manager))
                return;
            if (_registered != _manager)
            {
                Unregister();
                _manager.RegisterDynamicObstacle(_collider);
                _registered = _manager;
                _reportedBounds = _collider.bounds;
                return;
            }

            Bounds bounds = _collider.bounds;
            if (bounds == _reportedBounds)
                return;
            _reportedBounds = bounds;
            // False: a Release cleared the registration; register again next frame.
            if (!_manager.NotifyDynamicObstacleMoved(_collider))
                _registered = null;
        }

        private static bool CanTakeInput(FlowFieldManager manager)
            => manager.IsInitialized
                && !manager.IsFaulted
                && manager.BakeMode != FlowFieldBakeMode.StaticBaked;

        private void Unregister()
        {
            FlowFieldManager registered = _registered;
            _registered = null;
            if (registered != null && registered.IsInitialized && _collider is not null)
                registered.UnregisterDynamicObstacle(_collider);
        }
    }
}
