using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Common.OptimizeTool.Tests
{
    public sealed class MeshRefactoringTests
    {
        private Scene _scene;
        private string _folder;
        private readonly List<Object> _owned = new List<Object>();
        private Material _material;

        [SetUp]
        public void SetUp()
        {
            _scene = EditorSceneManager.NewPreviewScene();
            _folder = "Assets/__MeshRefactoring_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(_folder));
            _material = Own(new Material(Shader.Find("Universal Render Pipeline/Lit")));
        }
        [TearDown]
        public void TearDown()
        {
            Undo.ClearAll();
            if (_scene.IsValid()) EditorSceneManager.ClosePreviewScene(_scene);
            foreach (Object item in _owned) if (item != null) Object.DestroyImmediate(item);
            _owned.Clear();
            AssetDatabase.DeleteAsset(_folder);
        }
        private T Own<T>(T item) where T : Object { _owned.Add(item); return item; }
        private GameObject Create(string name)
        {
            var go = new GameObject(name); SceneManager.MoveGameObjectToScene(go, _scene); return go;
        }
        private MeshFilter Source(string name)
        {
            Mesh mesh = Own(new Mesh { name = name });
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 }; mesh.RecalculateNormals();
            GameObject go = Create(name);
            go.AddComponent<MeshRenderer>().sharedMaterial = _material;
            MeshFilter filter = go.AddComponent<MeshFilter>(); filter.sharedMesh = mesh; return filter;
        }
        private MeshCombiner Combiner(params MeshFilter[] filters)
        {
            var component = Create("Combiner").AddComponent<MeshCombiner>();
            Set(component, "_targetMeshFilterArray", filters); Set(component, "_savePath", _folder);
            Set(component, "_meshName", "Combined"); Set(component, "_combineAllIntoSingleMesh", true);
            return component;
        }
        private static void Set(Object target, string field, object value) => target.GetType()
            .GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(target, value);

        [Test]
        public void InMemoryBuildDoesNotRequireSaveSettingsAndOwnsOnlyTransientOutputs()
        {
            MeshFilter source = Source("Source");
            MeshCombiner component = Combiner(source);
            Set(component, "_savePath", null); Set(component, "_meshName", null);
            MeshBuildResult result = component.BuildMeshes();
            Mesh output = result.Meshes[0];
            Assert.Throws<NotSupportedException>(() => ((IList<Mesh>)result.Meshes).Add(source.sharedMesh));
            Material[] exposed = result.Parts[0].Materials; exposed[0] = null;
            Assert.AreSame(_material, result.Parts[0].Materials[0]);
            result.Dispose(); result.Dispose();
            Assert.IsTrue(output == null); Assert.IsTrue(source.sharedMesh != null);
            Assert.IsTrue(_material != null); Assert.IsEmpty(result.Parts); Assert.IsEmpty(result.SourceRenderers);
            Assert.IsEmpty(Directory.GetFiles(_folder, "*.asset"));
        }

        [Test]
        public void TypedRequestCopiesTheSourceListAndMatchesTheComponent()
        {
            MeshFilter source = Source("Source"); var inputs = new[] { source };
            var request = new MeshBuildRequest(inputs, Matrix4x4.identity, singleMesh: true);
            inputs[0] = null;
            using (MeshBuildResult typed = MeshBuildPipeline.Build(request))
            using (MeshBuildResult component = Combiner(source).BuildMeshes())
            {
                CollectionAssert.AreEqual(component.Meshes[0].vertices, typed.Meshes[0].vertices);
                CollectionAssert.AreEqual(component.Meshes[0].triangles, typed.Meshes[0].triangles);
            }
        }

        [TestCase("layer")]
        [TestCase("shadows")]
        [TestCase("sorting")]
        [TestCase("renderingLayers")]
        [TestCase("probeAnchor")]
        public void IncompatibleRendererSettingsFailBeforeSavingOrDisablingSources(string setting)
        {
            MeshFilter a = Source("A"), b = Source("B"); MeshRenderer renderer = b.GetComponent<MeshRenderer>();
            switch (setting)
            {
                case "layer": b.gameObject.layer = 2; break;
                case "shadows": renderer.shadowCastingMode = ShadowCastingMode.Off; break;
                case "sorting": renderer.sortingOrder = 4; break;
                case "renderingLayers": renderer.renderingLayerMask = 8; break;
                case "probeAnchor": renderer.probeAnchor = b.transform; break;
            }
            Assert.Throws<ArgumentException>(() => Combiner(a, b).CombineAndSaveMeshes());
            Assert.IsTrue(a.GetComponent<MeshRenderer>().enabled); Assert.IsTrue(renderer.enabled);
            Assert.IsEmpty(Directory.GetFiles(_folder, "*.asset"));
        }

        [TestCase("propertyBlock")]
        [TestCase("lightmap")]
        [TestCase("streams")]
        [TestCase("lod")]
        public void UnsupportedPerRendererDataFailsBeforeSaving(string setting)
        {
            MeshFilter source = Source("Source"); MeshRenderer renderer = source.GetComponent<MeshRenderer>();
            switch (setting)
            {
                case "propertyBlock": var block = new MaterialPropertyBlock(); block.SetFloat("_Test", 1); renderer.SetPropertyBlock(block); break;
                case "lightmap": renderer.lightmapIndex = 0; break;
                case "streams": renderer.additionalVertexStreams = source.sharedMesh; break;
                case "lod": source.gameObject.AddComponent<LODGroup>(); break;
            }
            Assert.Throws<ArgumentException>(() => Combiner(source).CombineAndSaveMeshes());
            Assert.IsEmpty(Directory.GetFiles(_folder, "*.asset"));
        }

        [Test]
        public void SceneOutputPreservesSupportedSettingsAndUndoRedoRestoresSources()
        {
            MeshFilter source = Source("Source"); MeshRenderer renderer = source.GetComponent<MeshRenderer>();
            source.gameObject.layer = 2; renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false; renderer.sortingOrder = 5; renderer.renderingLayerMask = 9;
            renderer.probeAnchor = source.transform; renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            MeshCombiner combiner = Combiner(source); combiner.CombineAndSaveMeshes();
            MeshRenderer output = combiner.GetComponentInChildren<MeshRenderer>();
            Assert.AreEqual(2, output.gameObject.layer); Assert.AreEqual(ShadowCastingMode.Off, output.shadowCastingMode);
            Assert.IsFalse(output.receiveShadows); Assert.AreEqual(5, output.sortingOrder);
            Assert.AreEqual(9u, output.renderingLayerMask); Assert.AreSame(source.transform, output.probeAnchor);
            Assert.AreEqual(renderer.motionVectorGenerationMode, output.motionVectorGenerationMode);
            Assert.IsFalse(renderer.enabled);
            Undo.PerformUndo(); Assert.IsTrue(renderer.enabled); Assert.IsNull(combiner.GetComponentInChildren<MeshRenderer>());
            Undo.PerformRedo(); Assert.IsFalse(renderer.enabled); Assert.NotNull(combiner.GetComponentInChildren<MeshRenderer>());
        }

        [Test]
        public void RebuildRestoresRemovedSourcesAndOptionChangesWithUndoRedo()
        {
            MeshFilter a = Source("A"), b = Source("B");
            MeshRenderer ar = a.GetComponent<MeshRenderer>(), br = b.GetComponent<MeshRenderer>();
            MeshCombiner combiner = Combiner(a, b);
            Set(combiner, "_overwriteExistingAssets", true);
            combiner.CombineAndSaveMeshes();
            Set(combiner, "_targetMeshFilterArray", new[] { a });
            combiner.CombineAndSaveMeshes();
            Assert.IsFalse(ar.enabled); Assert.IsTrue(br.enabled);
            Assert.AreEqual(1, new SerializedObject(combiner).FindProperty("_sourceRendererStates").arraySize);
            Assert.IsTrue(new SerializedObject(combiner).FindProperty("_sourceRendererStates").GetArrayElementAtIndex(0).FindPropertyRelative("Enabled").boolValue, "New ownership must remember the restored state.");
            Undo.PerformUndo(); Assert.IsFalse(ar.enabled); Assert.IsFalse(br.enabled);
            Undo.PerformRedo(); Assert.IsFalse(ar.enabled); Assert.IsTrue(br.enabled);
            Assert.IsTrue(new SerializedObject(combiner).FindProperty("_sourceRendererStates").GetArrayElementAtIndex(0).FindPropertyRelative("Enabled").boolValue, "Redo must preserve ownership's original state.");
            Set(combiner, "_disableSourceRenderers", false);
            combiner.CombineAndSaveMeshes();
            Assert.IsTrue(ar.enabled); Assert.IsTrue(br.enabled);
            Undo.PerformUndo(); Assert.IsFalse(ar.enabled); Assert.IsTrue(br.enabled);
            Undo.PerformRedo(); Assert.IsTrue(ar.enabled); Assert.IsTrue(br.enabled);
        }

        [Test]
        public void RebuildKeepsOriginallyDisabledSourcesDisabled()
        {
            MeshFilter a = Source("A"), b = Source("B");
            b.GetComponent<MeshRenderer>().enabled = false;
            MeshCombiner combiner = Combiner(a, b);
            Set(combiner, "_overwriteExistingAssets", true);
            combiner.CombineAndSaveMeshes();
            Set(combiner, "_disableSourceRenderers", false);
            combiner.CombineAndSaveMeshes();
            Assert.IsTrue(a.GetComponent<MeshRenderer>().enabled);
            Assert.IsFalse(b.GetComponent<MeshRenderer>().enabled);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RepeatedRebuildUndoRedoPreservesOriginsAndManualRendererChanges(bool manuallyEnable)
        {
            MeshFilter a = Source("A"), b = Source("B");
            Renderer ar = a.GetComponent<Renderer>(), br = b.GetComponent<Renderer>();
            br.enabled = false;
            MeshCombiner combiner = Combiner(a, b);
            Set(combiner, "_overwriteExistingAssets", true);
            Mesh original = combiner.CombineAndSaveMeshes()[0];
            string guid = AssetDatabase.AssetPathToGUID(_folder + "/Combined.asset");
            for (int i = 0; i < 3; i++)
            {
                ar.enabled = manuallyEnable;
                Assert.AreSame(original, combiner.CombineAndSaveMeshes()[0]);
                Assert.IsFalse(ar.enabled); Assert.IsFalse(br.enabled);
                Undo.PerformUndo(); Assert.AreEqual(manuallyEnable, ar.enabled); Assert.IsFalse(br.enabled);
                Undo.PerformRedo(); Assert.IsFalse(ar.enabled); Assert.IsFalse(br.enabled);
                var ledger = new SerializedObject(combiner).FindProperty("_sourceRendererStates");
                Assert.AreEqual(2, ledger.arraySize);
                Assert.IsTrue(ledger.GetArrayElementAtIndex(0).FindPropertyRelative("Enabled").boolValue);
                Assert.IsFalse(ledger.GetArrayElementAtIndex(1).FindPropertyRelative("Enabled").boolValue);
            }
            Set(combiner, "_disableSourceRenderers", false);
            combiner.CombineAndSaveMeshes();
            Assert.IsTrue(ar.enabled); Assert.IsFalse(br.enabled);
            Undo.PerformUndo(); Assert.IsFalse(ar.enabled); Assert.IsFalse(br.enabled);
            Undo.PerformRedo(); Assert.IsTrue(ar.enabled); Assert.IsFalse(br.enabled);
            Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID(_folder + "/Combined.asset"));
        }

        [Test]
        public void FailedMixedRebuildRestoresRetainedRemovedAndNewSourceStates()
        {
            MeshFilter a = Source("A"), b = Source("B"), c = Source("C");
            MeshCombiner combiner = Combiner(a, b);
            Mesh original = combiner.CombineAndSaveMeshes()[0];
            Set(combiner, "_targetMeshFilterArray", new[] { a, c });
            using (MeshBuildResult result = combiner.BuildMeshes())
                Assert.Throws<InvalidOperationException>(() => MeshAssetTransaction.Save(result.Meshes,
                    new[] { _folder + "/Combined.asset" }, true, "Fail mixed rebuild", saved =>
                    {
                        typeof(MeshCombiner).GetMethod("CreateSceneOutput", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                            .Invoke(combiner, new object[] { saved, result });
                        Assert.IsFalse(a.GetComponent<Renderer>().enabled);
                        Assert.IsTrue(b.GetComponent<Renderer>().enabled);
                        Assert.IsFalse(c.GetComponent<Renderer>().enabled);
                        throw new InvalidOperationException("Interrupted after mixed scene replacement");
                    }));
            Assert.IsFalse(a.GetComponent<Renderer>().enabled);
            Assert.IsFalse(b.GetComponent<Renderer>().enabled);
            Assert.IsTrue(c.GetComponent<Renderer>().enabled);
            Assert.AreSame(original, combiner.GetComponentInChildren<MeshFilter>().sharedMesh);
            var ledger = new SerializedObject(combiner).FindProperty("_sourceRendererStates");
            Assert.AreEqual(2, ledger.arraySize);
            Assert.AreSame(b.GetComponent<Renderer>(), ledger.GetArrayElementAtIndex(1).FindPropertyRelative("Renderer").objectReferenceValue);
            Set(combiner, "_targetMeshFilterArray", new[] { a, b });
            Set(combiner, "_overwriteExistingAssets", true);
            Set(combiner, "_disableSourceRenderers", false);
            combiner.CombineAndSaveMeshes();
            Assert.IsTrue(a.GetComponent<Renderer>().enabled); Assert.IsTrue(b.GetComponent<Renderer>().enabled);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FreshSavePersistsCompleteGeometryAndCallbackMutationsWithoutChangingStagedMesh(bool withCallback)
        {
            Mesh staged = Source("Staged").sharedMesh;
            staged.uv = new[] { Vector2.zero, Vector2.right, Vector2.up };
            staged.colors = new[] { Color.red, Color.green, Color.blue };
            staged.tangents = Enumerable.Repeat(new Vector4(1, 0, 0, 1), 3).ToArray();
            Vector3[] vertices = staged.vertices, normals = staged.normals;
            var changedBounds = new Bounds(Vector3.one, Vector3.one * 8);
            string path = _folder + "/Saved.asset";
            Action<Mesh[]> callback = withCallback ? saved =>
            {
                // Callers may import or inspect the on-disk asset while the transaction applies.
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                Mesh disk = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                Assert.AreEqual(3, disk.vertexCount); Assert.AreEqual(3u, disk.GetIndexCount(0));
                Assert.AreEqual("Saved", disk.name); Assert.IsFalse(disk.isReadable);
                CollectionAssert.AreEqual(vertices, disk.vertices);
                saved[0].name = "Callback name"; saved[0].bounds = changedBounds;
            } : (Action<Mesh[]>)null;
            MeshAssetTransaction.Save(new[] { staged }, new[] { path }, false, "Save complete mesh", callback);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            Mesh loaded = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            Assert.IsFalse(loaded.isReadable);
            Assert.AreEqual(withCallback ? "Callback name" : "Saved", loaded.name);
            CollectionAssert.AreEqual(vertices, loaded.vertices);
            CollectionAssert.AreEqual(normals, loaded.normals);
            CollectionAssert.AreEqual(staged.triangles, loaded.triangles);
            CollectionAssert.AreEqual(staged.uv, loaded.uv);
            CollectionAssert.AreEqual(staged.colors, loaded.colors);
            CollectionAssert.AreEqual(staged.tangents, loaded.tangents);
            Assert.AreEqual(withCallback ? changedBounds : staged.bounds, loaded.bounds);
            Assert.AreEqual("Staged", staged.name); Assert.IsTrue(staged.isReadable);
        }

        [Test]
        public void FailedRebuildRestoresPreviousOutputOwnershipAndMesh()
        {
            MeshFilter a = Source("A"), b = Source("B");
            MeshCombiner combiner = Combiner(a, b);
            Mesh previous = combiner.CombineAndSaveMeshes()[0];
            string guid = AssetDatabase.AssetPathToGUID(_folder + "/Combined.asset");
            Set(combiner, "_targetMeshFilterArray", new[] { a });
            using (MeshBuildResult result = combiner.BuildMeshes())
            {
                Assert.Throws<InvalidOperationException>(() => MeshAssetTransaction.Save(result.Meshes,
                    new[] { _folder + "/Combined.asset" }, true, "Fail rebuilt scene", saved =>
                    {
                        typeof(MeshCombiner).GetMethod("CreateSceneOutput", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                            .Invoke(combiner, new object[] { saved, result });
                        Assert.IsTrue(b.GetComponent<MeshRenderer>().enabled);
                        throw new InvalidOperationException("Injected failure after scene replacement");
                    }));
            }
            Assert.IsFalse(a.GetComponent<MeshRenderer>().enabled); Assert.IsFalse(b.GetComponent<MeshRenderer>().enabled);
            Assert.AreEqual(2, new SerializedObject(combiner).FindProperty("_sourceRendererStates").arraySize);
            Assert.AreEqual(6, previous.GetIndexCount(0));
            Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID(_folder + "/Combined.asset"));
            Set(combiner, "_overwriteExistingAssets", true);
            Set(combiner, "_disableSourceRenderers", false);
            combiner.CombineAndSaveMeshes();
            Assert.IsTrue(a.GetComponent<MeshRenderer>().enabled); Assert.IsTrue(b.GetComponent<MeshRenderer>().enabled);
        }

        [Test]
        public void PrefabInstanceRebuildUsesItsSerializedOwnershipWithoutTouchingTheOriginal()
        {
            MeshFilter a = Source("A"), b = Source("B");
            MeshCombiner original = Combiner(a, b);
            a.transform.SetParent(original.transform); b.transform.SetParent(original.transform);
            var material = new Material(_material);
            AssetDatabase.CreateAsset(material, _folder + "/Source.mat");
            a.GetComponent<MeshRenderer>().sharedMaterial = material;
            b.GetComponent<MeshRenderer>().sharedMaterial = material;
            Mesh[] meshes = MeshAssetTransaction.Save(new[] { a.sharedMesh, b.sharedMesh },
                new[] { _folder + "/A.asset", _folder + "/B.asset" }, false, "Save sources");
            a.sharedMesh = meshes[0]; b.sharedMesh = meshes[1];
            Set(original, "_allowReadableCopy", true); Set(original, "_overwriteExistingAssets", true);
            original.CombineAndSaveMeshes();
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(original.gameObject, _folder + "/Rebuild.prefab");
            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, _scene);
            MeshCombiner component = instance.GetComponent<MeshCombiner>();
            MeshFilter instanceA = instance.transform.Find("A").GetComponent<MeshFilter>();
            Renderer instanceB = instance.transform.Find("B").GetComponent<Renderer>();
            Set(component, "_targetMeshFilterArray", new[] { instanceA });
            component.CombineAndSaveMeshes();
            Assert.IsTrue(instanceB.enabled); Assert.IsFalse(b.GetComponent<Renderer>().enabled);
            var ledger = new SerializedObject(component).FindProperty("_sourceRendererStates");
            Assert.AreEqual(1, ledger.arraySize);
            Assert.AreSame(instanceA.GetComponent<Renderer>(), ledger.GetArrayElementAtIndex(0).FindPropertyRelative("Renderer").objectReferenceValue);
        }

        [Test]
        public void DirectBuildCanRetryCorrectedSettingsAfterFailure()
        {
            MeshFilter source = Source("Source"); MeshCombiner component = Combiner(source);
            Set(component, "_targetMeshFilterArray", new MeshFilter[] { null });
            Assert.Throws<ArgumentException>(() => component.BuildMeshes());
            Set(component, "_targetMeshFilterArray", new[] { source });
            using (MeshBuildResult result = component.BuildMeshes()) Assert.AreEqual(1, result.Parts.Count);
            Assert.AreEqual(1, component.CombineAndSaveMeshes().Length);
        }

        [Test]
        public void UnsafeMaterialAndLightingAssumptionsAreAvailableAsBuildDiagnostics()
        {
            MeshFilter source = Source("Source"); _material.renderQueue = 3000;
            using (MeshBuildResult result = Combiner(source).BuildMeshes())
            {
                Assert.That(result.Warnings, Has.Some.Contains("Transparent sorting"));
                Assert.That(result.Warnings, Has.Some.Contains("light-probe"));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FailureAfterSavingAPrefabRestoresBothAssetsAndTheirGuids(bool overwrite)
        {
            MeshFilter source = Source("Source"); Mesh mesh = source.sharedMesh;
            string meshPath = _folder + "/Mesh.asset", prefabPath = _folder + "/Companion.prefab";
            byte[] oldPrefab = null; string meshGuid = null, prefabGuid = null;
            if (overwrite)
            {
                Mesh initial = MeshAssetTransaction.Save(new[] { mesh }, new[] { meshPath }, false, "Seed")[0];
                source.sharedMesh = initial;
                PrefabUtility.SaveAsPrefabAsset(source.gameObject, prefabPath);
                oldPrefab = File.ReadAllBytes(prefabPath);
                meshGuid = AssetDatabase.AssetPathToGUID(meshPath); prefabGuid = AssetDatabase.AssetPathToGUID(prefabPath);
            }
            Assert.Throws<InvalidOperationException>(() => MeshAssetTransaction.Save(new[] { mesh }, new[] { meshPath }, overwrite, "Fail", saved =>
            {
                source.sharedMesh = saved[0]; source.name = "Changed";
                Assert.NotNull(PrefabUtility.SaveAsPrefabAsset(source.gameObject, prefabPath));
                throw new InvalidOperationException("Failure after prefab save");
            }, new[] { prefabPath }));
            if (overwrite)
            {
                CollectionAssert.AreEqual(oldPrefab, File.ReadAllBytes(prefabPath));
                Assert.AreEqual(meshGuid, AssetDatabase.AssetPathToGUID(meshPath));
                Assert.AreEqual(prefabGuid, AssetDatabase.AssetPathToGUID(prefabPath));
                Assert.AreSame(AssetDatabase.LoadAssetAtPath<Mesh>(meshPath), AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath).GetComponent<MeshFilter>().sharedMesh);
            }
            else { Assert.IsFalse(File.Exists(meshPath)); Assert.IsFalse(File.Exists(prefabPath)); }
        }

        [Test]
        public void RollbackContinuesRestoringOtherAssetsWhenOneRestorationFails()
        {
            string blocked = _folder + "/Blocked.txt", later = _folder + "/Later.txt";
            File.WriteAllText(blocked, "old first"); File.WriteAllText(later, "old later");
            AssetDatabase.Refresh(); Mesh mesh = Source("Source").sharedMesh;
            AggregateException error = Assert.Throws<AggregateException>(() => MeshAssetTransaction.Save(new[] { mesh },
                new[] { _folder + "/Mesh.asset" }, false, "Fail", saved =>
                {
                    File.Delete(blocked); Directory.CreateDirectory(blocked); File.WriteAllText(later, "changed");
                    throw new InvalidOperationException("Primary failure");
                }, new[] { blocked, later }));
            Assert.IsInstanceOf<InvalidOperationException>(error.InnerExceptions[0]);
            Assert.AreEqual("old later", File.ReadAllText(later));
            Assert.IsFalse(File.Exists(_folder + "/Mesh.asset"));
            Directory.Delete(blocked);
        }

        [Test]
        public void ShowcaseBuilderRegeneratesRealOutputsInAnIsolatedPrefabAndPreservesAssetGuids()
        {
            const string original = "Assets/_OptimizeTool/Prefab/OptimizeToolShowcase.prefab";
            byte[] before = File.ReadAllBytes(original);
            string copy = _folder + "/Showcase.prefab";
            Assert.IsTrue(AssetDatabase.CopyAsset(original, copy));
            string prefabGuid = AssetDatabase.AssetPathToGUID(copy);
            Samples.Editor.OptimizeToolShowcaseEditor.BuildShowcaseAssets(copy, _folder);
            string[] names = { "OptimizeToolSampleCombined", "OptimizeToolSampleBackface", "OptimizeToolSampleOcclusion" };
            ulong[] triangleCounts = { 108, 30, 36 };
            var guids = new string[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                string path = _folder + "/" + names[i] + ".asset";
                Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                Assert.NotNull(mesh); Assert.AreEqual(triangleCounts[i], mesh.GetIndexCount(0) / 3);
                guids[i] = AssetDatabase.AssetPathToGUID(path);
            }
            Samples.Editor.OptimizeToolShowcaseEditor.BuildShowcaseAssets(copy, _folder);
            Assert.AreEqual(prefabGuid, AssetDatabase.AssetPathToGUID(copy));
            for (int i = 0; i < names.Length; i++)
                Assert.AreEqual(guids[i], AssetDatabase.AssetPathToGUID(_folder + "/" + names[i] + ".asset"));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(original));
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(copy);
            for (int i = 0; i < names.Length; i++)
            {
                MeshFilter filter = prefab.transform.Find(Samples.OptimizeToolSampleModes.All[i + 1].Group).GetComponentInChildren<MeshFilter>(true);
                Assert.AreEqual(_folder + "/" + names[i] + ".asset", AssetDatabase.GetAssetPath(filter.sharedMesh));
            }
        }

        [Test]
        public void ShowcasePrefabResolvesAllScriptsAndCurrentModeReferences()
        {
            const string path = "Assets/_OptimizeTool/Prefab/OptimizeToolShowcase.prefab";
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.NotNull(prefab);
            foreach (Transform tf in prefab.GetComponentsInChildren<Transform>(true))
                Assert.IsTrue(tf.GetComponents<Component>().All(c => c != null), tf.name);
            var overview = new SerializedObject(prefab.GetComponent<Samples.OptimizeToolOverviewController>());
            SerializedProperty groups = overview.FindProperty("_featureGroups");
            Assert.AreEqual(Samples.OptimizeToolSampleModes.Count, groups.arraySize);
            for (int i = 0; i < groups.arraySize; i++) Assert.NotNull(groups.GetArrayElementAtIndex(i).objectReferenceValue);
        }
    }
}
