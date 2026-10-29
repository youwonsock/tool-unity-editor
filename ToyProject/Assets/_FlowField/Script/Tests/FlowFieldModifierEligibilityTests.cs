using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// Surface2D and Volume3D share one Modifier rule: blocked, Goal anchor,
    /// unreachable and zero-direction cells keep their stopped base vector.
    /// </summary>
    public sealed class FlowFieldModifierEligibilityTests
    {
        private GameObject _area;

        [TearDown]
        public void TearDown()
        {
            if (_area != null)
                Object.DestroyImmediate(_area);
        }

        // FlowFieldGoalFlags is internal, so the cases pass its byte value.
        [TestCase(false, (byte)FlowFieldGoalFlagsValue.Directed, true)]
        [TestCase(false, (byte)0, true)]
        [TestCase(true, (byte)FlowFieldGoalFlagsValue.Directed, false)]
        [TestCase(false, (byte)(FlowFieldGoalFlagsValue.Directed | FlowFieldGoalFlagsValue.Anchor), false)]
        [TestCase(false, (byte)FlowFieldGoalFlagsValue.Unreachable, false)]
        public void EligibilityFollowsCellState(bool blocked, byte flagsValue, bool expected)
        {
            var flags = (FlowFieldGoalFlags)flagsValue;
            Assert.That(FlowFieldVectorUtility.CanApplyModifier(blocked, flags, Vector3.forward), Is.EqualTo(expected));
            Assert.That(FlowFieldVectorUtility.CanApplyModifier(blocked, flags, Vector3.zero), Is.False);
        }

        [Test]
        public void SurfaceDefaultDirectionAlongNormalIsStoppedAndIgnoresModifiers()
        {
            using var session = CreateSurface(out var failures);
            session.RegisterModifierCommand(SpeedModifier(new Vector3(1.5f, 0.5f, 1.5f)));
            session.Submit(FlowFieldRegressionFixture.Request(goal: null, direction: Vector3.up));
            session.Pump();
            Assert.That(session.IsReady, Is.True, session.LastError);

            FlowFieldSample sample = session.Sample(new Vector3(1.5f, 0f, 1.5f));
            Assert.That(sample.Direction, Is.EqualTo(Vector3.zero));
            Assert.That(sample.SpeedMultiplier, Is.Zero);
            failures.AssertNone();
        }

        [Test]
        public void SurfaceGoalAnchorIsStoppedAndIgnoresModifiers()
        {
            using var session = CreateSurface(out var failures);
            Vector3 goal = new Vector3(2.5f, 0f, 2.5f);
            session.RegisterModifierCommand(SpeedModifier(new Vector3(1.5f, 0.5f, 1.5f)));
            session.Submit(FlowFieldRegressionFixture.Request(goal));
            session.Pump();
            Assert.That(session.IsReady, Is.True, session.LastError);

            FlowFieldSample anchor = session.Sample(goal);
            Assert.That(anchor.Direction, Is.EqualTo(Vector3.zero));
            Assert.That(anchor.SpeedMultiplier, Is.Zero);
            Assert.That(session.Sample(new Vector3(0.5f, 0f, 0.5f)).SpeedMultiplier, Is.EqualTo(2f),
                "Directed cells still receive the Modifier.");
            failures.AssertNone();
        }

        [Test]
        public void VolumeGoalAnchorIsStoppedAndIgnoresModifiers()
        {
            var grid = FlowFieldGridSpace.FromVolumeCellGrid(Vector3.zero, 3, 3, 3, 1f);
            using var session = new FlowFieldVolumeSession();
            var failures = new FlowFieldRegressionFixture.EventFailures(session);
            session.Initialize(FlowFieldBakeMode.RuntimeDynamic);
            session.RegisterModifierCommand(SpeedModifier(Vector3.one * 1.5f));
            Vector3 goal = Vector3.one * 2.5f;
            session.Submit(new FlowFieldVolumeRequest(
                grid, new Bounds(Vector3.one * 1.5f, Vector3.one * 3f), FlowFieldBakeMode.RuntimeDynamic,
                1 << FlowFieldRegressionFixture.Layer, 0f, true, goal, 0f, Vector3.forward, 1024, null, null));
            for (int i = 0; i < 100 && session.IsRebuilding; i++)
                session.Pump(10d);
            Assert.That(session.IsReady, Is.True, session.LastError);

            FlowFieldSample anchor = session.Sample(goal);
            Assert.That(anchor.Direction, Is.EqualTo(Vector3.zero));
            Assert.That(anchor.SpeedMultiplier, Is.Zero);
            Assert.That(session.Sample(Vector3.one * 0.5f).SpeedMultiplier, Is.EqualTo(2f),
                "Directed cells still receive the Modifier.");
            failures.AssertNone();
        }

        private static FlowFieldSession CreateSurface(out FlowFieldRegressionFixture.EventFailures failures)
        {
            var session = new FlowFieldSession(FlowFieldRegressionFixture.Surface());
            failures = new FlowFieldRegressionFixture.EventFailures(session);
            session.Initialize(
                FlowFieldBakeMode.RuntimeDynamic,
                FlowFieldSessionSourceKind.SceneBuild,
                FlowFieldBfsBackendPolicy.ManagedOnly,
                null);
            return session;
        }

        [System.Flags]
        public enum FlowFieldGoalFlagsValue : byte
        {
            Directed = FlowFieldGoalFlags.Directed,
            Anchor = FlowFieldGoalFlags.Anchor,
            Unreachable = FlowFieldGoalFlags.Unreachable,
        }

        private FlowFieldRegressionFixture.Modifier SpeedModifier(Vector3 center)
        {
            _area = new GameObject("Modifier eligibility") { layer = FlowFieldRegressionFixture.Layer };
            var box = _area.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.center = center;
            box.size = Vector3.one * 4f;
            Physics.SyncTransforms();
            return new FlowFieldRegressionFixture.Modifier { InfluenceCollider = box, Priority = 100 };
        }
    }
}
