using System;
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// Opens a sample scene, sets every Manager's Bake Mode without saving and
    /// enters Play Mode from an Edit Mode test. This needs Enter Play Mode
    /// Options with domain and scene reload off, as this project has, so the
    /// test keeps running across the transition. It switches Play Mode itself
    /// and yields only null, as the Test Framework's EnterPlayMode does inside,
    /// because the framework accepts its own instruction only from the test
    /// method's enumerator, not from a helper like this one.
    /// </summary>
    internal static class FlowFieldPlayScene
    {
        internal const string SampleScene = "Assets/_FlowField/Samples/Scenes/FlowFieldSample.unity";
        internal const string VolumeScene = "Assets/_FlowField/Samples/Scenes/FlowFieldVolume3DTest.unity";

        // Ready after this long, then still Ready: the build has settled.
        private const float SettleSeconds = 3f;
        private const float TimeoutSeconds = 60f;

        internal static IEnumerator Enter(string scenePath, FlowFieldBakeMode bakeMode)
        {
            Assert.That(EditorApplication.isPlaying, Is.False, "A test entered Play Mode twice.");
            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            SetBakeMode(bakeMode);
            Assert.That(EditorUtility.scriptCompilationFailed, Is.False, "Scripts do not compile.");
            EditorApplication.isPlaying = true;
            float deadline = Time.realtimeSinceStartup + TimeoutSeconds;
            while (!EditorApplication.isPlaying)
            {
                if (Time.realtimeSinceStartup > deadline)
                    Assert.Fail("Timed out entering Play Mode.");
                yield return null;
            }
            // The sample controllers set their first Goal once the Manager is
            // Ready; settling once more lets that land before a test acts.
            yield return WaitUntilSettled(1f);
        }

        /// <summary>Sets every Manager's serialized Bake Mode, inactive ones included, without saving.</summary>
        internal static void SetBakeMode(FlowFieldBakeMode bakeMode)
        {
            FlowFieldManager[] managers = UnityEngine.Object.FindObjectsByType<FlowFieldManager>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            Assert.That(managers.Length, Is.GreaterThan(0), "The scene has no FlowFieldManager.");
            foreach (FlowFieldManager manager in managers)
            {
                var serialized = new SerializedObject(manager);
                serialized.FindProperty(FlowFieldManager.PropertyNames.BakeMode).intValue = (int)bakeMode;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        internal static IEnumerator Exit()
        {
            if (EditorApplication.isPlaying)
            {
                EditorApplication.isPlaying = false;
                while (EditorApplication.isPlaying)
                    yield return null;
            }
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            FlowFieldBuildSettings.ResetToDefault();
        }

        /// <summary>The first active Manager, the one the scenarios drive.</summary>
        internal static FlowFieldManager Manager
        {
            get
            {
                FlowFieldManager[] managers = UnityEngine.Object.FindObjectsByType<FlowFieldManager>(FindObjectsSortMode.None);
                Assert.That(managers.Length, Is.GreaterThan(0), "No active FlowFieldManager.");
                return managers[0];
            }
        }

        /// <summary>Waits until every active Manager is Ready.</summary>
        internal static IEnumerator WaitUntilReady(float timeoutSeconds = TimeoutSeconds)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            yield return null;
            while (!AllReady())
            {
                if (Time.realtimeSinceStartup > deadline)
                    Assert.Fail("Timed out waiting for Ready: " + Describe());
                yield return null;
            }
        }

        /// <summary>Ready, then the same after a while: nothing was still owed.</summary>
        internal static IEnumerator WaitUntilSettled(float seconds = SettleSeconds)
        {
            yield return WaitUntilReady();
            yield return WaitSeconds(seconds);
            yield return WaitUntilReady();
        }

        internal static IEnumerator WaitSeconds(float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end)
                yield return null;
        }

        internal static IEnumerator WaitFrames(int frames)
        {
            for (int i = 0; i < frames; i++)
                yield return null;
        }

        /// <summary>The published field's hash, after the Manager is Ready.</summary>
        internal static string Hash(FlowFieldManager manager)
        {
            Assert.That(manager.TryGetPublishedField(out FlowFieldField field), Is.True,
                "No published field: " + Describe());
            return FlowFieldFieldHash.Format(FlowFieldFieldHash.Compute(field));
        }

        internal static int CellCount(FlowFieldManager manager)
        {
            Assert.That(manager.TryGetPublishedField(out FlowFieldField field), Is.True);
            return field.Blocked.Length;
        }

        private static bool AllReady()
        {
            FlowFieldManager[] managers = UnityEngine.Object.FindObjectsByType<FlowFieldManager>(FindObjectsSortMode.None);
            if (managers.Length == 0)
                return false;
            foreach (FlowFieldManager manager in managers)
                if (manager.State != FlowFieldRuntimeState.Ready)
                    return false;
            return true;
        }

        internal static string Describe()
        {
            var text = new System.Text.StringBuilder("frame=" + Time.frameCount + " playing=" + EditorApplication.isPlaying + " | ");
            foreach (FlowFieldManager manager in UnityEngine.Object.FindObjectsByType<FlowFieldManager>(FindObjectsSortMode.None))
                text.Append(manager.name + " " + manager.SpaceMode + "/" + manager.BakeMode + " state=" + manager.State
                    + " rev=" + manager.Revision + " err=" + manager.LastError + "; ");
            return text.ToString();
        }
    }
}
