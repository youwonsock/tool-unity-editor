#if UNITY_EDITOR
using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Common.OptimizeTool.Samples;

namespace Common.OptimizeTool.Tests
{
    public sealed class OptimizeToolContractTests
    {
        [TestCase(typeof(MeshCombiner))]
        [TestCase(typeof(BackfaceCullMeshBaker))]
        public void MeshOperationsExposeNoInitializationOrFaultLifecycle(Type type)
        {
            Assert.IsNull(type.GetMethod("Init")); Assert.IsNull(type.GetMethod("Release"));
            Assert.IsNull(type.GetProperty("IsInitialized")); Assert.IsNull(type.GetProperty("IsFaulted"));
        }

        [Test]
        public void RecordingResultsHaveOneAccessPath()
        {
            Assert.IsNull(typeof(PhysicsRecorder).GetProperty("ResultClip"));
            Assert.IsNull(typeof(PhysicsRecorder).GetProperty("ResultPrefab"));
            Assert.IsNull(typeof(PhysicsRecorder).GetProperty("IsSaveCompleted"));
            Assert.IsNull(typeof(MeshBuildResult).GetProperty("Materials"));
        }

        [Test]
        public void ShowcaseSerializesItsRequiredReferenceForPlaybackOutsideTheEditor()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_OptimizeTool/Prefab/OptimizeToolShowcase.prefab");
            var demo = prefab.GetComponent<OptimizeToolPhysicsDemo>();
            Assert.NotNull(demo.SavedPrefab);
            Assert.NotNull(demo.RecordingReference, "Player playback cannot load the reference through AssetDatabase.");
            AnimationClip clip = demo.SavedPrefab.GetComponent<Animation>().clip;
            Assert.AreSame(clip, demo.RecordingReference.Clip);
            demo.RecordingReference.Validate(clip);
        }

        [Test]
        public void RecordingLifecycleAndSerializedPrefabReferencesAreValid()
        {
            Type recorder = typeof(PhysicsRecorder);
            Assert.NotNull(recorder.GetMethod("Init", Type.EmptyTypes));
            Assert.NotNull(recorder.GetMethod("Release", Type.EmptyTypes));
            Assert.NotNull(recorder.GetMethod("StopRecording", Type.EmptyTypes));
            MethodInfo start = recorder.GetMethod("StartRecording", new[] { typeof(float), typeof(string) });
            Assert.NotNull(start);
            Assert.IsTrue(start.GetParameters()[1].IsOptional);
            Assert.IsNull(start.GetParameters()[1].DefaultValue);
            foreach (string name in new[] { "IsInitialized", "IsFaulted", "IsRecording", "IsSaving" })
                Assert.AreEqual(typeof(bool), recorder.GetProperty(name)?.PropertyType, name);
            Assert.AreEqual(typeof(Exception), recorder.GetProperty("LastError")?.PropertyType);
            Assert.AreEqual(typeof(PhysicsRecordingResult), recorder.GetProperty("Result")?.PropertyType);
            foreach (string name in new[] { "Record", "Compare", "ResetDemo", "GetStatusText" })
                Assert.NotNull(typeof(OptimizeToolPhysicsDemo).GetMethod(name, Type.EmptyTypes), name);

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_OptimizeTool/Prefab/OptimizeToolShowcase.prefab");
            Assert.NotNull(prefab);
            OptimizeToolPhysicsDemo demo = prefab.GetComponent<OptimizeToolPhysicsDemo>();
            Assert.NotNull(demo);
            PhysicsRecorder component = demo.Recorder;
            Assert.NotNull(component);
            var serialized = new SerializedObject(component);
            foreach (string name in new[] { "_root", "_recordInterval", "_recordOnlyIfMoved", "_positionThreshold",
                "_rotationThreshold", "_savePath", "_animationFileName", "_overwriteExistingAssets" })
                Assert.NotNull(serialized.FindProperty(name), name);
            Assert.NotNull(serialized.FindProperty("_root").objectReferenceValue);
            var presentation = new SerializedObject(demo);
            foreach (string name in new[] { "_demoRoot", "_sourceRoot", "_replayAnchor", "_view", "_recorder", "_savedPrefab", "_recordingReference" })
                Assert.NotNull(presentation.FindProperty(name).objectReferenceValue, name);
            var reference = (PhysicsRecordingReference)presentation.FindProperty("_recordingReference").objectReferenceValue;
            var recordedPrefab = (GameObject)presentation.FindProperty("_savedPrefab").objectReferenceValue;
            Assert.DoesNotThrow(() => reference.Validate(recordedPrefab.GetComponent<Animation>().clip));
            Assert.IsFalse(prefab.GetComponentsInChildren<MonoBehaviour>(true).Any(c => c == null));
            Assert.AreEqual(component, presentation.FindProperty("_recorder").objectReferenceValue);
        }
    }
}
#endif
