using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace Supercent.Common.FlowField
{
    [MovedFrom(true, "Supercent.XpHero.Actor.Enemy.FlowField", "Assembly-CSharp", "FlowFieldSpeedModifier")]
    public sealed class FlowFieldSpeedModifier : FlowFieldVectorModifierVolume
    {
        private const int DEFAULT_PRIORITY = 100;

        [SerializeField, Min(0f)] private float _speedMultiplier = 1.5f;

        public float SpeedMultiplier => _speedMultiplier;

        protected override int DefaultPriority => DEFAULT_PRIORITY;
        protected override int ModifierValueHash => _speedMultiplier.GetHashCode();

        public void SetSpeedMultiplier(float speedMultiplier)
        {
            float sanitized = Mathf.Max(0f, speedMultiplier);
            if (Mathf.Approximately(_speedMultiplier, sanitized))
                return;

            _speedMultiplier = sanitized;
            MarkModifierDirty();
        }

        public override FlowFieldVectorState Modify(
            in FlowFieldVectorState current,
            in FlowFieldVectorModifierContext context)
            => new FlowFieldVectorState(
                current.Direction,
                current.SpeedMultiplier * _speedMultiplier);

        protected override void SanitizeSettings()
        {
            _speedMultiplier = Mathf.Max(0f, _speedMultiplier);
        }
    }
}
