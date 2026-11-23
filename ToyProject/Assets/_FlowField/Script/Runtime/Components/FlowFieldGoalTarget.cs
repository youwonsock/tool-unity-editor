using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// A moving Goal. While enabled it sets the Manager's Goal to its position
    /// and, at most once a frame, sets it again when it moves, its Influence
    /// Radius changes, a Release dropped it (in one frame or two) or the
    /// Manager field changed; disabling it clears the Goal it set.
    /// FlowField never polls the scene, so a Goal that
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

        [NonSerialized] private FlowFieldManager _reportedTo;
        [NonSerialized] private int _reportedGoalLifetime;
        [NonSerialized] private Vector3 _reportedPosition;
        [NonSerialized] private float _reportedRadius;

        public FlowFieldManager Manager => _manager;
        public float InfluenceRadius => _influenceRadius;

        private void OnDisable() => ClearReportedGoal();

        private void LateUpdate()
        {
            // A new Manager takes the Goal from the old one.
            if (_reportedTo != null && _reportedTo != _manager)
                ClearReportedGoal();
            if (_manager == null || !CanTakeInput(_manager))
                return;
            Vector3 position = transform.position;
            if (_reportedTo == _manager
                && _reportedGoalLifetime == _manager.GoalLifetime
                && (position - _reportedPosition).sqrMagnitude <= 0.00000001f
                && _influenceRadius == _reportedRadius)
                return;
            // Recorded first: a handler that releases the Manager during
            // SetGoal advances its Goal lifetime, so the next frame sets the
            // Goal again.
            _reportedTo = _manager;
            _reportedGoalLifetime = _manager.GoalLifetime;
            _reportedPosition = position;
            _reportedRadius = _influenceRadius;
            _manager.SetGoal(FlowFieldGoalRequest.Position(position, _influenceRadius));
        }

        // Clears the Goal this target set, unless a Release already dropped
        // it. A faulted Manager takes it too, so its retry leaves it out.
        private void ClearReportedGoal()
        {
            FlowFieldManager reported = _reportedTo;
            _reportedTo = null;
            if (reported == null || reported.GoalLifetime != _reportedGoalLifetime)
                return;
            if (CanTakeInput(reported))
                reported.SetGoal(FlowFieldGoalRequest.None);
            else if (reported.IsFaulted)
                reported.ClearGoalWhileFaulted();
        }

        private static bool CanTakeInput(FlowFieldManager manager)
            => manager.IsInitialized
                && !manager.IsFaulted
                && manager.BakeMode != FlowFieldBakeMode.StaticBaked;
    }
}
