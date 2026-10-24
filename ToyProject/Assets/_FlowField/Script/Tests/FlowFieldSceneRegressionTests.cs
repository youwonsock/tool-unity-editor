using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    public sealed class FlowFieldSceneRegressionTests
    {
        [UnityTest]
        public IEnumerator SurfaceShowcasePublishesAndAcceptsRuntimeGoalChange()
            => CheckScene("Assets/_FlowField/Scene/FlowFieldSample.unity", FlowFieldSpaceMode.Surface2D, FlowFieldBakeMode.RuntimeDynamic);

        [UnityTest]
        public IEnumerator VolumeShowcaseLoadsExistingStaticBake()
            => CheckScene("Assets/_FlowField/Scene/FlowFieldVolume3DTest.unity", FlowFieldSpaceMode.Volume3D, FlowFieldBakeMode.StaticBaked);

        private static IEnumerator CheckScene(string path, FlowFieldSpaceMode space, FlowFieldBakeMode bake)
        {
            yield return new EnterPlayMode();
            yield return SceneManager.LoadSceneAsync(path);
            var manager = Object.FindFirstObjectByType<FlowFieldManager>();
            Assert.That(manager, Is.Not.Null);
            float deadline = Time.realtimeSinceStartup + 30f;
            while (!manager.IsReady && !manager.IsFaulted && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(manager.IsReady, Is.True, manager.LastError);
            Assert.That(manager.SpaceMode, Is.EqualTo(space));
            Assert.That(manager.BakeMode, Is.EqualTo(bake));
            Assert.That(manager.TryGetFieldInfo(out var info), Is.True);
            Assert.That(info.Grid.CellCount, Is.GreaterThan(0));
            if (bake == FlowFieldBakeMode.RuntimeDynamic)
            {
                int revision = manager.Revision;
                Vector3 goal = info.Grid.Origin + new Vector3(info.Grid.CellSize * 2.5f, 0f, info.Grid.CellSize * 2.5f);
                manager.SetGoal(FlowFieldGoalRequest.Position(goal));
                manager.RequestRebuild();
                deadline = Time.realtimeSinceStartup + 30f;
                while (manager.Revision == revision && !manager.IsFaulted && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.That(manager.Revision, Is.GreaterThan(revision), manager.LastError);
            }
            yield return new ExitPlayMode();
        }
    }
}
