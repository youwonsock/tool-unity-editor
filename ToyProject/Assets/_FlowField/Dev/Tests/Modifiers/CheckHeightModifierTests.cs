using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// M4: a Speed Modifier trigger sits above the Surface2D obstacle check box,
    /// so it touches no cell. Raising Check Height in Play must bring its cells
    /// in, exactly as a fresh Release and Init with the same inputs does.
    /// </summary>
    [TestFixture, Category("Modifiers")]
    internal sealed class CheckHeightModifierTests : FlowFieldPlayTestBase
    {
        private const string ProbeName = "FlowFieldTestM4";
        private const string GateName = "West Ramp Gate";
        private const string CheckHeight = FlowFieldManager.PropertyNames.Surface + "." + nameof(FlowFieldSurfaceSettings.ObstacleCheckHeight);

        [UnityTest, Timeout(300000)]
        public IEnumerator SampleRuntimeDynamic()
        {
            yield return FlowFieldPlayScene.Enter(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);
            yield return FlowFieldPlayScene.WaitUntilSettled(FlowFieldModifierScenarios.StartSeconds);
            FlowFieldManager manager = Manager;
            Bounds bounds = FlowFieldModifierScenarios.WorldBounds(manager);
            Vector3 spot = bounds.center + new Vector3(10f, 0f, -20f);
            int groundMask = new SerializedObject(manager).FindProperty(FlowFieldManager.PropertyNames.GroundLayer).intValue;
            Assert.That(
                Physics.Raycast(new Vector3(spot.x, bounds.max.y + 50f, spot.z), Vector3.down, out RaycastHit hit, 500f, groundMask),
                Is.True, "No ground below the Modifier spot.");
            // 2.6 above the ground, a 0.4 high box: above the default 2 m check box.
            FlowFieldModifierScenarios.CreateSpeedModifier(
                manager, ProbeName, new Vector3(spot.x, hit.point.y + 2.6f, spot.z), new Vector3(4f, 0.4f, 4f),
                priority: 777, speedMultiplier: 2f);
            yield return FlowFieldPlayScene.WaitUntilSettled(0.5f);
            Assert.That(Influence(manager), Is.EqualTo("influenced=0 fast=0"), "Check Height 2 already reaches the trigger.");

            var serialized = new SerializedObject(manager);
            serialized.FindProperty(CheckHeight).floatValue = 4f;
            serialized.ApplyModifiedProperties();
            yield return FlowFieldPlayScene.WaitUntilSettled(0.5f);
            string edited = Influence(manager);
            string editedHash = FlowFieldPlayScene.Hash(manager);
            Assert.That(FlowFieldModifierScenarios.InfluencedCells(manager), Is.GreaterThan(0), "Raising Check Height brought no cell in.");

            ReleaseAndInitWithSameInputs(manager);
            yield return FlowFieldPlayScene.WaitUntilSettled(0.5f);
            string fresh = Influence(manager);
            string freshHash = FlowFieldPlayScene.Hash(manager);

            Assert.That(fresh, Is.EqualTo(edited), "The edit and a fresh build touch different cells.");
            Assert.That(editedHash, Is.EqualTo(freshHash), "The edited field differs from a fresh build: " + FlowFieldPlayScene.Describe());
            FlowFieldRecorded.AreEqual("influenced=10 fast=10 hash=26566BDD99B3CAEC", edited + " hash=" + editedHash, "m4 Check Height 4 field");
        }

        private static string Influence(FlowFieldManager manager)
            => "influenced=" + FlowFieldModifierScenarios.InfluencedCells(manager)
                + " fast=" + FlowFieldModifierScenarios.FasterCells(manager);

        // Release drops the gate registration and the Goal; both come back as
        // they were. The Modifier Volume registers itself again at Init.
        private static void ReleaseAndInitWithSameInputs(FlowFieldManager manager)
        {
            GameObject gateObject = GameObject.Find(GateName);
            Collider gate = gateObject == null ? null : gateObject.GetComponent<Collider>();
            bool gateRegistered = gate != null && manager.IsDynamicObstacleRegistered(gate);
            Assert.That(manager.TryGetFieldInfo(out FlowFieldFieldInfo info), Is.True);
            manager.Release();
            manager.Init();
            if (gateRegistered)
                manager.RegisterDynamicObstacle(gate);
            if (info.HasRequestedGoal)
                manager.SetGoal(FlowFieldGoalRequest.Position(info.RequestedGoalWorld, info.GoalInfluenceRadius));
        }
    }
}
