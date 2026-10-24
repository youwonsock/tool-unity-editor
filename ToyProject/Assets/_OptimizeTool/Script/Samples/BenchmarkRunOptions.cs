using System;
using System.Globalization;

namespace Common.OptimizeTool.Samples
{
    internal sealed class BenchmarkRunOptions
    {
        public bool Offscreen { get; }
        public bool DrainGpu { get; }
        public bool Screenshots { get; }
        public float WarmupSeconds { get; }
        public float PreconditionSeconds { get; }
        public int OrderOffset { get; }
        public int? FrameRate { get; }
        public int? CubeCount { get; }

        public BenchmarkRunOptions(string[] args, bool editor, bool batchMode)
        {
            Offscreen = Array.IndexOf(args, "--optimize-benchmark-offscreen") >= 0;
            DrainGpu = Array.IndexOf(args, "--optimize-benchmark-drain-gpu") >= 0;
            Screenshots = Array.IndexOf(args, "--optimize-benchmark-screenshots") >= 0;
            if ((!editor && Offscreen != batchMode) || (editor && Offscreen))
                throw new ArgumentException("Offscreen capture requires a graphics-enabled standalone -batchmode Player; do not use -nographics.");
            if (editor && DrainGpu) throw new ArgumentException("GPU drain diagnostics require a separate Player.");
            WarmupSeconds = Seconds(args, "--optimize-benchmark-warmup-seconds", OptimizeToolPerformanceBenchmark.MinimumWarmupSeconds, OptimizeToolPerformanceBenchmark.MinimumWarmupSeconds, 30f);
            PreconditionSeconds = Seconds(args, "--optimize-benchmark-precondition-seconds", 0f, 0f, 120f);
            OrderOffset = Integer(args, "--optimize-benchmark-order-offset", 0, OptimizeToolSampleModes.MeshCount * 2 - 1) ?? 0;
            FrameRate = Integer(args, "--optimize-benchmark-fps", 1, 1000);
            CubeCount = Integer(args, "--optimize-benchmark-cubes", 1, int.MaxValue);
        }

        private static int? Integer(string[] args, string flag, int minimum, int maximum)
        {
            int index = Array.IndexOf(args, flag);
            if (index < 0) return null;
            if (index + 1 == args.Length || !int.TryParse(args[index + 1], out int value) || value < minimum || value > maximum)
                throw new ArgumentException($"{flag} requires an integer between {minimum} and {maximum}.");
            return value;
        }

        private static float Seconds(string[] args, string flag, float fallback, float minimum, float maximum)
        {
            int index = Array.IndexOf(args, flag);
            if (index < 0) return fallback;
            if (index + 1 == args.Length || !float.TryParse(args[index + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                || float.IsNaN(value) || value < minimum || value > maximum)
                throw new ArgumentException($"{flag} requires seconds between {minimum} and {maximum}.");
            return value;
        }
    }
}
