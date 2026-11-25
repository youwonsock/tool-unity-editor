using UnityEngine;

namespace Common.FlowField
{
    /// <summary>A Modifier that multiplies the speed of each cell inside its Influence Collider.</summary>
    public sealed class FlowFieldSpeedModifier : FlowFieldVectorModifierVolume
    {

        [SerializeField] private float _speedMultiplier = 1.5f;

        /// <summary>The factor each cell's speed is multiplied by; 0 or more.</summary>
        public float SpeedMultiplier => _speedMultiplier;

        protected override int DefaultPriority => 100;
        protected override int ModifierValueHash => _speedMultiplier.GetHashCode();

        /// <summary>
        /// Sets the factor and reports a value change; an equal value changes nothing. On failure
        /// the previous value stays.
        /// </summary>
        /// <exception cref="System.ArgumentOutOfRangeException">
        /// <paramref name="speedMultiplier"/> is not finite or is negative.
        /// </exception>
        /// <exception cref="System.InvalidOperationException">
        /// The Modifier is faulted, or in Play Mode while enabled its Manager is faulted or runs
        /// without it registered.
        /// </exception>
        public void SetSpeedMultiplier(float speedMultiplier)
        {
            if (float.IsNaN(speedMultiplier) || float.IsInfinity(speedMultiplier) || speedMultiplier < 0f)
                throw new System.ArgumentOutOfRangeException(nameof(speedMultiplier));
            if (Mathf.Approximately(_speedMultiplier, speedMultiplier))
                return;

            float previous = _speedMultiplier;
            _speedMultiplier = speedMultiplier;
            try
            {
                NotifyValueChanged();
            }
            catch
            {
                _speedMultiplier = previous;
                throw;
            }
        }

        protected override IFlowFieldModifierSnapshot CreateSnapshot()
            => new Snapshot(
                _speedMultiplier);

        protected override void ValidateModifierSettings()
        {
            if (float.IsNaN(_speedMultiplier) || float.IsInfinity(_speedMultiplier) || _speedMultiplier < 0f)
                throw new System.ArgumentOutOfRangeException(nameof(_speedMultiplier));
        }

        private sealed class Snapshot : IFlowFieldModifierSnapshot
        {
            private readonly float _speedMultiplier;

            internal Snapshot(float speedMultiplier)
            {
                _speedMultiplier = speedMultiplier;
            }

            public FlowFieldVectorState Modify(
                in FlowFieldVectorState current,
                in FlowFieldVectorModifierContext context)
                => new FlowFieldVectorState(
                    current.Direction,
                    current.SpeedMultiplier * _speedMultiplier);
        }
    }
}
