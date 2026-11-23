using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// A moving Goal. While enabled it sets the Manager's Goal to its position
    /// and, at most once a frame, sets it again when it moves or its Influence
    /// Radius changes; disabling it
    /// clears the Goal it set. FlowField never polls the scene, so a Goal that
    /// moves without this component must call
    /// <see cref="IFlowFieldController.SetGoal"/>. StaticBaked Managers keep
    /// their baked Goal; it does nothing there.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FlowFieldGoalTarget : MonoBehaviour
    {
        [SerializeField] private FlowFieldManager _manager;
        [SerializeField, Min(0f), Tooltip("0 is a global hybrid Goal; a positive value is a ranged Goal within an XYZ sphere.")]
        private float _influenceRadius;

        [NonSerialized] private bool _reported;
        [NonSerialized] private Vector3 _reportedPosition;
        [NonSerialized] private float _reportedRadius;

        public FlowFieldManager Manager => _manager;
        public float InfluenceRadius => _influenceRadius;

        private void OnDisable()
        {
            if (_reported && CanTakeInput(_manager))
                _manager.SetGoal(FlowFieldGoalRequest.None);
            _reported = false;
        }

        private void LateUpdate()
        {
            if (_manager == null || !CanTakeInput(_manager))
            {
                // A Release dropped the Goal; set it again after the next Init.
                if (_manager == null || !_manager.IsInitialized)
                    _reported = false;
                return;
            }
            Vector3 position = transform.position;
            if (_reported
                && (position - _reportedPosition).sqrMagnitude <= 0.00000001f
                && _influenceRadius == _reportedRadius)
                return;
            _reported = true;
            _reportedPosition = position;
            _reportedRadius = _influenceRadius;
            _manager.SetGoal(FlowFieldGoalRequest.Position(position, _influenceRadius));
        }

        private static bool CanTakeInput(FlowFieldManager manager)
            => manager != null
                && manager.IsInitialized
                && !manager.IsFaulted
                && manager.BakeMode != FlowFieldBakeMode.StaticBaked;
    }
}
