using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// R1: SetGoal on a Manager moved in Play Mode. A BakedBase Manager no
    /// longer matches its bake: SetGoal does not throw, the Manager faults
    /// with the mismatch and logs it once. A RuntimeDynamic one just builds
    /// there. Moving back and RequestRebuild gives the same field as a fresh
    /// Release and Init with the same Goal.
    /// </summary>
    [TestFixture, Category("SpaceMode")]
    internal sealed class MovedBakeManagerTests : FlowFieldPlayTestBase
    {
        private const string SurfaceMismatch = "Surface2D Bake Surface settings differ from the Manager settings.";
        private const string VolumeMismatch = "Volume3D Bake settings differ from the Manager settings.";
        private const string SampleHash = "72F30E598F91B32F";
        private const string VolumeHash = "E2B18A93A20865A2";

        [UnityTest, Timeout(300000)]
        public IEnumerator SampleBakedBase() => Run(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.BakedBase, SurfaceMismatch, SampleHash);

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeBakedBase() => Run(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.BakedBase, VolumeMismatch, VolumeHash);

        [UnityTest, Timeout(300000)]
        public IEnumerator SampleRuntimeDynamic() => Run(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic, null, SampleHash);

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeRuntimeDynamic() => Run(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.RuntimeDynamic, null, VolumeHash);

        private IEnumerator Run(string scene, FlowFieldBakeMode bakeMode, string mismatch, string recordedHash)
        {
            yield return FlowFieldSpaceModeScenarios.EnterAndSettle(scene, bakeMode);
            FlowFieldManager manager = Manager;
            Vector3 goal = FlowFieldSpaceModeScenarios.WorldBounds(manager).center + new Vector3(5f, 0f, 5f);
            var offset = new Vector3(3f, 0f, 0f);

            if (mismatch != null)
                FlowFieldSpaceModeScenarios.ExpectBuildFailed(Log, mismatch);
            manager.transform.position += offset;
            Assert.DoesNotThrow(() => manager.SetGoal(FlowFieldGoalRequest.Position(goal, 0f)));
            if (mismatch != null)
            {
                Assert.That(manager.IsFaulted, Is.True, FlowFieldSpaceModeScenarios.Report(manager));
                Assert.That(manager.LastError, Is.EqualTo(mismatch));
                yield return FlowFieldPlayScene.WaitSeconds(1f);
                Assert.That(manager.IsFaulted, Is.True);
                Assert.That(manager.LastError, Is.EqualTo(mismatch));
            }
            else
            {
                Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Building));
                yield return FlowFieldPlayScene.WaitSeconds(1f);
                yield return FlowFieldPlayScene.WaitUntilReady();
                Assert.That(manager.IsFaulted, Is.False);
            }

            manager.transform.position -= offset;
            Assert.DoesNotThrow(() => manager.RequestRebuild());
            Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Building));
            Assert.That(manager.IsFaulted, Is.False);
            yield return FlowFieldPlayScene.WaitUntilSettled(1f);
            string retry = FlowFieldPlayScene.Hash(manager);

            FlowFieldSpaceModeScenarios.ReleaseAndInit(manager, goal);
            yield return FlowFieldPlayScene.WaitUntilSettled(1f);
            string fresh = FlowFieldPlayScene.Hash(manager);

            Assert.That(retry, Is.EqualTo(fresh), "The retry differs from a fresh build: " + FlowFieldPlayScene.Describe());
            FlowFieldRecorded.AreEqual(recordedHash, retry, "r1 " + bakeMode + " " + manager.SpaceMode + " retry hash");
        }
    }
}
