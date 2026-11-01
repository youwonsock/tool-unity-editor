using UnityEngine;

namespace Common.TransformPath.PlayerProbe
{
    [DefaultExecutionOrder(-32000)]
    internal sealed class TransformPathProbeFrameStart : MonoBehaviour
    {
        private void Update() => TransformPathProbeFrameClock.BeginFrame();
    }
}
