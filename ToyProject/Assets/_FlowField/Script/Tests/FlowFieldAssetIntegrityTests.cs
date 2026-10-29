using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Common.FlowField.Tests
{
    public sealed class FlowFieldAssetIntegrityTests
    {
        [Test]
        public void SerializedAssetGuidsResolve()
        {
            var missing = new List<string>();
            int references = 0;
            foreach (string path in AssetDatabase.GetAllAssetPaths())
            {
                if (!path.StartsWith("Assets/_FlowField/", StringComparison.Ordinal)) continue;
                string ext = Path.GetExtension(path);
                if (ext != ".unity" && ext != ".prefab" && ext != ".asset" && ext != ".mat" && ext != ".physicMaterial") continue;
                foreach (Match m in Regex.Matches(File.ReadAllText(path), @"guid: ([a-fA-F0-9]{32})"))
                {
                    string guid = m.Groups[1].Value;
                    references++;
                    if (guid.StartsWith("0000000000000000", StringComparison.Ordinal)) continue;
                    if (string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(guid))) missing.Add(path + " -> " + guid);
                }
            }
            Assert.That(references, Is.GreaterThan(0));
            Assert.That(missing, Is.Empty);
            TestContext.WriteLine("Resolved serialized GUID references: " + references);
        }

        [Test]
        public void BothCurrentBakeAssetsUseV6AndValidateTheirPayloads()
        {
            int count = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:FlowFieldStaticBakeData", new[] { "Assets/_FlowField" }))
            {
                var data = AssetDatabase.LoadAssetAtPath<FlowFieldStaticBakeData>(AssetDatabase.GUIDToAssetPath(guid));
                Assert.That(data.FormatVersion, Is.EqualTo(6));
                Assert.That(data.Validate(out string reason), Is.True, reason);
                count++;
            }
            foreach (string guid in AssetDatabase.FindAssets("t:FlowFieldVolumeBakeData", new[] { "Assets/_FlowField" }))
            {
                var data = AssetDatabase.LoadAssetAtPath<FlowFieldVolumeBakeData>(AssetDatabase.GUIDToAssetPath(guid));
                Assert.That(data.FormatVersion, Is.EqualTo(6));
                Assert.That(data.TryGetValidationView(out var view), Is.True);
                var grid = FlowFieldGridSpace.FromVolumeCellGrid(view.Origin, view.Width, view.Height, view.Depth, view.CellSize);
                Assert.That(data.Matches(grid, new Bounds(view.BoundsCenter, view.BoundsSize), view.ObstacleLayerMask,
                    view.ObstacleClearance, out string reason), Is.True, reason);
                count++;
            }
            Assert.That(count, Is.EqualTo(2));
        }

        [Test]
        public void SavedScenesAndPrefabsHaveNoMissingScriptsOrBrokenObjectReferences()
        {
            var missing = new List<string>();
            var setup = EditorSceneManager.GetSceneManagerSetup();
            int scenes = 0;
            int prefabs = 0;
            try
            {
                foreach (string guid in AssetDatabase.FindAssets("t:Scene", new[] { "Assets/_FlowField" }))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                    foreach (GameObject root in scene.GetRootGameObjects()) Inspect(root, path, missing);
                    scenes++;
                }
                foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/_FlowField" }))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    var root = PrefabUtility.LoadPrefabContents(path);
                    try { Inspect(root, path, missing); }
                    finally { PrefabUtility.UnloadPrefabContents(root); }
                    prefabs++;
                }
            }
            finally
            {
                bool canRestore = false;
                foreach (var item in setup)
                    if (item.isActive && item.isLoaded && !string.IsNullOrEmpty(item.path)) canRestore = true;
                if (canRestore) EditorSceneManager.RestoreSceneManagerSetup(setup);
                else EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
            Assert.That(scenes, Is.EqualTo(2));
            Assert.That(prefabs, Is.EqualTo(2));
            Assert.That(missing, Is.Empty);
        }

        [Test]
        public void FlowFieldSerializedFilesHaveNoRemovedOrUnknownFields()
        {
            var unknown = new List<string>();
            int components = 0;
            foreach (string path in AssetDatabase.GetAllAssetPaths())
            {
                if (!path.StartsWith("Assets/_FlowField/", StringComparison.Ordinal)) continue;
                string ext = Path.GetExtension(path);
                if (ext != ".unity" && ext != ".prefab" && ext != ".asset") continue;
                foreach (string block in Regex.Split(File.ReadAllText(path), @"(?m)^--- !u!\d+.*$"))
                {
                    var script = Regex.Match(block, @"m_Script: \{fileID: \d+, guid: ([a-fA-F0-9]{32})");
                    if (!script.Success) continue;
                    string scriptPath = AssetDatabase.GUIDToAssetPath(script.Groups[1].Value);
                    var mono = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
                    Type type = mono != null ? mono.GetClass() : null;
                    if (type == null || type.Namespace == null || !type.Namespace.StartsWith("Common.FlowField", StringComparison.Ordinal)) continue;
                    var fields = new HashSet<string>();
                    for (Type current = type; current != null && current != typeof(Object); current = current.BaseType)
                        foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                            if (!field.IsStatic && !field.IsInitOnly && !field.IsNotSerialized
                                && (field.IsPublic || field.GetCustomAttribute<SerializeField>() != null || field.GetCustomAttribute<SerializeReference>() != null))
                                fields.Add(field.Name);
                    foreach (Match field in Regex.Matches(block, @"(?m)^  ([A-Za-z_]\w*):"))
                    {
                        string name = field.Groups[1].Value;
                        if (!name.StartsWith("m_", StringComparison.Ordinal) && !fields.Contains(name))
                            unknown.Add(path + " : " + type.Name + "." + name);
                    }
                    components++;
                }
            }
            Assert.That(components, Is.GreaterThan(0));
            Assert.That(unknown, Is.Empty);
            TestContext.WriteLine("Inspected serialized FlowField components/assets: " + components);
        }

        private static void Inspect(GameObject root, string path, List<string> missing)
        {
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
            {
                int count = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(transform.gameObject);
                if (count != 0) missing.Add(path + ": " + transform.name + " missing scripts=" + count);
                foreach (Component component in transform.GetComponents<Component>())
                {
                    if (component == null) continue;
                    using (var serialized = new SerializedObject(component))
                    {
                        var property = serialized.GetIterator();
                        while (property.Next(true))
                            if (property.propertyType == SerializedPropertyType.ObjectReference
                                && property.objectReferenceValue == null && property.objectReferenceInstanceIDValue != 0)
                                missing.Add(path + ": " + transform.name + "." + property.propertyPath);
                    }
                }
            }
        }
    }
}
