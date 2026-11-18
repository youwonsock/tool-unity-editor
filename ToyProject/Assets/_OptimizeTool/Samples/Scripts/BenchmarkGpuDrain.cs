using System;
using UnityEngine;

namespace Common.OptimizeTool.Samples
{
    /// <summary>Diagnostic only: synchronous one-pixel readback after rendering bounds inter-frame GPU overlap.</summary>
    internal sealed class BenchmarkGpuDrain : IDisposable
    {
        private readonly Texture2D _pixel = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
        public int LastFrame { get; private set; } = -1;

        public void Drain(RenderTexture target = null)
        {
            RenderTexture previous = RenderTexture.active;
            try
            {
                RenderTexture.active = target;
                _pixel.ReadPixels(new Rect((target == null ? Screen.width : target.width) / 2, (target == null ? Screen.height : target.height) / 2, 1, 1), 0, 0, false);
                LastFrame = Time.frameCount;
            }
            finally { RenderTexture.active = previous; }
        }

        public void Dispose() => UnityEngine.Object.Destroy(_pixel);
    }
}
