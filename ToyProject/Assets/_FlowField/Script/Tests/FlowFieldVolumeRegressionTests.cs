using System;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Common.FlowField.Tests
{
    public sealed class FlowFieldVolumeRegressionTests
    {
        private FlowFieldVolumeSession _session;
        private FlowFieldRegressionFixture.EventFailures _eventFailures;
        private GameObject _area;
        private GameObject _obstacle;
        private static readonly FlowFieldGridSpace Grid = FlowFieldGridSpace.FromVolumeCellGrid(Vector3.zero, 3, 3, 3, 1f);
        private static readonly Vector3 Position = Vector3.one * 0.5f;
        private static readonly Vector3 Goal = Vector3.one * 2.5f;

        [SetUp]
        public void SetUp()
        {
            _session = new FlowFieldVolumeSession();
            _eventFailures = new FlowFieldRegressionFixture.EventFailures(_session);
            _session.Initialize(FlowFieldBakeMode.RuntimeDynamic);
        }

        [TearDown]
        public void TearDown()
        {
            _session.Dispose();
            if (_area != null) Object.DestroyImmediate(_area);
            if (_obstacle != null) Object.DestroyImmediate(_obstacle);
            _eventFailures?.AssertNone();
        }

        private static FlowFieldVolumeRequest Request(Vector3? goal = null) => new FlowFieldVolumeRequest(
            Grid, new Bounds(Vector3.one * 1.5f, Vector3.one * 3f), FlowFieldBakeMode.RuntimeDynamic,
            1 << FlowFieldRegressionFixture.Layer, 0f, true, goal ?? Goal, 0f, Vector3.forward, null);

        private void Finish()
        {
            for (int i = 0; i < 100 && _session.IsRebuilding; i++) _session.Pump(10d);
            Assert.That(_session.IsFaulted, Is.False, _session.LastError);
            Assert.That(_session.IsReady, Is.True, _session.LastError);
            Assert.That(_session.IsRebuilding, Is.False);
        }

        [Test]
        public void ModifierChangeRecomposesThePublishedNavigation()
        {
            var modifier = Modifier();
            _session.RegisterModifier(modifier);
            _session.Submit(Request());
            Finish();
            Assert.That(_session.Sample(Position).SpeedMultiplier, Is.EqualTo(2f));
            int revision = _session.Revision;

            // A collider nothing tracks only appears in a full build.
            _obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _obstacle.layer = FlowFieldRegressionFixture.Layer;
            _obstacle.transform.position = Position;
            Physics.SyncTransforms();

            modifier.Speed = 3f;
            modifier.Revision++;
            _session.NotifyModifierChangedCommand(modifier, FlowFieldModifierChange.Value);
            Assert.That(_session.IsRebuilding, Is.True);
            Finish();
            Assert.That(_session.Revision, Is.EqualTo(revision + 1));
            Assert.That(_session.Sample(Position).SpeedMultiplier, Is.EqualTo(3f));

            _session.Submit(Request());
            Finish();
            Assert.That(_session.Sample(Position).SpeedMultiplier, Is.Not.EqualTo(3f));
        }

        [Test]
        public void AViewKeepsItsArraysAcrossLaterBuilds()
        {
            _session.Submit(Request(new Vector3(0.5f, 0.5f, 0.5f)));
            Finish();
            Assert.That(_session.TryGetView(out _, out bool[] blocked, out Vector3[] directions, out float[] speeds), Is.True);
            Vector3[] directionsBefore = (Vector3[])directions.Clone();
            float[] speedsBefore = (float[])speeds.Clone();

            for (int goal = 0; goal < 4; goal++)
            {
                _session.Submit(Request(new Vector3(2.5f - goal % 2, 2.5f, 0.5f + goal % 3)));
                Finish();
            }

            Assert.That(_session.TryGetView(out _, out _, out Vector3[] latest, out _), Is.True);
            Assert.That(latest, Is.Not.SameAs(directions));
            Assert.That(directions, Is.EqualTo(directionsBefore), "A later build wrote into arrays a view still holds.");
            Assert.That(speeds, Is.EqualTo(speedsBefore));
            Assert.That(blocked, Is.Not.Null);
        }

        [Test]
        public void AReplacedFieldNobodyHoldsIsReusedByTheNextBuild()
        {
            _session.Submit(Request(new Vector3(0.5f, 0.5f, 0.5f)));
            Finish();
            Assert.That(_session.HasSpareArrays, Is.True, "The build's search arrays stay for the next one.");
            var committed = typeof(FlowFieldVolumeSession).GetField(
                "_committed",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var first = (FlowFieldVolumeFieldData)committed.GetValue(_session);

            _session.Submit(Request(new Vector3(2.5f, 2.5f, 0.5f)));
            Finish();
            _session.Submit(Request(new Vector3(0.5f, 2.5f, 2.5f)));
            Finish();

            var third = (FlowFieldVolumeFieldData)committed.GetValue(_session);
            Assert.That(third.FinalDirections, Is.SameAs(first.FinalDirections));
            Assert.That(third.NextCells, Is.SameAs(first.NextCells));
        }

        [Test]
        public void GoalChangeReusesObstaclesUntilAnExplicitRebuild()
        {
            _session.Submit(Request());
            Finish();

            // A collider nothing tracks only appears in a full build.
            _obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _obstacle.layer = FlowFieldRegressionFixture.Layer;
            _obstacle.transform.position = Vector3.one * 1.5f;
            Physics.SyncTransforms();
            Vector3 cell = Vector3.one * 1.5f;

            Vector3 goal = new Vector3(0.5f, 2.5f, 0.5f);
            _session.Submit(Request(goal));
            Finish();
            Assert.That(_session.TryGetFieldInfo(out var info), Is.True);
            Assert.That(info.ResolvedGoalWorld, Is.EqualTo(goal));
            Assert.That(_session.Sample(cell).SpeedMultiplier, Is.GreaterThan(0f));

            _session.Submit(Request(goal));
            Finish();
            Assert.That(_session.TryGetView(out _, out bool[] blocked, out _, out _), Is.True);
            Assert.That(blocked[Grid.ToFlatIndex(1, 1, 1)], Is.True);
        }

        [Test]
        public void ExplicitRebuildQueriesObstaclesEvenWhenTheGoalChanges()
        {
            _session.Submit(Request());
            Finish();

            _obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _obstacle.layer = FlowFieldRegressionFixture.Layer;
            _obstacle.transform.position = Vector3.one * 1.5f;
            Physics.SyncTransforms();

            _session.Submit(Request(new Vector3(0.5f, 2.5f, 0.5f)), requeryObstacles: true);
            Finish();
            Assert.That(_session.TryGetView(out _, out bool[] blocked, out _, out _), Is.True);
            Assert.That(blocked[Grid.ToFlatIndex(1, 1, 1)], Is.True);
        }

        [Test]
        public void ModifierChangeDuringAGoalBuildKeepsReusingObstacles()
        {
            var modifier = Modifier();
            _session.RegisterModifier(modifier);
            _session.Submit(Request());
            Finish();

            _obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _obstacle.layer = FlowFieldRegressionFixture.Layer;
            _obstacle.transform.position = Vector3.one * 1.5f;
            Physics.SyncTransforms();

            _session.Submit(Request(new Vector3(0.5f, 2.5f, 0.5f)));
            modifier.Speed = 3f;
            modifier.Revision++;
            _session.NotifyModifierChangedCommand(modifier, FlowFieldModifierChange.Value);
            Finish();

            Assert.That(_session.TryGetView(out _, out bool[] blocked, out _, out _), Is.True);
            Assert.That(blocked[Grid.ToFlatIndex(1, 1, 1)], Is.False);
            Assert.That(_session.Sample(Position).SpeedMultiplier, Is.EqualTo(3f));
        }

        [Test]
        public void GoalChangeQueriesObstaclesWhenATrackedObstacleMoved()
        {
            _obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _obstacle.layer = FlowFieldRegressionFixture.Layer;
            _obstacle.transform.position = new Vector3(1.5f, 1.5f, 0.5f);
            Physics.SyncTransforms();
            _session.RegisterDynamicObstacle(_obstacle.GetComponent<Collider>());
            _session.Submit(Request());
            Finish();

            _obstacle.transform.position = Vector3.one * 1.5f;
            Physics.SyncTransforms();
            _session.MarkObstacleRegionDirty(new Bounds(Vector3.one * 1.5f, Vector3.one * 3f));
            _session.Submit(Request(new Vector3(0.5f, 2.5f, 0.5f)));
            Finish();
            Assert.That(_session.TryGetView(out _, out bool[] blocked, out _, out _), Is.True);
            Assert.That(blocked[Grid.ToFlatIndex(1, 1, 1)], Is.True);
            Assert.That(blocked[Grid.ToFlatIndex(1, 1, 0)], Is.False);
        }

        [Test]
        public void ModifierChangeDuringABuildIsComposedAfterIt()
        {
            var modifier = Modifier();
            _session.RegisterModifier(modifier);
            _session.Submit(Request());
            Finish();

            _session.Submit(Request(Vector3.one * 0.5f + Vector3.right * 2f));
            FlowFieldVolumeSafetyRegressionTests.PumpOneStep(_session);
            Assert.That(_session.IsRebuilding, Is.True);
            modifier.Speed = 4f;
            modifier.Revision++;
            _session.NotifyModifierChangedCommand(modifier, FlowFieldModifierChange.Value);
            Finish();
            Assert.That(_session.Sample(Position).SpeedMultiplier, Is.EqualTo(4f));
        }

        [Test]
        public void GoalMoveWithinACellKeepsThePublishedField()
        {
            _session.Submit(Request());
            Finish();
            int revision = _session.Revision;
            Vector3 moved = Goal + new Vector3(0.1f, -0.2f, 0.3f);
            _session.Submit(Request(moved));
            Assert.That(_session.HasPendingRequest, Is.False);
            Assert.That(_session.IsRebuilding, Is.False);
            Finish();
            Assert.That(_session.Revision, Is.EqualTo(revision));
            Assert.That(_session.TryGetFieldInfo(out var info), Is.True);
            Assert.That(info.RequestedGoalWorld, Is.EqualTo(moved));
            Assert.That(info.ResolvedGoalWorld, Is.EqualTo(Goal));
        }

        [Test]
        public void GoalMoveWithinACellDoesNotQueueAfterTheRunningBuild()
        {
            _session.Submit(Request());
            FlowFieldVolumeSafetyRegressionTests.PumpOneStep(_session);
            Assert.That(_session.IsRebuilding, Is.True);
            int publications = 0;
            _session.FieldCommitted += () => publications++;
            _session.Submit(Request(Goal + new Vector3(0.1f, 0f, 0f)));
            Finish();
            Assert.That(publications, Is.EqualTo(1));
        }

        [Test]
        public void ExplicitRebuildStillRunsForAGoalMoveWithinACell()
        {
            _session.Submit(Request());
            Finish();
            int revision = _session.Revision;
            _session.Submit(Request(Goal + new Vector3(0.1f, 0f, 0f)), requeryObstacles: true);
            Assert.That(_session.HasPendingRequest, Is.True);
            Finish();
            Assert.That(_session.Revision, Is.EqualTo(revision + 1));
        }

        [Test]
        public void GoalMoveToAnotherCellStillRebuilds()
        {
            _session.Submit(Request());
            Finish();
            int revision = _session.Revision;
            _session.Submit(Request(Goal + new Vector3(-1f, 0f, 0f)));
            Finish();
            Assert.That(_session.Revision, Is.EqualTo(revision + 1));
        }

        [Test]
        public void ModifierChangeAfterAGoalMoveWithinACellStillRecomposes()
        {
            var modifier = Modifier();
            _session.RegisterModifier(modifier);
            _session.Submit(Request());
            Finish();
            _session.Submit(Request(Goal + new Vector3(0.1f, 0f, 0f)));
            modifier.Speed = 3f;
            modifier.Revision++;
            _session.NotifyModifierChangedCommand(modifier, FlowFieldModifierChange.Value);
            Assert.That(_session.HasPendingRequest, Is.True);
            Finish();
            Assert.That(_session.Sample(Position).SpeedMultiplier, Is.EqualTo(3f));
        }

        [Test]
        public void ModifierChangeDuringABuildSurvivesAGoalMoveWithinItsCell()
        {
            var modifier = Modifier();
            _session.RegisterModifier(modifier);
            _session.Submit(Request());
            Finish();
            Vector3 goal = new Vector3(0.5f, 2.5f, 2.5f);
            _session.Submit(Request(goal));
            FlowFieldVolumeSafetyRegressionTests.PumpOneStep(_session);
            Assert.That(_session.IsRebuilding, Is.True);
            _session.Submit(Request(goal + new Vector3(0.1f, 0f, 0f)));
            modifier.Speed = 4f;
            modifier.Revision++;
            _session.NotifyModifierChangedCommand(modifier, FlowFieldModifierChange.Value);
            Finish();
            Assert.That(_session.Sample(Position).SpeedMultiplier, Is.EqualTo(4f));
            Assert.That(_session.TryGetFieldInfo(out var info), Is.True);
            Assert.That(info.ResolvedGoalWorld, Is.EqualTo(goal));
        }

        [Test]
        public void ReleaseCancelsAPendingModifierRecompose()
        {
            var modifier = Modifier();
            _session.RegisterModifier(modifier);
            _session.Submit(Request());
            Finish();
            modifier.Speed = 3f;
            modifier.Revision++;
            _session.NotifyModifierChangedCommand(modifier, FlowFieldModifierChange.Value);
            Assert.That(_session.HasPendingRequest, Is.True);
            _session.Release();
            Assert.That(_session.HasPendingRequest, Is.False);
            Assert.DoesNotThrow(() => _session.Pump());
        }

        private FlowFieldRegressionFixture.Modifier Modifier(int priority = 100)
        {
            if (_area == null)
            {
                _area = new GameObject("Volume modifier regression");
                _area.layer = FlowFieldRegressionFixture.Layer;
                var box = _area.AddComponent<BoxCollider>();
                box.isTrigger = true;
                box.center = Vector3.one * 1.5f;
                box.size = Vector3.one * 4f;
            }
            return new FlowFieldRegressionFixture.Modifier { InfluenceCollider = _area.GetComponent<Collider>(), Priority = priority };
        }

        [Test]
        public void SubmissionWaitsForOwnerAndPublicationRemainsAvailableDuringRebuild()
        {
            _session.Submit(Request());
            Assert.That(_session.TrySample(Position, out _), Is.False);
            Finish();
            var before = _session.Sample(Position);
            int revision = _session.Revision;
            _session.Submit(Request(new Vector3(0.5f, 2.5f, 2.5f)));
            Assert.That(_session.IsReady && _session.IsRebuilding, Is.True);
            Assert.That(_session.Sample(Position).Direction, Is.EqualTo(before.Direction));
            Assert.That(_session.Revision, Is.EqualTo(revision));
            Finish();
        }

        [Test]
        public void RepeatedVolumeBuildRetainsItsExistingRevisionPolicy()
        {
            _session.Submit(Request());
            Finish();
            int revision = _session.Revision;
            int events = 0;
            _session.FieldCommitted += () => events++;
            _session.Submit(Request());
            Finish();
            Assert.That(_session.Revision, Is.EqualTo(revision + 1));
            Assert.That(events, Is.EqualTo(1));
        }

        [Test]
        public void LatestPendingGoalWinsBeforeBuildStarts()
        {
            _session.Submit(Request());
            var latest = new Vector3(0.5f, 2.5f, 2.5f);
            _session.Submit(Request(latest));
            Finish();
            Assert.That(_session.TryGetFieldInfo(out var info), Is.True);
            Assert.That(info.ResolvedGoalWorld, Is.EqualTo(latest));
        }

        [Test]
        public void ValueChangeAndRemovalRecomposeWithoutChangingNavigation()
        {
            var modifier = Modifier();
            _session.RegisterModifier(modifier);
            _session.Submit(Request());
            Finish();
            var before = _session.Sample(Position);
            Assert.That(before.SpeedMultiplier, Is.EqualTo(2f));
            modifier.Speed = 3f;
            modifier.Revision++;
            _session.NotifyModifierChangedCommand(modifier, FlowFieldModifierChange.Value);
            Finish();
            Assert.That(_session.Sample(Position).SpeedMultiplier, Is.EqualTo(3f));
            Assert.That(_session.Sample(Position).Direction, Is.EqualTo(before.Direction));
            _session.UnregisterModifier(modifier);
            Finish();
            Assert.That(_session.Sample(Position).SpeedMultiplier, Is.EqualTo(1f));
        }

        [Test]
        public void ModifierCannotMoveGoalAnchorOrBlockedCell()
        {
            _session.RegisterModifier(Modifier());
            _session.Submit(Request());
            Finish();
            var sample = _session.Sample(Goal);
            Assert.That(sample.Direction, Is.EqualTo(Vector3.zero));
            Assert.That(sample.SpeedMultiplier, Is.Zero);
        }

        [Test]
        public void DuplicatePriorityAndNonTriggerAreRejectedWithoutRegistering()
        {
            var first = Modifier();
            Assert.That(_session.RegisterModifier(first), Is.True);
            var duplicate = Modifier();
            Assert.Throws<InvalidOperationException>(() => _session.RegisterModifier(duplicate));
            Assert.That(_session.ContainsModifier(duplicate), Is.False);
            var invalid = Modifier(200);
            _area.GetComponent<Collider>().isTrigger = false;
            Assert.Throws<ArgumentException>(() => _session.RegisterModifier(invalid));
            Assert.That(_session.ContainsModifier(invalid), Is.False);
        }

        [Test]
        public void ModifierObservationDetectsValueAndAreaChanges()
        {
            var modifier = Modifier();
            _session.RegisterModifier(modifier);
            _session.Submit(Request());
            Finish();
            modifier.Speed = 4f;
            modifier.Revision++;
            _session.DetectModifierChanges();
            Finish();
            Assert.That(_session.Sample(Position).SpeedMultiplier, Is.EqualTo(4f));
            _area.transform.position = Vector3.one * 100f;
            Physics.SyncTransforms();
            _session.DetectModifierChanges();
            Finish();
            Assert.That(_session.Sample(Position).SpeedMultiplier, Is.EqualTo(1f));
        }

        [Test]
        public void NewObstacleStopsPreviousRouteBeforeReplacementPublication()
        {
            _session.Submit(Request());
            Finish();
            _obstacle = new GameObject("New route obstacle");
            _obstacle.layer = FlowFieldRegressionFixture.Layer;
            _obstacle.transform.position = Vector3.one * 1.5f;
            _obstacle.AddComponent<BoxCollider>();
            Physics.SyncTransforms();
            _session.RegisterObstacleCommand(_obstacle.GetComponent<Collider>());
            var sample = _session.Sample(Position);
            Assert.That(sample.SpeedMultiplier, Is.Zero);
        }

        [Test]
        public void PublicationCallbackCanReleaseAndInitializeNewLifetime()
        {
            bool restarted = false;
            _session.FieldCommitted += () =>
            {
                if (restarted) return;
                restarted = true;
                _session.Release();
                _session.Initialize(FlowFieldBakeMode.RuntimeDynamic);
                _session.Submit(Request(new Vector3(0.5f, 2.5f, 2.5f)));
            };
            _session.Submit(Request());
            Finish();
            Assert.That(restarted, Is.True);
            Assert.That(_session.TryGetFieldInfo(out var info), Is.True);
            Assert.That(info.ResolvedGoalWorld, Is.EqualTo(new Vector3(0.5f, 2.5f, 2.5f)));
        }

        [Test]
        public void SuspendDiscardsPendingWorkUntilResumed()
        {
            _session.Submit(Request());
            _session.Suspend();
            _session.Pump(100d);
            Assert.That(_session.IsReady, Is.False);
            Assert.That(_session.State, Is.EqualTo(FlowFieldRuntimeState.Suspended));
            _session.Resume();
            _session.Submit(Request());
            Finish();
        }
    }
}
