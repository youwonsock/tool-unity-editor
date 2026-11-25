using System;
using System.Collections;
using NUnit.Framework;
using Common.FlowField.Editor;
using UnityEngine.TestTools;
using static Common.FlowField.Tests.FlowFieldGoalTestSupport;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// A Goal Influence Radius edit in Play on a StaticBaked Manager, which
    /// keeps its baked Goal. SBRAD: the field and the radius the field info
    /// reports stay the baked ones through the edit and a RequestRebuild, and
    /// Release and Init refuse the edited settings. E9TVC: the Editor bake
    /// check sees the edit as a Goal that differs from the bake, and accepts
    /// the radius again once the edit is undone; the session never faults.
    /// </summary>
    [TestFixture, Category("Goal")]
    internal sealed class StaticBakedGoalRadiusTests : FlowFieldPlayTestBase
    {
        private const float RadiusEdit = 2.5f;
        private const string GoalDiffers = " Bake Goal differs from the Manager settings. ReBake is required.";

        [UnityTest, Timeout(300000)]
        public IEnumerator SampleFieldKeepsTheBakedRadius() => FieldKeepsTheBakedRadius(FlowFieldPlayScene.SampleScene);

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeFieldKeepsTheBakedRadius() => FieldKeepsTheBakedRadius(FlowFieldPlayScene.VolumeScene);

        [UnityTest, Timeout(300000)]
        public IEnumerator SampleBakeCheckSeesTheRadiusEdit() => BakeCheckSeesTheRadiusEdit(FlowFieldPlayScene.SampleScene);

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeBakeCheckSeesTheRadiusEdit() => BakeCheckSeesTheRadiusEdit(FlowFieldPlayScene.VolumeScene);

        private static IEnumerator FieldKeepsTheBakedRadius(string scene)
        {
            yield return FlowFieldPlayScene.Enter(scene, FlowFieldBakeMode.StaticBaked);
            FlowFieldManager manager = Manager;
            string goalDiffers = manager.SpaceMode + GoalDiffers;
            float authored = SerializedRadius(manager);
            float baked = Info(manager).GoalInfluenceRadius;
            Assert.That(baked, Is.EqualTo(authored), "The committed bake must match the authored radius: " + DescribeGoal(manager));
            string field = FlowFieldPlayScene.Hash(manager);

            SetSerializedRadius(manager, authored + RadiusEdit);
            AssertBakedField(manager, baked, null, "right after the edit");
            yield return FlowFieldPlayScene.WaitSeconds(1.5f);
            yield return FlowFieldPlayScene.WaitUntilReady();
            AssertBakedField(manager, baked, field, "a while after the edit");
            Assert.That(SerializedRadius(manager), Is.EqualTo(authored + RadiusEdit));

            Assert.DoesNotThrow(() => manager.RequestRebuild());
            yield return Step();
            AssertBakedField(manager, baked, field, "after RequestRebuild");

            manager.Release();
            var refused = Assert.Throws<InvalidOperationException>(() => manager.Init());
            Assert.That(refused.Message, Is.EqualTo(goalDiffers));
            Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Released), FlowFieldPlayScene.Describe());
            Assert.That(manager.TryGetFieldInfo(out _), Is.False, "A refused Init must leave no field.");
        }

        private static IEnumerator BakeCheckSeesTheRadiusEdit(string scene)
        {
            yield return FlowFieldPlayScene.Enter(scene, FlowFieldBakeMode.StaticBaked);
            FlowFieldManager manager = Manager;
            string goalDiffers = manager.SpaceMode + GoalDiffers;
            float authored = SerializedRadius(manager);
            AssertBakeCheck(manager, null, "authored radius");

            SetSerializedRadius(manager, authored + RadiusEdit);
            AssertBakeCheck(manager, goalDiffers, "right after the edit");
            yield return Step();
            AssertBakeCheck(manager, goalDiffers, "after the edit settled");
            Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Ready), FlowFieldPlayScene.Describe());

            SetSerializedRadius(manager, authored);
            yield return Step();
            AssertBakeCheck(manager, null, "radius edited back");
            Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Ready), FlowFieldPlayScene.Describe());
        }

        // A null hash skips the field check, for a read in the frame of the edit.
        private static void AssertBakedField(FlowFieldManager manager, float bakedRadius, string fieldHash, string what)
        {
            Assert.That(manager.IsFaulted, Is.False, what + ": " + FlowFieldPlayScene.Describe());
            Assert.That(Info(manager).GoalInfluenceRadius, Is.EqualTo(bakedRadius),
                what + ": the field info must report the baked radius, " + DescribeGoal(manager));
            if (fieldHash != null)
                Assert.That(FlowFieldPlayScene.Hash(manager), Is.EqualTo(fieldHash), what + ": the baked field changed.");
        }

        // A null reason expects the check to pass.
        private static void AssertBakeCheck(FlowFieldManager manager, string expectedReason, string what)
        {
            bool valid = manager.TryValidateConfig(out string reason);
            if (expectedReason == null)
            {
                Assert.That(valid, Is.True, what + ": " + reason);
                return;
            }
            Assert.That(valid, Is.False, what + ": the bake check must refuse the edited radius.");
            Assert.That(reason, Is.EqualTo(expectedReason), what);
        }
    }
}
