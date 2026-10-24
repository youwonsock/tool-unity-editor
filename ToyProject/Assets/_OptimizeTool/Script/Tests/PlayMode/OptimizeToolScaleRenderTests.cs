#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEditor.SceneManagement;

namespace Common.OptimizeTool.Tests
{
    public sealed class OptimizeToolScaleRenderTests
    {
        [UnityTest]
        public IEnumerator AllScalesUseRealBakesAndKeepTheOriginalRenderedImage()
            => CheckRenderedModes("Assets/_OptimizeTool/Scene/OptimizeToolScaleBenchmark.unity", new[] { 225, 900, 3600 }, 1);

        [UnityTest]
        [PrebuildSetup("Common.OptimizeTool.Tests.OptimizeToolGpuSceneSetup")]
        public IEnumerator DenseGpuBakesKeepTheOriginalRenderedImage()
            => CheckRenderedModes("Assets/_OptimizeTool/Scene/OptimizeToolGpuBenchmark.unity", new[] { 9, 36, 225 }, 64);

        private static IEnumerator CheckRenderedModes(string scene, int[] cubeCounts, int subdivisions)
        {
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(scene, new LoadSceneParameters(LoadSceneMode.Single));
            yield return null;
            var workload = Object.FindFirstObjectByType<Samples.OptimizeToolScaleWorkload>();
            Assert.NotNull(workload); Assert.AreEqual(3, workload.CaseCount);
            workload.enabled = false;
            Camera camera = workload.Camera;
            var target = new RenderTexture(960, 540, 24);
            var pixels = new Texture2D(960, 540, TextureFormat.RGB24, false);
            RenderTexture previous = RenderTexture.active; RenderTexture oldTarget = camera.targetTexture;
            try
            {
                camera.targetTexture = target;
                for (int c = 0; c < workload.CaseCount; c++)
                {
                    workload.SelectCase(c);
                    Assert.AreEqual(cubeCounts[c], workload.Current.cubeCount);
                    Assert.AreEqual(subdivisions, System.Math.Max(1, workload.Current.faceSubdivisions));
                    CollectionAssert.AreEqual(new[] { workload.Current.cubeCount, 1, 1, 1 }, workload.Current.rendererCounts);
                    long factor = (long)cubeCounts[c] * subdivisions * subdivisions;
                    CollectionAssert.AreEqual(new[] { 12 * factor, 12 * factor, 2 * factor, 4 * factor }, workload.Current.triangleCounts);
                    Color32[] original = null;
                    for (int mode = 0; mode < 4; mode++)
                    {
                        workload.SelectMode(mode); yield return null;
                        camera.Render(); RenderTexture.active = target;
                        pixels.ReadPixels(new Rect(0, 0, 960, 540), 0, 0); pixels.Apply();
                        Color32[] current = pixels.GetPixels32();
                        if (mode == 0) { original = current; continue; }
                        long error = 0;
                        long foregroundError = 0; int foreground = 0;
                        Color32 background = original[0];
                        for (int i = 0; i < current.Length; i++)
                        {
                            int difference = System.Math.Abs(current[i].r - original[i].r) + System.Math.Abs(current[i].g - original[i].g) + System.Math.Abs(current[i].b - original[i].b);
                            error += difference;
                            if (System.Math.Abs(original[i].r - background.r) + System.Math.Abs(original[i].g - background.g) + System.Math.Abs(original[i].b - background.b) > 15)
                            { foregroundError += difference; foreground++; }
                        }
                        Assert.Less((double)error / (current.Length * 3 * 255), 0.005d, $"Scale {c}, mode {mode}");
                        Assert.Greater(foreground, 1000, "The camera must actually see the sample.");
                        Assert.Less((double)foregroundError / (foreground * 3 * 255), 0.01d, $"Foreground scale {c}, mode {mode}");
                    }
                }
                Assert.IsEmpty(workload.GetComponentsInChildren<Collider>(true));
            }
            finally
            {
                camera.targetTexture = oldTarget; RenderTexture.active = previous;
                target.Release(); Object.Destroy(target); Object.Destroy(pixels);
            }
            LogAssert.NoUnexpectedReceived();
        }
    }
}
#endif
