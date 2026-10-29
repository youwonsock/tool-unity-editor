using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Profiling;
using Debug = UnityEngine.Debug;
using Random = System.Random;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// Explicit measurements for the FlowField improvement plan. They never
    /// run with the regular suite; select the "Performance" category to run
    /// them. Each probe appends rows to Logs/FlowFieldPerformance/probes.csv.
    /// </summary>
    [Explicit("Performance probes are run on demand.")]
    [Category("Performance")]
    public sealed class FlowFieldPerformanceProbes
    {
        private const int VolumeSize = 100;
        private const int SurfaceSize = 316;
        private const int LayerObstacleCount = 40;

        private static string _runId;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            // Unity's batch runner selects Explicit fixtures when filtering by
            // assembly, so the environment variable is the actual opt-in.
            if (Environment.GetEnvironmentVariable("FLOWFIELD_PERF") != "1")
                Assert.Ignore("Set FLOWFIELD_PERF=1 to run the FlowField performance probes.");
            _runId = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        }

        [TestCase(0)]
        [TestCase(10)]
        [TestCase(50)]
        public void VolumeFullBuild(int registeredObstacles)
        {
            using var scenario = CreateVolume(registeredObstacles, modifiers: 0);
            Measure($"volume.build.registered{registeredObstacles}", scenario.Grid.CellCount, () => scenario.Build());
        }

        [Test]
        public void VolumeFullBuildWithinFrameBudget()
        {
            using var scenario = CreateVolume(registeredObstacles: 10, modifiers: 0);
            int pumps = 0;
            Measure("volume.build.registered10.budget2ms", scenario.Grid.CellCount,
                () => pumps = scenario.Build(budgetMilliseconds: 2d));
            Record("volume.build.registered10.budget2ms", "pumps", pumps);
        }

        [Test]
        public void VolumeRebuildAfterMovingOneRegisteredObstacle()
        {
            using var scenario = CreateVolume(registeredObstacles: 10, modifiers: 0);
            scenario.Build();
            int revision = scenario.Session.Revision;
            Collider moved = scenario.RegisteredObstacles[0];
            Measure("volume.rebuild.moveRegistered", scenario.Grid.CellCount, () =>
            {
                moved.transform.position += new Vector3(2f, 0f, 0f);
                scenario.Build();
            });
            Assert.That(scenario.Session.Revision, Is.GreaterThan(revision));
        }

        [Test]
        public void VolumeFullBuildWithModifiers()
        {
            using var scenario = CreateVolume(registeredObstacles: 0, modifiers: 3);
            Measure("volume.build.modifiers3", scenario.Grid.CellCount, () => scenario.Build());
        }

        [Test]
        public void SurfaceBuildAndRebuilds()
        {
            using var scenario = new FlowFieldScenarioFixture.SurfaceScenario(SurfaceSize, SurfaceSize)
            {
                Goal = new Vector3(SurfaceSize - 2.5f, 0f, 2.5f),
            };
            var random = new Random(1234);
            for (int i = 0; i < LayerObstacleCount; i++)
            {
                float x = random.Next(4, SurfaceSize - 8);
                float z = random.Next(4, SurfaceSize - 8);
                scenario.StaticWall(x, x + random.Next(1, 6), z, z + random.Next(1, 30));
            }
            int cells = scenario.Grid.CellCount;
            Measure("surface.build.initial", cells, scenario.Build);

            BoxCollider dynamic = scenario.DynamicObstacle(
                new Vector3(100.5f, scenario.HeightAt(100.5f) + 0.5f, 100.5f), new Vector3(4f, 2f, 4f));
            Measure("surface.rebuild.registerDynamic", cells, scenario.Build);

            FlowFieldScenarioFixture.RotateModifier modifier = scenario.Modifier(
                new Vector3(150f, 10f, 150f), new Vector3(80f, 80f, 80f), 10, 30f, 0.5f);
            scenario.Modifier(new Vector3(60f, 5f, 200f), new Vector3(40f, 40f, 40f), 20, -20f, 1.5f);
            scenario.Modifier(new Vector3(250f, 20f, 60f), new Vector3(60f, 60f, 60f), 30, 10f, 0.8f);
            Measure("surface.rebuild.registerModifiers3", cells, scenario.Build);

            int revision = scenario.Session.Revision;
            Measure("surface.rebuild.modifierValue", cells, () =>
            {
                modifier.Speed = 0.25f;
                modifier.Revision++;
                scenario.Session.NotifyModifierChangedCommand(modifier, FlowFieldModifierChange.Value);
                scenario.Build();
            });
            Assert.That(scenario.Session.Revision, Is.GreaterThan(revision));

            revision = scenario.Session.Revision;
            Measure("surface.rebuild.moveDynamic", cells, () =>
            {
                Bounds before = dynamic.bounds;
                dynamic.transform.position += new Vector3(6f, 0f, 0f);
                Physics.SyncTransforms();
                Bounds after = dynamic.bounds;
                before.Encapsulate(after);
                scenario.Session.NotifyObstacleRegionDirtyCommand(before);
                scenario.Build();
            });
            Assert.That(scenario.Session.Revision, Is.GreaterThan(revision));

            Measure("surface.rebuild.goal", cells, () =>
            {
                scenario.Goal = new Vector3(2.5f, 0f, SurfaceSize - 2.5f);
                scenario.Build();
            });
        }

        private static FlowFieldScenarioFixture.VolumeScenario CreateVolume(int registeredObstacles, int modifiers)
        {
            var scenario = new FlowFieldScenarioFixture.VolumeScenario(VolumeSize, VolumeSize, VolumeSize)
            {
                Goal = new Vector3(VolumeSize - 2.5f, 2.5f, VolumeSize - 2.5f),
            };
            var random = new Random(4321);
            for (int i = 0; i < LayerObstacleCount; i++)
                scenario.LayerObstacle(RandomPoint(random, 8, VolumeSize - 8), RandomSize(random, 2, 12));
            for (int i = 0; i < registeredObstacles; i++)
                scenario.RegisteredObstacle(RandomPoint(random, 8, VolumeSize - 8), RandomSize(random, 1, 4));
            for (int i = 0; i < modifiers; i++)
                scenario.Modifier(RandomPoint(random, 20, VolumeSize - 20), Vector3.one * 30f, 10 * (i + 1), 15f * (i + 1), 0.5f + i * 0.25f);
            return scenario;
        }

        private static Vector3 RandomPoint(Random random, int min, int max)
            => new Vector3(random.Next(min, max) + 0.5f, random.Next(min, max) + 0.5f, random.Next(min, max) + 0.5f);

        private static Vector3 RandomSize(Random random, int min, int max)
            => new Vector3(random.Next(min, max), random.Next(min, max), random.Next(min, max));

        private static void Measure(string scenario, int cells, Action action)
        {
            // Unity's Mono reports 0 from GC.GetAllocatedBytesForCurrentThread,
            // so record managed heap growth plus the collections that ran
            // during the action. Growth undercounts churn when collections > 0.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            long heapBefore = Profiler.GetMonoUsedSizeLong();
            int collectionsBefore = GC.CollectionCount(0);
            var stopwatch = Stopwatch.StartNew();
            action();
            stopwatch.Stop();
            long heapAfter = Profiler.GetMonoUsedSizeLong();
            Record(scenario, "cells", cells);
            Record(scenario, "elapsedMs", stopwatch.Elapsed.TotalMilliseconds);
            Record(scenario, "monoHeapGrowthMiB", (heapAfter - heapBefore) / (1024d * 1024d));
            Record(scenario, "gcCollections", GC.CollectionCount(0) - collectionsBefore);
        }

        private static void Record(string scenario, string metric, double value)
        {
            string line = string.Join(",",
                _runId,
                SystemInfo.processorType.Replace(',', ' '),
                Application.unityVersion,
                scenario,
                metric,
                value.ToString("0.###", CultureInfo.InvariantCulture));
            TestContext.Progress.WriteLine($"[FlowField perf] {scenario} {metric}={value:0.###}");
            try
            {
                string directory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "FlowFieldPerformance"));
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "probes.csv");
                if (!File.Exists(path))
                    File.AppendAllText(path, "run,cpu,unity,scenario,metric,value\n");
                File.AppendAllText(path, line + "\n");
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"FlowField performance probe could not write its CSV row: {exception.Message}");
            }
        }
    }
}
