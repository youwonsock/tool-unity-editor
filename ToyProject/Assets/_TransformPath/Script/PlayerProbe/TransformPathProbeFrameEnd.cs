using UnityEngine;

namespace Common.TransformPath.PlayerProbe
{
    [DefaultExecutionOrder(32000)]
    internal sealed class TransformPathProbeFrameEnd : MonoBehaviour
    {
        private void Update() => TransformPathProbeFrameClock.EndFrame();
    }
}
