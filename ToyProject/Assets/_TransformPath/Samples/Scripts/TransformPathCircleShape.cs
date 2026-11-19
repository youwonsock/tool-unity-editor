using UnityEngine;

namespace Common.TransformPath.Samples
{
    /// <summary>
    /// A circle in the XZ plane around this Transform, built on the public
    /// <see cref="PathShapeProvider"/> API alone. The sample's shape lane plays it.
    /// </summary>
    public sealed class TransformPathCircleShape : PathShapeProvider
    {
        private const float MIN_RADIUS = 0.01f;

        [SerializeField, Min(MIN_RADIUS)] private float _radius = 3f;
        [Tooltip("Runs clockwise when seen from above.")]
        [SerializeField] private bool _clockwise = true;

        // What the last build kept; Evaluate reads only these.
        private Vector3 _center;
        private float _builtRadius;
        private bool _builtClockwise;

        public float Radius => _radius;

        protected override bool TryPrepare(out float length, out bool shapeChanged, out string error)
        {
            length = 0f;
            shapeChanged = false;
            if (float.IsNaN(_radius) || float.IsInfinity(_radius) || _radius < MIN_RADIUS)
            {
                error = $"Radius must be finite and at least {MIN_RADIUS}.";
                return false;
            }
            Vector3 center = transform.position;
            shapeChanged = center != _center || _radius != _builtRadius || _clockwise != _builtClockwise;
            _center = center;
            _builtRadius = _radius;
            _builtClockwise = _clockwise;
            length = 2f * Mathf.PI * _radius;
            error = null;
            return true;
        }

        protected override Vector3 Evaluate(float distance)
        {
            float angle = distance / _builtRadius;
            if (_builtClockwise)
                angle = -angle;
            return _center + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * _builtRadius;
        }
    }
}
