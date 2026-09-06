using UnityEngine;

namespace Common.FlowField
{
    public sealed class FlowFieldSpeedModifier : FlowFieldVectorModifierVolume
    {
        private const int DEFAULT_PRIORITY = 100;

        [SerializeField] private float _speedMultiplier = 1.5f;

        public float SpeedMultiplier => _speedMultiplier;

        protected override int DefaultPriority => DEFAULT_PRIORITY;
        protected override int ModifierValueHash => _speedMultiplier.GetHashCode();

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
