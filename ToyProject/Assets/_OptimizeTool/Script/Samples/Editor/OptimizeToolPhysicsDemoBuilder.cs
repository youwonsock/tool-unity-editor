#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Common.OptimizeTool.Samples.Editor
{
    public static class OptimizeToolPhysicsDemoBuilder
    {
        private const string MaterialFolder = "Assets/_OptimizeTool/Settings/PhysicsDemo";

        public static readonly IReadOnlyList<string> OutputAssetPaths = Array.AsReadOnly(new[]
        {
            MaterialFolder + "/PhysicsDemoBody.mat", MaterialFolder + "/PhysicsDemoFloor.mat",
            MaterialFolder + "/PhysicsDemoRamp.mat", MaterialFolder + "/PhysicsDemoSurface.physicMaterial"
        });

        [MenuItem("Tools/OptimizeTool/Build Physics Recorder Demo")]
        public static void BuildPhysicsDemo()
        {
            MeshProcessingUtility.RequireEditMode();
            GameObject root = PrefabUtility.LoadPrefabContents(OptimizeToolShowcaseEditor.PrefabPath);
            try
            {
                var paths = new List<string>(OutputAssetPaths) { OptimizeToolShowcaseEditor.PrefabPath };
                AssetFileTransaction.Execute(paths, () =>
                {
                    GameObject group = BuildContents(root);
                    WireOverview(root, group);
                    if (PrefabUtility.SaveAsPrefabAsset(root, OptimizeToolShowcaseEditor.PrefabPath) == null)
                        throw new InvalidOperationException("Unable to save the Physics Recorder demo.");
                });
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
            Debug.Log("Physics Recorder demo is ready. Open OptimizeToolSample, Play, select PHYSICS and press RECORD.");
        }

        public static GameObject BuildContents(GameObject root)
        {
            EnsureFolder(MaterialFolder);
            Material bodyMaterial = GetMaterial("PhysicsDemoBody", new Color(1f, 0.61f, 0.15f));
            Material floorMaterial = GetMaterial("PhysicsDemoFloor", new Color(0.12f, 0.2f, 0.29f));
            Material rampMaterial = GetMaterial("PhysicsDemoRamp", new Color(0.3f, 0.43f, 0.54f));
            string physicsPath = MaterialFolder + "/PhysicsDemoSurface.physicMaterial";
            PhysicMaterial physics = AssetDatabase.LoadAssetAtPath<PhysicMaterial>(physicsPath);
            if (physics == null)
            {
                physics = new PhysicMaterial("PhysicsDemoSurface") { dynamicFriction = 0.5f, staticFriction = 0.6f, bounciness = 0.3f };
                AssetDatabase.CreateAsset(physics, physicsPath);
            }
            Transform previous = root.transform.Find("PhysicsRecorderDemo");
            if (previous != null) Object.DestroyImmediate(previous.gameObject);
            GameObject group = Child(root.transform, "PhysicsRecorderDemo");
            group.SetActive(false);
            GameObject left = Child(group.transform, "Live Physics");
            left.transform.localPosition = new Vector3(-3.4f, 0.05f, 0f);
            GameObject right = Child(group.transform, "Recorded Playback");
            right.transform.localPosition = new Vector3(3.4f, 0.05f, 0f);
            BuildArena(left.transform, floorMaterial, rampMaterial, physics, true);
            BuildArena(right.transform, floorMaterial, rampMaterial, physics, false);
            GameObject source = Child(left.transform, "Recorded Bodies");
            Vector3[] positions =
            {
                new Vector3(-0.95f, 2.7f, -0.3f), new Vector3(0.15f, 3.5f, 0.2f),
                new Vector3(0.9f, 4.4f, 0.1f), new Vector3(-0.6f, 5.2f, 0.2f),
                new Vector3(0.7f, 2.9f, -0.65f), new Vector3(-0.2f, 4.5f, -0.7f),
            };
            for (int i = 0; i < positions.Length; i++)
            {
                GameObject body = Primitive(source.transform, i == 3 ? PrimitiveType.Sphere : PrimitiveType.Cube,
                    "Body_" + (i + 1).ToString("00"), positions[i], Vector3.one * (i == 3 ? 0.9f : 0.75f), bodyMaterial);
                body.transform.localRotation = Quaternion.Euler(8f * (i % 2), 13f * i, 5f * (i % 3 - 1));
                body.GetComponent<Collider>().sharedMaterial = physics;
                Rigidbody rb = body.AddComponent<Rigidbody>();
                rb.mass = 1f;
                rb.isKinematic = true;
                rb.useGravity = true;
                rb.interpolation = RigidbodyInterpolation.None;
                rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
            }
            Transform view = root.transform.Find("Physics Demo View");
            if (view == null) view = Child(root.transform, "Physics Demo View").transform;
            view.localPosition = new Vector3(0f, 7.1f, -14f);
            view.localRotation = Quaternion.LookRotation(new Vector3(0f, 2.8f, 0f) - view.localPosition);
            Label(left.transform, "RECORDED ORIGINAL", new Color(1f, 0.74f, 0.3f), view.localRotation);
            Label(right.transform, "RECORDED PREFAB", new Color(0.25f, 0.85f, 1f), view.localRotation);

            PhysicsRecorder recorder = Child(group.transform, "Recorder").AddComponent<PhysicsRecorder>();
            var recorderSettings = new SerializedObject(recorder);
            recorderSettings.FindProperty("_root").objectReferenceValue = source.transform;
            recorderSettings.FindProperty("_savePath").stringValue = OptimizeToolPhysicsDemo.RecordingFolder;
            recorderSettings.FindProperty("_animationFileName").stringValue = OptimizeToolPhysicsDemo.RecordingName;
            recorderSettings.FindProperty("_recordInterval").floatValue = 0.02f;
            recorderSettings.FindProperty("_recordOnlyIfMoved").boolValue = true;
            recorderSettings.FindProperty("_overwriteExistingAssets").boolValue = true;
            recorderSettings.ApplyModifiedPropertiesWithoutUndo();
            OptimizeToolPhysicsDemo demo = root.GetComponent<OptimizeToolPhysicsDemo>();
            if (demo == null) demo = root.AddComponent<OptimizeToolPhysicsDemo>();
            var settings = new SerializedObject(demo);
            settings.FindProperty("_demoRoot").objectReferenceValue = group;
            settings.FindProperty("_sourceRoot").objectReferenceValue = source.transform;
            settings.FindProperty("_replayAnchor").objectReferenceValue = right.transform;
            settings.FindProperty("_view").objectReferenceValue = view;
            settings.FindProperty("_recorder").objectReferenceValue = recorder;
            settings.FindProperty("_savedPrefab").objectReferenceValue = AssetDatabase.LoadAssetAtPath<GameObject>(OptimizeToolPhysicsDemo.RecordingPrefabPath);
            settings.FindProperty("_recordingReference").objectReferenceValue = AssetDatabase.LoadAssetAtPath<PhysicsRecordingReference>(
                PhysicsRecordingReference.AssetPathForClip(OptimizeToolPhysicsDemo.RecordingFolder + "/" + OptimizeToolPhysicsDemo.RecordingName + ".anim"));
            settings.FindProperty("_duration").floatValue = 3.2f;
            settings.FindProperty("_releaseDelay").floatValue = 0.4f;
            settings.ApplyModifiedPropertiesWithoutUndo();
            return group;
        }

        private static void WireOverview(GameObject root, GameObject group)
        {
            var controller = new SerializedObject(root.GetComponent<OptimizeToolOverviewController>());
            SerializedProperty groups = controller.FindProperty("_featureGroups");
            SerializedProperty names = controller.FindProperty("_featureNames");
            groups.arraySize = OptimizeToolSampleModes.Count;
            names.arraySize = OptimizeToolSampleModes.Count;
            groups.GetArrayElementAtIndex(OptimizeToolSampleModes.Physics).objectReferenceValue = group;
            names.GetArrayElementAtIndex(OptimizeToolSampleModes.Physics).stringValue = OptimizeToolSampleModes.All[OptimizeToolSampleModes.Physics].Name;
            controller.FindProperty("_physicsDemo").objectReferenceValue = root.GetComponent<OptimizeToolPhysicsDemo>();
            controller.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void BuildArena(Transform parent, Material floor, Material ramp, PhysicMaterial surface, bool colliders)
        {
            GameObject platform = Primitive(parent, PrimitiveType.Cube, "Platform", new Vector3(0f, -0.25f, 0f), new Vector3(5.8f, 0.5f, 5f), floor);
            GameObject slope = Primitive(parent, PrimitiveType.Cube, "Ramp", new Vector3(0f, 1.1f, 0.5f), new Vector3(3.8f, 0.22f, 1.4f), ramp);
            slope.transform.localRotation = Quaternion.Euler(0f, 0f, 14f);
            foreach (GameObject obj in new[] { platform, slope })
            {
                if (colliders) obj.GetComponent<Collider>().sharedMaterial = surface;
                else Object.DestroyImmediate(obj.GetComponent<Collider>());
            }
        }

        private static GameObject Primitive(Transform parent, PrimitiveType type, string name, Vector3 position, Vector3 scale, Material material)
        {
            GameObject obj = GameObject.CreatePrimitive(type);
            obj.name = name;
            obj.transform.SetParent(parent, false);
            obj.transform.localPosition = position;
            obj.transform.localScale = scale;
            obj.GetComponent<Renderer>().sharedMaterial = material;
            return obj;
        }

        private static void Label(Transform parent, string text, Color color, Quaternion rotation)
        {
            GameObject label = Child(parent, text);
            label.transform.localPosition = new Vector3(0f, 6.15f, 0.3f);
            label.transform.localRotation = rotation;
            TextMesh mesh = label.AddComponent<TextMesh>();
            mesh.text = text;
            mesh.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            mesh.fontSize = 48;
            mesh.characterSize = 0.08f;
            mesh.anchor = TextAnchor.MiddleCenter;
            mesh.alignment = TextAlignment.Center;
            mesh.color = color;
            label.GetComponent<Renderer>().sharedMaterial = mesh.font.material;
        }

        private static GameObject Child(Transform parent, string name)
        {
            var obj = new GameObject(name);
            obj.transform.SetParent(parent, false);
            return obj;
        }

        private static Material GetMaterial(string name, Color color)
        {
            string path = MaterialFolder + "/" + name + ".mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null) return material;
            Material source = AssetDatabase.LoadAssetAtPath<Material>("Assets/_OptimizeTool/Settings/OptimizeToolSource.mat");
            if (source == null) throw new InvalidOperationException("The showcase source material is missing.");
            material = new Material(source) { name = name };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            int split = path.LastIndexOf('/');
            EnsureFolder(path.Substring(0, split));
            AssetDatabase.CreateFolder(path.Substring(0, split), path.Substring(split + 1));
        }
    }
}
#endif
