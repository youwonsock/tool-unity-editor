using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Common.OptimizeTool.Tests
{
    public sealed class OptimizeToolGpuSceneSetup : UnityEngine.TestTools.IPrebuildSetup
    {
        public void Setup()
        {
            if (!System.IO.File.Exists(Samples.Editor.OptimizeToolScaleBuilder.GpuScenePath)) Samples.Editor.OptimizeToolScaleBuilder.BuildGpu();
        }
    }

    public sealed class OptimizeToolGpuGeometryTests
    {
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(8)]
        [TestCase(128)]
        public void DenseCubeHasTheSameSurfaceAndOutwardWinding(int subdivisions)
        {
            Mesh mesh = Samples.OptimizeToolBenchmarkGeometry.CreateCube(subdivisions);
            try
            {
                Assert.AreEqual(6 * (subdivisions + 1) * (subdivisions + 1), mesh.vertexCount);
                Assert.AreEqual(36 * subdivisions * subdivisions, mesh.triangles.Length);
                Assert.AreEqual(Vector3.one, mesh.bounds.size);
                Vector3[] vertices = mesh.vertices, normals = mesh.normals;
                int[] indices = mesh.triangles;
                for (int i = 0; i < indices.Length; i += 3)
                {
                    int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                    Assert.Greater(Vector3.Dot(Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]), normals[a]), 0f);
                    Assert.AreEqual(0.5f, Vector3.Dot(vertices[a], normals[a]));
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [TestCase(0)]
        [TestCase(129)]
        public void InvalidDensityIsRejected(int subdivisions) => Assert.Throws<ArgumentOutOfRangeException>(() => Samples.OptimizeToolBenchmarkGeometry.CreateCube(subdivisions));

        [Test]
        public void DenseBakesRemoveFacesAndHiddenLayersAndKeepSavedSourceReferences()
        {
            string folder = "Assets/__OptimizeGpu_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder.Substring(7));
            Scene scene = default;
            try
            {
                Samples.Editor.OptimizeToolScaleBuilder.BuildAt(folder + "/GPU.unity", folder, new[] { 1 }, 2);
                scene = EditorSceneManager.OpenScene(folder + "/GPU.unity", OpenSceneMode.Additive);
                var workload = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Samples.OptimizeToolScaleWorkload>(true)).Single();
                Assert.AreEqual(9, workload.Current.cubeCount); Assert.AreEqual(2, workload.Current.faceSubdivisions);
                CollectionAssert.AreEqual(new long[] { 432, 432, 72, 144 }, workload.Current.triangleCounts);
                Mesh cube = AssetDatabase.LoadAssetAtPath<Mesh>(folder + "/DenseCube.asset");
                foreach (MeshFilter filter in workload.Current.modes[0].GetComponentsInChildren<MeshFilter>(true)) Assert.AreSame(cube, filter.sharedMesh);
                Assert.IsEmpty(workload.GetComponentsInChildren<Collider>(true));
            }
            finally
            {
                if (scene.IsValid()) EditorSceneManager.CloseScene(scene, true);
                AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}
