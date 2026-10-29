using NUnit.Framework;
using UnityEngine;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// Golden hashes of every public sample in two fixed scenes. Refactoring
    /// steps must keep these values; a step that intentionally changes field
    /// output records the new value and the reason in its commit.
    /// </summary>
    public sealed class FlowFieldGoldenFieldTests
    {
        // Recorded with the Managed solver. "PENDING" reports the current
        // value as Inconclusive until a baseline run fills it in.
        private const string SurfaceGolden = "576:db7be2db64899a59";
        private const string VolumeGolden = "2560:d91d8eddafd33b3d";

        private static FlowFieldScenarioFixture.SurfaceScenario CreateSurface()
        {
            var scenario = new FlowFieldScenarioFixture.SurfaceScenario(24, 24)
            {
                Goal = new Vector3(21.5f, 0f, 3.5f),
                GoalInfluenceRadius = 16f,
            };
            scenario.StaticWall(11f, 13f, 0f, 18f);
            scenario.StaticWall(4f, 8f, 14f, 16f);
            scenario.Build();
            scenario.DynamicObstacle(new Vector3(17.5f, scenario.HeightAt(17.5f) + 0.5f, 9.5f), new Vector3(3f, 2f, 1f));
            scenario.Modifier(new Vector3(6f, 1f, 6f), new Vector3(6f, 6f, 6f), 10, 30f, 0.5f);
            scenario.Modifier(new Vector3(8f, 1f, 8f), new Vector3(4f, 6f, 4f), 20, -45f, 1.5f);
            scenario.Build();
            return scenario;
        }

        private static FlowFieldScenarioFixture.VolumeScenario CreateVolume()
        {
            var scenario = new FlowFieldScenarioFixture.VolumeScenario(16, 10, 16)
            {
                Goal = new Vector3(14.5f, 1.5f, 14.5f),
                GoalInfluenceRadius = 14f,
            };
            scenario.LayerObstacle(new Vector3(8f, 4f, 6f), new Vector3(1f, 8f, 12f));
            scenario.LayerObstacle(new Vector3(4f, 6f, 12f), new Vector3(8f, 1f, 2f));
            scenario.RegisteredObstacle(new Vector3(12.5f, 2.5f, 10.5f), new Vector3(3f, 3f, 1f));
            scenario.Modifier(new Vector3(4f, 3f, 4f), new Vector3(6f, 6f, 6f), 10, 30f, 0.5f);
            scenario.Modifier(new Vector3(5f, 3f, 5f), new Vector3(4f, 4f, 4f), 20, -45f, 1.5f);
            scenario.Build();
            return scenario;
        }

        [Test]
        public void SurfaceFieldIsDeterministicAcrossSessions()
        {
            string first;
            using (var scenario = CreateSurface())
                first = scenario.Hash().ToString();
            using (var scenario = CreateSurface())
                Assert.That(scenario.Hash().ToString(), Is.EqualTo(first));
        }

        [Test]
        public void VolumeFieldIsDeterministicAcrossSessions()
        {
            string first;
            using (var scenario = CreateVolume())
                first = scenario.Hash().ToString();
            using (var scenario = CreateVolume())
                Assert.That(scenario.Hash().ToString(), Is.EqualTo(first));
        }

        [Test]
        public void SurfaceFieldMatchesGolden()
        {
            using var scenario = CreateSurface();
            AssertGolden("Surface", scenario.Hash(), SurfaceGolden);
        }

        [Test]
        public void VolumeFieldMatchesGolden()
        {
            using var scenario = CreateVolume();
            AssertGolden("Volume", scenario.Hash(), VolumeGolden);
        }

        private static void AssertGolden(string name, FlowFieldScenarioFixture.FieldHash hash, string expected)
        {
            string actual = hash.ToString();
            TestContext.Progress.WriteLine($"[FlowField golden] {name} = {actual} ({hash.Coverage})");
            // The scenes must reach blocked/unreachable and modifier cells, or
            // the hash would not guard those paths.
            Assert.That(hash.Stopped, Is.GreaterThan(0), hash.Coverage);
            Assert.That(hash.Modified, Is.GreaterThan(0), hash.Coverage);
            if (expected == "PENDING")
                Assert.Inconclusive($"{name} golden hash not recorded yet. Current value: {actual}");
            Assert.That(actual, Is.EqualTo(expected), $"{name} published field changed.");
        }
    }
}
