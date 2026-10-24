#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Common.OptimizeTool.Samples.Editor
{
    public static class OptimizeToolShowcaseEditor
    {
        public const string PrefabPath = "Assets/_OptimizeTool/Prefab/OptimizeToolShowcase.prefab";
        public const string OutputFolder = "Assets/_OptimizeTool/Settings/Generated";
        private static readonly string[] MeshNames =
        { "OptimizeToolSampleCombined", "OptimizeToolSampleBackface", "OptimizeToolSampleOcclusion" };

        [MenuItem("Tools/OptimizeTool/Build Showcase Assets")]
        public static void BuildShowcaseAssets() => BuildShowcaseAssets(PrefabPath, OutputFolder);

        public static void BuildShowcaseAssets(string prefabPath, string outputFolder)
        {
            MeshProcessingUtility.RequireEditMode();
            MeshAssetTransaction.ValidateFolder(outputFolder);
            GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
            var results = new List<MeshBuildResult>();
            GameObject cameraObject = null;
            try
            {
                Transform original = root.transform.Find("OriginalMeshes");
                if (original == null) throw new InvalidOperationException("The showcase needs OriginalMeshes.");
                original.localPosition = Vector3.zero;
                original.localRotation = Quaternion.identity;
                original.localScale = Vector3.one;
                MeshFilter[] filters = original.GetComponentsInChildren<MeshFilter>(true);
                Array.Sort(filters, (a, b) => string.CompareOrdinal(a.name, b.name));
                if (filters.Length != 9) throw new InvalidOperationException("The showcase requires nine source cubes.");
                for (int i = 0; i < filters.Length; i++)
                {
                    int depth = i / 3, lane = i % 3 - 1;
                    filters[i].transform.localPosition = new Vector3(lane * (1.5f + depth * 0.375f), 0.5f, -2f + depth * 2f);
                    filters[i].transform.localRotation = Quaternion.identity;
                    filters[i].transform.localScale = Vector3.one;
                    filters[i].GetComponent<MeshRenderer>().enabled = true;
                    if (filters[i].GetComponent<BoxCollider>() == null) filters[i].gameObject.AddComponent<BoxCollider>();
                }
                Transform bakeView = root.transform.Find("Bake View");
                if (bakeView == null)
                {
                    bakeView = new GameObject("Bake View").transform;
                    bakeView.SetParent(root.transform, false);
                }
                bakeView.localPosition = new Vector3(0f, 0.5f, -10f);
                bakeView.localRotation = Quaternion.identity;
                cameraObject = new GameObject("Temporary Bake Camera", typeof(Camera));
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(cameraObject, root.scene);
                cameraObject.transform.SetPositionAndRotation(bakeView.position, bakeView.rotation);
                Camera camera = cameraObject.GetComponent<Camera>();
                camera.enabled = false;
                camera.fieldOfView = 60f;
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 100f;
                for (int mode = 0; mode < MeshNames.Length; mode++)
                    results.Add(MeshBuildPipeline.Build(new MeshBuildRequest(filters, original.worldToLocalMatrix,
                        camera, allowReadableCopy: true, singleMesh: true, backfaces: mode == 1, occlusion: mode == 2)));
                if (TriangleCount(results[1].Meshes[0]) >= TriangleCount(results[0].Meshes[0])
                    || results[2].CulledObjectCount != 6)
                    throw new InvalidOperationException("The showcase must remove backfaces and six occluded cubes.");
                var meshes = new List<Mesh>();
                var paths = new List<string>();
                for (int i = 0; i < results.Count; i++)
                { meshes.Add(results[i].Meshes[0]); paths.Add(outputFolder + "/" + MeshNames[i] + ".asset"); }
                // Update all three real outputs on every build, preserving their existing GUIDs.
                MeshAssetTransaction.Save(meshes, paths, true, "Build OptimizeTool showcase", saved =>
                {
                    var groups = new GameObject[OptimizeToolSampleModes.Count];
                    groups[OptimizeToolSampleModes.Original] = original.gameObject;
                    groups[OptimizeToolSampleModes.Physics] = OptimizeToolPhysicsDemoBuilder.BuildContents(root);
                    for (int i = 0; i < MeshNames.Length; i++)
                    {
                        Transform group = root.transform.Find(OptimizeToolSampleModes.All[i + 1].Group);
                        if (group == null) throw new InvalidOperationException($"Missing sample group: {OptimizeToolSampleModes.All[i + 1].Group}");
                        MeshFilter[] outputs = group.GetComponentsInChildren<MeshFilter>(true);
                        if (outputs.Length != 1) throw new InvalidOperationException("Each result group must have one MeshFilter.");
                        group.localPosition = Vector3.zero;
                        group.localRotation = Quaternion.identity;
                        group.localScale = Vector3.one;
                        outputs[0].transform.localPosition = Vector3.zero;
                        outputs[0].transform.localRotation = Quaternion.identity;
                        outputs[0].transform.localScale = Vector3.one;
                        outputs[0].sharedMesh = saved[i];
                        MeshRenderer renderer = outputs[0].GetComponent<MeshRenderer>();
                        renderer.sharedMaterials = results[i].Parts[0].Materials;
                        results[i].RendererSettings.ApplyTo(renderer);
                        group.gameObject.SetActive(false);
                        groups[i + 1] = group.gameObject;
                    }
                    original.gameObject.SetActive(true);
                    var controller = new SerializedObject(root.GetComponent<OptimizeToolOverviewController>());
                    SerializedProperty featureGroups = controller.FindProperty("_featureGroups");
                    featureGroups.arraySize = groups.Length;
                    for (int i = 0; i < groups.Length; i++) featureGroups.GetArrayElementAtIndex(i).objectReferenceValue = groups[i];
                    SerializedProperty names = controller.FindProperty("_featureNames");
                    names.arraySize = OptimizeToolSampleModes.Count;
                    for (int i = 0; i < names.arraySize; i++) names.GetArrayElementAtIndex(i).stringValue = OptimizeToolSampleModes.All[i].Name;
                    controller.FindProperty("_bakeView").objectReferenceValue = bakeView;
                    controller.FindProperty("_physicsDemo").objectReferenceValue = root.GetComponent<OptimizeToolPhysicsDemo>();
                    controller.ApplyModifiedPropertiesWithoutUndo();
                    if (PrefabUtility.SaveAsPrefabAsset(root, prefabPath) == null)
                        throw new InvalidOperationException("Could not save the showcase prefab.");
                }, new List<string>(OptimizeToolPhysicsDemoBuilder.OutputAssetPaths) { prefabPath });
                Debug.Log($"OptimizeTool showcase rebuilt using MeshCombiner: combined {TriangleCount(meshes[0])} triangles, "
                    + $"backface {TriangleCount(meshes[1])}, occlusion {TriangleCount(meshes[2])} (6 objects removed).");
            }
            finally
            {
                foreach (MeshBuildResult result in results) result.Dispose();
                if (cameraObject != null) UnityEngine.Object.DestroyImmediate(cameraObject);
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static ulong TriangleCount(Mesh mesh)
        {
            ulong indices = 0;
            for (int i = 0; i < mesh.subMeshCount; i++) indices += mesh.GetIndexCount(i);
            return indices / 3;
        }
    }
}
#endif
