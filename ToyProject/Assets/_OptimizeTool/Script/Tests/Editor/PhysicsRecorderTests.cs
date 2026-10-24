#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Common.OptimizeTool.Tests
{
    public sealed class PhysicsRecorderTests
    {
        private string _folder;
        private GameObject _host, _root, _playback;
        private Transform _body;
        private PhysicsRecorder _recorder;
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        private void CreateFixture(float interval = 0.02f, bool onlyMoved = true)
        {
            _folder = "Assets/__PhysicsRecorderTests_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(_folder));
            _root = new GameObject("RecordedRoot");
            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = "Body";
            body.transform.SetParent(_root.transform, false);
            body.AddComponent<Rigidbody>().useGravity = false;
            _body = body.transform;
            _host = new GameObject("RecorderHost");
            _host.SetActive(false);
            _recorder = _host.AddComponent<PhysicsRecorder>();
            Set("_root", _root.transform);
            Set("_savePath", _folder);
            Set("_animationFileName", "Recorded");
            Set("_recordInterval", interval);
            Set("_recordOnlyIfMoved", onlyMoved);
            _host.SetActive(true);
            if (!_recorder.IsInitialized)
                _recorder.Init();
        }

        [TearDown]
        public void TearDown()
        {
            if (_recorder != null && (_recorder.IsInitialized || _recorder.IsFaulted))
                _recorder.Release();
            if (_playback != null) Object.DestroyImmediate(_playback);
            if (_host != null) Object.DestroyImmediate(_host);
            if (_root != null) Object.DestroyImmediate(_root);
            if (!string.IsNullOrEmpty(_folder)) AssetDatabase.DeleteAsset(_folder);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void InitialFinalAndIdlePosesSurviveSavedClipSampling(bool onlyMoved)
        {
            CreateFixture(0.1f, onlyMoved);
            _body.localRotation = Quaternion.Euler(0f, 20f, 0f);
            _recorder.StartRecording(0.8f);
            Observe(0.1f, 0f, 20f);
            Observe(0.2f, 0f, 20f);
            Observe(0.3f, 1f, 40f);
            Observe(0.4f, 1f, 40f);
            Observe(0.5f, 1f, 40f);
            Observe(0.6f, 2f, 80f);
            Observe(0.7f, 2f, 80f);
            Observe(0.8f, 2f, 80f);
            AssertSaved(0.8f);
            _playback = Object.Instantiate(_recorder.Result?.Prefab);
            AssertPose(0f, 0f, 20f);
            AssertPose(0.15f, 0f, 20f);
            AssertPose(0.25f, 0.5f, 30f);
            AssertPose(0.45f, 1f, 40f);
            AssertPose(0.55f, 1.5f, 60f);
            AssertPose(0.75f, 2f, 80f);
            AssertPose(0.8f, 2f, 80f);
            Assert.That(_body.localPosition.x, Is.EqualTo(2f), "Saving must not rewind the source.");
            Assert.That(_recorder.Result?.Prefab.transform.Find("Body").localPosition.x, Is.EqualTo(0f));
        }

        [Test]
        public void ReleaseClearsWorkingBuffersAndKeepsThePublishedResult()
        {
            CreateFixture();
            _recorder.StartRecording(0.1f);
            Frame(0.1f);
            PhysicsRecordingResult result = _recorder.Result;
            _recorder.Release();
            foreach (string name in new[] { "_tracks", "_trackTable", "_hierarchyBuffer", "_rigidbodyBuffer", "_poseBuffer", "_paths" })
            {
                var collection = (ICollection)typeof(PhysicsRecorder).GetField(name, PrivateInstance).GetValue(_recorder);
                Assert.AreEqual(0, collection.Count, name);
            }
            Assert.AreSame(result, _recorder.Result);
            Assert.IsTrue((_recorder.Result?.Succeeded ?? false));
        }

        [Test]
        public void SharedAssetSnapshotRestoresMetadataAndRemovesMetaOnlyPartialOutputs()
        {
            CreateFixture();
            _recorder.StartRecording(0.1f);
            Frame(0.1f);
            string path = AssetDatabase.GetAssetPath(_recorder.Result?.Clip);
            byte[] contents = File.ReadAllBytes(path), meta = File.ReadAllBytes(path + ".meta");
            var snapshot = new AssetFileSnapshot(path);
            Assert.AreEqual(contents.LongLength, snapshot.ContentBytes);
            File.WriteAllText(path + ".meta", "incomplete metadata");
            snapshot.Restore();
            Assert.AreEqual(contents, File.ReadAllBytes(path));
            Assert.AreEqual(meta, File.ReadAllBytes(path + ".meta"));
            string incomplete = _folder + "/Incomplete.anim";
            var absent = new AssetFileSnapshot(incomplete);
            File.WriteAllText(incomplete + ".meta", "partial");
            absent.Restore();
            Assert.IsFalse(File.Exists(incomplete + ".meta"));
        }

        [Test]
        public void StationaryRecordingRetainsBothBoundaryKeysAndFullDuration()
        {
            CreateFixture();
            _body.localPosition = new Vector3(3f, 2f, 1f);
            _recorder.StartRecording(0.3f);
            Frame(0.1f);
            Frame(0.2f);
            Frame(0.3f);
            AssertSaved(0.3f);
            AnimationCurve curve = PositionCurve();
            Assert.That(curve.length, Is.EqualTo(2));
            Assert.That(curve.keys[0].time, Is.EqualTo(0f));
            Assert.That(curve.keys[1].time, Is.EqualTo(0.3f).Within(0.00001f));
            Assert.That(curve.Evaluate(0.15f), Is.EqualTo(3f));
        }

        [Test]
        public void DurationBetweenFixedTicksInterpolatesTheBoundaryPose()
        {
            CreateFixture(0.03f);
            _recorder.StartRecording(0.05f);
            Observe(0.02f, 2f);
            Observe(0.04f, 4f);
            Observe(0.06f, 6f);
            AssertSaved(0.05f);
            Assert.That(PositionCurve().Evaluate(0.05f), Is.EqualTo(5f).Within(0.0001f));
        }

        [Test]
        public void IntervalStaysAnchoredAndInspectorEditsDoNotChangeActiveRecording()
        {
            CreateFixture(0.03f, false);
            _recorder.StartRecording(0.12f);
            Set("_recordInterval", 0.5f);
            Set("_animationFileName", "EditedDuringRecording");
            for (int i = 1; i <= 6; i++) Observe(i * 0.02f, i);
            AssertSaved(0.12f);
            float[] times = Array.ConvertAll(PositionCurve().keys, k => k.time);
            Assert.That(times, Has.Some.EqualTo(0.06f).Within(0.00001f));
            Assert.That(AssetDatabase.GetAssetPath(_recorder.Result?.Clip), Is.EqualTo(_folder + "/Recorded.anim"));
            Assert.That(_recorder.Result?.Clip.frameRate, Is.EqualTo(1f / 0.03f).Within(0.001f));
        }

        [Test]
        public void PlaybackStripsInactivePhysicsAndExistingAnimationComponents()
        {
            CreateFixture();
            _body.gameObject.AddComponent<FixedJoint>();
            _body.gameObject.AddComponent<ConstantForce>();
            _root.AddComponent<Animator>();
            _root.AddComponent<Animation>();
            var inactive = new GameObject("InactiveBody");
            inactive.transform.SetParent(_root.transform, false);
            inactive.AddComponent<Rigidbody>();
            inactive.AddComponent<BoxCollider>();
            inactive.SetActive(false);
            var body2D = new GameObject("Body2D");
            body2D.transform.SetParent(_root.transform, false);
            body2D.AddComponent<Rigidbody2D>();
            body2D.AddComponent<CircleCollider2D>();
            body2D.AddComponent<HingeJoint2D>();
            _recorder.StartRecording(0.1f);
            Frame(0.1f);
            AssertSaved(0.1f);
            GameObject prefab = _recorder.Result?.Prefab;
            Assert.That(prefab.GetComponentsInChildren<Rigidbody>(true), Is.Empty);
            Assert.That(prefab.GetComponentsInChildren<Collider>(true), Is.Empty);
            Assert.That(prefab.GetComponentsInChildren<Joint>(true), Is.Empty);
            Assert.That(prefab.GetComponentsInChildren<Rigidbody2D>(true), Is.Empty);
            Assert.That(prefab.GetComponentsInChildren<Collider2D>(true), Is.Empty);
            Assert.That(prefab.GetComponentsInChildren<Joint2D>(true), Is.Empty);
            Assert.That(prefab.GetComponentsInChildren<MonoBehaviour>(true), Is.Empty);
            Assert.That(prefab.GetComponentsInChildren<Animator>(true), Is.Empty);
            Assert.That(prefab.GetComponentsInChildren<Animation>(true).Length, Is.EqualTo(1));
            Assert.That(prefab.GetComponentsInChildren<MeshRenderer>(true).Length, Is.EqualTo(1));
            Assert.That(prefab.transform.Find("InactiveBody").gameObject.activeSelf, Is.False);
            Assert.That(_body.GetComponent<Rigidbody>(), Is.Not.Null);
            Assert.That(_body.GetComponent<FixedJoint>(), Is.Not.Null);
        }

        [Test]
        public void OverwritePreservesBothAssetGuidsAndTheClipReference()
        {
            CreateFixture();
            _recorder.StartRecording(0.1f);
            Observe(0.1f, 1f);
            string clipPath = AssetDatabase.GetAssetPath(_recorder.Result?.Clip);
            string prefabPath = AssetDatabase.GetAssetPath(_recorder.Result?.Prefab);
            string clipGuid = AssetDatabase.AssetPathToGUID(clipPath);
            string prefabGuid = AssetDatabase.AssetPathToGUID(prefabPath);
            AnimationClip originalReference = _recorder.Result?.Clip;
            Set("_overwriteExistingAssets", true);
            _recorder.StartRecording(0.2f);
            Observe(0.2f, 4f);
            AssertSaved(0.2f);
            Assert.That(AssetDatabase.AssetPathToGUID(clipPath), Is.EqualTo(clipGuid));
            Assert.That(AssetDatabase.AssetPathToGUID(prefabPath), Is.EqualTo(prefabGuid));
            Assert.That(_recorder.Result?.Clip, Is.SameAs(originalReference));
            Assert.That(_recorder.Result?.Prefab.GetComponent<Animation>().clip, Is.SameAs(originalReference));
        }

        [Test]
        public void OutputConflictAtStopFaultsOnceAndReleaseAllowsRetry()
        {
            CreateFixture();
            _recorder.StartRecording(1f);
            Observe(0.1f, 1f);
            var conflict = new GameObject("ExistingPrefab");
            string path = _folder + "/Recorded.prefab";
            PrefabUtility.SaveAsPrefabAsset(conflict, path);
            Object.DestroyImmediate(conflict);
            byte[] original = File.ReadAllBytes(path);
            Assert.Throws<InvalidOperationException>(() => _recorder.StopRecording());
            Assert.That(_recorder.IsRecording, Is.False);
            Assert.That(_recorder.IsSaving, Is.False);
            Assert.That((_recorder.Result?.Succeeded ?? false), Is.False);
            Assert.That(_recorder.IsFaulted, Is.True);
            Assert.That(_recorder.LastError, Is.Not.Null);
            Assert.That(_recorder.Result.Error, Is.SameAs(_recorder.LastError));
            Assert.That(_recorder.Result.Succeeded, Is.False);
            Assert.That(File.Exists(_folder + "/Recorded.anim"), Is.False);
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(original));
            _recorder.Release();
            _recorder.Init();
            _recorder.StartRecording(0.1f, "Retry");
            Frame(0.1f);
            AssertSaved(0.1f);
        }

        [Test]
        public void DestroyedOrRenamedHierarchyFailsBeforeWritingAssets()
        {
            CreateFixture();
            _recorder.StartRecording(1f);
            _body.name = "Renamed";
            Assert.Throws<InvalidOperationException>(() => _recorder.StopRecording());
            Assert.That(_recorder.IsFaulted, Is.True);
            Assert.That(_recorder.IsRecording, Is.False);
            Assert.That(File.Exists(_folder + "/Recorded.anim"), Is.False);
        }

        [Test]
        public void DuplicatePathsAndNestedRecordingAreRejected()
        {
            CreateFixture();
            var duplicate = new GameObject("Body");
            duplicate.transform.SetParent(_root.transform, false);
            Assert.Throws<InvalidOperationException>(() => _recorder.StartRecording(1f));
            Assert.That(_recorder.IsRecording, Is.False);
            Object.DestroyImmediate(duplicate);
            _recorder.StartRecording(1f);
            Assert.Throws<InvalidOperationException>(() => _recorder.StartRecording(2f));
            Frame(1f);
            AssertSaved(1f);
        }

        [Test]
        public void ReleaseDuringRecordingCompletesBothAssetsSynchronously()
        {
            CreateFixture();
            _recorder.StartRecording(1f);
            Observe(0.1f, 2f);
            _recorder.Release();
            AssertSaved(0.1f);
            Assert.That(_recorder.IsInitialized, Is.False);
            Assert.That(_recorder.Result.StopReason, Is.EqualTo(PhysicsRecordingStopReason.Released));
        }

        [Test]
        public void CompletionResultSurvivesReleaseUntilTheNextTake()
        {
            CreateFixture();
            _recorder.StartRecording(1f);
            Observe(0.1f, 2f);
            _recorder.StopRecording();
            AssertSaved(0.1f);
            PhysicsRecordingResult result = _recorder.Result;
            Assert.That(result.StopReason, Is.EqualTo(PhysicsRecordingStopReason.Stopped));
            _recorder.Release();
            Assert.That(_recorder.Result, Is.SameAs(result));
            Assert.That((_recorder.Result?.Succeeded ?? false), Is.True);
            _recorder.Init();
            _recorder.StartRecording(0.2f, "Next");
            Assert.That(_recorder.Result, Is.Null);
            Assert.That((_recorder.Result?.Succeeded ?? false), Is.False);
            Observe(0.2f, 3f);
            AssertSaved(0.2f);
            Assert.That(_recorder.Result.StopReason, Is.EqualTo(PhysicsRecordingStopReason.DurationReached));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void UnavailablePrefabImportRollsBackBothOutputAssets(bool overwrite)
        {
            CreateFixture();
            string clipPath = _folder + "/Recorded.anim";
            string prefabPath = _folder + "/Recorded.prefab";
            string referencePath = PhysicsRecordingReference.AssetPathForClip(clipPath);
            byte[] oldClip = null, oldPrefab = null, oldReference = null;
            string clipGuid = null, prefabGuid = null;
            if (overwrite)
            {
                _recorder.StartRecording(0.1f);
                Observe(0.1f, 1f);
                oldClip = File.ReadAllBytes(clipPath);
                oldPrefab = File.ReadAllBytes(prefabPath);
                oldReference = File.ReadAllBytes(referencePath);
                clipGuid = AssetDatabase.AssetPathToGUID(clipPath);
                prefabGuid = AssetDatabase.AssetPathToGUID(prefabPath);
                Set("_overwriteExistingAssets", true);
            }
            _recorder.StartRecording(1f);
            Observe(0.1f, 3f);
            // PrefabUtility returns no imported prefab while imports are suspended.
            // This exercises failure after the clip has already been written.
            AssetDatabase.StartAssetEditing();
            try { Assert.Throws<InvalidOperationException>(() => _recorder.StopRecording()); }
            finally { AssetDatabase.StopAssetEditing(); }
            Assert.That((_recorder.Result?.Succeeded ?? false), Is.False);
            Assert.That(_recorder.IsFaulted, Is.True);
            Assert.That(_recorder.Result?.Clip, Is.Null);
            Assert.That(_recorder.Result?.Prefab, Is.Null);
            if (overwrite)
            {
                AssetDatabase.ImportAsset(clipPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                AssetDatabase.ImportAsset(prefabPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                Assert.That(File.ReadAllBytes(clipPath), Is.EqualTo(oldClip));
                Assert.That(File.ReadAllBytes(prefabPath), Is.EqualTo(oldPrefab));
                Assert.That(File.ReadAllBytes(referencePath), Is.EqualTo(oldReference));
                Assert.That(AssetDatabase.AssetPathToGUID(clipPath), Is.EqualTo(clipGuid));
                Assert.That(AssetDatabase.AssetPathToGUID(prefabPath), Is.EqualTo(prefabGuid));
            }
            else
            {
                Assert.That(File.Exists(clipPath), Is.False);
                Assert.That(File.Exists(prefabPath), Is.False);
                Assert.That(File.Exists(referencePath), Is.False);
            }
        }

        [TestCase(16)]
        [TestCase(128)]
        public void RecordingMetricsExposeObservationKeysExportPhasesAndOverwriteBackupSize(int transformCount)
        {
            CreateFixture();
            for (int i = 2; i < transformCount; i++)
                new GameObject("Track_" + i).transform.SetParent(_root.transform, false);
            Transform[] tracks = _root.GetComponentsInChildren<Transform>();
            _recorder.StartRecording(0.4f);
            for (int i = 1; i <= 20; i++)
            {
                for (int j = 1; j < tracks.Length; j++) tracks[j].localPosition += Vector3.right * 0.01f;
                Frame((float)(i / 50d));
            }
            AssertSaved(0.4f);
            PhysicsRecordingMetrics metrics = _recorder.Result.Metrics;
            Assert.AreEqual(transformCount, metrics.TransformCount);
            Assert.AreEqual(21, metrics.ObservationCount);
            Assert.AreEqual((transformCount - 1) * 21 + 2, metrics.StoredPoseCount);
            Assert.AreEqual(0, metrics.BackupBytes);
            Assert.Greater(metrics.HierarchyValidationMilliseconds, 0d);
            Assert.Greater(metrics.SamplingMilliseconds, 0d);
            Assert.Greater(metrics.CurveBuildMilliseconds, 0d);
            Assert.Greater(metrics.PrefabBuildMilliseconds, 0d);
            Assert.Greater(metrics.AssetSaveMilliseconds, 0d);
            Assert.GreaterOrEqual(metrics.TotalExportMilliseconds, metrics.AssetSaveMilliseconds);
            TestContext.WriteLine(FormattableString.Invariant($"RECORDING_METRICS transforms={metrics.TransformCount} observations={metrics.ObservationCount} poses={metrics.StoredPoseCount} hierarchy_ms={metrics.HierarchyValidationMilliseconds:F4} sampling_ms={metrics.SamplingMilliseconds:F4} curves_ms={metrics.CurveBuildMilliseconds:F4} prefab_ms={metrics.PrefabBuildMilliseconds:F4} save_ms={metrics.AssetSaveMilliseconds:F4} export_ms={metrics.TotalExportMilliseconds:F4}"));
            string path = AssetDatabase.GetAssetPath(_recorder.Result?.Clip);
            string prefabPath = AssetDatabase.GetAssetPath(_recorder.Result?.Prefab);
            long bytes = new FileInfo(path).Length + new FileInfo(prefabPath).Length
                + new FileInfo(AssetDatabase.GetAssetPath(_recorder.Result.Reference)).Length;
            Set("_overwriteExistingAssets", true);
            _recorder.StartRecording(0.1f);
            Frame(0.1f);
            Assert.AreEqual(bytes, _recorder.Result.Metrics.BackupBytes);
            TestContext.WriteLine($"OVERWRITE_BACKUP transforms={transformCount} bytes={_recorder.Result.Metrics.BackupBytes}");
            Assert.AreEqual(0, metrics.BackupBytes, "Published metrics must remain a snapshot after the next take.");
        }

        [Test]
        public void SavedReferenceRetainsUnreducedObservationsAndSurvivesOverwriteAndReimport()
        {
            CreateFixture(0.1f);
            Set("_positionThreshold", 0.5f);
            _recorder.StartRecording(0.1f);
            Observe(0.02f, 0.1f, 15f);
            Observe(0.04f, 0.2f, 30f);
            Observe(0.06f, 0.3f, 45f);
            Observe(0.08f, 0.4f, 60f);
            Observe(0.1f, 0.5f, 75f);
            var reference = _recorder.Result.Reference;
            Assert.NotNull(reference);
            string path = AssetDatabase.GetAssetPath(reference);
            string guid = AssetDatabase.AssetPathToGUID(path);
            Assert.AreEqual(6, reference.ObservationCount);
            Assert.AreEqual(2, reference.TrackCount);
            Assert.AreEqual("Body", reference.Path(1));
            Assert.That(reference.Sample(1, 0.04f).Position.x, Is.EqualTo(0.2f).Within(0.00001f));
            Assert.That(reference.Sample(1, 0.03f).Position.x, Is.EqualTo(0.15f).Within(0.00001f));
            Assert.Less(Quaternion.Angle(reference.Sample(1, 0.04f).Rotation, Quaternion.Euler(0, 30, 0)), 0.05f);
            Assert.Less(PositionCurve().length, reference.ObservationCount);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            reference = AssetDatabase.LoadAssetAtPath<PhysicsRecordingReference>(path);
            Assert.AreSame(_recorder.Result?.Clip, reference.Clip);
            Assert.That(reference.Sample(1, 0.06f).Position.x, Is.EqualTo(0.3f).Within(0.00001f));
            Set("_overwriteExistingAssets", true);
            _recorder.StartRecording(0.2f);
            Observe(0.2f, 2f);
            Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID(path));
            Assert.That(_recorder.Result.Reference.Duration, Is.EqualTo(0.2f));
        }

        [Test]
        public void InitializationFailurePublishesOnlyItsFailureAndLeavesSavedAssetsIntact()
        {
            CreateFixture();
            _recorder.StartRecording(0.1f);
            Frame(0.1f);
            AnimationClip clip = _recorder.Result?.Clip;
            GameObject prefab = _recorder.Result?.Prefab;
            PhysicsRecordingResult previous = _recorder.Result;
            string clipPath = AssetDatabase.GetAssetPath(clip), prefabPath = AssetDatabase.GetAssetPath(prefab);
            _recorder.Release();
            Set("_recordInterval", 0f);
            Assert.Throws<ArgumentOutOfRangeException>(_recorder.Init);
            Assert.IsTrue(_recorder.IsFaulted);
            Assert.IsFalse((_recorder.Result?.Succeeded ?? false));
            Assert.IsNull(_recorder.Result.Clip); Assert.IsNull(_recorder.Result.Prefab);
            Assert.IsTrue(previous.Succeeded);
            Assert.AreSame(clip, AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath));
            Assert.AreSame(prefab, AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath));
            Assert.AreSame(_recorder.LastError, _recorder.Result.Error);
        }

        private void Set(string field, object value) => typeof(PhysicsRecorder).GetField(field, PrivateInstance).SetValue(_recorder, value);

        private void Observe(float time, float x, float yaw = 0f)
        {
            _body.localPosition = new Vector3(x, 0f, 0f);
            _body.localRotation = Quaternion.Euler(0f, yaw, 0f);
            Frame(time);
        }

        private void Frame(float time)
        {
            try { typeof(PhysicsRecorder).GetMethod("RecordFrame", PrivateInstance).Invoke(_recorder, new object[] { time }); }
            catch (TargetInvocationException exception) { throw exception.InnerException; }
        }

        private AnimationCurve PositionCurve() => AnimationUtility.GetEditorCurve(_recorder.Result?.Clip,
            EditorCurveBinding.FloatCurve("Body", typeof(Transform), "m_LocalPosition.x"));

        private void AssertPose(float time, float x, float yaw)
        {
            _recorder.Result?.Clip.SampleAnimation(_playback, time);
            Transform body = _playback.transform.Find("Body");
            Assert.That(body.localPosition.x, Is.EqualTo(x).Within(0.001f), $"position at {time}");
            Assert.That(Quaternion.Angle(body.localRotation, Quaternion.Euler(0f, yaw, 0f)), Is.LessThan(0.05f), $"rotation at {time}");
        }

        private void AssertSaved(float duration)
        {
            Assert.That((_recorder.Result?.Succeeded ?? false), Is.True, _recorder.LastError?.ToString());
            Assert.That(_recorder.IsSaving, Is.False);
            Assert.That(_recorder.IsRecording, Is.False);
            Assert.That(_recorder.IsFaulted, Is.False);
            Assert.That(_recorder.Result?.Clip, Is.Not.Null);
            Assert.That(_recorder.Result?.Prefab, Is.Not.Null);
            Assert.That(_recorder.Result?.Clip.length, Is.EqualTo(duration).Within(0.0001f));
            Assert.That(_recorder.Result?.Clip.legacy, Is.True);
            Animation animation = _recorder.Result?.Prefab.GetComponent<Animation>();
            Assert.That(animation.clip, Is.EqualTo(_recorder.Result?.Clip));
            Assert.That(animation.playAutomatically, Is.True);
        }
    }

}
#endif
