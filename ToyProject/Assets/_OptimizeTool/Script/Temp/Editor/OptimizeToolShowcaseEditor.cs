#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace Common.OptimizeTool.Samples.Editor
{
    /// <summary>
    /// OptimizeTool 쇼케이스용 결과 Mesh와 PhysicsRecorder 미리보기 자산을 명시적으로 생성합니다.
    /// </summary>
    public static class OptimizeToolShowcaseEditor
    {
        private const string OutputFolder = "Assets/_OptimizeTool/Settings/Generated";
        private const string CombinedMeshPath = OutputFolder + "/OptimizeToolSampleCombined.asset";
        private const string BackfaceMeshPath = OutputFolder + "/OptimizeToolSampleBackface.asset";
        private const string OcclusionMeshPath = OutputFolder + "/OptimizeToolSampleOcclusion.asset";
        private const string RecordedClipPath = OutputFolder + "/OptimizeToolSampleRecorded.anim";
        private const string RecordedPrefabPath = OutputFolder + "/OptimizeToolSampleRecorded.prefab";

        [MenuItem("Tools/OptimizeTool/Build Showcase Assets")]
        public static void BuildShowcaseAssets()
        {
            if (!AssetDatabase.IsValidFolder(OutputFolder))
                throw new InvalidOperationException($"Required output folder is missing: {OutputFolder}");

            BuildMeshes();
            BuildRecordedPreview();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            Debug.Log("OptimizeTool showcase assets are ready.");
        }

        private static void BuildMeshes()
        {
            GameObject original = GetSelectedOriginalMeshes();
            MeshFilter[] filters = original.GetComponentsInChildren<MeshFilter>(true);
            if (filters.Length != 9)
                throw new InvalidOperationException("OptimizeTool showcase requires exactly 9 original MeshFilters.");

            Mesh combined = AssetDatabase.LoadAssetAtPath<Mesh>(CombinedMeshPath);
            if (combined == null)
            {
                combined = Combine(filters, "OptimizeToolSampleCombined");
                AssetDatabase.CreateAsset(combined, CombinedMeshPath);
            }
            CreateMeshIfMissing(BackfaceMeshPath, UnityEngine.Object.Instantiate(combined));
            CreateMeshIfMissing(OcclusionMeshPath, UnityEngine.Object.Instantiate(combined));
        }

        private static void BuildRecordedPreview()
        {
            AnimationClip existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(RecordedClipPath);
            if (existing == null)
            {
                existing = new AnimationClip { name = "OptimizeToolSampleRecorded" };
                AnimationCurve curve = AnimationCurve.Linear(0f, 0f, 1f, 1f);
                existing.SetCurve(string.Empty, typeof(Transform), "localPosition.x", curve);
                AssetDatabase.CreateAsset(existing, RecordedClipPath);
            }

            if (AssetDatabase.LoadAssetAtPath<GameObject>(RecordedPrefabPath) != null)
                return;

            GameObject original = GetSelectedOriginalMeshes();
            GameObject preview = null;
            try
            {
                preview = UnityEngine.Object.Instantiate(original);
                preview.name = "OptimizeToolSampleRecorded";
                Animation animation = preview.AddComponent<Animation>();
                animation.AddClip(existing, existing.name);

                // Keep a configured PhysicsRecorder on the preview asset as an explicit,
                // editor-generated record target. It is never instantiated by Play Mode.
                PhysicsRecorder recorder = preview.AddComponent<PhysicsRecorder>();
                SerializedObject serializedRecorder = new SerializedObject(recorder);
                serializedRecorder.FindProperty("_root").objectReferenceValue = preview.transform;
                serializedRecorder.FindProperty("_recordInterval").floatValue = 0.02f;
                serializedRecorder.FindProperty("_savePath").stringValue = OutputFolder;
                serializedRecorder.FindProperty("_animationFileName").stringValue = existing.name;
                serializedRecorder.FindProperty("_overwriteExistingAssets").boolValue = false;
                serializedRecorder.ApplyModifiedPropertiesWithoutUndo();

                GameObject savedPrefab = PrefabUtility.SaveAsPrefabAsset(preview, RecordedPrefabPath);
                if (savedPrefab == null || AssetDatabase.LoadAssetAtPath<GameObject>(RecordedPrefabPath) == null)
                    throw new InvalidOperationException($"Unable to save recorded preview prefab: {RecordedPrefabPath}");
            }
            finally
            {
                if (preview != null)
                    UnityEngine.Object.DestroyImmediate(preview);
            }
        }

        private static Mesh Combine(MeshFilter[] filters, string name)
        {
            CombineInstance[] instances = new CombineInstance[filters.Length];
            Transform root = filters[0].transform.root;
            for (int i = 0; i < filters.Length; i++)
            {
                if (filters[i] == null || filters[i].sharedMesh == null)
                    throw new InvalidOperationException($"MeshFilter {i} is missing a readable Mesh.");
                instances[i] = new CombineInstance
                {
                    mesh = filters[i].sharedMesh,
                    transform = root.worldToLocalMatrix * filters[i].transform.localToWorldMatrix
                };
            }

            Mesh combined = new Mesh { name = name };
            combined.CombineMeshes(instances, true, true, false);
            if (combined.vertexCount == 0)
                throw new InvalidOperationException("Mesh combination produced no vertices.");
            return combined;
        }

        private static void CreateMeshIfMissing(string path, Mesh mesh)
        {
            if (AssetDatabase.LoadAssetAtPath<Mesh>(path) != null)
            {
                UnityEngine.Object.DestroyImmediate(mesh);
                return;
            }
            AssetDatabase.CreateAsset(mesh, path);
        }

        private static GameObject GetSelectedOriginalMeshes()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null || selected.name != "OriginalMeshes")
                throw new InvalidOperationException("Select the OriginalMeshes GameObject before building showcase assets.");
            if (selected.GetComponentsInChildren<MeshFilter>(true).Length != 9)
                throw new InvalidOperationException("Selected OriginalMeshes must contain exactly nine MeshFilters.");
            return selected;
        }
    }
}
#endif
