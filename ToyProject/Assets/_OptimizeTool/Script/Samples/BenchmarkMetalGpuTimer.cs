using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace Common.OptimizeTool.Samples
{
    /// <summary>GPU command-buffer timestamps with explicit frame boundaries. No CPU stopwatch is presented as GPU time.</summary>
    internal sealed class BenchmarkMetalGpuTimer : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Result
        {
            public int frame, status;
            public double milliseconds, start, end;
        }
        private const string Library = "OptimizeToolGpuTimer";
        [DllImport(Library)] private static extern int OT_Start();
        [DllImport(Library)] private static extern void OT_Stop();
        [DllImport(Library)] private static extern void OT_Clear();
        [DllImport(Library)] private static extern int OT_Poll(out Result value);
        [DllImport(Library)] private static extern IntPtr OT_GetEvent();
        [DllImport(Library)] private static extern void OT_Stats(out int events, out int ends, out int frame);
        public string Diagnostic() { OT_Stats(out int events, out int ends, out int frame); return $"issued={_issuedFrames}, nativeEvents={events}, nativeEnds={ends}, nativeFrames={frame}"; }
        private readonly IntPtr _callback;
        private readonly CommandBuffer _commands;
        private bool _disposed;
        public int NextFrame => _issuedFrames + 1;
        private int _issuedFrames;

        public BenchmarkMetalGpuTimer()
        {
            if (Application.platform != RuntimePlatform.OSXPlayer || OT_Start() != 1) throw new NotSupportedException("The Metal GPU timestamp plugin is unavailable.");
            try
            {
                _callback = OT_GetEvent();
                _commands = new CommandBuffer { name = "OptimizeTool Metal GPU scope" };
                RenderPipelineManager.beginFrameRendering += Begin;
                RenderPipelineManager.endFrameRendering += End;
            }
            catch { OT_Stop(); _commands?.Dispose(); throw; }
        }
        private void Issue(ScriptableRenderContext context, int marker)
        {
            _commands.Clear(); _commands.IssuePluginEvent(_callback, marker); context.ExecuteCommandBuffer(_commands);
        }
        private void Begin(ScriptableRenderContext context, Camera[] cameras) { _issuedFrames++; Issue(context, 1); }
        private void End(ScriptableRenderContext context, Camera[] cameras) { Issue(context, 2); context.Submit(); }
        public void Clear() => OT_Clear();
        public bool TryRead(int minimumFrame, out Result value)
        {
            while (OT_Poll(out value) != 0)
            {
                if (value.frame < minimumFrame) continue;
                if (value.status != 0 || value.milliseconds <= 0 || value.start <= 0 || value.end <= value.start)
                    throw new InvalidOperationException($"Invalid Metal frame scope: frame={value.frame}, status={value.status}, GPU={value.milliseconds} ms. Begin/end must reference the same command buffer.");
                return true;
            }
            return false;
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            RenderPipelineManager.beginFrameRendering -= Begin;
            RenderPipelineManager.endFrameRendering -= End;
            OT_Stop(); _commands.Dispose();
        }
    }
}
