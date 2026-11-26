using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// The pieces of the world tests: worlds, ground and walls on the sample
    /// layers, area Managers whose grid starts at their position, and a wait
    /// until every area has settled.
    /// </summary>
    internal static class FlowFieldWorldTestScene
    {
        internal const int GroundLayer = 8;
        internal const int ObstacleLayer = 9;
        internal const float CellSize = 1f;
        internal const float SettleTimeoutSeconds = 60f;

        // Play Mode on an empty scene, entered as the sample scenes are.
        internal static IEnumerator EnterEmptyPlayMode()
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

        internal static FlowFieldWorld CreateWorld(FlowFieldSpaceMode mode) => CreateWorld(mode, CellSize, Vector3.zero);

        internal static FlowFieldWorld CreateWorld(FlowFieldSpaceMode mode, float cellSize, Vector3 latticeOrigin)
        {
            var world = ScriptableObject.CreateInstance<FlowFieldWorld>();
            var serialized = new SerializedObject(world);
            serialized.FindProperty("_spaceMode").enumValueIndex = (int)mode;
            serialized.FindProperty("_cellSize").floatValue = cellSize;
            serialized.FindProperty("_latticeOrigin").vector3Value = latticeOrigin;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return world;
        }

        internal static void CreateGround()
        {
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "Ground";
            ground.layer = GroundLayer;
            ground.transform.position = new Vector3(10f, -0.5f, 5f);
            ground.transform.localScale = new Vector3(40f, 1f, 30f);
        }

        // A wall across both areas with a gap at each end.
        internal static void CreateWall(bool volume, float areaWidth)
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
        internal static FlowFieldManager CreateManager(string name, FlowFieldSpaceMode mode, Vector3 position, Vector3 size, FlowFieldWorld world)
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
        internal static IEnumerator WaitUntilSettled(
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
