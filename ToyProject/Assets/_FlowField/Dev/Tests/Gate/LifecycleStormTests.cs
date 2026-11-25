using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// A lifecycle storm: disable and enable, sweep toggles, a Modifier added
    /// and toggled, cells dirty, Goals, region notices and rebuilds. The final
    /// inputs must give the same field as a fresh Release and Init with them.
    /// </summary>
    [TestFixture, Category("Gate")]
    internal sealed class LifecycleStormTests : FlowFieldPlayTestBase
    {
        private const int Steps = 28;
        private const string ProbeName = "FlowFieldTestLifecycleProbe";
        private const string GateName = "West Ramp Gate";

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

        private IEnumerator Storm(string scene, FlowFieldBakeMode bakeMode, int framesBetweenSteps)
        {
            yield return FlowFieldPlayScene.Enter(scene, bakeMode);
            FlowFieldManager manager = Manager;
            Assert.That(manager.TryGetFieldInfo(out FlowFieldFieldInfo info), Is.True);
            Bounds bounds = info.WorldBounds;
            GameObject probe = null;
            for (int step = 0; step < Steps; step++)
            {
                probe = RunStep(manager, bounds, step, probe);
                yield return FlowFieldPlayScene.WaitFrames(framesBetweenSteps);
            }

            Vector3 goal = StormTests.FinalGoal(bounds);
            Collider gate = FindGate();
            bool gateRegistered = gate != null && manager.IsDynamicObstacleRegistered(gate);
            ApplyFinalInputs(manager, probe, goal);
            yield return StormTests.Settle();
            string afterStorm = FlowFieldPlayScene.Hash(manager);

            manager.Release();
            manager.Init();
            if (gateRegistered)
                manager.RegisterDynamicObstacle(gate);
            ApplyFinalInputs(manager, probe, goal);
            yield return StormTests.Settle();
            string fresh = FlowFieldPlayScene.Hash(manager);
            Assert.That(afterStorm, Is.EqualTo(fresh), "The lifecycle storm left a trace: " + FlowFieldPlayScene.Describe());
        }

        private static GameObject RunStep(FlowFieldManager manager, Bounds bounds, int step, GameObject probe)
        {
            var random = new System.Random(77 + step);
            switch (step % 7)
            {
                case 0:
                    manager.enabled = false;
                    manager.enabled = true;
                    break;
                case 1:
                    SetSweep(manager, step % 2 == 1);
                    break;
                case 2:
                    if (probe == null)
                        probe = CreateProbe(manager, bounds.center);
                    else
                        probe.SetActive(!probe.activeSelf);
                    break;
                case 3:
                    // BakedBase keeps the baked ground, so it refuses to regenerate cells.
                    if (manager.BakeMode == FlowFieldBakeMode.BakedBase)
                        Assert.Throws<System.InvalidOperationException>(manager.NotifyCellsDirty);
                    else
                        manager.NotifyCellsDirty();
                    break;
                case 4:
                    manager.SetGoal(FlowFieldGoalRequest.Position(StormTests.Pick(bounds, random), 2f));
                    break;
                case 5:
                    manager.NotifyObstacleRegionDirty(new Bounds(StormTests.Pick(bounds, random), new Vector3(8f, 8f, 8f)));
                    break;
                default:
                    manager.RequestRebuild();
                    break;
            }
            return probe;
        }

        // Sweep off, the probe Modifier on, Goal G.
        private static void ApplyFinalInputs(FlowFieldManager manager, GameObject probe, Vector3 goal)
        {
            SetSweep(manager, false);
            if (probe != null && !probe.activeSelf)
                probe.SetActive(true);
            StormTests.SetGoal(manager, goal);
        }

        internal static void SetSweep(FlowFieldManager manager, bool on)
        {
            var serialized = new SerializedObject(manager);
            serialized.FindProperty(FlowFieldManager.PropertyNames.Obstacles + ".SweepUnregistered").boolValue = on;
            serialized.ApplyModifiedProperties();
        }

        // A Speed Modifier Volume made in Play Mode; it goes when Play Mode ends.
        private static GameObject CreateProbe(FlowFieldManager manager, Vector3 center)
        {
            var probe = new GameObject(ProbeName);
            probe.SetActive(false);
            probe.transform.position = center;
            var box = probe.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = new Vector3(6f, 6f, 6f);
            var modifier = probe.AddComponent<FlowFieldSpeedModifier>();
            var serialized = new SerializedObject(modifier);
            serialized.FindProperty("_flowFieldManager").objectReferenceValue = manager;
            serialized.FindProperty("_influenceCollider").objectReferenceValue = box;
            serialized.FindProperty("_priority").intValue = 4321;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            probe.SetActive(true);
            return probe;
        }

        private static Collider FindGate()
        {
            GameObject gate = GameObject.Find(GateName);
            return gate == null ? null : gate.GetComponent<Collider>();
        }
    }
}
