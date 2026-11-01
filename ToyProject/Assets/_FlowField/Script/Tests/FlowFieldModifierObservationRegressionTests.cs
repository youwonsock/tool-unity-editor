using System;
using NUnit.Framework;
using UnityEngine;

namespace Common.FlowField.Tests
{
    public sealed class FlowFieldModifierObservationRegressionTests
    {
        private GameObject _owner;
        private BoxCollider _collider;
        [SetUp] public void SetUp()
        {
            _owner = new GameObject("Modifier observation regression");
            _owner.layer = FlowFieldRegressionFixture.Layer;
            _owner.transform.position = Vector3.one * 1.5f;
            _collider = _owner.AddComponent<BoxCollider>();
            _collider.isTrigger = true;
            _collider.size = Vector3.one * 5f;
        }
        [TearDown] public void TearDown() => UnityEngine.Object.DestroyImmediate(_owner);
        private static FlowFieldVolumeRequest VolumeRequest(Vector3 direction)
            => new FlowFieldVolumeRequest(FlowFieldGridSpace.FromVolumeCellGrid(Vector3.zero,3,3,3,1f),
                new Bounds(Vector3.one * 1.5f, Vector3.one * 3f),FlowFieldBakeMode.RuntimeDynamic,
                1 << FlowFieldRegressionFixture.Layer,0f,false,Vector3.zero,0f,direction,1024,null,null);
        private static void Finish(FlowFieldVolumeSession session)
        {
            for (int i=0;i<100 && session.IsRebuilding;i++) session.Pump(10d);
            Assert.That(session.IsReady,Is.True,session.LastError);
        }

        [Test]
        public void SurfaceRequestKeepsTheCapturedBakeVersionWhenAssetIsReapplied()
            => CheckCapturedBakeVersion(false);

        [Test]
        public void SurfaceRequestKeepsCapturedContentGenerationWhenRevisionIsUnchanged()
            => CheckCapturedBakeVersion(true);

        private static void CheckCapturedBakeVersion(bool inspectorEdit)
        {
            using var source = new FlowFieldSession(FlowFieldRegressionFixture.Surface());
            source.Initialize(FlowFieldBakeMode.RuntimeDynamic,FlowFieldSessionSourceKind.SceneBuild,
                FlowFieldBfsBackendPolicy.ManagedOnly,null);
            var asset=ScriptableObject.CreateInstance<FlowFieldStaticBakeData>();
            try
            {
                void Bake(Vector3? goal)
                {
                    source.Submit(FlowFieldRegressionFixture.Request(goal));source.Pump();
                    var workspace=source.CommittedWorkspace;
                    asset.Apply(FlowFieldRegressionFixture.Settings,source.CommittedSurface,1<<FlowFieldRegressionFixture.Layer,
                        1f,.5f,0f,goal.HasValue,goal??Vector3.zero,0f,workspace.ResolvedGoalIndex,workspace);
                }
                FlowFieldSessionRequest Request() => FlowFieldSessionRequest.ForStaticSnapshot(
                    FlowFieldRegressionFixture.Settings,asset,asset.CreateSnapshot(FlowFieldRegressionFixture.Settings,
                        1<<FlowFieldRegressionFixture.Layer,1f,.5f,0f),1<<FlowFieldRegressionFixture.Layer,
                    1f,.5f,0f,Vector3.forward,FlowFieldDirtyFlags.None,1024);
                Bake(null);
                var before = Request();
                var unchanged = Request();
                Assert.That(before.HasSameInputs(unchanged), Is.True);
                Assert.That(before.GetInputDirtyFlags(unchanged), Is.EqualTo(FlowFieldDirtyFlags.None));
                using var session = new FlowFieldSession(FlowFieldRegressionFixture.Surface());
                var errors = new FlowFieldRegressionFixture.EventFailures(session);
                session.Initialize(FlowFieldBakeMode.StaticBaked, FlowFieldSessionSourceKind.StaticSnapshot,
                    FlowFieldBfsBackendPolicy.ManagedOnly, null);
                session.Submit(before); session.Pump();
                Assert.That(session.IsReady, Is.True, session.LastError);
                int revision = session.Revision;
                if (inspectorEdit)
                {
                    // Simulate a serialized reload/Undo: valid new data can carry
                    // the old persisted revision while OnValidate advances content generation.
                    Bake(new Vector3(2.5f, 0f, 2.5f));
                    typeof(FlowFieldStaticBakeData).GetField("_revision",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                        .SetValue(asset, before.StaticBakeSnapshot.Revision);
                    typeof(FlowFieldStaticBakeData).GetMethod("OnValidate",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(asset, null);
                }
                else Bake(new Vector3(2.5f,0f,2.5f));
                var after = Request();
                Assert.That(after.StaticBakeSnapshot.Revision, inspectorEdit
                    ? Is.EqualTo(before.StaticBakeSnapshot.Revision)
                    : Is.GreaterThan(before.StaticBakeSnapshot.Revision));
                Assert.That(before.HasSameInputs(after),Is.False,"Input comparison must use captured bake versions.");
                Assert.That(before.HasSameSource(after),Is.False);
                Assert.That(before.GetInputDirtyFlags(after),Is.EqualTo(FlowFieldDirtyFlags.All));
                session.Submit(after);
                Assert.That(session.IsReady, Is.False, "A changed bake must hide the old source immediately.");
                Assert.That(session.TrySample(new Vector3(.5f, 0f, .5f), out _), Is.False);
                session.Pump();
                Assert.That(session.IsReady, Is.True, session.LastError);
                Assert.That(session.Revision, Is.EqualTo(revision + 1));
                Assert.That(session.Sample(new Vector3(.5f, 0f, .5f)).Direction, Is.Not.EqualTo(Vector3.forward));
                session.Submit(Request()); session.Pump();
                Assert.That(session.Revision, Is.EqualTo(revision + 1), "An unchanged bake must not republish.");
                errors.AssertNone();
            }
            finally {UnityEngine.Object.DestroyImmediate(asset);}
        }

        [Test]
        public void NestedObservationKeepsTheAcceptedRegistrationSet([Values(false, true)] bool volume)
        {
            using var surfaceSession = new FlowFieldSession(FlowFieldRegressionFixture.Surface());
            using var volumeSession = new FlowFieldVolumeSession();
            FlowFieldSessionBase owner = volume ? (FlowFieldSessionBase)volumeSession : surfaceSession;
            if (volume) volumeSession.Initialize(FlowFieldBakeMode.RuntimeDynamic);
            else surfaceSession.Initialize(FlowFieldBakeMode.RuntimeDynamic, FlowFieldSessionSourceKind.SceneBuild,
                FlowFieldBfsBackendPolicy.ManagedOnly, null);
            var first = new Modifier { Collider = _collider, Order = 1 };
            var second = new Modifier { Collider = _collider, Order = 2 };
            owner.RegisterModifierCommand(first); owner.RegisterModifierCommand(second);
            bool entered = false;
            second.ReadRevision = () =>
            {
                if (entered) return; entered = true;
                owner.UnregisterModifierCommand(first);
                if (volume) volumeSession.DetectModifierChanges(); else surfaceSession.DetectModifierChanges();
            };
            Assert.DoesNotThrow(() => { if (volume) volumeSession.DetectModifierChanges(); else surfaceSession.DetectModifierChanges(); });
            Assert.That(entered, Is.True);
            if (volume) { volumeSession.Submit(VolumeRequest(Vector3.forward)); Finish(volumeSession); }
            else { surfaceSession.Submit(FlowFieldRegressionFixture.Request()); surfaceSession.Pump(); }
            Assert.That(owner.Sample(Vector3.one * .5f).SpeedMultiplier, Is.EqualTo(2f));
        }

        [Test]
        public void CurrentMetadataExceptionsRemainVisible(
            [Values(false, true)] bool volume,
            [Values("register", "observe")] string operation,
            [Values("collider", "priority", "revision")] string getter)
        {
            var surface = new FlowFieldModifierRegistry();
            var registry = new FlowFieldVolumeModifierRegistry();
            var modifier = new Modifier { Collider = _collider, Order = 1 };
            if (operation == "observe")
            {
                if (volume) registry.Register(modifier); else surface.Register(modifier);
            }
            var expected = new InvalidOperationException("Current metadata failure");
            Action callback = () => throw expected;
            if (getter == "collider") modifier.ReadCollider = callback;
            else if (getter == "priority") modifier.ReadPriority = callback;
            else modifier.ReadRevision = callback;
            var actual = Assert.Throws<InvalidOperationException>(() =>
            {
                if (operation == "register") { if (volume) registry.Register(modifier); else surface.Register(modifier); }
                else { if (volume) registry.DetectChanges(); else surface.DetectChanges(); }
            });
            Assert.That(actual, Is.SameAs(expected));
            Assert.That(volume ? registry.Count : surface.Entries.Count, Is.EqualTo(operation == "register" ? 0 : 1));
        }

        [Test]
        public void ObservedPrioritiesAreValidatedBeforeAnyCacheIsUpdated([Values(false, true)] bool volume)
        {
            var surface = new FlowFieldModifierRegistry();
            var registry = new FlowFieldVolumeModifierRegistry();
            var first = new Modifier { Collider = _collider, Order = 1 };
            var second = new Modifier { Collider = _collider, Order = 2 };
            if (volume) { registry.Register(first); registry.Register(second); }
            else { surface.Register(first); surface.Register(second); }
            first.Order = second.Order = 3;
            Assert.Throws<InvalidOperationException>(() =>
            {
                if (volume) registry.DetectChanges(); else surface.DetectChanges();
            });
            first.Order = 4;
            int reads = 0;
            first.ReadPriority = second.ReadPriority = () => reads++;
            if (volume)
            {
                Assert.That(registry.DetectChanges(), Is.True);
                Assert.That(reads, Is.EqualTo(2), "Sorting must use captured priorities.");
                Assert.That(registry.CaptureSnapshots()[0].Snapshot, Is.SameAs(second));
                Assert.That(registry.DetectChanges(), Is.False);
            }
            else
            {
                Assert.That(surface.DetectChanges().FinalDirty, Is.True);
                Assert.That(reads, Is.EqualTo(2), "Validation and sorting must share captured priorities.");
                Assert.That(surface.Entries[0].Modifier, Is.SameAs(second));
                Assert.That(surface.DetectChanges().FinalDirty, Is.False);
            }
        }

        private sealed class Modifier : IFlowFieldVectorModifier,IFlowFieldModifierSnapshot
        {
            internal Collider Collider;
            internal int Order;
            internal Action ReadCollider, ReadRevision, ReadPriority;
            public Collider InfluenceCollider {get{ReadCollider?.Invoke();return Collider;}}
            public int Priority { get { ReadPriority?.Invoke(); return Order; } }
            public int Revision {get{ReadRevision?.Invoke();return 0;}}
            public FlowFieldModifierObservation Observe() => new FlowFieldModifierObservation(InfluenceCollider, Priority, Revision);
            public IFlowFieldModifierSnapshot CaptureSnapshot()=>this;
            public FlowFieldVectorState Modify(in FlowFieldVectorState state,in FlowFieldVectorModifierContext context)
                =>new FlowFieldVectorState(state.Direction,state.SpeedMultiplier*2f);
        }
    }
}
