using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Common.OptimizeTool.Samples
{
    internal struct BenchmarkSample
    {
        public int round, order, mode, observedFrame;
        public ulong timestamp;
        public uint syncInterval;
        public double widthScale, heightScale;
        public long draws, batches, setPass, triangles, vertices;
        public double cpu, main, render, presentWait, gpu, unscaledDelta;
        public bool focused;
        public bool gpuDrainComplete;
        public BenchmarkMetalGpuTimer.Result metal;
    }

    internal static class BenchmarkCsvWriter
    {
        internal static string Serialize(IReadOnlyList<BenchmarkSample> samples)
        {
            var csv = new StringBuilder(samples.Count * 160);
            csv.AppendLine("round,order,mode,observed_frame,timing_timestamp,draw_calls,batches,setpass_calls,triangles,vertices,cpu_frame_ms,cpu_main_ms,cpu_render_ms,cpu_present_wait_ms,gpu_frame_ms,wall_delta_ms,focused,sync_interval,width_scale,height_scale,gpu_drain_complete,metal_frame_id,metal_gpu_ms,metal_gpu_start_seconds,metal_gpu_end_seconds,cpu_timing_valid,metal_timing_valid");
            foreach (BenchmarkSample s in samples)
                csv.AppendFormat(CultureInfo.InvariantCulture,
                    "{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10:R},{11:R},{12:R},{13:R},{14:R},{15:R},{16},{17},{18:R},{19:R},{20},{21},{22:R},{23:R},{24:R},{25},{26}\n",
                    s.round, s.order, s.mode, s.observedFrame, s.timestamp, s.draws, s.batches, s.setPass, s.triangles,
                    s.vertices, s.cpu, s.main, s.render, s.presentWait, s.gpu, s.unscaledDelta, s.focused ? 1 : 0, s.syncInterval, s.widthScale, s.heightScale, s.gpuDrainComplete ? 1 : 0, s.metal.frame, s.metal.milliseconds, s.metal.start, s.metal.end, s.cpu > 0 && s.timestamp > 0 ? 1 : 0, s.metal.milliseconds > 0 ? 1 : 0);
            return csv.ToString();
        }
    }
}
