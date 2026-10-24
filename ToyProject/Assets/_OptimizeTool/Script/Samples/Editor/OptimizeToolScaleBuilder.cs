#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Common.OptimizeTool.Samples.Editor
{
    public static class OptimizeToolScaleBuilder
    {
        public const string ScenePath = "Assets/_OptimizeTool/Scene/OptimizeToolScaleBenchmark.unity";
        public const string OutputFolder = "Assets/_OptimizeTool/Settings/Generated/ScaleBenchmark";
        public const string GpuScenePath = "Assets/_OptimizeTool/Scene/OptimizeToolGpuBenchmark.unity";
        public const string GpuOutputFolder = "Assets/_OptimizeTool/Settings/Generated/GpuBenchmark";
        public static readonly int[] TileSides = { 5, 10, 20 };

        [MenuItem("Tools/OptimizeTool/Benchmark/Build Scale Benchmark Scene")]
        public static void Build() => BuildAt(ScenePath, OutputFolder, TileSides);

        [MenuItem("Tools/OptimizeTool/Benchmark/Build GPU Benchmark Scene")]
        public static void BuildGpu() => BuildAt(GpuScenePath, GpuOutputFolder, new[] { 1, 2, 5 }, 64);

        public static void BuildAt(string scenePath, string folder, int[] sides, int faceSubdivisions = 1)
        {
            MeshProcessingUtility.RequireEditMode();
            MeshAssetTransaction.ValidateFolder(folder);
            if (SceneManager.GetSceneByPath(scenePath).isLoaded)
                throw new InvalidOperationException("Close the scale benchmark scene before rebuilding it.");
            var paths = new List<string> { scenePath, folder + "/Source.mat" };
            if (faceSubdivisions < 1 || faceSubdivisions > 128) throw new ArgumentOutOfRangeException(nameof(faceSubdivisions));
            if (faceSubdivisions > 1) paths.Add(folder + "/DenseCube.asset");
            foreach (int side in sides)
            {
                if (side < 1 || side > 32) throw new ArgumentOutOfRangeException(nameof(sides));
                for (int mode = 1; mode < 4; mode++) paths.Add($"{folder}/Cubes-{side * side * 9}-Mode-{mode}.asset");
            }
            AssetFileTransaction.Execute(paths, () => BuildScene(scenePath, folder, sides, faceSubdivisions));
        }

        private static void BuildScene(string path, string folder, int[] sides, int faceSubdivisions)
        {
            EnsureFolder(folder);
            Material source = AssetDatabase.LoadAssetAtPath<Material>("Assets/_OptimizeTool/Settings/OptimizeToolSource.mat");
            if (source == null) throw new InvalidOperationException("The source material is missing.");
            Material material = AssetDatabase.LoadAssetAtPath<Material>(folder + "/Source.mat");
            if (material == null)
            {
                material = new Material(source) { name = "ScaleBenchmarkSource", enableInstancing = false };
                AssetDatabase.CreateAsset(material, folder + "/Source.mat");
            }
            if (material.enableInstancing) throw new InvalidOperationException("Scale benchmark material must have GPU instancing disabled.");
            Scene previous = SceneManager.GetActiveScene();
            // A named staging copy also works when the user has an unsaved Untitled scene open.
            string stagingPath = AssetDatabase.GenerateUniqueAssetPath(folder + "/ScaleAuthoring.unity");
            if (!AssetDatabase.CopyAsset("Assets/_OptimizeTool/Scene/OptimizeToolSample.unity", stagingPath))
                throw new InvalidOperationException("Cannot create the scale authoring scene.");
            Scene target = EditorSceneManager.OpenScene(stagingPath, OpenSceneMode.Additive);
            foreach (GameObject obj in target.GetRootGameObjects()) Object.DestroyImmediate(obj);
            Scene scene = EditorSceneManager.NewPreviewScene();
            var results = new List<MeshBuildResult>();
            Mesh denseCube = faceSubdivisions > 1 ? OptimizeToolBenchmarkGeometry.CreateCube(faceSubdivisions) : null;
            try
            {
                SceneManager.SetActiveScene(target);
                var root = Create(scene, "OptimizeTool Scale Benchmark");
                Camera camera = Create(scene, "Main Camera", typeof(Camera)).GetComponent<Camera>();
                camera.tag = "MainCamera"; camera.orthographic = true;
                camera.transform.position = new Vector3(0, 0, -50);
                camera.nearClipPlane = 0.1f; camera.farClipPlane = 200f;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.025f, 0.04f, 0.07f);
                Light light = Create(scene, "Directional Light", typeof(Light)).GetComponent<Light>();
                light.type = LightType.Directional; light.intensity = 1.2f; light.shadows = LightShadows.None;
                light.transform.rotation = Quaternion.Euler(25, -25, 0);
                var cases = new OptimizeToolScaleWorkload.ScaleCase[sides.Length];
                var outputs = new List<MeshFilter>();
                var denseSources = new List<MeshFilter>();
                var meshes = new List<Mesh>(); var assetPaths = new List<string>();
                for (int c = 0; c < sides.Length; c++)
                {
                    int side = sides[c], count = side * side * 9;
                    var group = Create(scene, "Case " + count); group.transform.SetParent(root.transform, false);
                    var data = new OptimizeToolScaleWorkload.ScaleCase
                    {
                        cubeCount = count, faceSubdivisions = faceSubdivisions, orthographicSize = Mathf.Max(side * 2.2f / 2f, side * 5f / (2f * (16f / 9f))) * 1.08f,
                        modes = new GameObject[4], rendererCounts = new int[4], vertexCounts = new int[4], triangleCounts = new long[4]
                    };
                    data.modes[0] = Create(scene, "Original"); data.modes[0].transform.SetParent(group.transform, false);
                    camera.orthographicSize = data.orthographicSize;
                    var sources = new List<MeshFilter>(count);
                    for (int row = 0; row < side; row++)
                    for (int col = 0; col < side; col++)
                    for (int depth = 0; depth < 3; depth++)
                    for (int lane = 0; lane < 3; lane++)
                    {
                        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        SceneManager.MoveGameObjectToScene(cube, scene);
                        cube.name = $"Cube-{row}-{col}-{lane}-{depth}";
                        cube.transform.SetParent(data.modes[0].transform, false);
                        cube.transform.localPosition = new Vector3((col - (side - 1) * 0.5f) * 5f + (lane - 1) * 1.5f,
                            (row - (side - 1) * 0.5f) * 2.2f, depth * 2f);
                        MeshRenderer renderer = cube.GetComponent<MeshRenderer>(); renderer.sharedMaterial = material;
                        renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
                        renderer.lightProbeUsage = LightProbeUsage.Off; renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                        MeshFilter sourceFilter = cube.GetComponent<MeshFilter>();
                        if (denseCube != null) { sourceFilter.sharedMesh = denseCube; denseSources.Add(sourceFilter); }
                        sources.Add(sourceFilter);
                    }
                    for (int mode = 1; mode < 4; mode++)
                    {
                        MeshBuildResult result = MeshBuildPipeline.Build(new MeshBuildRequest(sources, Matrix4x4.identity,
                            camera, allowReadableCopy: true, singleMesh: true, backfaces: mode == 2,
                            backfaceAngle: 89f, occlusion: mode == 3, samplingCount: 24));
                        results.Add(result);
                        if (mode == 3 && result.CulledObjectCount != count * 2 / 3)
                            throw new InvalidOperationException("Occlusion must remove exactly the rear two layers.");
                        GameObject output = Create(scene, OptimizeToolSampleModes.All[mode].Name, typeof(MeshFilter), typeof(MeshRenderer));
                        output.transform.SetParent(group.transform, false); data.modes[mode] = output;
                        MeshFilter filter = output.GetComponent<MeshFilter>(); filter.sharedMesh = result.Meshes[0]; outputs.Add(filter);
                        MeshRenderer renderer = output.GetComponent<MeshRenderer>(); renderer.sharedMaterials = result.Parts[0].Materials;
                        result.RendererSettings.ApplyTo(renderer);
                        meshes.Add(result.Meshes[0]); assetPaths.Add($"{folder}/Cubes-{count}-Mode-{mode}.asset");
                    }
                    // Colliders are authoring inputs only; they add no Player workload.
                    foreach (Collider collider in data.modes[0].GetComponentsInChildren<Collider>()) Object.DestroyImmediate(collider);
                    for (int m = 0; m < 4; m++)
                    {
                        data.rendererCounts[m] = data.modes[m].GetComponentsInChildren<MeshRenderer>(true).Length;
                        foreach (MeshFilter filter in data.modes[m].GetComponentsInChildren<MeshFilter>(true))
                        {
                            data.vertexCounts[m] += filter.sharedMesh.vertexCount;
                            for (int s = 0; s < filter.sharedMesh.subMeshCount; s++) data.triangleCounts[m] += (long)filter.sharedMesh.GetIndexCount(s) / 3;
                        }
                    }
                    cases[c] = data; foreach (GameObject mode in data.modes) mode.SetActive(false);
                }
                var workload = root.AddComponent<OptimizeToolScaleWorkload>(); workload.Configure(camera, cases, material);
                if (denseCube != null) { meshes.Add(denseCube); assetPaths.Add(folder + "/DenseCube.asset"); }
                MeshAssetTransaction.Save(meshes, assetPaths, true, "Build scale benchmark", saved =>
                {
                    for (int i = 0; i < outputs.Count; i++) outputs[i].sharedMesh = saved[i];
                    if (denseCube != null) foreach (MeshFilter filter in denseSources) filter.sharedMesh = saved[saved.Length - 1];
                    foreach (GameObject obj in scene.GetRootGameObjects()) SceneManager.MoveGameObjectToScene(obj, target);
                    SceneManager.SetActiveScene(target);
                    RenderSettings.ambientMode = AmbientMode.Flat; RenderSettings.ambientLight = Color.gray;
                    if (!EditorSceneManager.SaveScene(target, path)) throw new InvalidOperationException("Cannot save the scale benchmark scene.");
                }, new[] { path });
                AssetDatabase.SaveAssetIfDirty(material);
                Debug.Log("Built scale benchmark: " + string.Join(", ", cases.Select(c => c.cubeCount)) + " cubes.");
            }
            finally
            {
                foreach (MeshBuildResult result in results) result.Dispose();
                if (denseCube != null) Object.DestroyImmediate(denseCube);
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                EditorSceneManager.ClosePreviewScene(scene);
                EditorSceneManager.CloseScene(target, true);
                AssetDatabase.DeleteAsset(stagingPath);
            }
        }

        private static GameObject Create(Scene scene, string name, params Type[] components)
        {
            var obj = new GameObject(name, components); SceneManager.MoveGameObjectToScene(obj, scene); return obj;
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            int split = folder.LastIndexOf('/'); EnsureFolder(folder.Substring(0, split));
            AssetDatabase.CreateFolder(folder.Substring(0, split), folder.Substring(split + 1));
        }
    }
}
#endif
