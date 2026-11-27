using System.Collections;
using Common.FlowField.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using static Common.FlowField.Tests.FlowFieldWorldTestScene;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// The world bake: with every area open, it searches the Bake Goals and
    /// keeps the steps of each area's outer cells. In Play Mode, when the
    /// Goal's area is not loaded, a loaded area beside it takes those steps
    /// and every cell gets the steps it had with the Goal's area loaded. An
    /// area the bake never saw, and every area with no bake, falls back to
    /// the straight-line estimate. The bake refuses what Init would refuse
    /// and a Goal no area holds, and the Inspector warns about an area the
    /// bake does not hold.
    /// </summary>
    [TestFixture, Category("World")]
    internal sealed class WorldBakedGoalTests : FlowFieldPlayTestBase
    {
        [UnityTest, Timeout(300000)]
        public IEnumerator SurfaceUnloadedGoalFollowsTheBake() => UnloadedGoalFollowsTheBake(FlowFieldSpaceMode.Surface2D);

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeUnloadedGoalFollowsTheBake() => UnloadedGoalFollowsTheBake(FlowFieldSpaceMode.Volume3D);

        // B and C side by side; in C a wall with a gap at its far end stands
        // between the border and the Goal, so the way from B bends around it
        // where the straight line does not.
        private IEnumerator UnloadedGoalFollowsTheBake(FlowFieldSpaceMode mode)
        {
            bool volume = mode == FlowFieldSpaceMode.Volume3D;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Vector3 size = volume ? new Vector3(8f, 6f, 8f) : new Vector3(10f, 4f, 10f);
            Vector3 goalPosition = volume ? new Vector3(size.x + 6.5f, 2.5f, 1.5f) : new Vector3(size.x + 7.5f, 0f, 2.5f);
            FlowFieldWorld world = CreateWorld(mode);
            SetBakeGoals(world, goalPosition);
            if (!volume)
                CreateGround();
            float wallLength = size.z - 3f;
            CreateBox(
                "Wall",
                volume ? new Vector3(size.x + 3.5f, 3f, wallLength * 0.5f) : new Vector3(size.x + 3.5f, 1f, wallLength * 0.5f),
                volume ? new Vector3(0.8f, 6f, wallLength) : new Vector3(0.8f, 2f, wallLength));
            FlowFieldManager b = CreateManager("B", mode, Vector3.zero, size, world);
            FlowFieldManager c = CreateManager("C", mode, new Vector3(size.x, 0f, 0f), size, world);

            Assert.That(FlowFieldWorldBaker.TryBake(world, out string message), Is.True, message);
            Assert.That(world.Bake.GoalCount, Is.EqualTo(1), "One baked Goal for the Default type.");
            FlowFieldWorldBakedGoal baked = world.Bake.Goal(0);
            Assert.That(baked.AgentType, Is.EqualTo(FlowFieldAgentType.DefaultName));
            Assert.That(baked.CellCount, Is.GreaterThan(0));
            Assert.That(world.FindBakedGoal(goalPosition, FlowFieldAgentType.DefaultName), Is.SameAs(baked));
            Assert.That(world.FindBakedGoal(goalPosition + new Vector3(0f, 0f, CellSize), FlowFieldAgentType.DefaultName), Is.Null,
                "Another cell names no baked Goal.");

            yield return EnterPlayModeKeepingScene();
            yield return WaitUntilSettled(b, c);
            var request = FlowFieldGoalRequest.Position(goalPosition);
            FlowFieldGoalHandle goal = world.AddGoal(FlowFieldGoalKind.Shared, request);
            yield return WaitUntilSettled(b, c, world, goal);
            int[] loaded = (int[])b.Session.GoalField(goal.Id).Goal.Distances.Clone();
            int reached = 0;
            foreach (int steps in loaded)
            {
                if (steps != FlowFieldGoalLayer.Unreachable)
                    reached++;
            }
            Assert.That(reached, Is.GreaterThan(loaded.Length / 2), "B reaches too few cells with C loaded.");

            // C leaves: B seeds its border from the bake and keeps every step.
            Object.Destroy(c.gameObject);
            yield return null;
            Assert.That(world.Managers, Is.EqualTo(new[] { b }));
            yield return WaitUntilSettled(b, null, world, goal);
            Assert.That(b.Session.GoalField(goal.Id).Goal.Distances, Is.EqualTo(loaded),
                "With C not loaded, B takes the steps it had with C loaded.");

            // An area the bake never saw, touching no other: its outer cells
            // lead by the estimate, and B keeps its steps.
            Vector3 farSize = volume ? new Vector3(4f, 6f, 4f) : new Vector3(4f, 4f, 4f);
            FlowFieldManager far = CreateManager("Far", mode, new Vector3(0f, 0f, size.z + 2f), farSize, world);
            yield return WaitUntilSettled(b, far, world, goal);
            FlowFieldField farField = far.Session.GoalField(goal.Id);
            Assert.That(farField, Is.Not.Null, "An area outside the bake leads by the estimate.");
            int farEdge = volume
                ? farField.Grid.ToFlatIndex(farField.Grid.Width - 1, 2, 0)
                : farField.Grid.ToFlatIndex(farField.Grid.Width - 1, 0);
            Assert.That(FlowFieldNextCell.IsPortal(farField.Goal.NextCells[farEdge]), Is.True, "Far's corner toward the Goal steps out.");
            Assert.That(b.Session.GoalField(goal.Id).Goal.Distances, Is.EqualTo(loaded), "B keeps its baked steps.");
            Object.Destroy(far.gameObject);
            yield return null;

            // Without the bake, B leads by the straight-line estimate instead.
            world.Bake = null;
            Assert.That(world.SetGoal(goal, request), Is.True);
            yield return WaitUntilSettled(b, null, world, goal);
            Assert.That(b.Session.GoalField(goal.Id).Goal.Distances, Is.Not.EqualTo(loaded),
                "The straight-line estimate does not see the wall in C.");
        }

        [Test]
        public void BakeRefusesWhatItCannotSearch()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            FlowFieldWorld world = CreateWorld(FlowFieldSpaceMode.Surface2D);
            Assert.That(FlowFieldWorldBaker.TryBake(world, out string message), Is.False);
            Assert.That(message, Does.Contain("Bake Goal"));

            SetBakeGoals(world, new Vector3(5.5f, 0f, 5.5f));
            Assert.That(FlowFieldWorldBaker.TryBake(world, out message), Is.False);
            Assert.That(message, Does.Contain("No Manager"));

            CreateGround();
            CreateManager("A", FlowFieldSpaceMode.Surface2D, Vector3.zero, new Vector3(10f, 4f, 10f), world);
            SetBakeGoals(world, new Vector3(25.5f, 0f, 5.5f));
            Assert.That(FlowFieldWorldBaker.TryBake(world, out message), Is.False);
            Assert.That(message, Does.Contain("lies in no area"));
            Assert.That(world.Bake == null || world.Bake.GoalCount == 0, Is.True, "A refused bake keeps the asset's bake.");

            CreateManager("Overlap", FlowFieldSpaceMode.Surface2D, new Vector3(5f, 0f, 0f), new Vector3(10f, 4f, 10f), world);
            SetBakeGoals(world, new Vector3(5.5f, 0f, 5.5f));
            Assert.That(FlowFieldWorldBaker.TryBake(world, out message), Is.False);
            Assert.That(message, Does.Contain("overlaps"));
            Object.DestroyImmediate(GameObject.Find("Overlap"));

            Assert.That(FlowFieldWorldBaker.TryBake(world, out message), Is.True, message);
            Assert.That(FlowFieldWorldBaker.Describe(world, out MessageType type), Does.StartWith("Baked 1 Goal cell"));
            Assert.That(type, Is.EqualTo(MessageType.Info));
            GameObject late = CreateManager("Late", FlowFieldSpaceMode.Surface2D, new Vector3(0f, 0f, 12f), new Vector3(4f, 4f, 4f), world).gameObject;
            Assert.That(FlowFieldWorldBaker.Describe(world, out type), Does.Contain("Late is not in the bake"));
            Assert.That(type, Is.EqualTo(MessageType.Warning));
            Object.DestroyImmediate(late);
            var serialized = new SerializedObject(world);
            serialized.FindProperty("_cellSize").floatValue = 0.5f;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            Assert.That(world.FindBakedGoal(new Vector3(5.5f, 0f, 5.5f), FlowFieldAgentType.DefaultName), Is.Null,
                "A bake of other world settings is unused.");
            FlowFieldWorldBaker.Describe(world, out type);
            Assert.That(type, Is.EqualTo(MessageType.Warning));
        }

        private static void SetBakeGoals(FlowFieldWorld world, params Vector3[] positions)
        {
            var serialized = new SerializedObject(world);
            SerializedProperty goals = serialized.FindProperty("_bakeGoals");
            goals.arraySize = positions.Length;
            for (int i = 0; i < positions.Length; i++)
                goals.GetArrayElementAtIndex(i).vector3Value = positions[i];
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void CreateBox(string name, Vector3 position, Vector3 scale)
        {
            GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = name;
            box.layer = ObstacleLayer;
            box.transform.position = position;
            box.transform.localScale = scale;
            Physics.SyncTransforms();
        }

        // Play Mode on the scene the test built in Edit Mode.
        private static IEnumerator EnterPlayModeKeepingScene()
        {
            Assert.That(EditorApplication.isPlaying, Is.False, "A test entered Play Mode twice.");
            EditorApplication.isPlaying = true;
            float deadline = Time.realtimeSinceStartup + SettleTimeoutSeconds;
            while (!EditorApplication.isPlaying)
            {
                if (Time.realtimeSinceStartup > deadline)
                    Assert.Fail("Timed out entering Play Mode.");
                yield return null;
            }
        }
    }
}
