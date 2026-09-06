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

        protected override IFlowFieldModifierSnapshot CreateSnapshot()
            => new Snapshot(
                _maxAngleDegrees,
                _spatialFrequency,
                _seed);

        public void SetNoise(float maxAngleDegrees, float spatialFrequency, int seed)
        {
            ValidateNoiseSettings(maxAngleDegrees, spatialFrequency);
            if (Mathf.Approximately(_maxAngleDegrees, maxAngleDegrees)
                && Mathf.Approximately(_spatialFrequency, spatialFrequency)
                && _seed == seed)
                return;

            float previousAngle = _maxAngleDegrees;
            float previousFrequency = _spatialFrequency;
            int previousSeed = _seed;
            _maxAngleDegrees = maxAngleDegrees;
            _spatialFrequency = spatialFrequency;
            _seed = seed;
            try
            {
                NotifyValueChanged();
            }
            catch
            {
                _maxAngleDegrees = previousAngle;
                _spatialFrequency = previousFrequency;
                _seed = previousSeed;
                throw;
            }
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

        private sealed class Snapshot : IFlowFieldModifierSnapshot
        {
            private readonly float _maxAngleDegrees;
            private readonly float _spatialFrequency;
            private readonly int _seed;

            internal Snapshot(
                float maxAngleDegrees,
                float spatialFrequency,
                int seed)
            {
                _maxAngleDegrees = maxAngleDegrees;
                _spatialFrequency = spatialFrequency;
                _seed = seed;
            }

            public FlowFieldVectorState Modify(
                in FlowFieldVectorState current,
                in FlowFieldVectorModifierContext context)
                => new FlowFieldVectorState(
                    context.GridSpace.SpaceMode == FlowFieldSpaceMode.Volume3D
                        ? FlowFieldNoiseUtility.ApplyStaticRotation3D(
                            current.Direction,
                            context.CellCenter,
                            _maxAngleDegrees,
                            _spatialFrequency,
                            _seed)
                        : FlowFieldNoiseUtility.ApplyStaticRotation(
                            current.Direction,
                            context.CellCenter,
                            context.SurfaceNormal,
                            _maxAngleDegrees,
                            _spatialFrequency,
                            _seed),
                    current.SpeedMultiplier);
        }
    }
}
