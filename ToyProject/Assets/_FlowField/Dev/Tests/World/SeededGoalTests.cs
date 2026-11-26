using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using static Common.FlowField.Tests.FlowFieldEditSessions;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// The searches of world Goals, on sessions the test pumps itself. A Goal
    /// seeded only at its own cell builds the same field as an additional
    /// Goal there. Seeds with costs give every cell the fewest steps from any
    /// seed, as a multi-source search over the field's links counts them, and
    /// a cell whose cheapest way is its seed across the border steps there.
    /// </summary>
    [TestFixture, Category("World")]
    internal sealed class SeededGoalTests
    {
        private readonly FlowFieldTestLog _log = new FlowFieldTestLog();
        private readonly List<FlowFieldSession> _sessions = new List<FlowFieldSession>();

        [SetUp]
        public void StartLog() => _log.Start();

        [TearDown]
        public void CloseScene()
        {
            foreach (FlowFieldSession session in _sessions)
                session.Dispose();
            _sessions.Clear();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _log.StopAndAssertClean();
        }

        [TestCase(FlowFieldPlayScene.SampleScene)]
        [TestCase(FlowFieldPlayScene.VolumeScene)]
        public void GoalCellSeedBuildsTheAdditionalGoalsField(string scene)
        {
            FlowFieldSession session = Open(scene);
            FlowFieldField published = Published(session);
            int cell = FlowFieldObstacleProbe.PickOpenCell(published, published.Grid.CellCount / 3, fullSpeed: true);
            Vector3 position = published.Grid.CellCenter(cell);
            int plain = session.AddGoalCommand(FlowFieldGoalKind.Shared, FlowFieldGoalRequest.Position(position));
            int seeded = session.AddGoalCommand(FlowFieldGoalKind.Shared, FlowFieldGoalRequest.Position(position));
            published.Grid.Locate(cell, out int x, out int y, out int z);
            Assert.That(session.SetGoalSeedsCommand(seeded, Seeds(new[] { cell }, new[] { 0 }, new[] { FlowFieldGoalSeeds.NoDirection }, cell, x, y, z)), Is.True);
            Settle(session, "with both Goals");

            FlowFieldField expected = session.GoalField(plain);
            FlowFieldField actual = session.GoalField(seeded);
            Assert.That(actual.Goal.Distances, Is.EqualTo(expected.Goal.Distances));
            Assert.That(Hash(actual), Is.EqualTo(Hash(expected)), "A Goal seeded at its cell must build the plain Goal's field.");
        }

        [TestCase(FlowFieldPlayScene.SampleScene, 3)]
        [TestCase(FlowFieldPlayScene.SampleScene, 4)]
        [TestCase(FlowFieldPlayScene.VolumeScene, 3)]
        [TestCase(FlowFieldPlayScene.VolumeScene, 4)]
        public void SeedsGiveTheFewestStepsFromAnySeed(string scene, int seed)
        {
            FlowFieldSession session = Open(scene);
            FlowFieldField published = Published(session);
            FlowFieldGridSpace grid = published.Grid;
            var random = new System.Random(seed);
            int count = 6;
            var cells = new int[count];
            var costs = new int[count];
            var directions = new int[count];
            int steps = grid.SpaceMode == FlowFieldSpaceMode.Volume3D ? FlowFieldNeighborUtility.VolumeCount : FlowFieldNeighborUtility.SurfaceCount;
            for (int i = 0; i < count; i++)
            {
                cells[i] = FlowFieldObstacleProbe.PickOpenCell(published, random.Next(grid.CellCount / 2), fullSpeed: true);
                costs[i] = random.Next(0, 30);
                // Half the seeds lead across a border, as from a touching area.
                directions[i] = i % 2 == 0 ? FlowFieldGoalSeeds.NoDirection : random.Next(steps);
            }
            int id = session.AddGoalCommand(FlowFieldGoalKind.Shared, FlowFieldGoalRequest.Position(grid.CellCenter(cells[0])));
            Assert.That(session.SetGoalSeedsCommand(id, Seeds(cells, costs, directions, -1, -5, 0, -5)), Is.True);
            Settle(session, "with the seeded Goal");

            FlowFieldField field = session.GoalField(id);
            int[] expected = MultiSourceSteps(field, cells, costs, steps);
            Assert.That(field.Goal.Distances, Is.EqualTo(expected), "Every cell takes the fewest steps from any seed.");
            for (int index = 0; index < grid.CellCount; index++)
            {
                int next = field.Goal.NextCells[index];
                if (!FlowFieldNextCell.IsPortal(next))
                    continue;
                int portal = FlowFieldNextCell.PortalDirection(next);
                bool matches = false;
                for (int i = 0; i < count; i++)
                    matches |= cells[i] == index && directions[i] == portal && costs[i] == field.Goal.Distances[index];
                Assert.That(matches, Is.True, "A cell steps across the border only along its cheapest seed.");
            }
            Assert.That(session.TrySampleGoal(id, grid.CellCenter(cells[1]), out _), Is.True);
        }

        // The steps a field's links need from the nearest seed, with its cost.
        private static int[] MultiSourceSteps(FlowFieldField field, int[] cells, int[] costs, int steps)
        {
            FlowFieldGridSpace grid = field.Grid;
            var distances = new int[grid.CellCount];
            for (int i = 0; i < distances.Length; i++)
                distances[i] = FlowFieldGoalLayer.Unreachable;
            var queue = new SortedSet<(int cost, int cell)>();
            for (int i = 0; i < cells.Length; i++)
            {
                bool walkable = !field.Agent.Blocked[cells[i]]
                    && (field.Scene == null || field.Scene.Surface.IsSurfaceValid(cells[i]));
                if (!walkable || costs[i] >= distances[cells[i]])
                    continue;
                distances[cells[i]] = costs[i];
                queue.Add((costs[i], cells[i]));
            }
            while (queue.Count > 0)
            {
                (int cost, int cell) = queue.Min;
                queue.Remove(queue.Min);
                if (cost != distances[cell])
                    continue;
                grid.Locate(cell, out int x, out int y, out int z);
                for (int direction = 0; direction < steps; direction++)
                {
                    if ((field.Goal.Topology[cell] & (1u << direction)) == 0u)
                        continue;
                    int dy = grid.SpaceMode == FlowFieldSpaceMode.Volume3D ? FlowFieldNeighborUtility.DeltaY[direction] : 0;
                    if (!grid.TryLocal(x + FlowFieldNeighborUtility.DeltaX[direction], y + dy, z + FlowFieldNeighborUtility.DeltaZ[direction], out int neighbor)
                        || cost + 1 >= distances[neighbor])
                        continue;
                    distances[neighbor] = cost + 1;
                    queue.Add((cost + 1, neighbor));
                }
            }
            return distances;
        }

        private static FlowFieldGoalSeeds Seeds(int[] cells, int[] costs, int[] directions, int goalCell, int x, int y, int z)
            => new FlowFieldGoalSeeds(cells, costs, directions, new float[cells.Length], goalCell, x, y, z, epoch: 1);

        private FlowFieldSession Open(string scene)
        {
            EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);
            FlowFieldManager manager = Object.FindObjectsByType<FlowFieldManager>(FindObjectsSortMode.None)[0];
            FlowFieldSession session = CreateSettled(manager, manager.CaptureConfig(FlowFieldBakeMode.RuntimeDynamic), null);
            _sessions.Add(session);
            return session;
        }
    }
}
