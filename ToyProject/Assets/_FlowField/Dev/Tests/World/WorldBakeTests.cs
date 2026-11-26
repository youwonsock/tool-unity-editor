using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using static Common.FlowField.Tests.FlowFieldWorldTestScene;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// A StaticBaked area in a world: it keeps its baked field and needs no
    /// Physics, and searches a world Goal on its baked cells. Every cell takes
    /// the fewest steps its baked links allow, and a mover reads moving
    /// samples there.
    /// </summary>
    [TestFixture, Category("World")]
    internal sealed class WorldBakeTests : FlowFieldPlayTestBase
    {
        [UnityTest, Timeout(300000)]
        public IEnumerator SampleStaticBakedAreaSearchesWorldGoals() => StaticBakedArea(FlowFieldPlayScene.SampleScene);

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeStaticBakedAreaSearchesWorldGoals() => StaticBakedArea(FlowFieldPlayScene.VolumeScene);

        private IEnumerator StaticBakedArea(string scene)
        {
            yield return FlowFieldPlayScene.Enter(scene, FlowFieldBakeMode.StaticBaked);
            FlowFieldManager manager = Manager;
            Assert.That(manager.TryGetPublishedField(out FlowFieldField baked), Is.True);
            FlowFieldGridSpace grid = baked.Grid;
            FlowFieldWorld world = CreateWorld(manager.SpaceMode, grid.CellSize, grid.Origin);
            manager.Release();
            var serialized = new SerializedObject(manager);
            serialized.FindProperty(FlowFieldManager.PropertyNames.World).objectReferenceValue = world;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            manager.Init();
            yield return FlowFieldPlayScene.WaitUntilReady();
            Assert.That(world.Managers, Is.EqualTo(new[] { manager }));

            Assert.That(manager.TryGetPublishedField(out baked), Is.True);
            int goalCell = FlowFieldObstacleProbe.PickOpenCell(baked, grid.CellCount / 3, fullSpeed: true);
            FlowFieldGoalHandle goal = world.AddGoal(FlowFieldGoalKind.Shared, FlowFieldGoalRequest.Position(grid.CellCenter(goalCell)));
            float deadline = Time.realtimeSinceStartup + SettleTimeoutSeconds;
            while (!world.IsGoalReady(goal) || manager.State != FlowFieldRuntimeState.Ready)
            {
                if (Time.realtimeSinceStartup > deadline)
                    Assert.Fail("The StaticBaked area never built the world Goal: " + FlowFieldPlayScene.Describe());
                yield return null;
            }

            FlowFieldField field = manager.Session.GoalField(goal.Id);
            Assert.That(field.Agent, Is.SameAs(baked.Agent), "The world Goal reads the baked cells.");
            int[] expected = Steps(field, goalCell);
            Assert.That(field.Goal.Distances, Is.EqualTo(expected), "Every cell takes the fewest steps its baked links allow.");
            int moving = 0;
            for (int cell = 0; cell < grid.CellCount; cell += 7)
            {
                if (expected[cell] == FlowFieldGoalLayer.Unreachable || expected[cell] == 0)
                    continue;
                if (world.TrySample(grid.CellCenter(cell), goal, out FlowFieldSample sample) && sample.SpeedMultiplier > 0f)
                    moving++;
            }
            Assert.That(moving, Is.GreaterThan(0), "No reachable cell moves toward the world Goal.");
        }

        // The steps along the field's links from the Goal cell.
        private static int[] Steps(FlowFieldField field, int goal)
        {
            FlowFieldGridSpace grid = field.Grid;
            bool volume = grid.SpaceMode == FlowFieldSpaceMode.Volume3D;
            int steps = volume ? FlowFieldNeighborUtility.VolumeCount : FlowFieldNeighborUtility.SurfaceCount;
            var distances = new int[grid.CellCount];
            for (int i = 0; i < distances.Length; i++)
                distances[i] = FlowFieldGoalLayer.Unreachable;
            var queue = new Queue<int>();
            distances[goal] = 0;
            queue.Enqueue(goal);
            while (queue.Count > 0)
            {
                int cell = queue.Dequeue();
                grid.Locate(cell, out int x, out int y, out int z);
                for (int direction = 0; direction < steps; direction++)
                {
                    if ((field.Goal.Topology[cell] & (1u << direction)) == 0u)
                        continue;
                    int dy = volume ? FlowFieldNeighborUtility.DeltaY[direction] : 0;
                    if (!grid.TryLocal(x + FlowFieldNeighborUtility.DeltaX[direction], y + dy, z + FlowFieldNeighborUtility.DeltaZ[direction], out int next)
                        || field.Agent.Blocked[next] || distances[next] != FlowFieldGoalLayer.Unreachable)
                        continue;
                    distances[next] = distances[cell] + 1;
                    queue.Enqueue(next);
                }
            }
            return distances;
        }
    }
}
