using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// Small numeric predicates shared by the runtime path components.
    /// Movement and geometry policy belongs to the higher-level utilities.
    /// </summary>
    internal static class PathValueUtility
    {
        public static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        public static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        public static bool IsInRange(float value, float minimum, float maximum)
        {
            return IsFinite(value) && value >= minimum && value <= maximum;
        }

        public static bool IsNonNegativeFinite(float value)
        {
            return IsFinite(value) && value >= 0f;
        }
    }
}
