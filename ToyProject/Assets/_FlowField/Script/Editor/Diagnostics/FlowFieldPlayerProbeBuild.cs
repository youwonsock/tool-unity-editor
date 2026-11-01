using System;
using System.Linq;
using Common.FlowField.PlayerProbe;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Random = System.Random;

namespace Common.FlowField.Editor
{
    /// <summary>
    /// Builds the measurement Player for Tools/run-player-probe.sh. The probe
    /// scene is generated in the disposable test worktree, so it never enters
    /// the project or a normal build.
    /// </summary>
    internal static class FlowFieldPlayerProbeBuild
    {
        private const string ScenePath = "Assets/FlowFieldPlayerProbe.unity";
        private const string VariantPrefix = "OtherBakeMode_";
        private const string OutputArgument = "-flowFieldProbeApp";
        private const int SurfaceSize = 316;
        private const int VolumeSize = 100;
        private const int ObstacleCount = 40;

        internal static void Build()
        {
            string output = ReadOutput();
            string[] sampleScenes = EditorBuildSettings.scenes
                .Where(scene => scene.enabled)
                .Select(scene => scene.path)
                .ToArray();
            sampleScenes = sampleScenes.Concat(sampleScenes.SelectMany(CreateModeVariant)).ToArray();
            GenerateScene(sampleScenes);

            EditorUserBuildSettings.SetPlatformSettings("Standalone", "OSXUniversal", "Architecture", "ARM64");
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, ScriptingImplementation.Mono2x);

            BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath }.Concat(sampleScenes).ToArray(),
                locationPathName = output,
                target = BuildTarget.StandaloneOSX,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None,
            });
            if (report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException($"Probe Player build {report.summary.result}: {report.summary.totalErrors} errors");
        }

        // Saves a copy of a FlowField sample in its other bake mode, so the
        // Player starts that mode through the normal scene startup.
        private static string[] CreateModeVariant(string scenePath)
        {
            Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            FlowFieldManager manager = UnityEngine.Object.FindAnyObjectByType<FlowFieldManager>(FindObjectsInactive.Include);
            if (manager == null)
                return Array.Empty<string>();

            using var settings = new SerializedObject(manager);
            SerializedProperty bakeMode = settings.FindProperty("_bakeMode");
            bakeMode.enumValueIndex = bakeMode.enumValueIndex == (int)FlowFieldBakeMode.StaticBaked
                ? (int)FlowFieldBakeMode.RuntimeDynamic
                : (int)FlowFieldBakeMode.StaticBaked;
            settings.ApplyModifiedPropertiesWithoutUndo();

            string path = $"Assets/{VariantPrefix}{scene.name}.unity";
            if (!EditorSceneManager.SaveScene(scene, path, saveAsCopy: true))
                throw new InvalidOperationException($"Could not save {path}.");
            return new[] { path };
        }

        private static void GenerateScene(string[] sampleScenes)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            GameObject ground = new GameObject("Probe ground") { layer = FlowFieldPlayerProbeLayers.Ground };
            ground.transform.position = new Vector3(SurfaceSize * 0.5f, -0.5f, SurfaceSize * 0.5f);
            ground.AddComponent<BoxCollider>().size = new Vector3(SurfaceSize, 1f, SurfaceSize);

            var probe = new GameObject("Probe").AddComponent<FlowFieldPlayerProbe>();
            using (var settings = new SerializedObject(probe))
            {
                settings.FindProperty("_surface").objectReferenceValue = CreateSurfaceManager();
                settings.FindProperty("_volume").objectReferenceValue = CreateVolumeManager();
                settings.FindProperty("_surfaceObstacles").objectReferenceValue = CreateSurfaceWalls();
                settings.FindProperty("_volumeObstacles").objectReferenceValue = CreateVolumeObstacles();
                settings.FindProperty("_surfaceSize").intValue = SurfaceSize;
                settings.FindProperty("_volumeSize").intValue = VolumeSize;
                SerializedProperty scenes = settings.FindProperty("_sampleScenes");
                scenes.arraySize = sampleScenes.Length;
                for (int i = 0; i < sampleScenes.Length; i++)
                    scenes.GetArrayElementAtIndex(i).stringValue =
                        System.IO.Path.GetFileNameWithoutExtension(sampleScenes[i]);
                settings.ApplyModifiedPropertiesWithoutUndo();
            }

            if (!EditorSceneManager.SaveScene(scene, ScenePath))
                throw new InvalidOperationException($"Could not save {ScenePath}.");
        }

        private static FlowFieldManager CreateSurfaceManager()
            => CreateManager("Surface", FlowFieldSpaceMode.Surface2D,
                new Bounds(new Vector3(SurfaceSize * 0.5f, 0f, SurfaceSize * 0.5f), new Vector3(SurfaceSize, 10f, SurfaceSize)));

        private static FlowFieldManager CreateVolumeManager()
            => CreateManager("Volume", FlowFieldSpaceMode.Volume3D,
                new Bounds(Vector3.one * (VolumeSize * 0.5f), Vector3.one * VolumeSize));

        private static FlowFieldManager CreateManager(string name, FlowFieldSpaceMode mode, Bounds bounds)
        {
            var managerObject = new GameObject(name);
            managerObject.SetActive(false);
            var manager = managerObject.AddComponent<FlowFieldManager>();
            using var settings = new SerializedObject(manager);
            settings.FindProperty("_spaceMode").enumValueIndex = (int)mode;
            settings.FindProperty("_bakeMode").enumValueIndex = (int)FlowFieldBakeMode.RuntimeDynamic;
            settings.FindProperty("_bakeBoundsLocal").boundsValue = bounds;
            settings.FindProperty("_cellSize").floatValue = 1f;
            settings.FindProperty("_groundBakeLayer").intValue = 1 << FlowFieldPlayerProbeLayers.Ground;
            settings.FindProperty("_obstacleLayer").intValue = 1 << FlowFieldPlayerProbeLayers.Obstacle;
            settings.ApplyModifiedPropertiesWithoutUndo();
            return manager;
        }

        private static GameObject CreateSurfaceWalls()
        {
            var parent = new GameObject("Surface walls");
            var random = new Random(1234);
            for (int i = 0; i < ObstacleCount; i++)
            {
                float minX = random.Next(4, SurfaceSize - 8);
                float minZ = random.Next(4, SurfaceSize - 8);
                float width = random.Next(1, 6);
                float depth = random.Next(1, 30);
                CreateBox(parent, new Vector3(minX + width * 0.5f, 1f, minZ + depth * 0.5f), new Vector3(width, 6f, depth));
            }
            parent.SetActive(false);
            return parent;
        }

        private static GameObject CreateVolumeObstacles()
        {
            var parent = new GameObject("Volume obstacles");
            var random = new Random(4321);
            for (int i = 0; i < ObstacleCount; i++)
            {
                Vector3 center = new Vector3(
                    random.Next(8, VolumeSize - 8) + 0.5f,
                    random.Next(8, VolumeSize - 8) + 0.5f,
                    random.Next(8, VolumeSize - 8) + 0.5f);
                Vector3 size = new Vector3(random.Next(2, 12), random.Next(2, 12), random.Next(2, 12));
                CreateBox(parent, center, size);
            }
            parent.SetActive(false);
            return parent;
        }

        private static void CreateBox(GameObject parent, Vector3 center, Vector3 size)
        {
            var box = new GameObject("Obstacle") { layer = FlowFieldPlayerProbeLayers.Obstacle, isStatic = true };
            box.transform.SetParent(parent.transform, false);
            box.transform.position = center;
            box.AddComponent<BoxCollider>().size = size;
        }

        private static string ReadOutput()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < arguments.Length; i++)
            {
                if (arguments[i] == OutputArgument)
                    return arguments[i + 1];
            }
            throw new InvalidOperationException($"{OutputArgument} is required.");
        }
    }
}
