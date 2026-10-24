#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Common.OptimizeTool.Tests
{
    public sealed class OptimizeToolPhysicsDemoTests
    {
        private string _folder;
        private Samples.OptimizeToolOverviewController _overview;
        private Samples.OptimizeToolPhysicsDemo _demo;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            yield return SceneManager.LoadSceneAsync("Assets/_OptimizeTool/Scene/OptimizeToolSample.unity");
            yield return null;
            _overview = Object.FindFirstObjectByType<Samples.OptimizeToolOverviewController>();
            _demo = _overview.PhysicsDemo;
            _folder = "Assets/__PhysicsDemoTests_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(_folder));
            Set(_demo.Recorder, "_savePath", _folder);
            Set(_demo.Recorder, "_animationFileName", "TestTake");
            Set(_demo, "_savedPrefab", null);
            _overview.SelectFeature(4);
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_overview != null) Object.Destroy(_overview.gameObject);
            yield return null;
            if (!string.IsNullOrEmpty(_folder)) AssetDatabase.DeleteAsset(_folder);
        }

        [UnityTest]
        public IEnumerator RebindingTheExistingPanelPreservesExternalListenersAndDoesNotDuplicateControls()
        {
            var ui = _overview.GetComponentInChildren<Samples.OptimizeToolShowcaseUI>(true);
            Button original = ButtonNamed("ORIGINAL");
            int externalCalls = 0;
            original.onClick.AddListener(() => externalCalls++);
            int count = ui.GetComponentsInChildren<Button>(true).Length;
            for (int i = 0; i < 3; i++)
            {
                ui.enabled = false;
                ui.enabled = true;
                original.onClick.Invoke();
                Assert.AreEqual(i + 1, externalCalls);
                Assert.AreEqual(0, _overview.ActiveIndex);
                ButtonNamed("PHYSICS").onClick.Invoke();
                Assert.AreEqual(4, _overview.ActiveIndex);
            }
            Assert.AreEqual(count, ui.GetComponentsInChildren<Button>(true).Length);
            ButtonNamed("RESET").onClick.Invoke();
            Assert.AreEqual(Samples.OptimizeToolPhysicsDemo.DemoState.Ready, _demo.State);
            yield return null;
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator DeletedPlaybackDuringComparisonReportsFailureAndCanRetry()
        {
            Set(_demo, "_duration", 0.7f);
            Set(_demo, "_releaseDelay", 0.1f);
            _demo.Record();
            double deadline = Time.realtimeSinceStartupAsDouble + 10d;
            while (_demo.IsBusy && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.NotNull(_demo.SavedPrefab, _demo.LastError);
            string path = AssetDatabase.GetAssetPath(_demo.SavedPrefab);
            byte[] before = File.ReadAllBytes(path);
            yield return new WaitForFixedUpdate();
            LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex(".*"));
            Object.Destroy(_demo.Playback);
            yield return null;
            yield return new WaitForFixedUpdate();
            yield return null;
            Assert.AreEqual(Samples.OptimizeToolPhysicsDemo.DemoState.Error, _demo.State);
            Assert.IsNull(_demo.Playback);
            Assert.IsNull(typeof(Samples.OptimizeToolPhysicsDemo).GetField("_routine", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_demo));
            StringAssert.Contains("PLAYBACK FAILED", _demo.GetStatusText());
            StringAssert.Contains("Playback did not change", _demo.GetStatusText());
            Assert.IsTrue(_demo.SourceRoot.GetComponentsInChildren<Rigidbody>().All(b => b.isKinematic));
            Assert.AreEqual(before, File.ReadAllBytes(path));
            ButtonNamed("COMPARE").onClick.Invoke();
            deadline = Time.realtimeSinceStartupAsDouble + 10d;
            while (_demo.State == Samples.OptimizeToolPhysicsDemo.DemoState.Comparing && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.AreEqual(Samples.OptimizeToolPhysicsDemo.DemoState.Finished, _demo.State, _demo.LastError);
            Assert.AreEqual(_demo.RecordingReference.ObservationCount, _demo.ComparisonSampleCount);
            Assert.AreEqual(before, File.ReadAllBytes(path));
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator DisablingComparisonBeforeItsFirstTickRestoresReadyAndAllowsReplay()
        {
            Set(_demo, "_duration", 0.3f);
            Set(_demo, "_releaseDelay", 0.1f);
            _demo.Record();
            double deadline = Time.realtimeSinceStartupAsDouble + 10d;
            while (_demo.IsBusy && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            _demo.Compare();
            _demo.enabled = false;
            Assert.AreEqual(Samples.OptimizeToolPhysicsDemo.DemoState.Ready, _demo.State);
            _demo.enabled = true;
            _demo.Compare();
            deadline = Time.realtimeSinceStartupAsDouble + 10d;
            while (_demo.State == Samples.OptimizeToolPhysicsDemo.DemoState.Comparing && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.AreEqual(Samples.OptimizeToolPhysicsDemo.DemoState.Finished, _demo.State, _demo.LastError);
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator InvalidPlaybackIsRejectedWithoutRunningClonedGameplayCallbacks()
        {
            var template = new GameObject("Unsafe playback");
            template.SetActive(false);
            var clip = new AnimationClip { legacy = true, name = "Unsafe" };
            Animation animation = template.AddComponent<Animation>();
            animation.AddClip(clip, clip.name);
            animation.clip = clip;
            template.AddComponent<PhysicsRecorderCallbackProbe>();
            template.GetComponent<Rigidbody>().isKinematic = true;
            template.SetActive(true);
            int before = PhysicsRecorderCallbackProbe.AwakeCount;
            try
            {
                Set(_demo, "_savedPrefab", template);
                LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("The saved recording must"));
                _demo.Compare();
                Assert.AreEqual(Samples.OptimizeToolPhysicsDemo.DemoState.Error, _demo.State);
                Assert.IsNull(_demo.Playback);
                Assert.AreEqual(before, PhysicsRecorderCallbackProbe.AwakeCount);
                yield return null;
                Transform anchor = (Transform)typeof(Samples.OptimizeToolPhysicsDemo).GetField("_replayAnchor", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_demo);
                Assert.IsEmpty(anchor.GetComponentsInChildren<Animation>(true));
                Assert.IsNull(anchor.Find("Playback validation"));
            }
            finally { Object.Destroy(template); Object.Destroy(clip); }
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator TwoDimensionalPhysicsAndDamagedReferenceDoNotEnterComparison()
        {
            Set(_demo, "_duration", 0.3f);
            Set(_demo, "_releaseDelay", 0.1f);
            _demo.Record();
            double deadline = Time.realtimeSinceStartupAsDouble + 10d;
            while (_demo.IsBusy && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            _demo.ResetDemo();
            GameObject saved = _demo.SavedPrefab;
            GameObject invalid = Object.Instantiate(saved);
            invalid.AddComponent<Rigidbody2D>().simulated = false;
            try
            {
                Set(_demo, "_savedPrefab", invalid);
                LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("no physics"));
                _demo.Compare();
                Assert.AreEqual(Samples.OptimizeToolPhysicsDemo.DemoState.Error, _demo.State);
                Assert.IsNull(_demo.Playback);
                Set(_demo, "_savedPrefab", saved);
                PhysicsRecordingReference reference = _demo.RecordingReference;
                var times = (float[])typeof(PhysicsRecordingReference).GetField("_times", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(reference);
                float original = times[1];
                try
                {
                    times[1] = times[0];
                    LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("observation times"));
                    _demo.Compare();
                    Assert.AreEqual(Samples.OptimizeToolPhysicsDemo.DemoState.Error, _demo.State);
                    Assert.AreEqual(0, _demo.ComparisonSampleCount);
                    Assert.IsNull(_demo.Playback);
                }
                finally { times[1] = original; }
                _demo.Compare();
                deadline = Time.realtimeSinceStartupAsDouble + 10d;
                while (_demo.State == Samples.OptimizeToolPhysicsDemo.DemoState.Comparing && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
                Assert.AreEqual(Samples.OptimizeToolPhysicsDemo.DemoState.Finished, _demo.State, _demo.LastError);
            }
            finally { Object.Destroy(invalid); }
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator EmptyErrorDoesNotDisplayARecordingFailureInTheReadyPanel()
        {
            Set(_demo, "<LastError>k__BackingField", string.Empty);
            yield return new WaitForSecondsRealtime(0.15f);
            Assert.AreEqual(Samples.OptimizeToolPhysicsDemo.DemoState.Ready, _demo.State);
            StringAssert.Contains("No recording yet", _demo.GetStatusText());
            StringAssert.DoesNotContain("Recording failed:", _demo.GetStatusText());
            Text status = _overview.GetComponentsInChildren<Text>(true)
                .Single(text => text.text.Contains("RIGHT  RECORDED PREFAB"));
            StringAssert.Contains("No recording yet", status.text);
            StringAssert.DoesNotContain("Recording failed:", status.text);
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator RecordButtonSavesActualPhysicsAndComparesTheGeneratedPrefab()
        {
            Assert.AreEqual(5, _overview.FeatureCount);
            Assert.IsFalse(_demo.CanCompare);
            Rigidbody[] bodies = _demo.SourceRoot.GetComponentsInChildren<Rigidbody>();
            Assert.AreEqual(6, bodies.Length);
            Vector3[] initial = bodies.Select(b => b.transform.localPosition).ToArray();
            ButtonNamed("RECORD").onClick.Invoke();
            Assert.AreEqual(Samples.OptimizeToolPhysicsDemo.DemoState.Recording, _demo.State);
            _overview.SelectFeature(0);
            Assert.AreEqual(4, _overview.ActiveIndex, "Mode switching must not interrupt an active recording.");
            yield return new WaitForSeconds(0.2f);
            Assert.IsTrue(bodies.All(b => b.isKinematic));
            for (int i = 0; i < bodies.Length; i++) Assert.AreEqual(initial[i], bodies[i].transform.localPosition);
            yield return new WaitForSeconds(0.6f);
            Assert.IsTrue(bodies.Any(b => !b.isKinematic));
            Assert.IsTrue(bodies.Where((b, i) => Vector3.Distance(b.transform.localPosition, initial[i]) > 0.1f).Any());
            double deadline = Time.realtimeSinceStartupAsDouble + 15d;
            while (_demo.IsBusy && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.IsTrue((_demo.Recorder.Result?.Succeeded ?? false), _demo.LastError);
            Assert.NotNull(_demo.Playback);
            Assert.AreEqual(_folder + "/TestTake.prefab", AssetDatabase.GetAssetPath(_demo.SavedPrefab));
            Assert.IsEmpty(_demo.Playback.GetComponentsInChildren<Rigidbody>(true));
            Assert.IsEmpty(_demo.Playback.GetComponentsInChildren<Collider>(true));
            Assert.IsEmpty(_demo.Playback.GetComponentsInChildren<MonoBehaviour>(true));
            AnimationClip clip = _demo.SavedPrefab.GetComponent<Animation>().clip;
            Assert.IsTrue(clip.legacy);
            Assert.That(clip.length, Is.EqualTo(_demo.Duration).Within(0.0001f));
            AnimationCurve y = AnimationUtility.GetEditorCurve(clip,
                EditorCurveBinding.FloatCurve("Body_01", typeof(Transform), "m_LocalPosition.y"));
            Assert.Greater(y.length, 10, "This must contain sampled physics, not a two-key preview.");
            Assert.Less(y.Evaluate(clip.length), y.Evaluate(0f) - 0.5f);
            Assert.That(y.Evaluate(0.2f), Is.EqualTo(y.Evaluate(0f)).Within(0.001f));
            deadline = Time.realtimeSinceStartupAsDouble + 15d;
            while (_demo.State == Samples.OptimizeToolPhysicsDemo.DemoState.Comparing
                && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.AreEqual(Samples.OptimizeToolPhysicsDemo.DemoState.Finished, _demo.State, _demo.LastError);
            Assert.That(_demo.Elapsed, Is.EqualTo(clip.length).Within(0.0001f));
            Assert.IsFalse(float.IsNaN(_demo.MaxPositionError));
            Transform replayBody = _demo.Playback.transform.Find("Body_01");
            Assert.That(replayBody.localPosition.y, Is.EqualTo(y.Evaluate(clip.length)).Within(0.001f));
            Assert.IsTrue(bodies.All(b => b.isKinematic));
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator RecordedReferenceIsIndependentOfNewPhysicsSettingsAndDetectsAnAlteredClip()
        {
            Set(_demo, "_duration", 0.8f);
            Set(_demo, "_releaseDelay", 0.1f);
            _demo.Record();
            double deadline = Time.realtimeSinceStartupAsDouble + 10d;
            while (_demo.IsBusy && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.NotNull(_demo.RecordingReference, _demo.LastError);
            while (_demo.State == Samples.OptimizeToolPhysicsDemo.DemoState.Comparing
                && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            float baselinePositionError = _demo.MaxPositionError;
            float baselineRotationError = _demo.MaxRotationError;
            _demo.ResetDemo();
            var reference = _demo.RecordingReference;
            Vector3 original = reference.Sample(1, 0.4f).Position;
            Vector3 gravity = Physics.gravity;
            float step = Time.fixedDeltaTime;
            try
            {
                Physics.gravity = Vector3.up * 40f;
                Time.fixedDeltaTime = 0.07f; // Auditing must not skip the original 0.02-second observations.
                _demo.Compare();
                deadline = Time.realtimeSinceStartupAsDouble + 10d;
                while (_demo.State == Samples.OptimizeToolPhysicsDemo.DemoState.Comparing
                    && Time.realtimeSinceStartupAsDouble < deadline)
                {
                    Assert.IsTrue(_demo.SourceRoot.GetComponentsInChildren<Rigidbody>().All(b => b.isKinematic));
                    yield return null;
                }
                Assert.AreEqual(Samples.OptimizeToolPhysicsDemo.DemoState.Finished, _demo.State, _demo.LastError);
                Assert.AreEqual(reference.ObservationCount, _demo.ComparisonSampleCount);
                Assert.That(_demo.MaxPositionError, Is.EqualTo(baselinePositionError).Within(0.000001f));
                Assert.That(_demo.MaxRotationError, Is.EqualTo(baselineRotationError).Within(0.000001f));
                AnimationClip clip = _demo.SavedPrefab.GetComponent<Animation>().clip;
                var binding = EditorCurveBinding.FloatCurve("Body_01", typeof(Transform), "m_LocalPosition.x");
                AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
                Keyframe[] keys = curve.keys;
                for (int i = 0; i < keys.Length; i++) keys[i].value += 0.25f;
                curve.keys = keys;
                AnimationUtility.SetEditorCurve(clip, binding, curve);
                Quaternion altered = Quaternion.Euler(0f, 90f, 0f);
                string[] components = { "x", "y", "z", "w" };
                for (int i = 0; i < components.Length; i++)
                    AnimationUtility.SetEditorCurve(clip,
                        EditorCurveBinding.FloatCurve("Body_01", typeof(Transform), "m_LocalRotation." + components[i]),
                        AnimationCurve.Constant(0f, clip.length, altered[i]));
                AssetDatabase.SaveAssetIfDirty(clip);
                _demo.Compare();
                deadline = Time.realtimeSinceStartupAsDouble + 10d;
                while (_demo.State == Samples.OptimizeToolPhysicsDemo.DemoState.Comparing
                    && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
                Assert.AreEqual(Samples.OptimizeToolPhysicsDemo.DemoState.Finished, _demo.State, _demo.LastError);
                Assert.Greater(_demo.MaxPositionError, 0.24f, "Comparing the clip with itself would hide this deliberate corruption.");
                Assert.Greater(_demo.MaxRotationError, 80f);
                Assert.AreEqual(original, reference.Sample(1, 0.4f).Position);
            }
            finally { Physics.gravity = gravity; Time.fixedDeltaTime = step; }
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator MissingReferenceRejectsPlaybackWithoutChangingTheSavedTake()
        {
            Set(_demo, "_duration", 0.3f);
            Set(_demo, "_releaseDelay", 0.1f);
            _demo.Record();
            yield return WaitForFinished();
            _demo.ResetDemo();
            string prefabPath = AssetDatabase.GetAssetPath(_demo.SavedPrefab);
            byte[] before = File.ReadAllBytes(prefabPath);
            AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(_demo.RecordingReference));
            LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("Recording reference is required"));
            _demo.Compare();
            yield return null;
            Assert.AreEqual(Samples.OptimizeToolPhysicsDemo.DemoState.Error, _demo.State);
            Assert.IsNull(_demo.Playback); Assert.IsFalse(_demo.HasRecordingReference);
            Assert.AreEqual(0, _demo.ComparisonSampleCount);
            StringAssert.Contains("RECORD a new take", _demo.GetStatusText());
            StringAssert.DoesNotContain("COMPARE or RECORD", _demo.GetStatusText());
            StringAssert.DoesNotContain("Saved recording ready", _demo.GetStatusText());
            CollectionAssert.AreEqual(before, File.ReadAllBytes(prefabPath));
            Assert.IsTrue(_demo.SourceRoot.GetComponentsInChildren<Rigidbody>().All(b => b.isKinematic));
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator SceneReloadLoadsTheSavedReferenceWithoutStartingANewRecording()
        {
            Set(_demo, "_duration", 0.6f);
            Set(_demo, "_releaseDelay", 0.1f);
            _demo.Record();
            double deadline = Time.realtimeSinceStartupAsDouble + 10d;
            while (_demo.IsBusy && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            while (_demo.State == Samples.OptimizeToolPhysicsDemo.DemoState.Comparing
                && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            float baselinePositionError = _demo.MaxPositionError;
            float baselineRotationError = _demo.MaxRotationError;
            string path = AssetDatabase.GetAssetPath(_demo.SavedPrefab);
            string referencePath = AssetDatabase.GetAssetPath(_demo.RecordingReference);
            byte[] bytes = File.ReadAllBytes(referencePath);
            yield return SceneManager.LoadSceneAsync("Assets/_OptimizeTool/Scene/OptimizeToolSample.unity");
            yield return null;
            _overview = Object.FindFirstObjectByType<Samples.OptimizeToolOverviewController>();
            _demo = _overview.PhysicsDemo;
            Set(_demo, "_savedPrefab", AssetDatabase.LoadAssetAtPath<GameObject>(path));
            Set(_demo, "_recordingReference", null);
            _overview.SelectFeature(4);
            _demo.Compare();
            deadline = Time.realtimeSinceStartupAsDouble + 10d;
            while (_demo.State == Samples.OptimizeToolPhysicsDemo.DemoState.Comparing
                && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.AreEqual(Samples.OptimizeToolPhysicsDemo.DemoState.Finished, _demo.State, _demo.LastError);
            Assert.IsNull(_demo.Recorder.Result);
            Assert.AreEqual(referencePath, AssetDatabase.GetAssetPath(_demo.RecordingReference));
            Assert.AreEqual(bytes, File.ReadAllBytes(referencePath));
            Assert.AreEqual(_demo.RecordingReference.ObservationCount, _demo.ComparisonSampleCount);
            Assert.That(_demo.MaxPositionError, Is.EqualTo(baselinePositionError).Within(0.000001f));
            Assert.That(_demo.MaxRotationError, Is.EqualTo(baselineRotationError).Within(0.000001f));
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator CompareResetAndModeSwitchingPreserveTheTakeAndSupportRerecording()
        {
            Set(_demo, "_duration", 0.8f);
            Set(_demo, "_releaseDelay", 0.1f);
            _demo.Record();
            double deadline = Time.realtimeSinceStartupAsDouble + 10d;
            while (_demo.IsBusy && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.NotNull(_demo.SavedPrefab, _demo.LastError);
            string path = AssetDatabase.GetAssetPath(_demo.SavedPrefab);
            string guid = AssetDatabase.AssetPathToGUID(path);
            byte[] savedBytes = File.ReadAllBytes(path);
            _overview.SelectFeature(0);
            yield return null;
            Assert.AreEqual(9, _overview.ActiveRendererCount);
            Assert.AreEqual(108, _overview.ActiveTriangleCount);
            Assert.IsFalse(_demo.SourceRoot.gameObject.activeInHierarchy);
            _overview.SelectFeature(4);
            yield return null;
            Assert.AreEqual(Samples.OptimizeToolPhysicsDemo.DemoState.Ready, _demo.State);
            ButtonNamed("COMPARE").onClick.Invoke();
            yield return new WaitForSeconds(0.3f);
            ButtonNamed("RESET").onClick.Invoke();
            Assert.AreEqual(Samples.OptimizeToolPhysicsDemo.DemoState.Ready, _demo.State);
            Assert.AreEqual(0f, _demo.Elapsed);
            Assert.IsTrue(_demo.SourceRoot.GetComponentsInChildren<Rigidbody>().All(b => b.isKinematic));
            Object.Destroy(_demo.Playback);
            yield return null;
            Assert.DoesNotThrow(_demo.ResetDemo, "Reset must tolerate a playback instance deleted from the hierarchy.");
            Assert.NotNull(_demo.Playback);
            Assert.AreEqual(savedBytes, File.ReadAllBytes(path), "Compare and reset must not write assets.");
            _demo.Record();
            deadline = Time.realtimeSinceStartupAsDouble + 10d;
            while (_demo.IsBusy && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.IsTrue((_demo.Recorder.Result?.Succeeded ?? false), _demo.LastError);
            Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID(path));
            Assert.NotNull(_demo.Playback);
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator RerecordingReplacesAllThreeAssetsAndClearsThePreviousComparisonStatus()
        {
            Set(_demo, "_duration", 0.53f);
            Set(_demo, "_releaseDelay", 0.1f);
            _demo.Record();
            yield return WaitForFinished();
            Assert.Greater(_demo.ComparisonSampleCount, 0);
            var paths = OutputPaths();
            string[] guids = paths.Select(AssetDatabase.AssetPathToGUID).ToArray();
            byte[][] before = paths.Select(File.ReadAllBytes).ToArray();
            Set(_demo, "_duration", 0.87f);
            _demo.Record();
            Assert.AreEqual(0, _demo.ComparisonSampleCount, "A new recording must not display the previous take's audit count.");
            StringAssert.Contains("0.00 / 0.87 s", _demo.GetStatusText());
            StringAssert.Contains("Capturing original physics observations", _demo.GetStatusText());
            yield return WaitForFinished();
            Assert.IsTrue((_demo.Recorder.Result?.Succeeded ?? false), _demo.LastError);
            Assert.That(_demo.RecordingReference.Duration, Is.EqualTo(0.87f).Within(0.0001f));
            Assert.AreSame(_demo.Recorder.Result?.Clip, _demo.RecordingReference.Clip);
            for (int i = 0; i < paths.Length; i++)
                Assert.AreEqual(guids[i], AssetDatabase.AssetPathToGUID(paths[i]));
            Assert.IsFalse(before[0].SequenceEqual(File.ReadAllBytes(paths[0])));
            Assert.IsFalse(before[2].SequenceEqual(File.ReadAllBytes(paths[2])));
            Assert.AreEqual(_demo.RecordingReference.ObservationCount, _demo.ComparisonSampleCount);
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator FailedOverwriteRestoresTheTakeAndSupportsCompareAndRerecord()
        {
            yield return AssertFailedSaveCanRecover(true);
        }

        [UnityTest]
        public IEnumerator FailedFirstSaveRemovesPartialAssetsAndSupportsRerecord()
        {
            yield return AssertFailedSaveCanRecover(false);
        }

        private IEnumerator AssertFailedSaveCanRecover(bool overwrite)
        {
            Set(_demo, "_duration", 0.53f);
            Set(_demo, "_releaseDelay", 0.1f);
            string[] paths = OutputPaths();
            byte[][] bytes = null, metas = null;
            if (overwrite)
            {
                _demo.Record();
                yield return WaitForFinished();
                bytes = paths.Select(File.ReadAllBytes).ToArray();
                metas = paths.Select(p => File.ReadAllBytes(p + ".meta")).ToArray();
            }
            Set(_demo, "_duration", 1f);
            _demo.Record();
            yield return new WaitForSeconds(0.2f);
            Assert.IsTrue(_demo.Recorder.IsRecording);
            // Suspend import only for the synchronous save, not across a yielded frame.
            AssetDatabase.StartAssetEditing();
            try { Assert.Throws<InvalidOperationException>(() => _demo.Recorder.StopRecording()); }
            finally { AssetDatabase.StopAssetEditing(); }
            LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("Unable to save and link both recording assets"));
            double deadline = Time.realtimeSinceStartupAsDouble + 5d;
            while (_demo.State == Samples.OptimizeToolPhysicsDemo.DemoState.Recording
                && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.AreEqual(Samples.OptimizeToolPhysicsDemo.DemoState.Error, _demo.State);
            Assert.AreEqual("RECORDING FAILED", _demo.SourceRoot.parent.GetComponentInChildren<TextMesh>(true).text);
            StringAssert.Contains("LEFT   RECORDING FAILED", _demo.GetStatusText());
            StringAssert.Contains("/ 1.00 s", _demo.GetStatusText());
            StringAssert.Contains(overwrite ? "Previous saved take available" : "No saved take available", _demo.GetStatusText());
            StringAssert.DoesNotContain("Peak recorded-pose delta", _demo.GetStatusText());
            yield return null; // Let the board and controls observe the error transition.
            Assert.IsTrue(ButtonNamed("RECORD").interactable);
            Assert.AreEqual(overwrite, ButtonNamed("COMPARE").interactable);
            Assert.IsTrue(ButtonNamed("RESET").interactable);
            Assert.IsFalse((_demo.Recorder.Result?.Succeeded ?? false));
            Assert.IsTrue(_demo.CanRecord);
            Assert.IsTrue(_demo.SourceRoot.GetComponentsInChildren<Rigidbody>().All(b => b.isKinematic));
            for (int i = 0; i < paths.Length; i++)
            {
                if (overwrite)
                {
                    Assert.AreEqual(bytes[i], File.ReadAllBytes(paths[i]), paths[i]);
                    Assert.AreEqual(metas[i], File.ReadAllBytes(paths[i] + ".meta"), paths[i] + ".meta");
                }
                else
                {
                    Assert.IsFalse(File.Exists(paths[i]), paths[i]);
                    Assert.IsFalse(File.Exists(paths[i] + ".meta"), paths[i] + ".meta");
                }
            }
            if (overwrite)
            {
                _demo.Compare();
                yield return WaitForFinished();
                Assert.That(_demo.Elapsed, Is.EqualTo(0.53f).Within(0.0001f));
                Assert.AreEqual(_demo.RecordingReference.ObservationCount, _demo.ComparisonSampleCount);
            }
            Set(_demo, "_duration", 0.65f);
            _demo.Record();
            yield return WaitForFinished();
            Assert.IsFalse(_demo.Recorder.IsFaulted);
            Assert.IsTrue((_demo.Recorder.Result?.Succeeded ?? false), _demo.LastError);
            Assert.IsNull(_demo.LastError);
            Assert.That(_demo.RecordingReference.Duration, Is.EqualTo(0.65f).Within(0.0001f));
            Assert.AreSame(_demo.Recorder.Result?.Clip, _demo.RecordingReference.Clip);
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator MissingReferenceStaysRejectedAcrossResetAndModesUntilANewTakeIsRecorded()
        {
            Set(_demo, "_duration", 0.6f);
            Set(_demo, "_releaseDelay", 0.1f);
            _demo.Record();
            yield return WaitForFinished();
            string[] paths = OutputPaths();
            string[] guids = paths.Take(2).Select(AssetDatabase.AssetPathToGUID).ToArray();
            AssetDatabase.DeleteAsset(paths[2]);
            byte[][] bytes = paths.Take(2).Select(File.ReadAllBytes).ToArray();
            Set(_demo, "_duration", 0.8f);
            LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("Recording reference is required"));
            _demo.ResetDemo();
            LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("Recording reference is required"));
            _demo.Compare();
            Assert.IsNull(_demo.Playback);
            _overview.SelectFeature(0);
            yield return null;
            LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("Recording reference is required"));
            _overview.SelectFeature(4);
            yield return null;
            Assert.AreEqual(Samples.OptimizeToolPhysicsDemo.DemoState.Error, _demo.State);
            Assert.IsFalse(_demo.HasRecordingReference); Assert.IsNull(_demo.Playback);
            Assert.AreEqual(0, _demo.ComparisonSampleCount);
            StringAssert.DoesNotContain("Prefab playback remains available", _demo.GetStatusText());
            for (int i = 0; i < 2; i++) CollectionAssert.AreEqual(bytes[i], File.ReadAllBytes(paths[i]));
            _demo.Record();
            yield return WaitForFinished();
            Assert.IsTrue(_demo.HasRecordingReference);
            Assert.AreEqual(_demo.RecordingReference.ObservationCount, _demo.ComparisonSampleCount);
            for (int i = 0; i < 2; i++) Assert.AreEqual(guids[i], AssetDatabase.AssetPathToGUID(paths[i]));
            LogAssert.NoUnexpectedReceived();
        }

        private string[] OutputPaths() => new[]
        {
            _folder + "/TestTake.anim", _folder + "/TestTake.prefab", _folder + "/TestTake.reference.asset"
        };

        private IEnumerator WaitForFinished()
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 10d;
            while ((_demo.State == Samples.OptimizeToolPhysicsDemo.DemoState.Recording
                || _demo.State == Samples.OptimizeToolPhysicsDemo.DemoState.Comparing)
                && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.AreEqual(Samples.OptimizeToolPhysicsDemo.DemoState.Finished, _demo.State, _demo.LastError);
        }

        [UnityTest]
        public IEnumerator DisablingShowcaseSavesTheTakeAndRestoresControls()
        {
            yield return AssertInterruptedRecordingCanResume(false);
        }

        [UnityTest]
        public IEnumerator DisablingOnlyTheDemoComponentSavesTheTakeAndRestoresControls()
        {
            yield return AssertInterruptedRecordingCanResume(true);
        }

        private IEnumerator AssertInterruptedRecordingCanResume(bool componentOnly)
        {
            Set(_demo, "_duration", 0.8f);
            Set(_demo, "_releaseDelay", 0.1f);
            _demo.Record();
            yield return new WaitForSeconds(0.2f);
            Assert.IsTrue(_demo.Recorder.IsRecording);
            if (componentOnly) _demo.enabled = false;
            else _overview.gameObject.SetActive(false);
            Assert.IsFalse(_demo.Recorder.IsRecording, "Disabling must finish even if the recorder remains enabled.");
            Assert.IsTrue((_demo.Recorder.Result?.Succeeded ?? false), _demo.LastError);
            Assert.AreSame(_demo.Recorder.Result?.Prefab, _demo.SavedPrefab);
            Assert.That(_demo.Elapsed, Is.EqualTo(_demo.Recorder.Result?.Clip.length).Within(0.0001f));
            Assert.IsFalse(_demo.IsBusy);
            Assert.IsFalse(_demo.CanRecord);
            Assert.IsFalse(_demo.CanCompare);
            if (componentOnly) _demo.enabled = true;
            else _overview.gameObject.SetActive(true);
            yield return null;
            Assert.AreEqual(Samples.OptimizeToolPhysicsDemo.DemoState.Ready, _demo.State);
            Assert.IsTrue(_demo.CanRecord);
            Assert.IsTrue(_demo.CanCompare);
            Assert.AreEqual("RECORDED ORIGINAL", _demo.SourceRoot.parent.GetComponentInChildren<TextMesh>(true).text);
            _demo.Compare();
            double deadline = Time.realtimeSinceStartupAsDouble + 10d;
            while (_demo.State == Samples.OptimizeToolPhysicsDemo.DemoState.Comparing
                && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.AreEqual(Samples.OptimizeToolPhysicsDemo.DemoState.Finished, _demo.State, _demo.LastError);
            _demo.ResetDemo();
            Assert.AreEqual(Samples.OptimizeToolPhysicsDemo.DemoState.Ready, _demo.State);
            _overview.SelectFeature(0);
            Assert.AreEqual(0, _overview.ActiveIndex);
            _overview.SelectFeature(4);
            _demo.Record();
            deadline = Time.realtimeSinceStartupAsDouble + 10d;
            while (_demo.IsBusy && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.IsTrue((_demo.Recorder.Result?.Succeeded ?? false), _demo.LastError);
            Assert.NotNull(_demo.Playback);
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator DisablingBeforeTheFirstPhysicsTickCancelsWithoutLockingControls()
        {
            _demo.Record();
            _demo.enabled = false;
            Assert.IsFalse(_demo.IsBusy);
            Assert.IsFalse(_demo.Recorder.IsRecording);
            Assert.IsFalse((_demo.Recorder.Result?.Succeeded ?? false));
            Assert.IsNull(_demo.SavedPrefab);
            _demo.enabled = true;
            _demo.ResetDemo();
            Assert.AreEqual(Samples.OptimizeToolPhysicsDemo.DemoState.Ready, _demo.State);
            Assert.IsTrue(_demo.CanRecord);
            _overview.SelectFeature(0);
            Assert.AreEqual(0, _overview.ActiveIndex);
            yield return new WaitForFixedUpdate();
            Assert.IsFalse(_demo.Recorder.IsRecording, "The cancelled coroutine must not start recording later.");
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator FractionalDurationComparesAndFreezesAtTheSameClipEndpoint()
        {
            Set(_demo, "_duration", 0.53f);
            Set(_demo, "_releaseDelay", 0.1f);
            var positions = (Vector3[])typeof(Samples.OptimizeToolPhysicsDemo)
                .GetField("_initialPositions", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_demo);
            // Keep every body away from collisions so time alignment can be measured separately.
            for (int i = 0; i < positions.Length; i++) positions[i] = new Vector3(100f + i * 3f, 50f, 100f);
            _demo.Record();
            double deadline = Time.realtimeSinceStartupAsDouble + 10d;
            while (_demo.IsBusy && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.NotNull(_demo.Playback, _demo.LastError);
            Rigidbody[] bodies = _demo.SourceRoot.GetComponentsInChildren<Rigidbody>();
            float middleDelta = 0f;
            int middleSamples = 0;
            while (_demo.State == Samples.OptimizeToolPhysicsDemo.DemoState.Comparing
                && Time.realtimeSinceStartupAsDouble < deadline)
            {
                if (_demo.Elapsed >= 0.2f && _demo.Elapsed <= 0.48f)
                {
                    foreach (Rigidbody body in bodies)
                        middleDelta = Mathf.Max(middleDelta, Vector3.Distance(body.transform.localPosition,
                            _demo.Playback.transform.Find(body.name).localPosition));
                    middleSamples++;
                }
                yield return null;
            }
            Assert.AreEqual(Samples.OptimizeToolPhysicsDemo.DemoState.Finished, _demo.State, _demo.LastError);
            Assert.Greater(middleSamples, 0);
            Assert.Less(middleDelta, 0.003f);
            Assert.That(_demo.Elapsed, Is.EqualTo(0.53f).Within(0.0001f));
            Assert.Less(_demo.MaxPositionError, 0.003f, "The endpoint must not add an artificial peak error.");
            float endpointDelta = 0f;
            foreach (Rigidbody body in bodies)
            {
                Transform replay = _demo.Playback.transform.Find(body.name);
                float distance = Vector3.Distance(body.transform.localPosition, replay.localPosition);
                endpointDelta = Mathf.Max(endpointDelta, distance);
                Assert.Less(distance, 0.003f, body.name);
                Assert.Less(Quaternion.Angle(body.transform.localRotation, replay.localRotation), 0.06f, body.name);
                Assert.IsTrue(body.isKinematic);
            }
            TestContext.WriteLine($"Physics endpoint: middle delta={middleDelta:R} m, endpoint delta={endpointDelta:R} m, peak={_demo.MaxPositionError:R} m");
            Vector3[] frozen = bodies.Select(b => b.position).ToArray();
            yield return new WaitForFixedUpdate();
            for (int i = 0; i < bodies.Length; i++)
            {
                Assert.That(Vector3.Distance(bodies[i].position, frozen[i]), Is.LessThan(0.0001f));
                Assert.That(Vector3.Distance(bodies[i].position, bodies[i].transform.position), Is.LessThan(0.0001f));
            }
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator CancellingAPendingTakeAfterReleasedFailureDoesNotReuseTheOldError()
        {
            Transform body = _demo.SourceRoot.Find("Body_01");
            _demo.Recorder.StartRecording(1f);
            body.name = "Changed";
            Assert.Throws<InvalidOperationException>(() => _demo.Recorder.StopRecording());
            body.name = "Body_01";
            _demo.Recorder.Release();
            _demo.Recorder.Init();
            Assert.IsFalse(_demo.Recorder.IsFaulted);
            _demo.Record();
            _demo.enabled = false;
            Assert.AreEqual(Samples.OptimizeToolPhysicsDemo.DemoState.Ready, _demo.State);
            Assert.IsFalse(_demo.IsBusy);
            Assert.IsNull(_demo.LastError);
            Assert.IsNull(_demo.SavedPrefab);
            _demo.enabled = true;
            _overview.SelectFeature(0);
            Assert.AreEqual(0, _overview.ActiveIndex);
            yield return new WaitForFixedUpdate();
            Assert.IsFalse(_demo.Recorder.IsRecording);
            LogAssert.NoUnexpectedReceived();
        }

        private static Button ButtonNamed(string name) => Object.FindObjectsByType<Button>(FindObjectsSortMode.None)
            .Single(b => b.name == name + " Button");

        [UnityTest]
        public IEnumerator RejectedPlaybackDoesNotLeaveAnActiveOrOrphanedInstance()
        {
            var invalid = new GameObject("Mismatched recording");
            var clip = new AnimationClip { legacy = true, name = "Mismatched" };
            Animation animation = invalid.AddComponent<Animation>();
            animation.playAutomatically = false;
            animation.AddClip(clip, clip.name);
            animation.clip = clip;
            Transform anchor = (Transform)typeof(Samples.OptimizeToolPhysicsDemo)
                .GetField("_replayAnchor", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_demo);
            int count = anchor.childCount;
            try
            {
                Set(_demo, "_savedPrefab", invalid);
                LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("The recording hierarchy differs"));
                _demo.Compare();
                Assert.AreEqual(Samples.OptimizeToolPhysicsDemo.DemoState.Error, _demo.State);
                Assert.IsNull(_demo.Playback);
                Assert.IsEmpty(anchor.GetComponentsInChildren<Animation>());
                yield return null;
                Assert.AreEqual(count, anchor.childCount);
            }
            finally
            {
                Object.Destroy(invalid);
                Object.Destroy(clip);
            }
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator PhysicsViewAndFocusKeepBothArenasInsideAPortraitCamera()
        {
            Camera camera = Camera.main;
            camera.aspect = 9f / 16f;
            try
            {
                _overview.UseBakeView();
                yield return null;
                AssertArenasVisible(camera);
                _overview.FocusCamera();
                yield return null;
                AssertArenasVisible(camera);
            }
            finally { camera.ResetAspect(); }
        }

        private void AssertArenasVisible(Camera camera)
        {
            foreach (Renderer renderer in _demo.SourceRoot.parent.parent.GetComponentsInChildren<Renderer>())
            {
                Bounds bounds = renderer.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = bounds.center + Vector3.Scale(bounds.extents,
                        new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    Vector3 point = camera.WorldToViewportPoint(corner);
                    Assert.That(point.z, Is.GreaterThan(camera.nearClipPlane), renderer.name);
                    Assert.That(point.x, Is.InRange(0f, 1f), renderer.name);
                    Assert.That(point.y, Is.InRange(0f, 1f), renderer.name);
                }
            }
        }

        private static void Set(object target, string field, object value) => target.GetType()
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
    }
}
#endif
