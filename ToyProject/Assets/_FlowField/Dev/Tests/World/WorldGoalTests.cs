using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// World Goals over areas built in Play Mode. Two touching areas reach a
    /// Goal with the same steps as one Manager over both, in both space modes;
    /// a Goal no area holds leads to the outer border nearest it; and an area
    /// that leaves takes its field with it while the others keep theirs.
    /// </summary>
    [TestFixture, Category("World")]
    internal sealed class WorldGoalTests : FlowFieldPlayTestBase
    {
        private const int GroundLayer = 8;
        private const int ObstacleLayer = 9;
        private const float CellSize = 1f;
        private const float SettleTimeoutSeconds = 60f;

        [UnityTest, Timeout(300000)]
        public IEnumerator SurfaceAreasMatchOneManager() => AreasMatchOneManager(FlowFieldSpaceMode.Surface2D);

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeAreasMatchOneManager() => AreasMatchOneManager(FlowFieldSpaceMode.Volume3D);

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

        // ----- Scene ----------------------------------------------------------

        // Play Mode on an empty scene, entered as the sample scenes are.
        private static IEnumerator EnterEmptyPlayMode()
        {
            Assert.That(EditorApplication.isPlaying, Is.False, "A test entered Play Mode twice.");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorApplication.isPlaying = true;
            float deadline = Time.realtimeSinceStartup + SettleTimeoutSeconds;
            while (!EditorApplication.isPlaying)
            {
                if (Time.realtimeSinceStartup > deadline)
                    Assert.Fail("Timed out entering Play Mode.");
                yield return null;
            }
        }

        private static FlowFieldWorld CreateWorld(FlowFieldSpaceMode mode)
        {
            var world = ScriptableObject.CreateInstance<FlowFieldWorld>();
            var serialized = new SerializedObject(world);
            serialized.FindProperty("_spaceMode").enumValueIndex = (int)mode;
            serialized.FindProperty("_cellSize").floatValue = CellSize;
            serialized.FindProperty("_latticeOrigin").vector3Value = Vector3.zero;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return world;
        }

        private static void CreateGround()
        {
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "Ground";
            ground.layer = GroundLayer;
            ground.transform.position = new Vector3(10f, -0.5f, 5f);
            ground.transform.localScale = new Vector3(40f, 1f, 30f);
        }

        // A wall across both areas with a gap at each end.
        private static void CreateWall(bool volume, float areaWidth)
        {
            GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = "Wall";
            wall.layer = ObstacleLayer;
            float length = areaWidth * 2f - 4f;
            wall.transform.position = volume ? new Vector3(areaWidth, 3f, 4f) : new Vector3(areaWidth, 1f, 5f);
            wall.transform.localScale = volume ? new Vector3(length, 6f, 0.8f) : new Vector3(length, 2f, 0.8f);
            Physics.SyncTransforms();
        }

        // A Manager whose grid starts at the position: the local bounds' minimum is its origin.
        private static FlowFieldManager CreateManager(string name, FlowFieldSpaceMode mode, Vector3 position, Vector3 size, FlowFieldWorld world)
        {
            var gameObject = new GameObject(name);
            gameObject.SetActive(false);
            gameObject.transform.position = position;
            FlowFieldManager manager = gameObject.AddComponent<FlowFieldManager>();
            var serialized = new SerializedObject(manager);
            serialized.FindProperty(FlowFieldManager.PropertyNames.SpaceMode).enumValueIndex = (int)mode;
            serialized.FindProperty(FlowFieldManager.PropertyNames.BakeMode).enumValueIndex = (int)FlowFieldBakeMode.RuntimeDynamic;
            float bottom = mode == FlowFieldSpaceMode.Volume3D ? 0f : -2f;
            serialized.FindProperty(FlowFieldManager.PropertyNames.BakeBoundsLocal).boundsValue
                = new Bounds(new Vector3(size.x * 0.5f, bottom + size.y * 0.5f, size.z * 0.5f), size);
            serialized.FindProperty(FlowFieldManager.PropertyNames.CellSize).floatValue = CellSize;
            serialized.FindProperty(FlowFieldManager.PropertyNames.GroundLayer).intValue = 1 << GroundLayer;
            serialized.FindProperty(FlowFieldManager.PropertyNames.ObstacleLayer).intValue = 1 << ObstacleLayer;
            serialized.FindProperty(FlowFieldManager.PropertyNames.World).objectReferenceValue = world;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            gameObject.SetActive(true);
            return manager;
        }

        // Ready, then nothing published for a second: the world's seeds have
        // spread through every area.
        private static IEnumerator WaitUntilSettled(
            FlowFieldManager first,
            FlowFieldManager second,
            FlowFieldWorld world = null,
            FlowFieldGoalHandle goal = default)
        {
            float deadline = Time.realtimeSinceStartup + SettleTimeoutSeconds;
            int revision = -1;
            float quietSince = 0f;
            while (true)
            {
                if (Time.realtimeSinceStartup > deadline)
                    Assert.Fail("Timed out settling: " + FlowFieldPlayScene.Describe());
                bool ready = first.State == FlowFieldRuntimeState.Ready
                    && (second == null || second.State == FlowFieldRuntimeState.Ready)
                    && (world == null || world.IsGoalReady(goal));
                int now = first.Revision + (second != null ? second.Revision * 7919 : 0);
                if (!ready || now != revision)
                {
                    revision = now;
                    quietSince = Time.realtimeSinceStartup;
                }
                else if (Time.realtimeSinceStartup - quietSince > 1f)
                {
                    yield break;
                }
                yield return null;
            }
        }
    }
}
