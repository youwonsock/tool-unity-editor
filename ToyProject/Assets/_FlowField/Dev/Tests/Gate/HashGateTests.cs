using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// Each sample scene in each Bake Mode settles to the same published field
    /// as the recorded baseline, bit for bit. A refactor that changes any
    /// published value fails here.
    /// </summary>
    [TestFixture, Category("Gate")]
    internal sealed class HashGateTests : FlowFieldPlayTestBase
    {
        private const int SampleCells = 10000;
        private const int VolumeCells = 4800;

        [UnityTest, Timeout(180000)]
        public IEnumerator SampleRuntimeDynamic()
            => Check(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic, "73B09B0EBBE1CB44", SampleCells);

        [UnityTest, Timeout(180000)]
        public IEnumerator SampleStaticBaked()
            => Check(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.StaticBaked, "6ED20D0B26E197D4", SampleCells);

        [UnityTest, Timeout(180000)]
        public IEnumerator SampleBakedBase()
            => Check(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.BakedBase, "73B09B0EBBE1CB44", SampleCells);

        [UnityTest, Timeout(180000)]
        public IEnumerator VolumeRuntimeDynamic()
            => Check(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.RuntimeDynamic, "1D0A6031A29E4DE7", VolumeCells);

        [UnityTest, Timeout(180000)]
        public IEnumerator VolumeStaticBaked()
            => Check(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.StaticBaked, "1D0A6031A29E4DE7", VolumeCells);

        [UnityTest, Timeout(180000)]
        public IEnumerator VolumeBakedBase()
            => Check(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.BakedBase, "1D0A6031A29E4DE7", VolumeCells);

        private static IEnumerator Check(string scene, FlowFieldBakeMode bakeMode, string expectedHash, int expectedCells)
        {
            yield return FlowFieldPlayScene.Enter(scene, bakeMode);
            yield return FlowFieldPlayScene.WaitUntilSettled();
            FlowFieldManager manager = Manager;
            Assert.That(FlowFieldPlayScene.CellCount(manager), Is.EqualTo(expectedCells));
            Assert.That(FlowFieldPlayScene.Hash(manager), Is.EqualTo(expectedHash), FlowFieldPlayScene.Describe());
            FlowFieldFieldChecks.AssertInvariants(manager);
        }
    }
}
