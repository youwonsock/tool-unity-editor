using System;
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// P5: Init in Edit Mode, where Unity never calls this component's
    /// OnDestroy, throws before a session exists, so destroying the Manager
    /// leaves no session in the runtime scheduler (P5) and the next Play
    /// session schedules only the scene's Manager (P5b). Nothing is saved:
    /// the tear-down opens an empty scene.
    /// </summary>
    [TestFixture, Category("SpaceMode")]
    internal sealed class EditModeInitTests : FlowFieldPlayTestBase
    {
        private const string OutsidePlayMode =
            "FlowFieldManager.Init runs only in Play Mode. In Edit Mode, use Bake and the Scene View preview.";

        [UnityTest, Timeout(300000)]
        public IEnumerator InitThrowsAndLeavesNoSession()
        {
            InitInEditModeAndDestroy();
            yield break;
        }

        [UnityTest, Timeout(300000)]
        public IEnumerator NextPlaySchedulesOnlySceneManager()
        {
            InitInEditModeAndDestroy();
            yield return FlowFieldSpaceModeScenarios.EnterAndSettle(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);
            FlowFieldManager manager = Manager;
            Assert.That(FlowFieldBuildScheduler.Count, Is.EqualTo(1), "The scheduler holds a session besides the scene's Manager.");
            Assert.That(FlowFieldBuildScheduler.Contains(manager.Session), Is.True);
            Assert.That(manager.Session.State, Is.EqualTo(FlowFieldRuntimeState.Ready));
        }

        // A copy of the sample's Manager in Edit Mode: Init, then DestroyImmediate.
        private static void InitInEditModeAndDestroy()
        {
            Assert.That(EditorApplication.isPlaying, Is.False);
            EditorSceneManager.OpenScene(FlowFieldPlayScene.SampleScene, OpenSceneMode.Single);
            Assert.That(FlowFieldBuildScheduler.Count, Is.Zero, "Edit Mode starts with scheduled sessions.");
            FlowFieldManager source = UnityEngine.Object.FindObjectsByType<FlowFieldManager>(FindObjectsSortMode.None)[0];
            FlowFieldManager copy = UnityEngine.Object.Instantiate(source);

            var thrown = Assert.Throws<InvalidOperationException>(() => copy.Init());
            Assert.That(thrown.Message, Is.EqualTo(OutsidePlayMode));
            Assert.That(FlowFieldBuildScheduler.Count, Is.Zero, "Edit Mode Init scheduled a session.");

            UnityEngine.Object.DestroyImmediate(copy.gameObject);
            Assert.That(FlowFieldBuildScheduler.Count, Is.Zero);
            EditorSceneManager.OpenScene(FlowFieldPlayScene.SampleScene, OpenSceneMode.Single);
            Assert.That(FlowFieldBuildScheduler.Count, Is.Zero, "A session outlived its Edit Mode Manager.");
        }
    }
}
