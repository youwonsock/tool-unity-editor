using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Common.OptimizeTool.Tests
{
    public sealed class OptimizeToolShowcaseTests
    {
        [UnityTest]
        public IEnumerator SampleSwitchesBetweenDistinctRealBakesAtTheSameCameraPose()
        {
            yield return SceneManager.LoadSceneAsync("Assets/_OptimizeTool/Scene/OptimizeToolSample.unity");
            yield return null;
            var controller = Object.FindFirstObjectByType<Samples.OptimizeToolOverviewController>();
            Assert.NotNull(controller);
            Assert.IsTrue(controller.IsInitialized);
            Assert.AreEqual(5, controller.FeatureCount);
            Vector3 position = Camera.main.transform.position;
            int[] renderers = { 9, 1, 1, 1 };
            long[] triangles = { 108, 108, 30, 36 };
            var meshes = new Mesh[3];
            for (int i = 0; i < 4; i++)
            {
                controller.SelectFeature(i);
                yield return null;
                Assert.AreEqual(renderers[i], controller.ActiveRendererCount);
                Assert.AreEqual(triangles[i], controller.ActiveTriangleCount);
                Assert.AreEqual(position, Camera.main.transform.position);
                if (i > 0) meshes[i - 1] = controller.GetComponentsInChildren<MeshFilter>().Single(f => f.name == "CombinedMeshRenderer").sharedMesh;
            }
            Assert.AreNotSame(meshes[0], meshes[1]);
            Assert.AreNotSame(meshes[0], meshes[2]);
            var buttons = Object.FindObjectsByType<Button>(FindObjectsSortMode.None);
            Assert.IsTrue(buttons.Any(b => b.name == "BAKE VIEW Button"));
            Camera.main.transform.position += Vector3.one * 5;
            buttons.Single(b => b.name == "BAKE VIEW Button").onClick.Invoke();
            Assert.AreEqual(position, Camera.main.transform.position);
            LogAssert.NoUnexpectedReceived();
        }
        [UnityTest]
        public IEnumerator BakedResultsKeepTheRenderedImageFromTheBakeView()
        {
            yield return SceneManager.LoadSceneAsync("Assets/_OptimizeTool/Scene/OptimizeToolSample.unity");
            yield return null;
            var controller = Object.FindFirstObjectByType<Samples.OptimizeToolOverviewController>();
            Camera camera = Camera.main;
            RenderTexture previousTarget = camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;
            var target = new RenderTexture(512, 256, 24, RenderTextureFormat.ARGB32);
            var pixels = new Texture2D(512, 256, TextureFormat.RGB24, false);
            try
            {
                camera.targetTexture = target;
                Color32[] original = null;
                for (int mode = 0; mode < 4; mode++)
                {
                    controller.SelectFeature(mode);
                    yield return null;
                    camera.Render();
                    RenderTexture.active = target;
                    pixels.ReadPixels(new Rect(0, 0, 512, 256), 0, 0);
                    pixels.Apply();
                    Color32[] current = pixels.GetPixels32();
                    if (mode == 0) { original = current; continue; }
                    long difference = 0;
                    for (int i = 0; i < current.Length; i++)
                        difference += System.Math.Abs(current[i].r - original[i].r)
                            + System.Math.Abs(current[i].g - original[i].g) + System.Math.Abs(current[i].b - original[i].b);
                    float meanError = (float)difference / (current.Length * 3 * 255);
                    Assert.Less(meanError, 0.005f, $"Rendered view changed in mode {mode}");
                }
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                target.Release();
                Object.Destroy(target);
                Object.Destroy(pixels);
            }
            LogAssert.NoUnexpectedReceived();
        }
    }
}
