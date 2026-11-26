using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using static Common.FlowField.Tests.FlowFieldWorldTestScene;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// Areas streamed with their scenes and registrations shared by a world.
    /// Areas saved in two scenes join as the scenes load additively and leave
    /// as they unload, and a world Goal follows: the scene that comes back
    /// builds it again. An obstacle registered with the world reaches every
    /// area, also one that joins later, and its moves rebuild each area it
    /// leaves or enters.
    /// </summary>
    [TestFixture, Category("World")]
    internal sealed class WorldStreamingTests : FlowFieldPlayTestBase
    {
        private const string WorldPath = FlowFieldBakeTestSupport.TempFolder + "/StreamedWorld.asset";
        private const string ScenePathA = FlowFieldBakeTestSupport.TempFolder + "/StreamedA.unity";
        private const string ScenePathB = FlowFieldBakeTestSupport.TempFolder + "/StreamedB.unity";

        [UnityTearDown]
        public IEnumerator DeleteScenes()
        {
            yield return FlowFieldPlayScene.Exit();
            FlowFieldBakeTestSupport.DeleteTempFolder();
        }

        [UnityTest, Timeout(300000)]
        public IEnumerator AreasJoinAndLeaveWithTheirScenes()
        {
            FlowFieldBakeTestSupport.DeleteTempFolder();
            Assert.That(AssetDatabase.CreateFolder(FlowFieldBakeTestSupport.TestsFolder, FlowFieldBakeTestSupport.TempFolderName), Is.Not.Empty);
            FlowFieldWorld world = CreateWorld(FlowFieldSpaceMode.Surface2D);
            AssetDatabase.CreateAsset(world, WorldPath);
            SaveAreaScene(ScenePathA, "A", Vector3.zero, world, ground: true);
            SaveAreaScene(ScenePathB, "B", new Vector3(10f, 0f, 0f), world, ground: false);

            yield return EnterEmptyPlayMode();
            yield return Load(ScenePathA);
            yield return Load(ScenePathB);
            FlowFieldManager a = Find("A");
            FlowFieldManager b = Find("B");
            Assert.That(world.Managers, Is.EquivalentTo(new[] { a, b }), "Each loaded scene's area joins.");
            yield return WaitUntilSettled(a, b);

            FlowFieldGoalHandle goal = world.AddGoal(FlowFieldGoalKind.Shared, FlowFieldGoalRequest.Position(new Vector3(1.5f, 0f, 8.5f)));
            yield return WaitUntilSettled(a, b, world, goal);
            Assert.That(world.TrySample(new Vector3(18.5f, 0f, 1.5f), goal, out FlowFieldSample farSample), Is.True);
            Assert.That(farSample.SpeedMultiplier, Is.GreaterThan(0f), "B leads toward the Goal in A.");

            yield return SceneManager.UnloadSceneAsync(SceneManager.GetSceneByPath(ScenePathB));
            Assert.That(world.Managers, Is.EqualTo(new[] { a }), "The unloaded scene's area leaves.");
            Assert.That(world.TrySample(new Vector3(18.5f, 0f, 1.5f), goal, out _), Is.False, "No area holds the position now.");
            yield return WaitUntilSettled(a, null, world, goal);
            Assert.That(world.IsGoalReady(goal), Is.True);

            yield return Load(ScenePathB);
            b = Find("B");
            Assert.That(world.Managers, Is.EquivalentTo(new[] { a, b }), "The scene's area joins again.");
            yield return WaitUntilSettled(a, b, world, goal);
            Assert.That(b.IsGoalReady(goal), Is.True, "The area that came back builds the world Goal.");
            Assert.That(world.TrySample(new Vector3(18.5f, 0f, 1.5f), goal, out farSample), Is.True);
            Assert.That(farSample.SpeedMultiplier, Is.GreaterThan(0f));
        }

        [UnityTest, Timeout(300000)]
        public IEnumerator WorldObstacleReachesEveryArea()
        {
            yield return EnterEmptyPlayMode();
            FlowFieldWorld world = CreateWorld(FlowFieldSpaceMode.Surface2D);
            CreateGround();
            FlowFieldManager a = CreateManager("A", FlowFieldSpaceMode.Surface2D, Vector3.zero, new Vector3(10f, 4f, 10f), world);
            FlowFieldManager b = CreateManager("B", FlowFieldSpaceMode.Surface2D, new Vector3(10f, 0f, 0f), new Vector3(10f, 4f, 10f), world);
            yield return WaitUntilSettled(a, b);

            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.layer = ObstacleLayer;
            cube.transform.position = new Vector3(5.5f, 1f, 5.5f);
            cube.transform.localScale = new Vector3(0.6f, 2f, 0.6f);
            Physics.SyncTransforms();
            Collider collider = cube.GetComponent<Collider>();
            Assert.That(world.RegisterDynamicObstacle(collider), Is.True);
            Assert.That(world.RegisterDynamicObstacle(collider), Is.False, "The world already holds it.");
            Assert.That(a.IsDynamicObstacleRegistered(collider) && b.IsDynamicObstacleRegistered(collider), Is.True,
                "Every area holds a world obstacle.");
            yield return WaitUntilSettled(a, b);
            Assert.That(Blocked(a, cube.transform.position), Is.True);

            // Into B: A frees its cell, B blocks the new one.
            cube.transform.position = new Vector3(15.5f, 1f, 5.5f);
            Physics.SyncTransforms();
            Assert.That(world.NotifyDynamicObstacleMoved(collider), Is.True);
            yield return WaitUntilSettled(a, b);
            Assert.That(Blocked(a, new Vector3(5.5f, 0f, 5.5f)), Is.False, "A kept the obstacle that left it.");
            Assert.That(Blocked(b, cube.transform.position), Is.True, "B missed the obstacle that moved in.");

            // An area that joins later takes it too.
            FlowFieldManager c = CreateManager("C", FlowFieldSpaceMode.Surface2D, new Vector3(0f, 0f, 10f), new Vector3(10f, 4f, 10f), world);
            yield return WaitUntilSettled(c, null);
            yield return null;
            Assert.That(c.IsDynamicObstacleRegistered(collider), Is.True, "A later area takes the world obstacle.");

            Assert.That(world.UnregisterDynamicObstacle(collider), Is.True);
            Assert.That(a.IsDynamicObstacleRegistered(collider) || b.IsDynamicObstacleRegistered(collider) || c.IsDynamicObstacleRegistered(collider),
                Is.False, "Unregistering leaves every area.");
        }

        private static bool Blocked(FlowFieldManager manager, Vector3 position)
        {
            Assert.That(manager.TryGetPublishedField(out FlowFieldField field), Is.True);
            Assert.That(FlowFieldSampler.TryLocate(field, position, out int cell), Is.True);
            return field.Agent.Blocked[cell];
        }

        // A saved scene with one area Manager; the first also has the ground
        // under both areas.
        private static void SaveAreaScene(string path, string name, Vector3 position, FlowFieldWorld world, bool ground)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (ground)
                CreateGround();
            CreateManager(name, FlowFieldSpaceMode.Surface2D, position, new Vector3(10f, 4f, 10f), world);
            Assert.That(EditorSceneManager.SaveScene(scene, path), Is.True, "Could not save " + path);
        }

        private static IEnumerator Load(string path)
        {
            AsyncOperation load = EditorSceneManager.LoadSceneAsyncInPlayMode(path, new LoadSceneParameters(LoadSceneMode.Additive));
            while (!load.isDone)
                yield return null;
            yield return null;
        }

        private static FlowFieldManager Find(string name)
        {
            foreach (FlowFieldManager manager in Object.FindObjectsByType<FlowFieldManager>(FindObjectsSortMode.None))
            {
                if (manager.name == name)
                    return manager;
            }
            Assert.Fail("No Manager " + name + ": " + FlowFieldPlayScene.Describe());
            return null;
        }
    }
}
