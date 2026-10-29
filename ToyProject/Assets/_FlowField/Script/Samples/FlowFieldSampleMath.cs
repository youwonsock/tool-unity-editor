using UnityEngine;

namespace Common.FlowField.Samples
{
    /// <summary>
    /// Value checks shared by the sample components. Core keeps its own
    /// internal helpers; samples only use the public FlowField API.
    /// </summary>
    internal static class FlowFieldSampleMath
    {
        internal static bool IsFinite(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value);

        internal static bool IsFinite(Vector3 value)
            => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);

        internal static bool Approximately(Vector3 left, Vector3 right)
            => (left - right).sqrMagnitude <= 0.000001f;
    }
}
