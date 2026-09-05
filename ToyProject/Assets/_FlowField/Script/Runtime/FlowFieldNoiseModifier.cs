using UnityEngine;

namespace Common.FlowField
{
    public sealed class FlowFieldNoiseModifier : FlowFieldVectorModifierVolume
    {
        private const int DEFAULT_PRIORITY = 200;
        [SerializeField] private float _maxAngleDegrees = 15f;
        [SerializeField] private float _spatialFrequency = 0.3f;
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
            ValidateNoiseSettings(maxAngleDegrees, spatialFrequency);
            if (Mathf.Approximately(_maxAngleDegrees, maxAngleDegrees)
                && Mathf.Approximately(_spatialFrequency, spatialFrequency)
                && _seed == seed)
                return;

            _maxAngleDegrees = maxAngleDegrees;
            _spatialFrequency = spatialFrequency;
            _seed = seed;
            MarkModifierDirty();
        }

        protected override void ValidateModifierSettings()
        {
            ValidateNoiseSettings(_maxAngleDegrees, _spatialFrequency);
        }

        private static void ValidateNoiseSettings(float maxAngleDegrees, float spatialFrequency)
        {
            if (float.IsNaN(maxAngleDegrees) || float.IsInfinity(maxAngleDegrees)
                || maxAngleDegrees < 0f || maxAngleDegrees > 180f)
                throw new System.ArgumentOutOfRangeException(nameof(maxAngleDegrees));
            if (float.IsNaN(spatialFrequency) || float.IsInfinity(spatialFrequency) || spatialFrequency < 0f)
                throw new System.ArgumentOutOfRangeException(nameof(spatialFrequency));
        }
    }
}
