using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Common.OptimizeTool.Tests
{
    public sealed class MeshDefectRegressionTests
    {
        private Scene _scene;
        private string _folder;
        private Material _material;
        private readonly List<Object> _owned = new List<Object>();

        [SetUp]
        public void SetUp()
        {
            _scene = EditorSceneManager.NewPreviewScene();
            _folder = "Assets/__MeshDefects_" + Guid.NewGuid().ToString("N");
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

        [TestCase(1f, 1f)]
        [TestCase(0.01f, 1f)]
        [TestCase(0.002f, 1f)]
        [TestCase(0.00001f, 1f)]
        [TestCase(0.01f, 100f)]
        [TestCase(0.1f, 100f)]
        [TestCase(0.01f, 100000f)]
        public void FrontFacingTrianglesSurviveAtAnySize(float edge, float scale)
        {
            Transform source = Create("Source").transform;
            source.localScale = Vector3.one * scale;
            Mesh culled = Own(MeshProcessingUtility.CullBackfaces(Quad(edge), source, null, 90f, true, Vector3.forward));
            Assert.That(culled, Is.Not.Null);
            Assert.That(culled.GetIndexCount(0), Is.EqualTo(6));
        }

        [TestCase(1f)]
        [TestCase(0.002f)]
        public void BackFacingTrianglesAreStillRemovedAtAnySize(float edge)
        {
            Transform source = Create("Source").transform;
            Assert.That(MeshProcessingUtility.CullBackfaces(Quad(edge), source, null, 90f, true, Vector3.back), Is.Null);
        }

        [Test]
        public void DegenerateTrianglesAreStillRemoved()
        {
            Mesh mesh = Own(new Mesh
            {
                vertices = new[] { Vector3.zero, Vector3.right, Vector3.right * 2f },
                normals = new[] { Vector3.back, Vector3.back, Vector3.back },
                triangles = new[] { 0, 1, 2 },
            });
            Assert.That(MeshProcessingUtility.CullBackfaces(mesh, Create("Source").transform, null, 90f, true, Vector3.forward), Is.Null);
        }

        [Test]
        public void LargeScalesKeepUnitNormalsWhenTransformed()
        {
            Mesh mesh = Own(Object.Instantiate(Quad(1f)));
            MeshProcessingUtility.TransformMesh(mesh, Matrix4x4.Scale(Vector3.one * 1000000f));
            Assert.That(mesh.normals[0].magnitude, Is.EqualTo(1f).Within(0.0001f));
        }

        [Test]
        public void ChildCollectionLeavesOutRenderersTheUserDisabled()
        {
            MeshCombiner combiner = CollectingCombiner();
            MeshFilter visible = Child(combiner, "Visible"), hidden = Child(combiner, "Hidden");
            hidden.GetComponent<MeshRenderer>().enabled = false;

            using (MeshBuildResult result = combiner.BuildMeshes())
            {
                Assert.That(result.SourceRenderers, Is.EqualTo(new[] { visible.GetComponent<MeshRenderer>() }));
                Assert.That(result.Meshes[0].vertexCount, Is.EqualTo(4));
            }
            Assert.That(combiner.LastBuildWarnings.Any(warning => warning.Contains("disabled Renderer")), Is.True);
        }

        [Test]
        public void ChildCollectionKeepsSourcesAnEarlierBakeDisabled()
        {
            MeshCombiner combiner = CollectingCombiner();
            MeshFilter visible = Child(combiner, "Visible"), hidden = Child(combiner, "Hidden");
            hidden.GetComponent<MeshRenderer>().enabled = false;
            combiner.CombineAndSaveMeshes();
            Assert.That(visible.GetComponent<MeshRenderer>().enabled, Is.False);

            using (MeshBuildResult result = combiner.BuildMeshes())
                Assert.That(result.SourceRenderers, Is.EqualTo(new[] { visible.GetComponent<MeshRenderer>() }));
            combiner.CombineAndSaveMeshes();
            Assert.That(hidden.GetComponent<MeshRenderer>().enabled, Is.False);
            Assert.That(visible.GetComponent<MeshRenderer>().enabled, Is.False);
        }

        [Test]
        public void DirectlyAssignedDisabledRendererIsStillAnInput()
        {
            MeshCombiner combiner = CollectingCombiner();
            Set(combiner, "_collectChildMeshes", false);
            MeshFilter hidden = Child(combiner, "Hidden");
            hidden.GetComponent<MeshRenderer>().enabled = false;
            Set(combiner, "_targetMeshFilterArray", new[] { hidden });
            using (MeshBuildResult result = combiner.BuildMeshes())
                Assert.That(result.SourceRenderers, Is.EqualTo(new[] { hidden.GetComponent<MeshRenderer>() }));
        }

        private MeshCombiner CollectingCombiner()
        {
            MeshCombiner combiner = Create("Combiner").AddComponent<MeshCombiner>();
            Set(combiner, "_collectChildMeshes", true);
            Set(combiner, "_savePath", _folder);
            Set(combiner, "_meshName", "Combined");
            Set(combiner, "_overwriteExistingAssets", true);
            return combiner;
        }

        private MeshFilter Child(MeshCombiner combiner, string name)
        {
            GameObject child = Create(name);
            child.transform.SetParent(combiner.transform, false);
            child.AddComponent<MeshRenderer>().sharedMaterial = _material;
            MeshFilter filter = child.AddComponent<MeshFilter>();
            filter.sharedMesh = Quad(1f);
            return filter;
        }

        private Mesh Quad(float edge)
        {
            return Own(new Mesh
            {
                vertices = new[] { Vector3.zero, Vector3.right * edge, Vector3.up * edge, new Vector3(edge, edge, 0f) },
                normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back },
                triangles = new[] { 0, 2, 1, 2, 3, 1 },
            });
        }

        private GameObject Create(string name)
        {
            var result = new GameObject(name);
            SceneManager.MoveGameObjectToScene(result, _scene);
            return Own(result);
        }

        private T Own<T>(T item) where T : Object
        {
            _owned.Add(item);
            return item;
        }

        private static void Set(Object target, string field, object value)
        {
            target.GetType().GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(target, value);
        }
    }
}
