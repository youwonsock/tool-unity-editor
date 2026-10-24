using System;
using UnityEngine;

namespace Common.OptimizeTool.Samples
{
    internal sealed class BenchmarkRenderTargetScope : IDisposable
    {
        private readonly Camera _camera;
        private readonly RenderTexture _previous;
        private readonly bool _previousEnabled;
        private bool _disposed;
        public RenderTexture Target { get; }

        public BenchmarkRenderTargetScope(Camera camera)
        {
            _camera = camera; _previous = camera.targetTexture; _previousEnabled = camera.enabled;
            Target = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32) { name = "OptimizeTool GPU validation", antiAliasing = 4 };
            if (!Target.Create()) { UnityEngine.Object.Destroy(Target); throw new InvalidOperationException("Cannot create GPU validation target."); }
            camera.targetTexture = Target; camera.enabled = false;
        }

        public void Render() => _camera.Render();

        public void SavePng(string path)
        {
            RenderTexture previous = RenderTexture.active;
            var pixels = new Texture2D(Target.width, Target.height, TextureFormat.RGB24, false);
            try
            {
                RenderTexture.active = Target;
                pixels.ReadPixels(new Rect(0, 0, Target.width, Target.height), 0, 0, false);
                System.IO.File.WriteAllBytes(path, pixels.EncodeToPNG());
            }
            finally { RenderTexture.active = previous; UnityEngine.Object.Destroy(pixels); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_camera != null) { _camera.targetTexture = _previous; _camera.enabled = _previousEnabled; }
            Target.Release(); UnityEngine.Object.Destroy(Target);
        }
    }
}
