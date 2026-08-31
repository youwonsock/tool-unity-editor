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

            _speedMultiplier = speedMultiplier;
            MarkModifierDirty();
        }

        public override FlowFieldVectorState Modify(
            in FlowFieldVectorState current,
            in FlowFieldVectorModifierContext context)
            => new FlowFieldVectorState(
                current.Direction,
                current.SpeedMultiplier * _speedMultiplier);

        protected override void ValidateModifierSettings()
        {
            if (float.IsNaN(_speedMultiplier) || float.IsInfinity(_speedMultiplier) || _speedMultiplier < 0f)
                throw new System.ArgumentOutOfRangeException(nameof(_speedMultiplier));
        }
    }
}
