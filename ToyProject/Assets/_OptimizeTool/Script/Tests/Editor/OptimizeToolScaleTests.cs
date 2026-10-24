using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Common.OptimizeTool.Tests
{
    public sealed class OptimizeToolScaleTests
    {
        [Test]
        public void ScaleBuilderBakesIndependentDepthLayersAndPreservesExistingScene()
        {
            const string original = "Assets/_OptimizeTool/Scene/OptimizeToolSample.unity";
            byte[] before = System.IO.File.ReadAllBytes(original);
            string folder = "Assets/__OptimizeScale_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder.Substring(7));
            Scene opened = default;
            try
            {
                string scenePath = folder + "/Scale.unity";
                // Include another scene's blocker: local physics authoring must ignore it.
                GameObject blocker = GameObject.CreatePrimitive(PrimitiveType.Cube);
                blocker.transform.position = new Vector3(0, 0, -5); blocker.transform.localScale = Vector3.one * 10;
                try { Samples.Editor.OptimizeToolScaleBuilder.BuildAt(scenePath, folder, new[] { 2 }); }
                finally { UnityEngine.Object.DestroyImmediate(blocker); }
                opened = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
                var workload = opened.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Samples.OptimizeToolScaleWorkload>(true)).Single();
                Assert.AreEqual(36, workload.Current.cubeCount);
                CollectionAssert.AreEqual(new[] { 36, 1, 1, 1 }, workload.Current.rendererCounts);
                CollectionAssert.AreEqual(new long[] { 432, 432, 72, 144 }, workload.Current.triangleCounts);
                Assert.IsEmpty(workload.GetComponentsInChildren<Collider>(true));
                Vector3 position = workload.Camera.transform.position; float size = workload.Camera.orthographicSize;
                for (int mode = 0; mode < 4; mode++)
                {
                    workload.SelectMode(mode);
                    Assert.AreEqual(position, workload.Camera.transform.position); Assert.AreEqual(size, workload.Camera.orthographicSize);
                    for (int m = 0; m < 4; m++) Assert.AreEqual(m == mode, workload.Current.modes[m].activeSelf);
                }
                Assert.Throws<ArgumentException>(() => workload.SelectCubeCount(100));
                CollectionAssert.AreEqual(before, System.IO.File.ReadAllBytes(original));
            }
            finally
            {
                if (opened.IsValid()) EditorSceneManager.CloseScene(opened, true);
                AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}
