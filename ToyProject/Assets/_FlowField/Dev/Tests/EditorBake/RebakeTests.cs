using System.Collections;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// A re-bake of unchanged settings writes the same asset again: same path
    /// and owner, the Revision one higher, and every other serialized value
    /// the same. Runs on a Temp copy of each sample scene.
    /// </summary>
    [TestFixture, Category("EditorBake")]
    internal sealed class RebakeTests
    {
        private readonly FlowFieldTestLog _log = new FlowFieldTestLog();

        [SetUp]
        public void Prepare()
        {
            FlowFieldBakeTestSupport.DeleteTempFolder();
            _log.Start();
            _log.Expect(FlowFieldBakeTestSupport.BakeFinishedPattern);
        }

        [TearDown]
        public void CleanUp()
        {
            FlowFieldBakeTestSupport.Clean();
            _log.StopAndAssertClean();
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator SampleRebakeChangesOnlyTheRevision() => RebakeTwice(FlowFieldPlayScene.SampleScene);

        [UnityTest, Timeout(600000)]
        public IEnumerator VolumeRebakeChangesOnlyTheRevision() => RebakeTwice(FlowFieldPlayScene.VolumeScene);

        private static IEnumerator RebakeTwice(string scene)
        {
            FlowFieldManager manager = FlowFieldBakeTestSupport.OpenBakeCopy(scene);
            yield return FlowFieldBakeTestSupport.Bake(manager);
            FlowFieldBakeAsset first = FlowFieldBakeTestSupport.Assigned(manager);
            FlowFieldBakeTestSupport.AssertInTemp(first, "first bake");
            Assert.That(EditorSceneManager.SaveScene(manager.gameObject.scene), Is.True);
            string path = FlowFieldBakeTestSupport.PathOf(first);
            int revision = first.Revision;
            string owner = first.OwnerId;
            string text = FlowFieldBakeExport.Describe(first);

            yield return FlowFieldBakeTestSupport.Bake(manager);
            FlowFieldBakeAsset second = FlowFieldBakeTestSupport.Assigned(manager);
            FlowFieldBakeTestSupport.AssertInTemp(second, "second bake");
            Assert.That(FlowFieldBakeTestSupport.PathOf(second), Is.EqualTo(path), "The re-bake must write the Manager's own asset again.");
            Assert.That(second.Revision, Is.EqualTo(revision + 1), "Each bake advances the Revision once.");
            Assert.That(second.OwnerId, Is.EqualTo(owner));
            Assert.That(FlowFieldBakeExport.Describe(second), Is.EqualTo(text), "The re-bake changed more than the Revision.");
        }
    }
}
