using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using static Common.FlowField.Tests.FlowFieldWorldTestScene;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// World Goals over areas built in Play Mode. Two touching areas reach a
    /// Goal with the same steps as one Manager over both, in both space modes;
    /// a mover following the world's samples crosses the border and arrives;
    /// a change notified across the border stops the step into it at once; a
    /// Goal no area holds leads to the outer border nearest it; an area that
    /// leaves takes its field with it while the others keep theirs; and an
    /// area Manager refuses to move or remove a world Goal.
    /// </summary>
    [TestFixture, Category("World")]
    internal sealed class WorldGoalTests : FlowFieldPlayTestBase
    {

        [UnityTest, Timeout(300000)]
        public IEnumerator SurfaceAreasMatchOneManager() => AreasMatchOneManager(FlowFieldSpaceMode.Surface2D);

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeAreasMatchOneManager() => AreasMatchOneManager(FlowFieldSpaceMode.Volume3D);

        [UnityTest, Timeout(300000)]
        public IEnumerator SurfaceMoverCrossesTheBorder() => MoverCrossesTheBorder(FlowFieldSpaceMode.Surface2D);

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeMoverCrossesTheBorder() => MoverCrossesTheBorder(FlowFieldSpaceMode.Volume3D);

        // A mover in B follows the world's samples to the Goal in A; then a
        // registered obstacle in A beside the border stops the step into it
        // from B before A builds again.
        private IEnumerator MoverCrossesTheBorder(FlowFieldSpaceMode mode)
        {
            yield return EnterEmptyPlayMode();
            bool volume = mode == FlowFieldSpaceMode.Volume3D;
            FlowFieldWorld world = CreateWorld(mode);
            if (!volume)
                CreateGround();
            Vector3 size = volume ? new Vector3(8f, 6f, 8f) : new Vector3(10f, 4f, 10f);
            CreateWall(volume, size.x);
            FlowFieldManager a = CreateManager("A", mode, Vector3.zero, size, world);
            FlowFieldManager b = CreateManager("B", mode, new Vector3(size.x, 0f, 0f), size, world);
            yield return WaitUntilSettled(a, b);
            Vector3 goalPosition = volume ? new Vector3(1.5f, 2.5f, 6.5f) : new Vector3(1.5f, 0f, 8.5f);
            FlowFieldGoalHandle goal = world.AddGoal(FlowFieldGoalKind.Shared, FlowFieldGoalRequest.Position(goalPosition));
            yield return WaitUntilSettled(a, b, world, goal);

            Vector3 mover = volume ? new Vector3(size.x * 2f - 1.5f, 2.5f, 1.5f) : new Vector3(size.x * 2f - 1.5f, 0f, 1.5f);
            bool crossed = false;
            for (int step = 0; step < 4000 && Vector3.Distance(Flat(mover, volume), Flat(goalPosition, volume)) > 0.75f; step++)
            {
                Assert.That(world.TrySample(mover, goal, out FlowFieldSample sample), Is.True, "No sample at " + mover.ToString("F2"));
                Assert.That(sample.SpeedMultiplier, Is.GreaterThan(0f), "The mover stopped at " + mover.ToString("F2"));
                mover += sample.Direction * (CellSize * 0.1f);
                if (!volume)
                    mover.y = 0f;
                crossed |= mover.x < size.x;
            }
            Assert.That(crossed, Is.True, "The mover never crossed into A.");
            Assert.That(Vector3.Distance(Flat(mover, volume), Flat(goalPosition, volume)), Is.LessThanOrEqualTo(0.75f),
                "The mover did not reach the Goal: " + mover.ToString("F2"));

            // A cell of B that steps across into A, and an obstacle on the
            // cell it steps into: the step stops while A has not built again.
            FlowFieldField fieldB = b.Session.GoalField(goal.Id);
            int portal = -1;
            for (int cell = 0; cell < fieldB.Grid.CellCount && portal < 0; cell++)
            {
                if (FlowFieldNextCell.IsPortal(fieldB.Goal.NextCells[cell]))
                    portal = cell;
            }
            Assert.That(portal, Is.GreaterThanOrEqualTo(0), "B has no cell that steps into A.");
            Vector3 from = fieldB.Grid.CellCenter(portal);
            Assert.That(world.TrySample(from, goal, out FlowFieldSample open), Is.True);
            Assert.That(open.SpeedMultiplier, Is.GreaterThan(0f));
            int direction = FlowFieldNextCell.PortalDirection(fieldB.Goal.NextCells[portal]);
            Vector3 into = from + new Vector3(FlowFieldNeighborUtility.DeltaX[direction],
                volume ? FlowFieldNeighborUtility.DeltaY[direction] : 0f, FlowFieldNeighborUtility.DeltaZ[direction]) * CellSize;
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.layer = ObstacleLayer;
            cube.transform.position = volume ? into : new Vector3(into.x, 1f, into.z);
            cube.transform.localScale = volume ? Vector3.one * 0.6f : new Vector3(0.6f, 2f, 0.6f);
            Physics.SyncTransforms();
            Assert.That(a.RegisterDynamicObstacle(cube.GetComponent<Collider>()), Is.True);
            Assert.That(world.TrySample(from, goal, out FlowFieldSample stopped), Is.True);
            Assert.That(stopped.SpeedMultiplier, Is.EqualTo(0f), "The step into A's notified change must stop.");
        }

        // An area whose Bake Bounds shrink while it builds a world Goal: its
        // seeds belong to the old grid, so they go with its field, and the
        // area builds the Goal again on its new grid.
        [UnityTest, Timeout(300000)]
        public IEnumerator AreaRebuildsWorldGoalsOnANewGrid()
        {
            yield return EnterEmptyPlayMode();
            FlowFieldWorld world = CreateWorld(FlowFieldSpaceMode.Surface2D);
            CreateGround();
            FlowFieldManager a = CreateManager("A", FlowFieldSpaceMode.Surface2D, Vector3.zero, new Vector3(10f, 4f, 10f), world);
            FlowFieldManager b = CreateManager("B", FlowFieldSpaceMode.Surface2D, new Vector3(10f, 0f, 0f), new Vector3(10f, 4f, 10f), world);
            yield return WaitUntilSettled(a, b);
            FlowFieldGoalHandle goal = world.AddGoal(FlowFieldGoalKind.Shared, FlowFieldGoalRequest.Position(new Vector3(15.5f, 0f, 5.5f)));
            yield return WaitUntilSettled(a, b, world, goal);

            // A keeps touching B along its east edge, now 6 cells deep.
            var serialized = new UnityEditor.SerializedObject(a);
            serialized.FindProperty(FlowFieldManager.PropertyNames.BakeBoundsLocal).boundsValue
                = new Bounds(new Vector3(5f, 0f, 3f), new Vector3(10f, 4f, 6f));
            serialized.ApplyModifiedProperties();
            Assert.That(a.IsFaulted, Is.False, a.LastError);
            yield return WaitUntilSettled(a, b, world, goal);
            Assert.That(a.IsFaulted, Is.False, a.LastError);
            FlowFieldField field = a.Session.GoalField(goal.Id);
            Assert.That(field.Grid.Depth, Is.EqualTo(6));
            Assert.That(field.Goal.Distances[field.Grid.ToFlatIndex(0, 2)], Is.Not.EqualTo(FlowFieldGoalLayer.Unreachable),
                "A reaches the Goal in B on its new grid.");
        }

        // A world Goal inside the wall starts from the nearest walkable cell,
        // and an area that touches no other one gets no field for it.
        [UnityTest, Timeout(300000)]
        public IEnumerator GoalInAWallAndAnIsolatedArea()
        {
            yield return EnterEmptyPlayMode();
            FlowFieldWorld world = CreateWorld(FlowFieldSpaceMode.Surface2D);
            CreateGround();
            CreateWall(volume: false, 10f);
            FlowFieldManager a = CreateManager("A", FlowFieldSpaceMode.Surface2D, Vector3.zero, new Vector3(10f, 4f, 10f), world);
            FlowFieldManager b = CreateManager("B", FlowFieldSpaceMode.Surface2D, new Vector3(10f, 0f, 0f), new Vector3(10f, 4f, 10f), world);
            FlowFieldManager far = CreateManager("Far", FlowFieldSpaceMode.Surface2D, new Vector3(0f, 0f, 12f), new Vector3(4f, 4f, 4f), world);
            yield return WaitUntilSettled(a, b);
            yield return WaitUntilSettled(far, null);
            FlowFieldGoalHandle goal = world.AddGoal(FlowFieldGoalKind.Shared, FlowFieldGoalRequest.Position(new Vector3(10.5f, 0f, 5.5f)));
            float deadline = Time.realtimeSinceStartup + SettleTimeoutSeconds;
            while (!(a.IsGoalReady(goal) && b.IsGoalReady(goal)))
            {
                if (Time.realtimeSinceStartup > deadline)
                    Assert.Fail("The world Goal in the wall never built: " + FlowFieldPlayScene.Describe());
                yield return null;
            }
            yield return WaitUntilSettled(a, b);
            FlowFieldField fieldB = b.Session.GoalField(goal.Id);
            Assert.That(fieldB.Goal.Distances[fieldB.Grid.ToFlatIndex(5, 1)], Is.Not.EqualTo(FlowFieldGoalLayer.Unreachable),
                "A Goal in the wall must start from the nearest walkable cell.");
            Assert.That(far.IsGoalReady(goal), Is.False, "An isolated area has no field for a Goal it cannot reach.");
            Assert.That(far.Session.GoalField(goal.Id), Is.Null);
            Assert.That(world.IsGoalReady(goal), Is.False, "The world is not ready while an area has no field.");
        }

        // B joins first and an obstacle notice in A arrives every frame: the
        // epochs still spread the Goal in A to B.
        [UnityTest, Timeout(300000)]
        public IEnumerator ConstantInputStillReachesEveryArea()
        {
            yield return EnterEmptyPlayMode();
            FlowFieldWorld world = CreateWorld(FlowFieldSpaceMode.Surface2D);
            CreateGround();
            FlowFieldManager b = CreateManager("B", FlowFieldSpaceMode.Surface2D, new Vector3(10f, 0f, 0f), new Vector3(10f, 4f, 10f), world);
            FlowFieldManager a = CreateManager("A", FlowFieldSpaceMode.Surface2D, Vector3.zero, new Vector3(10f, 4f, 10f), world);
            yield return WaitUntilSettled(a, b);
            FlowFieldGoalHandle goal = world.AddGoal(FlowFieldGoalKind.Shared, FlowFieldGoalRequest.Position(new Vector3(1.5f, 0f, 5.5f)));
            float deadline = Time.realtimeSinceStartup + SettleTimeoutSeconds;
            bool reached = false;
            while (!reached)
            {
                if (Time.realtimeSinceStartup > deadline)
                    Assert.Fail("B never reached the Goal in A: " + FlowFieldPlayScene.Describe());
                a.NotifyObstacleRegionDirty(new Bounds(new Vector3(1.5f, 0f, 0.5f), Vector3.one * 0.5f));
                yield return null;
                FlowFieldField fieldB = b.Session.GoalField(goal.Id);
                reached = fieldB != null
                    && fieldB.Goal.Distances[fieldB.Grid.ToFlatIndex(9, 5)] != FlowFieldGoalLayer.Unreachable;
            }
        }

        // An area Manager reads a world Goal with its handle but refuses to
        // move or remove it; the world removes it from every area.
        [UnityTest, Timeout(300000)]
        public IEnumerator AreaManagerRefusesWorldGoalChanges()
        {
            yield return EnterEmptyPlayMode();
            FlowFieldWorld world = CreateWorld(FlowFieldSpaceMode.Surface2D);
            CreateGround();
            FlowFieldManager a = CreateManager("A", FlowFieldSpaceMode.Surface2D, Vector3.zero, new Vector3(10f, 4f, 10f), world);
            FlowFieldManager b = CreateManager("B", FlowFieldSpaceMode.Surface2D, new Vector3(10f, 0f, 0f), new Vector3(10f, 4f, 10f), world);
            yield return WaitUntilSettled(a, b);
            FlowFieldGoalHandle goal = world.AddGoal(FlowFieldGoalKind.Shared, FlowFieldGoalRequest.Position(new Vector3(1.5f, 0f, 5.5f)));
            yield return WaitUntilSettled(a, b, world, goal);

            const string refused = "A world Goal changes only through its FlowFieldWorld: call FlowFieldWorld.SetGoal or RemoveGoal.";
            var remove = Assert.Throws<System.InvalidOperationException>(() => b.RemoveGoal(goal));
            Assert.That(remove.Message, Is.EqualTo(refused));
            var move = Assert.Throws<System.InvalidOperationException>(
                () => b.SetGoal(goal, FlowFieldGoalRequest.Position(new Vector3(15.5f, 0f, 5.5f))));
            Assert.That(move.Message, Is.EqualTo(refused));
            Assert.That(b.Session.Goals.Find(goal.Id), Is.Not.Null, "The refused call removed the Goal.");
            Assert.That(b.IsGoalReady(goal), Is.True);

            Assert.That(world.RemoveGoal(goal), Is.True);
            yield return null;
            Assert.That(a.IsGoalReady(goal) || b.IsGoalReady(goal), Is.False, "The world left the Goal in an area.");
            Assert.That(b.RemoveGoal(goal), Is.False, "The handle names no Goal after the world removed it.");
        }

        private static Vector3 Flat(Vector3 position, bool volume) => volume ? position : new Vector3(position.x, 0f, position.z);

        [UnityTest, Timeout(300000)]
        public IEnumerator GoalOutsideEveryAreaLeadsToTheNearestBorder()
        {
            yield return EnterEmptyPlayMode();
            FlowFieldWorld world = CreateWorld(FlowFieldSpaceMode.Surface2D);
            CreateGround();
            FlowFieldManager a = CreateManager("A", FlowFieldSpaceMode.Surface2D, new Vector3(0f, 0f, 0f), new Vector3(10f, 4f, 10f), world);
            FlowFieldManager b = CreateManager("B", FlowFieldSpaceMode.Surface2D, new Vector3(10f, 0f, 0f), new Vector3(10f, 4f, 10f), world);
            yield return WaitUntilSettled(a, b);

            // Beyond B's far edge: B leads there, and A leads through B.
            FlowFieldGoalHandle goal = world.AddGoal(FlowFieldGoalKind.Shared, FlowFieldGoalRequest.Position(new Vector3(30.5f, 0f, 5.5f)));
            yield return WaitUntilSettled(a, b, world, goal);
            FlowFieldField fieldA = a.Session.GoalField(goal.Id);
            FlowFieldField fieldB = b.Session.GoalField(goal.Id);
            int edge = fieldB.Grid.ToFlatIndex(fieldB.Grid.Width - 1, 5);
            Assert.That(FlowFieldNextCell.IsPortal(fieldB.Goal.NextCells[edge]), Is.True, "B's far edge steps out toward the Goal.");
            Assert.That(fieldB.Goal.Distances[edge], Is.EqualTo(11), "The straight-line estimate from the cell beyond the edge.");
            int farA = fieldA.Grid.ToFlatIndex(0, 5);
            Assert.That(fieldA.Goal.Distances[farA], Is.EqualTo(fieldB.Goal.Distances[edge] + fieldB.Grid.Width - 1 + fieldA.Grid.Width),
                "A reaches the Goal through B along the row.");

            // B leaves: A keeps a field, now leading out of its own edge.
            Object.Destroy(b.gameObject);
            yield return null;
            Assert.That(world.Managers, Is.EqualTo(new[] { a }));
            yield return WaitUntilSettled(a, null, world, goal);
            fieldA = a.Session.GoalField(goal.Id);
            int edgeA = fieldA.Grid.ToFlatIndex(fieldA.Grid.Width - 1, 5);
            Assert.That(FlowFieldNextCell.IsPortal(fieldA.Goal.NextCells[edgeA]), Is.True, "Without B, A's edge steps out.");
            Assert.That(fieldA.Goal.Distances[edgeA], Is.EqualTo(21));
        }

        // A wall crosses the border with a gap on each side; the Goal is in A.
        // Every cell of both areas takes the steps one Manager over both takes.
        private IEnumerator AreasMatchOneManager(FlowFieldSpaceMode mode)
        {
            yield return EnterEmptyPlayMode();
            bool volume = mode == FlowFieldSpaceMode.Volume3D;
            FlowFieldWorld world = CreateWorld(mode);
            if (!volume)
                CreateGround();
            Vector3 size = volume ? new Vector3(8f, 6f, 8f) : new Vector3(10f, 4f, 10f);
            float width = size.x;
            CreateWall(volume, width);
            FlowFieldManager a = CreateManager("A", mode, Vector3.zero, size, world);
            FlowFieldManager b = CreateManager("B", mode, new Vector3(width, 0f, 0f), size, world);
            FlowFieldManager whole = CreateManager("Whole", mode, Vector3.zero, new Vector3(width * 2f, size.y, size.z), null);
            yield return WaitUntilSettled(a, b);
            yield return WaitUntilSettled(whole, null);
            Assert.That(a.WorldArea.BorderWith(b.WorldArea), Is.Not.Null, "A and B must touch.");

            Vector3 goalPosition = volume ? new Vector3(1.5f, 2.5f, 6.5f) : new Vector3(1.5f, 0f, 8.5f);
            FlowFieldGoalHandle goal = world.AddGoal(FlowFieldGoalKind.Shared, FlowFieldGoalRequest.Position(goalPosition));
            FlowFieldGoalHandle reference = whole.AddGoal(FlowFieldGoalKind.Shared, FlowFieldGoalRequest.Position(goalPosition));
            yield return WaitUntilSettled(a, b, world, goal);
            yield return WaitUntilSettled(whole, null);
            Assert.That(whole.IsGoalReady(reference), Is.True);

            FlowFieldField expected = whole.Session.GoalField(reference.Id);
            AssertSameSteps(a, a.Session.GoalField(goal.Id), expected);
            AssertSameSteps(b, b.Session.GoalField(goal.Id), expected);
        }

        private static void AssertSameSteps(FlowFieldManager area, FlowFieldField field, FlowFieldField expected)
        {
            FlowFieldGridSpace grid = field.Grid;
            int reached = 0;
            for (int cell = 0; cell < grid.CellCount; cell++)
            {
                Vector3 center = grid.CellCenter(cell);
                Assert.That(FlowFieldSampler.TryLocate(expected, center, out int other), Is.True);
                Assert.That(field.Goal.Distances[cell], Is.EqualTo(expected.Goal.Distances[other]),
                    area.name + " cell " + cell + " at " + center.ToString("F1"));
                if (field.Goal.Distances[cell] != FlowFieldGoalLayer.Unreachable)
                    reached++;
            }
            Assert.That(reached, Is.GreaterThan(grid.CellCount / 2), area.name + " reaches too few cells.");
        }
    }
}
