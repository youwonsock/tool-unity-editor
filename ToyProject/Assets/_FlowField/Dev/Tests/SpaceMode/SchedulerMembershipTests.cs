using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// R2: the runtime scheduler holds only sessions that build. Init on a
    /// Manager that was never active (Unity never calls its OnDestroy) keeps
    /// its session out, so destroying it leaves nothing behind. A running
    /// session leaves when its Manager is deactivated, joins again when it is
    /// activated, leaves when it is destroyed, and the next Play session holds
    /// only the scene's Manager.
    /// </summary>
    [TestFixture, Category("SpaceMode")]
    internal sealed class SchedulerMembershipTests : FlowFieldPlayTestBase
    {
        [UnityTest, Timeout(300000)]
        public IEnumerator NeverActiveAndToggledManagers()
        {
            yield return FlowFieldSpaceModeScenarios.EnterAndSettle(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);
            FlowFieldManager source = Manager;
            int baseline = FlowFieldBuildScheduler.Count;
            Assert.That(FlowFieldBuildScheduler.Contains(source.Session), Is.True, "The scene's Manager is not scheduled.");

            // Init on a Manager that never woke, then Destroy.
            FlowFieldManager neverActive = CreateInactiveCopy(source, "FlowFieldTestR2");
            neverActive.Init();
            FlowFieldSession neverActiveSession = neverActive.Session;
            Assert.That(neverActive.State, Is.EqualTo(FlowFieldRuntimeState.Suspended));
            Assert.That(FlowFieldBuildScheduler.Count, Is.EqualTo(baseline));
            Assert.That(FlowFieldBuildScheduler.Contains(neverActiveSession), Is.False, "A never-active Init joined the scheduler.");
            Object.Destroy(neverActive.gameObject);
            yield return FlowFieldPlayScene.WaitSeconds(1f);
            Assert.That(FlowFieldBuildScheduler.Count, Is.EqualTo(baseline));
            Assert.That(FlowFieldBuildScheduler.Contains(neverActiveSession), Is.False);

            // Init while inactive, then activate, deactivate, activate, Destroy.
            FlowFieldManager toggled = CreateInactiveCopy(source, "FlowFieldTestR2b");
            toggled.Init();
            Assert.That(toggled.State, Is.EqualTo(FlowFieldRuntimeState.Suspended));
            Assert.That(FlowFieldBuildScheduler.Count, Is.EqualTo(baseline));
            toggled.gameObject.SetActive(true);
            AssertScheduled(toggled, baseline + 1, FlowFieldRuntimeState.Building);
            yield return FlowFieldPlayScene.WaitSeconds(2f);
            yield return FlowFieldPlayScene.WaitUntilReady();
            AssertScheduled(toggled, baseline + 1, FlowFieldRuntimeState.Ready);

            toggled.gameObject.SetActive(false);
            Assert.That(toggled.State, Is.EqualTo(FlowFieldRuntimeState.Suspended));
            Assert.That(FlowFieldBuildScheduler.Count, Is.EqualTo(baseline));
            Assert.That(FlowFieldBuildScheduler.Contains(toggled.Session), Is.False, "A stopped session stayed in the scheduler.");
            yield return FlowFieldPlayScene.WaitSeconds(1f);

            toggled.gameObject.SetActive(true);
            AssertScheduled(toggled, baseline + 1, FlowFieldRuntimeState.Building);
            yield return FlowFieldPlayScene.WaitSeconds(2f);
            yield return FlowFieldPlayScene.WaitUntilReady();
            AssertScheduled(toggled, baseline + 1, FlowFieldRuntimeState.Ready);

            FlowFieldSession toggledSession = toggled.Session;
            Object.Destroy(toggled.gameObject);
            yield return FlowFieldPlayScene.WaitSeconds(1f);
            Assert.That(FlowFieldBuildScheduler.Count, Is.EqualTo(baseline));
            Assert.That(FlowFieldBuildScheduler.Contains(toggledSession), Is.False, "A destroyed Manager's session stayed in the scheduler.");

            // The next Play session.
            yield return FlowFieldPlayScene.Exit();
            yield return FlowFieldSpaceModeScenarios.EnterAndSettle(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);
            int managers = Object.FindObjectsByType<FlowFieldManager>(FindObjectsSortMode.None).Length;
            Assert.That(FlowFieldBuildScheduler.Count, Is.EqualTo(managers), "Sessions from the previous Play session remain.");
            Assert.That(FlowFieldBuildScheduler.Contains(Manager.Session), Is.True);
            Assert.That(FlowFieldBuildScheduler.Contains(neverActiveSession), Is.False);
            Assert.That(FlowFieldBuildScheduler.Contains(toggledSession), Is.False);
        }

        // A copy of the scene's Manager on an inactive object: its Awake has not run.
        private static FlowFieldManager CreateInactiveCopy(FlowFieldManager source, string name)
        {
            var copyObject = new GameObject(name);
            copyObject.SetActive(false);
            copyObject.transform.position = source.transform.position;
            var copy = copyObject.AddComponent<FlowFieldManager>();
            EditorUtility.CopySerialized(source, copy);
            return copy;
        }

        private static void AssertScheduled(FlowFieldManager manager, int count, FlowFieldRuntimeState state)
        {
            Assert.That(manager.State, Is.EqualTo(state), FlowFieldPlayScene.Describe());
            Assert.That(FlowFieldBuildScheduler.Count, Is.EqualTo(count));
            Assert.That(FlowFieldBuildScheduler.Contains(manager.Session), Is.True);
        }
    }
}
