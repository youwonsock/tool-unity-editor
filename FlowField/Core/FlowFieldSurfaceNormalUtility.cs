using UnityEngine;

namespace Supercent.Common.FlowField
{
    // Compatibility alias; the implementation is shared by FlowFieldVectorUtility.
    internal static class FlowFieldSurfaceNormalUtility
    {
        internal static Vector3 Sanitize(Vector3 surfaceNormal)
            => FlowFieldVectorUtility.SanitizeSurfaceNormal(surfaceNormal);
    }
}
