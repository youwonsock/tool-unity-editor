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
    public sealed class PhysicsRecorderPlaybackTests
    {
        private string _folder;
        private GameObject _host, _root, _playback;
        private Transform _body;
        private PhysicsRecorder _recorder;

        [SetUp]
        public void SetUp()
        {
            _folder = "Assets/__PhysicsRecorderPlaybackTests_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(_folder));
            _root = new GameObject("RecordedRoot");
            // Other scene tests can leave collider geometry around the origin.
            _root.transform.position = new Vector3(100f, 0f, 100f);
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
            _host.SetActive(true);
            Assert.That(_recorder.IsInitialized, Is.True);
        }

        [TearDown]
        public void TearDown()
        {
            if (_recorder != null && (_recorder.IsInitialized || _recorder.IsFaulted)) _recorder.Release();
            if (_playback != null) Object.DestroyImmediate(_playback);
            if (_host != null) Object.DestroyImmediate(_host);
            if (_root != null) Object.DestroyImmediate(_root);
            if (!string.IsNullOrEmpty(_folder)) AssetDatabase.DeleteAsset(_folder);
        }

        [UnityTest]
        public IEnumerator AutomaticPhysicsRecordingCreatesAnAutoplayingPhysicsFreePrefab()
        {
            _body.localPosition = new Vector3(0f, 5f, 0f);
            _body.GetComponent<Rigidbody>().useGravity = true;
            _body.gameObject.AddComponent<PhysicsRecorderCallbackProbe>();
            int awakeCount = PhysicsRecorderCallbackProbe.AwakeCount;
            _recorder.StartRecording(0.13f);
            double deadline = Time.realtimeSinceStartupAsDouble + 10d;
            while (_recorder.IsRecording && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            AssertSaved(0.13f);
            Assert.That(PhysicsRecorderCallbackProbe.AwakeCount, Is.EqualTo(awakeCount), "Saving must not awaken gameplay clones.");
            _playback = Object.Instantiate(_recorder.Result?.Prefab);
            Assert.That(_playback.GetComponentInChildren<Rigidbody>(), Is.Null);
            Assert.That(_playback.GetComponentInChildren<PhysicsRecorderCallbackProbe>(), Is.Null);
            Animation animation = _playback.GetComponent<Animation>();
            yield return null;
            Assert.That(animation.IsPlaying("Recorded"), Is.True);
            Transform body = _playback.transform.Find("Body");
            yield return new WaitForSeconds(0.06f);
            Assert.That(body.localPosition.y, Is.LessThan(5f));
            yield return new WaitForSeconds(0.2f);
            AnimationCurve y = AnimationUtility.GetEditorCurve(_recorder.Result?.Clip,
                EditorCurveBinding.FloatCurve("Body", typeof(Transform), "m_LocalPosition.y"));
            Assert.That(body.localPosition.y, Is.EqualTo(y.Evaluate(0.13f)).Within(0.002f));
        }

        [UnityTest]
        public IEnumerator DisablingRecorderFinishesSynchronouslyAndCanRestart()
        {
            _recorder.StartRecording(5f);
            yield return new WaitForFixedUpdate();
            _host.SetActive(false);
            Assert.That((_recorder.Result?.Succeeded ?? false), Is.True);
            Assert.That(_recorder.Result.StopReason, Is.EqualTo(PhysicsRecordingStopReason.Disabled));
            Assert.That(_recorder.Result?.Prefab, Is.Not.Null);
            _host.SetActive(true);
            Set("_animationFileName", "Restarted");
            _recorder.StartRecording(0.05f);
            double deadline = Time.realtimeSinceStartupAsDouble + 10d;
            while (_recorder.IsRecording && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            AssertSaved(0.05f);
        }

        [UnityTest]
        public IEnumerator ManualStopThenImmediateRestartDoesNotReusePreviousSampler()
        {
            _recorder.StartRecording(5f);
            yield return new WaitForFixedUpdate();
            _recorder.StopRecording();
            Assert.That((_recorder.Result?.Succeeded ?? false), Is.True);
            Set("_animationFileName", "Restarted");
            _recorder.StartRecording(0.05f);
            double deadline = Time.realtimeSinceStartupAsDouble + 10d;
            while (_recorder.IsRecording && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            AssertSaved(0.05f);
        }

        private void Set(string field, object value) => typeof(PhysicsRecorder)
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_recorder, value);

        private void AssertSaved(float duration)
        {
            Assert.That((_recorder.Result?.Succeeded ?? false), Is.True, _recorder.LastError?.ToString());
            Assert.That(_recorder.IsSaving, Is.False);
            Assert.That(_recorder.IsRecording, Is.False);
            Assert.That(_recorder.IsFaulted, Is.False);
            Assert.That(_recorder.Result?.Clip, Is.Not.Null);
            Assert.That(_recorder.Result?.Prefab, Is.Not.Null);
            Assert.That(_recorder.Result?.Clip.length, Is.EqualTo(duration).Within(0.0001f));
        }
    }

    [RequireComponent(typeof(Rigidbody))]
    public sealed class PhysicsRecorderCallbackProbe : MonoBehaviour
    {
        public static int AwakeCount;
        private void Awake() => AwakeCount++;
    }
}
#endif
