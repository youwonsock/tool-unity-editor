using System;
using System.Diagnostics;
using UnityEngine.Profiling;

namespace Common.TransformPath.PlayerProbe
{
    /// <summary>
    /// The span between the first and last Update of a frame, which holds the
    /// TransformPath runtime driver and nothing else that takes time.
    /// </summary>
    internal static class TransformPathProbeFrameClock
    {
        private static long _startTicks;
        private static long _startUsed;
        private static bool _hasStart;

        // GC.GetAllocatedBytesForCurrentThread returns 0 in this Player, so
        // allocation is read as growth of the used heap. A collection inside the
        // span hides that frame's growth.
        internal static bool MonoUsedWorks { get; } = CheckMonoUsed();
        internal static double LastUpdateMilliseconds { get; private set; }
        internal static long LastAllocatedBytes { get; private set; }
        internal static int CompletedFrames { get; private set; }

        internal static void BeginFrame()
        {
            _startUsed = Profiler.GetMonoUsedSizeLong();
            _hasStart = true;
            _startTicks = Stopwatch.GetTimestamp();
        }

        internal static void EndFrame()
        {
            long end = Stopwatch.GetTimestamp();
            if (!_hasStart)
                return;
            LastUpdateMilliseconds = (end - _startTicks) * 1000d / Stopwatch.Frequency;
            LastAllocatedBytes = Math.Max(0L, Profiler.GetMonoUsedSizeLong() - _startUsed);
            CompletedFrames++;
            _hasStart = false;
        }

        private static bool CheckMonoUsed()
        {
            long before = Profiler.GetMonoUsedSizeLong();
            GC.KeepAlive(new byte[1024 * 1024]);
            return Profiler.GetMonoUsedSizeLong() > before;
        }
    }
}
