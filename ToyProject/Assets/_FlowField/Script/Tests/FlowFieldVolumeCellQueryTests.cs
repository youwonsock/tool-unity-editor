using NUnit.Framework;
using UnityEngine;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// The per-step candidate filter must block exactly the cells that a
    /// CheckBox / OverlapsTarget query on every cell would block.
    /// </summary>
    public sealed class FlowFieldVolumeCellQueryTests
    {
        private const int OtherLayer = FlowFieldRegressionFixture.Layer - 1;

        [TestCase(0f)]
        [TestCase(0.35f)]
        public void CandidateFilterMatchesPerCellQueries(float clearance)
        {
            using var scenario = new FlowFieldScenarioFixture.VolumeScenario(14, 9, 13)
            {
                Goal = new Vector3(12.5f, 1.5f, 11.5f),
                ObstacleClearance = clearance,
            };
            scenario.LayerObstacle(new Vector3(4.2f, 3.1f, 5.7f), new Vector3(1.3f, 6f, 2.2f))
                .transform.rotation = Quaternion.Euler(0f, 37f, 12f);
            scenario.LayerObstacle(new Vector3(9.5f, 2f, 3f), new Vector3(0.2f, 0.2f, 0.2f));
            AddSphere(scenario, new Vector3(7.3f, 5.6f, 8.1f), 1.7f, FlowFieldRegressionFixture.Layer);
            AddCapsule(scenario, new Vector3(2.5f, 4f, 10.2f), FlowFieldRegressionFixture.Layer);
            scenario.RegisteredObstacle(new Vector3(10.4f, 6.2f, 9.9f), new Vector3(2.1f, 0.6f, 1.4f))
                .gameObject.layer = OtherLayer;
            scenario.RegisteredObstacle(new Vector3(5.5f, 1.5f, 1.5f), new Vector3(1f, 1f, 1f));
            scenario.Build();

            Assert.That(scenario.Session.TryGetView(out FlowFieldGridSpace grid, out bool[] blocked, out _, out _), Is.True);
            Physics.SyncTransforms();
            // Mirrors FlowFieldVolumeSession.ObstacleHalfExtents.
            Vector3 half = Vector3.one * Mathf.Max(0.000001f, grid.CellSize * 0.5f + clearance - 0.0001f);
            Collider[] buffer = null;
            int blockedCount = 0;
            for (int index = 0; index < grid.CellCount; index++)
            {
                grid.FromFlatIndex(index, out int x, out int y, out int z);
                Vector3 center = grid.LocalToWorldCenter(x, y, z);
                bool expected = Physics.CheckBox(center, half, Quaternion.identity, 1 << FlowFieldRegressionFixture.Layer,
                    QueryTriggerInteraction.Ignore);
                foreach (Collider registered in scenario.RegisteredObstacles)
                {
                    int mask = 1 << registered.gameObject.layer;
                    expected |= Physics.CheckBox(center, half, Quaternion.identity, mask, QueryTriggerInteraction.Ignore)
                        && FlowFieldOverlapUtility.OverlapsTarget(center, half, mask, registered,
                            QueryTriggerInteraction.Ignore, ref buffer);
                }
                Assert.That(blocked[index], Is.EqualTo(expected), $"cell ({x}, {y}, {z})");
                if (expected)
                    blockedCount++;
            }
            Assert.That(blockedCount, Is.GreaterThan(0));
        }

        [Test]
        public void InteriorTopologyFastPathMatchesCanTraverse()
        {
            using var scenario = new FlowFieldScenarioFixture.VolumeScenario(9, 7, 8)
            {
                Goal = new Vector3(7.5f, 1.5f, 6.5f),
            };
            scenario.LayerObstacle(new Vector3(4f, 3f, 4f), new Vector3(1f, 5f, 3f));
            scenario.LayerObstacle(new Vector3(2.5f, 5.5f, 2.5f), new Vector3(1f, 1f, 1f));
            scenario.LayerObstacle(new Vector3(6.5f, 2.5f, 1.5f), new Vector3(1f, 1f, 1f));
            scenario.Build();
            Assert.That(scenario.Session.TryExport(out FlowFieldVolumeRequest request, out bool[] blocked,
                out uint[] topology, out _, out _, out _, out _, out _), Is.True);

            var build = new FlowFieldVolumeBuildContext(request, 0, 0, null) { Blocked = blocked };
            for (int index = 0; index < request.Grid.CellCount; index++)
            {
                if (blocked[index])
                    continue;
                request.Grid.FromFlatIndex(index, out int x, out int y, out int z);
                uint expected = 0u;
                for (int direction = 0; direction < FlowFieldNeighborUtility.VolumeCount; direction++)
                {
                    if (FlowFieldVolumeNavigation.CanTraverse(build, index, x, y, z, direction, out _))
                        expected |= 1u << direction;
                }
                Assert.That(topology[index], Is.EqualTo(expected), $"cell ({x}, {y}, {z})");
            }
        }

        private static void AddSphere(FlowFieldScenarioFixture.VolumeScenario scenario, Vector3 center, float radius, int layer)
        {
            BoxCollider anchor = scenario.LayerObstacle(center, Vector3.one * 0.01f);
            anchor.enabled = false;
            var sphere = anchor.gameObject.AddComponent<SphereCollider>();
            sphere.radius = radius;
            anchor.gameObject.layer = layer;
        }

        private static void AddCapsule(FlowFieldScenarioFixture.VolumeScenario scenario, Vector3 center, int layer)
        {
            BoxCollider anchor = scenario.LayerObstacle(center, Vector3.one * 0.01f);
            anchor.enabled = false;
            var capsule = anchor.gameObject.AddComponent<CapsuleCollider>();
            capsule.radius = 0.6f;
            capsule.height = 4f;
            capsule.direction = 0;
            anchor.transform.rotation = Quaternion.Euler(20f, 0f, 45f);
            anchor.gameObject.layer = layer;
        }
    }
}
