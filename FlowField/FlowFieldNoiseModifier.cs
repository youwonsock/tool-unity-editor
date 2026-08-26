using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace Supercent.Common.FlowField
{
    [MovedFrom(true, "Supercent.XpHero.Actor.Enemy.FlowField", "Assembly-CSharp", "FlowFieldNoiseModifier")]
    public sealed class FlowFieldNoiseModifier : FlowFieldVectorModifierVolume
    {
        private const int DEFAULT_PRIORITY = 200;
        [SerializeField, Range(0f, 180f)] private float _maxAngleDegrees = 15f;
        [SerializeField, Min(0f)] private float _spatialFrequency = 0.3f;
        [SerializeField] private int _seed;

        public float MaxAngleDegrees => _maxAngleDegrees;
        public float SpatialFrequency => _spatialFrequency;
        public int Seed => _seed;

        protected override int DefaultPriority => DEFAULT_PRIORITY;
        protected override int ModifierValueHash
        {
            get
            {
                unchecked
                {
                    int hash = _maxAngleDegrees.GetHashCode();
                    hash = (hash * 397) ^ _spatialFrequency.GetHashCode();
                    return (hash * 397) ^ _seed;
                }
            }
        }

        public override FlowFieldVectorState Modify(
            in FlowFieldVectorState current,
            in FlowFieldVectorModifierContext context)
            => new FlowFieldVectorState(
                FlowFieldNoiseUtility.ApplyStaticRotation(
                    current.Direction,
                    context.CellCenter,
                    context.SurfaceNormal,
                    _maxAngleDegrees,
                    _spatialFrequency,
                    _seed),
                current.SpeedMultiplier);

        public void SetNoise(float maxAngleDegrees, float spatialFrequency, int seed)
        {
            float sanitizedAngle = Mathf.Clamp(maxAngleDegrees, 0f, 180f);
            float sanitizedFrequency = Mathf.Max(0f, spatialFrequency);
            if (Mathf.Approximately(_maxAngleDegrees, sanitizedAngle)
                && Mathf.Approximately(_spatialFrequency, sanitizedFrequency)
                && _seed == seed)
                return;

            _maxAngleDegrees = sanitizedAngle;
            _spatialFrequency = sanitizedFrequency;
            _seed = seed;
            MarkModifierDirty();
        }

        protected override void SanitizeSettings()
        {
            _maxAngleDegrees = Mathf.Clamp(_maxAngleDegrees, 0f, 180f);
            _spatialFrequency = Mathf.Max(0f, _spatialFrequency);
        }
    }
}
