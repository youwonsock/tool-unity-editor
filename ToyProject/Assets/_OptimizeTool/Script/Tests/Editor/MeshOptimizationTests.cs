using System;
using System.Collections.Generic;
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
    public sealed class MeshOptimizationTests
    {
        private Scene _scene;
        private readonly List<Object> _owned = new List<Object>();
        private string _folder;
        private Material _material;
        private Camera _camera;

        [SetUp]
        public void SetUp()
        {
            _scene = EditorSceneManager.NewPreviewScene();
            _folder = "Assets/__OptimizeToolTests_" + Guid.NewGuid().ToString("N");
            _material = Own(new Material(Shader.Find("Universal Render Pipeline/Lit")));
            _camera = Create("Camera").AddComponent<Camera>();
            _camera.transform.position = new Vector3(0, 0, 5);
            _camera.transform.rotation = Quaternion.Euler(0, 180, 0);
        }

        [TearDown]
        public void TearDown()
        {
            Undo.ClearAll();
            if (_scene.IsValid()) EditorSceneManager.ClosePreviewScene(_scene);
            foreach (Object item in _owned) if (item != null) Object.DestroyImmediate(item);
            _owned.Clear();
            if (AssetDatabase.IsValidFolder(_folder)) AssetDatabase.DeleteAsset(_folder);
        }

        private T Own<T>(T item) where T : Object { _owned.Add(item); return item; }
        private GameObject Create(string name)
        {
            var go = new GameObject(name);
            SceneManager.MoveGameObjectToScene(go, _scene);
            return go;
        }
        private Mesh Triangle(bool reverse = false)
        {
            var mesh = Own(new Mesh { name = "Triangle" });
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = reverse ? new[] { 0, 2, 1 } : new[] { 0, 1, 2 };
            mesh.RecalculateNormals();
            return mesh;
        }
        private MeshFilter Filter(string name, Mesh mesh)
        {
            GameObject go = Create(name);
            MeshFilter filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = _material;
            return filter;
        }
        private MeshCombiner Combiner(params MeshFilter[] filters)
        {
            var combiner = Create("Combiner").AddComponent<MeshCombiner>();
            Set(combiner, "_savePath", _folder);
            Set(combiner, "_meshName", "Combined");
            Set(combiner, "_targetMeshFilterArray", filters);
            Set(combiner, "_combineAllIntoSingleMesh", true);
            Set(combiner, "_optimizeMesh", false);
            return combiner;
        }
        private BackfaceCullMeshBaker Baker(params MeshFilter[] filters)
        {
            var baker = Create("Baker").AddComponent<BackfaceCullMeshBaker>();
            Set(baker, "_savePath", _folder);
            Set(baker, "_targetMeshFilterArray", filters);
            Set(baker, "_targetCamera", _camera);
            return baker;
        }
        private static void Set(Object target, string field, object value)
        {
            var info = target.GetType().GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(info, field);
            info.SetValue(target, value);
        }

        [Test]
        public void BackfaceCompactionPreservesAllVertexChannelsAndEmptySubmeshes()
        {
            Mesh mesh = Triangle();
            mesh.subMeshCount = 2;
            mesh.SetTriangles(new[] { 0, 2, 1 }, 1);
            mesh.tangents = Enumerable.Repeat(new Vector4(1, 0, 0, -1), 3).ToArray();
            mesh.colors32 = new[] { new Color32(1, 2, 3, 4), new Color32(5, 6, 7, 8), new Color32(9, 10, 11, 12) };
            for (int channel = 0; channel < 8; channel++)
                mesh.SetUVs(channel, new List<Vector4> { new Vector4(channel, 1, 2, 3), new Vector4(channel, 4, 5, 6), new Vector4(channel, 7, 8, 9) });
            Transform source = Create("Source").transform;
            Mesh result = Own(MeshProcessingUtility.CullBackfaces(mesh, source, _camera, 90, false, Vector3.zero));
            Assert.AreEqual(3, result.GetIndexCount(0));
            Assert.AreEqual(0, result.GetIndexCount(1));
            CollectionAssert.AreEqual(mesh.GetVertexAttributes(), result.GetVertexAttributes());
            CollectionAssert.AreEqual(mesh.tangents, result.tangents);
            CollectionAssert.AreEqual(mesh.colors32, result.colors32);
            for (int channel = 0; channel < 8; channel++)
            {
                var expected = new List<Vector4>(); var actual = new List<Vector4>();
                mesh.GetUVs(channel, expected); result.GetUVs(channel, actual);
                CollectionAssert.AreEqual(expected, actual);
            }
        }

        [Test]
        public void ReadableCopyHandlesUnreadableMeshesWithoutChangingTheSource()
        {
            Mesh mesh = Triangle();
            mesh.UploadMeshData(true);
            Assert.Throws<InvalidOperationException>(() => MeshProcessingUtility.ReadableCopy(mesh, false, false));
            Mesh copy = Own(MeshProcessingUtility.ReadableCopy(mesh, true, false));
            Assert.IsTrue(copy.isReadable);
            Assert.IsFalse(mesh.isReadable);
            Assert.AreEqual(3, copy.vertices.Length);
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, copy.triangles);
        }

        [Test]
        public void MissingNormalsRequirePermissionAndAreRecalculatedForSingleMaterialOutput()
        {
            Mesh mesh = Triangle(); mesh.normals = Array.Empty<Vector3>();
            MeshCombiner combiner = Combiner(Filter("Source", mesh));
            Assert.Throws<ArgumentException>(() => combiner.BuildMeshes());
            Set(combiner, "_allowNormalRecalculation", true);
            using (MeshBuildResult result = combiner.BuildMeshes())
                Assert.AreEqual(3, result.Meshes[0].normals.Length);
            Assert.AreEqual(0, mesh.normals.Length);
        }

        [Test]
        public void MixedChannelLayoutsKeepTangentsAndUv2DuringCombination()
        {
            Mesh a = Triangle(), b = Triangle();
            a.uv2 = new[] { Vector2.one, Vector2.right, Vector2.up };
            a.tangents = Enumerable.Repeat(new Vector4(0, 1, 0, -1), 3).ToArray();
            MeshCombiner combiner = Combiner(Filter("A", a), Filter("B", b));
            using (MeshBuildResult result = combiner.BuildMeshes())
            {
                Assert.AreEqual(6, result.Meshes[0].uv2.Length);
                CollectionAssert.AreEqual(a.uv2, result.Meshes[0].uv2.Take(3));
                CollectionAssert.AreEqual(a.tangents, result.Meshes[0].tangents.Take(3));
                Assert.AreEqual(6, result.Meshes[0].tangents.Length);
            }
        }

        [Test]
        public void PerspectiveUsesTrianglePositionWhileOrthographicUsesCameraForward()
        {
            Mesh mesh = Triangle();
            mesh.vertices = new[] { new Vector3(5, 0, 0), new Vector3(5, 1, 0), new Vector3(5, 0, 1) };
            _camera.transform.position = new Vector3(10, 0.3f, 0.3f);
            _camera.transform.rotation = Quaternion.identity;
            Transform source = Create("Source").transform;
            Mesh result = Own(MeshProcessingUtility.CullBackfaces(mesh, source, _camera, 30, false, Vector3.zero));
            Assert.NotNull(result);
            _camera.orthographic = true;
            Assert.IsNull(MeshProcessingUtility.CullBackfaces(mesh, source, _camera, 30, false, Vector3.zero));
        }

        [Test]
        public void MirroredNonUniformScalePreservesFrontFaceAndCorrectWinding()
        {
            Mesh mesh = Triangle();
            Transform source = Create("Source").transform;
            source.localScale = new Vector3(-2, 3, 1);
            Mesh culled = Own(MeshProcessingUtility.CullBackfaces(mesh, source, _camera, 90, false, Vector3.zero));
            Assert.NotNull(culled);
            MeshProcessingUtility.TransformMesh(culled, source.localToWorldMatrix);
            Vector3[] v = culled.vertices; int[] t = culled.triangles;
            Vector3 geometric = Vector3.Cross(v[t[1]] - v[t[0]], v[t[2]] - v[t[0]]).normalized;
            Assert.Greater(Vector3.Dot(geometric, culled.normals[t[0]]), 0.999f);
        }

        [Test]
        public void SmallValidScaleIsNotMistakenForAZeroScaleTransform()
        {
            Mesh mesh = Triangle();
            Transform source = Create("Small source").transform;
            source.localScale = Vector3.one * 0.001f;
            Mesh culled = Own(MeshProcessingUtility.CullBackfaces(mesh, source, _camera, 90, false, Vector3.zero));
            Assert.NotNull(culled);
            MeshProcessingUtility.TransformMesh(culled, source.localToWorldMatrix);
            Assert.AreEqual(3, culled.vertexCount);
        }

        [Test]
        public void OcclusionIgnoresOwnCollidersButDetectsAnExternalWallAndLayerMask()
        {
            MeshFilter filter = Filter("Source", Triangle());
            filter.gameObject.AddComponent<BoxCollider>().size = Vector3.one * 2;
            Physics.SyncTransforms();
            Assert.IsFalse(MeshProcessingUtility.IsOccluded(filter, filter.sharedMesh, _camera, 100, 0.01, ~0));
            GameObject wall = Create("Wall");
            wall.transform.position = new Vector3(0, 0, 2);
            wall.AddComponent<BoxCollider>().size = new Vector3(10, 10, 0.2f);
            wall.layer = 2;
            Physics.SyncTransforms();
            Assert.IsTrue(MeshProcessingUtility.IsOccluded(filter, filter.sharedMesh, _camera, 100, 0.01, ~0));
            Assert.IsFalse(MeshProcessingUtility.IsOccluded(filter, filter.sharedMesh, _camera, 100, 0.01, 1 << 0));
            wall.GetComponent<BoxCollider>().isTrigger = true;
            Assert.IsFalse(MeshProcessingUtility.IsOccluded(filter, filter.sharedMesh, _camera, 100, 0.01, ~0));
        }

        [Test]
        public void OcclusionExpandsTheHitBufferInsteadOfTreatingOwnHitsAsBlockers()
        {
            MeshFilter filter = Filter("Source", Triangle());
            for (int i = 0; i < 40; i++)
            {
                GameObject child = Create("Own Collider");
                child.transform.SetParent(filter.transform, false);
                child.transform.position = new Vector3(0, 0, 0.1f + i * 0.05f);
                child.AddComponent<BoxCollider>().size = new Vector3(4, 4, 0.01f);
            }
            Physics.SyncTransforms();
            Assert.IsFalse(MeshProcessingUtility.IsOccluded(filter, filter.sharedMesh, _camera, 100, 0.01, ~0));
        }

        [Test]
        public void ASourceOver65535VerticesUsesUInt32AndSurvivesCombination()
        {
            const int count = 66000;
            Mesh mesh = Own(new Mesh { indexFormat = IndexFormat.UInt32 });
            var vertices = new Vector3[count]; var normals = new Vector3[count];
            var indices = new int[count];
            for (int i = 0; i < count; i++)
            {
                int triangle = i / 3;
                vertices[i] = new Vector3(triangle % 100 + (i % 3 == 1 ? 0.5f : 0), triangle / 100 + (i % 3 == 2 ? 0.5f : 0), 0);
                normals[i] = Vector3.forward; indices[i] = i;
            }
            mesh.vertices = vertices; mesh.normals = normals; mesh.triangles = indices;
            MeshCombiner combiner = Combiner(Filter("Large", mesh));
            Set(combiner, "_enableBackfaceCulling", true);
            Set(combiner, "_targetCamera", _camera);
            using (MeshBuildResult result = combiner.BuildMeshes())
            {
                Assert.AreEqual(count, result.Meshes[0].vertexCount);
                Assert.AreEqual(IndexFormat.UInt32, result.Meshes[0].indexFormat);
                Assert.AreEqual(count, result.Meshes[0].GetIndexCount(0));
            }
        }

        [Test]
        public void CombinationCreatesMaterialMappedSceneOutputAndUndoRestoresSources()
        {
            MeshFilter a = Filter("A", Triangle()), b = Filter("B", Triangle());
            Material second = Own(new Material(_material));
            b.GetComponent<MeshRenderer>().sharedMaterial = second;
            MeshCombiner combiner = Combiner(a, b);
            combiner.transform.position = new Vector3(3, 4, 5);
            Mesh[] saved = combiner.CombineAndSaveMeshes();
            Assert.AreEqual(2, saved[0].subMeshCount);
            MeshFilter output = combiner.GetComponentInChildren<MeshFilter>();
            Assert.NotNull(output);
            CollectionAssert.AreEqual(new[] { _material, second }, output.GetComponent<MeshRenderer>().sharedMaterials);
            Assert.IsFalse(a.GetComponent<MeshRenderer>().enabled);
            Vector3 point = output.transform.TransformPoint(saved[0].vertices[0]);
            Assert.Less(Vector3.Distance(point, a.transform.TransformPoint(a.sharedMesh.vertices[0])), 0.0001f);
            Undo.PerformUndo();
            Assert.IsTrue(a.GetComponent<MeshRenderer>().enabled);
            Assert.IsNull(combiner.GetComponentInChildren<MeshFilter>());
            Undo.PerformRedo();
            Assert.NotNull(combiner.GetComponentInChildren<MeshFilter>());
            Assert.IsFalse(a.GetComponent<MeshRenderer>().enabled);
        }

        [Test]
        public void RepeatedChildCollectionDoesNotCombineItsOwnGeneratedOutput()
        {
            MeshFilter source = Filter("Source", Triangle());
            MeshCombiner combiner = Combiner(source);
            source.transform.SetParent(combiner.transform, true);
            Set(combiner, "_targetMeshFilterArray", Array.Empty<MeshFilter>());
            Set(combiner, "_collectChildMeshes", true);
            Set(combiner, "_overwriteExistingAssets", true);
            combiner.CombineAndSaveMeshes();
            Mesh[] saved = combiner.CombineAndSaveMeshes();
            Assert.AreEqual(3, saved[0].vertexCount);
        }

        [Test]
        public void BakerStagesEveryInputBeforeSavingOrReplacingAnyFilter()
        {
            MeshFilter a = Filter("First", Triangle()), b = Filter("Invalid", Triangle(true));
            Mesh original = a.sharedMesh;
            Assert.Throws<InvalidOperationException>(() => Baker(a, b).BakeBackFaceCullingMesh());
            Assert.AreSame(original, a.sharedMesh);
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<Mesh>(_folder + "/First_BackfaceCulled.asset"));
        }

        [Test]
        public void BakerUsesScalePerInstanceAndUndoRestoresMeshReferences()
        {
            Mesh shared = Triangle();
            MeshFilter a = Filter("First", shared), b = Filter("Second", shared);
            b.transform.localScale = new Vector3(-2, 3, 1);
            Mesh[] saved = Baker(a, b).BakeBackFaceCullingMesh();
            Assert.AreEqual(2, saved.Length);
            Assert.AreNotSame(shared, a.sharedMesh);
            Assert.AreNotSame(a.sharedMesh, b.sharedMesh);
            Undo.PerformUndo();
            Assert.AreSame(shared, a.sharedMesh);
            Assert.AreSame(shared, b.sharedMesh);
        }

        [Test]
        public void TransactionRollsBackExistingNewAssetsAndSceneChangesOnFailure()
        {
            Mesh before = Triangle(), after = Triangle(true);
            before.name = "Original Asset Name";
            string existingPath = _folder + "/Existing.asset", newPath = _folder + "/New.asset";
            Mesh saved = MeshAssetTransaction.Save(new[] { before }, new[] { existingPath }, false, "Seed")[0];
            saved.name = before.name;
            AssetDatabase.SaveAssets();
            string guid = AssetDatabase.AssetPathToGUID(existingPath);
            MeshFilter filter = Filter("Source", before);
            Assert.Throws<InvalidOperationException>(() => MeshAssetTransaction.Save(new[] { after, after }, new[] { existingPath, newPath }, true, "Fail", meshes =>
            {
                Undo.RegisterCompleteObjectUndo(filter, "Temporary assignment");
                filter.sharedMesh = meshes[0];
                throw new InvalidOperationException("Injected failure after asset writes");
            }));
            CollectionAssert.AreEqual(before.triangles, saved.triangles);
            Assert.AreEqual(before.name, saved.name);
            Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID(existingPath));
            Assert.IsNull(AssetDatabase.LoadMainAssetAtPath(newPath));
            Assert.AreSame(before, filter.sharedMesh);
        }

        [Test]
        public void SuccessfulOverwritePreservesGuidAndSupportsUndoAndRedo()
        {
            string path = _folder + "/Existing.asset";
            Mesh before = Triangle(), after = Triangle(true);
            Mesh saved = MeshAssetTransaction.Save(new[] { before }, new[] { path }, false, "Seed")[0];
            string guid = AssetDatabase.AssetPathToGUID(path);
            Mesh updated = MeshAssetTransaction.Save(new[] { after }, new[] { path }, true, "Overwrite")[0];
            Assert.AreSame(saved, updated);
            CollectionAssert.AreEqual(after.triangles, updated.triangles);
            Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID(path));
            Undo.PerformUndo();
            CollectionAssert.AreEqual(before.triangles, saved.triangles);
            Undo.PerformRedo();
            CollectionAssert.AreEqual(after.triangles, saved.triangles);
        }

        [Test]
        public void OutputCollisionIsRejectedBeforeAnyAssetIsCreated()
        {
            string path = _folder + "/Same.asset";
            Assert.Throws<ArgumentException>(() => MeshAssetTransaction.Save(new[] { Triangle(), Triangle() }, new[] { path, path }, false, "Duplicate"));
            Assert.IsNull(AssetDatabase.LoadMainAssetAtPath(path));
        }

        [Test]
        public void CustomVectorDoesNotRequireAnUnusedCameraReference()
        {
            var baker = Baker(Filter("Source", Triangle()));
            Set(baker, "_targetCamera", null);
            Set(baker, "_useCustomVector", true);
            Set(baker, "_customCameraVector", Vector3.back);
            Assert.AreEqual(1, baker.BakeBackFaceCullingMesh().Length);
        }

        [Test]
        public void BothToolsHaveRealCustomInspectors()
        {
            MeshCombiner combiner = Combiner(Filter("Source", Triangle()));
            UnityEditor.Editor inspector = UnityEditor.Editor.CreateEditor(combiner);
            try { Assert.AreEqual("MeshCombinerEditor", inspector.GetType().Name); }
            finally { Object.DestroyImmediate(inspector); }
            inspector = UnityEditor.Editor.CreateEditor(Baker(Filter("Second", Triangle())));
            try { Assert.AreEqual("BackfaceCullMeshBakerEditor", inspector.GetType().Name); }
            finally { Object.DestroyImmediate(inspector); }
        }
    }
}
