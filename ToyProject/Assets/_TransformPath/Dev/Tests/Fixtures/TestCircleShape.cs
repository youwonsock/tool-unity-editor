using System;
using UnityEngine;

namespace Common.TransformPath.Tests
{
    /// <summary>
    /// A circle in the XZ plane around its Transform that a test can break. It
    /// lives in a runtime assembly because Unity does not add components from
    /// Editor assemblies, and that assembly sees only the public API.
    /// </summary>
    public sealed class TestCircleShape : PathShapeProvider
    {
        public float Radius = 2f;
        public bool BreakEvaluate;
        public bool BreakLength;

        private Vector3 _center;
        private float _radius;

        protected override bool TryPrepare(out float length, out bool shapeChanged, out string error)
        {
            Vector3 center = transform.position;
            shapeChanged = center != _center || !Mathf.Approximately(Radius, _radius);
            _center = center;
            _radius = Radius;
            length = BreakLength ? float.NaN : 2f * Mathf.PI * Radius;
            error = null;
            return true;
        }

        protected override Vector3 Evaluate(float distance)
        {
            if (BreakEvaluate)
                throw new InvalidOperationException("The test broke this shape.");
            float angle = distance / _radius;
            return _center + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * _radius;
        }
    }
}
