using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// An input storm (Goals, region notices, rebuilds, Release and Init) with
    /// builds still running, then one fixed Goal. The field must equal a fresh
    /// Release, Init and the same Goal: no input left a trace behind.
    /// </summary>
    [TestFixture, Category("Gate")]
    internal sealed class StormTests : FlowFieldPlayTestBase
    {
        private const int Steps = 18;

        [UnityTest, Timeout(300000)]
        public IEnumerator SampleRuntimeDynamicEachFrame() => Storm(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic, 1);

        [UnityTest, Timeout(300000)]
        public IEnumerator SampleBakedBaseEachFrame() => Storm(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.BakedBase, 1);

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeRuntimeDynamicEachFrame() => Storm(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.RuntimeDynamic, 1);

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeBakedBaseEachFrame() => Storm(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.BakedBase, 1);

        [UnityTest, Timeout(300000)]
        public IEnumerator SampleRuntimeDynamicSpaced() => Storm(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic, 5);

        [UnityTest, Timeout(300000)]
        public IEnumerator SampleBakedBaseSpaced() => Storm(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.BakedBase, 5);

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeRuntimeDynamicSpaced() => Storm(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.RuntimeDynamic, 5);

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeBakedBaseSpaced() => Storm(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.BakedBase, 5);

        private static IEnumerator Storm(string scene, FlowFieldBakeMode bakeMode, int framesBetweenSteps)
        {
            yield return FlowFieldPlayScene.Enter(scene, bakeMode);
            FlowFieldManager manager = Manager;
            Assert.That(manager.TryGetFieldInfo(out FlowFieldFieldInfo info), Is.True);
            Bounds bounds = info.WorldBounds;
            for (int step = 0; step < Steps; step++)
            {
                RunStep(manager, bounds, step);
                yield return FlowFieldPlayScene.WaitFrames(framesBetweenSteps);
            }

            Vector3 goal = FinalGoal(bounds);
            SetGoal(manager, goal);
            yield return Settle();
            string afterStorm = FlowFieldPlayScene.Hash(manager);

            manager.Release();
            manager.Init();
            SetGoal(manager, goal);
            yield return Settle();
            string fresh = FlowFieldPlayScene.Hash(manager);
            Assert.That(afterStorm, Is.EqualTo(fresh), "The storm left a trace: " + FlowFieldPlayScene.Describe());
        }

        // The storm steps of the earlier local kit, with its seeds.
        private static void RunStep(FlowFieldManager manager, Bounds bounds, int step)
        {
            var random = new System.Random(1234 + step);
            switch (step % 6)
            {
                case 0:
                    SetGoal(manager, Pick(bounds, random));
                    break;
                case 1:
                    manager.NotifyObstacleRegionDirty(new Bounds(Pick(bounds, random), new Vector3(6f, 6f, 6f)));
                    break;
                case 2:
                    manager.SetGoal(FlowFieldGoalRequest.Position(Pick(bounds, random), 1.5f));
                    SetGoal(manager, Pick(bounds, random));
                    break;
                case 3:
                    manager.RequestRebuild();
                    break;
                case 4:
                    manager.Release();
                    manager.Init();
                    break;
                default:
                    manager.SetGoal(FlowFieldGoalRequest.None);
                    break;
            }
        }

        internal static Vector3 Pick(Bounds bounds, System.Random random)
            => new Vector3(
                Mathf.Lerp(bounds.min.x, bounds.max.x, (float)random.NextDouble()),
                Mathf.Lerp(bounds.min.y, bounds.max.y, (float)random.NextDouble()),
                Mathf.Lerp(bounds.min.z, bounds.max.z, (float)random.NextDouble()));

        internal static Vector3 FinalGoal(Bounds bounds)
            => new Vector3(
                Mathf.Lerp(bounds.min.x, bounds.max.x, 0.3f),
                bounds.center.y,
                Mathf.Lerp(bounds.min.z, bounds.max.z, 0.7f));

        internal static void SetGoal(FlowFieldManager manager, Vector3 position)
            => manager.SetGoal(FlowFieldGoalRequest.Position(position, 0f));

        // Ready, two seconds, Ready again, as the kit waited.
        internal static IEnumerator Settle() => FlowFieldPlayScene.WaitUntilSettled(2f);
    }
}
