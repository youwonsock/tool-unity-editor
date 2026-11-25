using System.Collections;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Common.FlowField.Editor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// E2: a bake refuses settings Init rejects (here a rotated Manager)
    /// before it writes anything, so the asset a working bake saved stays
    /// assigned and unchanged. Runs on a Temp copy of each sample scene.
    /// </summary>
    [TestFixture, Category("EditorBake")]
    internal sealed class BakeRefusalTests
    {
        private const string RotatedReason = "FlowField Manager must use an unrotated transform with unit scale.";

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
        public IEnumerator SampleRotatedManagerKeepsTheAsset() => RotatedManagerKeepsTheAsset(FlowFieldPlayScene.SampleScene);

        [UnityTest, Timeout(600000)]
        public IEnumerator VolumeRotatedManagerKeepsTheAsset() => RotatedManagerKeepsTheAsset(FlowFieldPlayScene.VolumeScene);

        private static IEnumerator RotatedManagerKeepsTheAsset(string scene)
        {
            FlowFieldManager manager = FlowFieldBakeTestSupport.OpenBakeCopy(scene);
            yield return FlowFieldBakeTestSupport.Bake(manager);
            FlowFieldBakeAsset asset = FlowFieldBakeTestSupport.Assigned(manager);
            FlowFieldBakeTestSupport.AssertInTemp(asset, "working bake");
            string path = FlowFieldBakeTestSupport.PathOf(asset);
            int revision = asset.Revision;
            byte[] bytes = FlowFieldBakeTestSupport.FileBytes(asset);

            manager.transform.rotation = Quaternion.Euler(0f, 10f, 0f);
            Assert.That(manager.TryValidateConfig(out string initReason), Is.False, "Init must reject a rotated Manager.");
            Assert.That(initReason, Is.EqualTo(RotatedReason));
            LogAssert.Expect(LogType.Exception, new Regex(Regex.Escape("FlowField Bake did not start: " + RotatedReason)));
            yield return FlowFieldBakeTestSupport.Bake(manager);

            Assert.That(FlowFieldBakeTestSupport.Assigned(manager), Is.SameAs(asset), "The refused bake must keep the asset assigned.");
            Assert.That(FlowFieldBakeTestSupport.PathOf(asset), Is.EqualTo(path));
            Assert.That(asset.Revision, Is.EqualTo(revision), "The refused bake must not write the asset.");
            Assert.That(FlowFieldBakeTestSupport.FileBytes(asset), Is.EqualTo(bytes), "The refused bake changed the asset file.");
        }
    }
}
