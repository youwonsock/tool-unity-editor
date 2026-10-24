#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Common.OptimizeTool.Tests
{
    public sealed class OptimizeToolBenchmarkTests
    {
        [UnityTest]
        public IEnumerator PendingGpuDrainCannotRunAfterCancellation()
        {
            var benchmark = new GameObject("Pending GPU drain cancellation").AddComponent<Samples.OptimizeToolPerformanceBenchmark>();
            var method = typeof(Samples.OptimizeToolPerformanceBenchmark).GetMethod("DrainGpuFrames", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var pending = (IEnumerator)method.Invoke(benchmark, null);
            Assert.IsTrue(pending.MoveNext(), "The worker must be suspended at end-of-frame.");
            benchmark.Cancel();
            Assert.IsFalse(pending.MoveNext(), "A pending callback must exit after the resources were released.");
            yield return null;
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator DiagnosticWriteFailureCannotPreventRestorationOnDisable()
        {
            string output = Path.Combine(Path.GetTempPath(), "OptimizeToolCleanup-" + Guid.NewGuid().ToString("N"));
            int vsync = QualitySettings.vSyncCount, target = Application.targetFrameRate;
            int interval = OnDemandRendering.renderFrameInterval;
            bool background = Application.runInBackground;
            Samples.OptimizeToolPerformanceBenchmark benchmark = null;
            int completionCount = 0, exitCode = -1;
            try
            {
                benchmark = Samples.OptimizeToolPerformanceBenchmark.StartEditorRun(output, 1);
                benchmark.Completed += code => { completionCount++; exitCode = code; };
                yield return null;
                Directory.CreateDirectory(Path.Combine(output, "error.txt"));
                Assert.AreEqual(0, QualitySettings.vSyncCount);
                LogAssert.Expect(LogType.Warning, new Regex("Unable to write benchmark diagnostic:"));
                benchmark.enabled = false;
                Assert.AreEqual(vsync, QualitySettings.vSyncCount); Assert.AreEqual(target, Application.targetFrameRate);
                Assert.AreEqual(interval, OnDemandRendering.renderFrameInterval); Assert.AreEqual(background, Application.runInBackground);
                Assert.AreEqual(1, completionCount); Assert.AreEqual(2, exitCode);
                Object.DestroyImmediate(benchmark.gameObject); benchmark = null;
                Assert.AreEqual(1, completionCount, "Completion must be emitted only once.");
            }
            finally
            {
                if (benchmark != null) Object.DestroyImmediate(benchmark.gameObject);
                Directory.Delete(output, true);
            }
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest] public IEnumerator CancelRestoresWorkloadCameraAndSettings() => VerifyRestoration(0);
        [UnityTest] public IEnumerator DisableRestoresWorkloadCameraAndSettings() => VerifyRestoration(1);
        [UnityTest] public IEnumerator DestroyRestoresWorkloadCameraAndSettings() => VerifyRestoration(2);
        [UnityTest] public IEnumerator OutputFailureRestoresWorkloadCameraAndSettings() => VerifyRestoration(3);

        [UnityTest]
        public IEnumerator ScaleCancellationRestoresCaseModeAndCustomCamera()
        {
            yield return UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(
                "Assets/_OptimizeTool/Scene/OptimizeToolScaleBenchmark.unity", new LoadSceneParameters(LoadSceneMode.Single));
            yield return null;
            var scale = Object.FindFirstObjectByType<Samples.OptimizeToolScaleWorkload>();
            scale.SelectCase(1); scale.SelectMode(2);
            Vector3 position = new Vector3(2f, 3f, -45f);
            Quaternion rotation = Quaternion.Euler(4f, 5f, 0f);
            scale.Camera.transform.SetPositionAndRotation(position, rotation);
            float size = scale.Camera.orthographicSize = 13f;
            string output = Path.Combine(Path.GetTempPath(), "OptimizeScaleRestore-" + Guid.NewGuid().ToString("N"));
            Samples.OptimizeToolPerformanceBenchmark benchmark = null;
            try
            {
                benchmark = Samples.OptimizeToolPerformanceBenchmark.StartEditorRun(output, 1);
                double deadline = Time.realtimeSinceStartupAsDouble + 10d;
                while (scale.enabled && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
                Assert.IsFalse(scale.enabled);
                scale.SelectCase(0); scale.SelectMode(1);
                benchmark.Cancel();
                Assert.IsTrue(scale.enabled); Assert.AreEqual(1, scale.ActiveCase); Assert.AreEqual(2, scale.ActiveMode);
                Assert.AreEqual(position, scale.Camera.transform.position); Assert.AreEqual(rotation, scale.Camera.transform.rotation);
                Assert.AreEqual(size, scale.Camera.orthographicSize);
                Assert.IsFalse(File.Exists(Path.Combine(output, "complete.json")));
                yield return null;
            }
            finally
            {
                if (benchmark != null) Object.DestroyImmediate(benchmark.gameObject);
                if (Directory.Exists(output)) Directory.Delete(output, true);
            }
            LogAssert.NoUnexpectedReceived();
        }

        private IEnumerator VerifyRestoration(int interruption)
        {
            yield return SceneManager.LoadSceneAsync("Assets/_OptimizeTool/Scene/OptimizeToolSample.unity");
            yield return null;
            var overview = Object.FindFirstObjectByType<Samples.OptimizeToolOverviewController>();
            overview.SelectFeature(3);
            Camera camera = Camera.main;
            Vector3 position = new Vector3(3f, 4f, -12f);
            Quaternion rotation = Quaternion.Euler(11f, 17f, 0f);
            camera.GetComponent<Samples.OptimizeToolFreeCamera>().SetPose(position, rotation);
            float fov = camera.fieldOfView;
            int vsync = QualitySettings.vSyncCount, target = Application.targetFrameRate;
            int interval = OnDemandRendering.renderFrameInterval;
            bool background = Application.runInBackground;
            var ui = Object.FindFirstObjectByType<Samples.OptimizeToolShowcaseUI>();
            string output = Path.Combine(Path.GetTempPath(), "OptimizeToolRestore-" + Guid.NewGuid().ToString("N"));
            Samples.OptimizeToolPerformanceBenchmark benchmark = null;
            int calls = 0, exit = -1;
            try
            {
                benchmark = Samples.OptimizeToolPerformanceBenchmark.StartEditorRun(output, 1);
                benchmark.Completed += code => { calls++; exit = code; };
                double deadline = Time.realtimeSinceStartupAsDouble + 15d;
                while (overview.enabled && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
                Assert.IsFalse(overview.enabled); Assert.IsFalse(ui.enabled); Assert.AreEqual(0, overview.ActiveIndex);
                if (interruption == 0) benchmark.Cancel();
                else if (interruption == 1) benchmark.enabled = false;
                else if (interruption == 2) Object.Destroy(benchmark.gameObject);
                else
                {
                    Directory.CreateDirectory(Path.Combine(output, "metadata.json"));
                    LogAssert.Expect(LogType.Exception, new Regex("(IOException|UnauthorizedAccessException)"));
                }
                while (calls == 0 && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
                yield return null;
                Assert.AreEqual(1, calls); Assert.AreEqual(interruption == 3 ? 1 : 2, exit);
                Assert.IsTrue(overview.enabled); Assert.IsTrue(ui.enabled); Assert.AreEqual(3, overview.ActiveIndex);
                Assert.That(Vector3.Distance(position, camera.transform.position), Is.LessThan(0.00001f));
                Assert.That(Quaternion.Angle(rotation, camera.transform.rotation), Is.LessThan(0.01f));
                Assert.AreEqual(fov, camera.fieldOfView);
                Assert.AreEqual(vsync, QualitySettings.vSyncCount); Assert.AreEqual(target, Application.targetFrameRate);
                Assert.AreEqual(interval, OnDemandRendering.renderFrameInterval); Assert.AreEqual(background, Application.runInBackground);
                Assert.IsFalse(File.Exists(Path.Combine(output, "complete.json")));
                Assert.IsTrue(File.Exists(Path.Combine(output, "error.txt")));
                if (benchmark != null) Object.DestroyImmediate(benchmark.gameObject);
                benchmark = null;
                Assert.AreEqual(1, calls);
            }
            finally
            {
                if (benchmark != null) Object.DestroyImmediate(benchmark.gameObject);
                if (Directory.Exists(output)) Directory.Delete(output, true);
            }
            LogAssert.NoUnexpectedReceived();
        }
    }
}
#endif
