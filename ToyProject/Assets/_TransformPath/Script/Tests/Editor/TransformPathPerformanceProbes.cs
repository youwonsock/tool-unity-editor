using System;
using System.Diagnostics;
using NUnit.Framework;
using UnityEngine;

namespace Common.TransformPath.Tests
{
    /// <summary>Local diagnostics; no hardware-dependent timing assertions.</summary>
    [Category("Performance")]
    public sealed class TransformPathPerformanceProbes
    {
        [TestCase(100)]
        [TestCase(500)]
        [TestCase(1000)]
        public void DriverAndQueueSteadyState(int count)
        {
            var driver = new PathRuntimeDriver();
            var pathObject = new GameObject("Performance path");
            var managerObject = new GameObject("Performance queue");
            try
            {
                var path = pathObject.AddComponent<PathData>();
                path.ConfigureBuildSettings(new PathBuildSettings(EPathCurveType.Linear, 10));
                path.ConfigureControlPoints(new[] { Vector3.zero, Vector3.right * count * 2f });
                path.Init();
                var manager = managerObject.AddComponent<QueuedPathManager>();
                manager.Init(); manager.ConfigureRoute(path);
                for (int i = 0; i < count; i++)
                {
                    var agent = new ProbeAgent(path, (float)i / count);
                    driver.Register(agent);
                    manager.Register(agent);
                }
                Measure("Driver-" + count, 128, () => driver.Tick(0.016f));
                Measure("Queue-" + count, 128,
                    () => ((IPathRuntimeTickable)manager).Tick(0.016f, 0.016f, 0));
                Assert.That(driver.LastException, Is.Null);
                Assert.That(manager.AgentCount, Is.EqualTo(count));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(managerObject);
                UnityEngine.Object.DestroyImmediate(pathObject);
            }
        }

        [TestCase(4)]
        [TestCase(16)]
        [TestCase(64)]
        public void LinearGeometryRebuild(int count)
        {
            var points = new Vector3[count];
            for (int i = 0; i < count; i++) points[i] = new Vector3(i, Mathf.Sin(i), 0f);
            var buffer = new PathGeometryBuildBuffer();
            bool built = false;
            Measure("Linear500-points-" + count, 32, () => built = PathGeometryUtility.TryBuild(
                points, EPathCurveType.Linear, 500, buffer, out _, out _));
            Assert.That(built, Is.True);
        }

        [Test]
        public void TimeCurveInverseWithQueueCap()
        {
            AnimationCurve curve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
            PathProgressStep step = default(PathProgressStep);
            Measure("TimeCurveCappedStep", 4096, () => step = PathProgressCalculator.AdvanceTime(
                0.2f, 5f, curve, 0.016f, 0.5f, 0.6f));
            Assert.That(step.Progress, Is.GreaterThan(0f));
        }

        private static void Measure(string label, int iterations, Action action)
        {
            // Some Unity Mono runtimes expose this API but return zero even
            // for a known allocation. Do not report that as allocation-free.
            long controlBefore = GC.GetAllocatedBytesForCurrentThread();
            GC.KeepAlive(new byte[1024]);
            bool allocationCounterAvailable = GC.GetAllocatedBytesForCurrentThread() > controlBefore;
            for (int i = 0; i < 16; i++) action();
            var timings = new double[5];
            var allocations = new long[5];
            for (int sample = 0; sample < timings.Length; sample++)
            {
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                for (int i = 0; i < iterations; i++) action();
                long elapsed = Stopwatch.GetTimestamp() - start;
                allocations[sample] = (GC.GetAllocatedBytesForCurrentThread() - allocated) / iterations;
                timings[sample] = (double)elapsed / Stopwatch.Frequency * 1000000d / iterations;
            }
            Array.Sort(timings); Array.Sort(allocations);
            string allocatedBytes = allocationCounterAvailable ? allocations[2].ToString() : "unavailable";
            TestContext.WriteLine($"PERF {label} median_us={timings[2]:F3} allocated_bytes={allocatedBytes}");
        }

        private sealed class ProbeAgent : IQueuedPathAgent, IPathRuntimeTickable
        {
            public ProbeAgent(IPathProvider provider, float progress)
            { QueueProvider = provider; GlobalNormalizedTime = progress; }
            public int RuntimeOrder => -100;
            public ulong PlaybackId => 1UL;
            public IPathProvider QueueProvider { get; }
            public bool IsMoving => true;
            public float GlobalNormalizedTime { get; }
            public int SnapshotRevision => QueueProvider.Revision;
            public PathQueueAgentSettings QueueSettings => new PathQueueAgentSettings(1.5f, true, true, true);
            public void Tick(float deltaTime, float unscaledDeltaTime, int frameId) { }
            public void ApplyQueueState(PathQueueRegistration registration, PathQueueState state) { }
            public void OnQueueDetached(PathQueueRegistration registration, EPathQueueDetachReason reason) { }
        }
    }
}
