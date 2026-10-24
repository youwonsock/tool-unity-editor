using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Common.FlowField.Tests
{
    public sealed class FlowFieldVolumeSafetyRegressionTests
    {
        private FlowFieldVolumeSession _session;
        private FlowFieldRegressionFixture.EventFailures _eventFailures;
        private readonly List<GameObject> _objects = new List<GameObject>();
        private FlowFieldVolumeBakeData _bake;
        internal static readonly FlowFieldGridSpace Grid = FlowFieldGridSpace.FromVolumeCellGrid(Vector3.zero, 8, 4, 8, 1f);
        private static readonly Vector3 Goal = new Vector3(7.5f, 3.5f, 7.5f);
        private static readonly Vector3 OldCell = new Vector3(3.5f, 1.5f, 3.5f);
        private static readonly Vector3 NewCell = new Vector3(5.5f, 1.5f, 3.5f);

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
            foreach (GameObject obj in _objects) if (obj != null) Object.DestroyImmediate(obj);
            _objects.Clear();
            if (_bake != null) Object.DestroyImmediate(_bake);
            _eventFailures?.AssertNone();
        }

        internal static FlowFieldVolumeRequest Request(FlowFieldGridSpace grid, bool hasGoal = true,
            Vector3? goal = null, Vector3? direction = null, ComputeShader shader = null,
            FlowFieldVolumeBakeData bake = null)
            => new FlowFieldVolumeRequest(grid,
                new Bounds(grid.Origin + new Vector3(grid.WorldSizeX, grid.WorldSizeY, grid.WorldSizeZ) * 0.5f,
                    new Vector3(grid.WorldSizeX, grid.WorldSizeY, grid.WorldSizeZ)),
                bake != null ? FlowFieldBakeMode.StaticBaked : FlowFieldBakeMode.RuntimeDynamic,
                1 << FlowFieldRegressionFixture.Layer, 0f, hasGoal, goal ?? Goal, 0f,
                direction ?? Vector3.forward, 1024, shader, bake);

        // A synthetic deadline permits one scheduler step without a wall-clock race.
        internal static void PumpOneStep(FlowFieldVolumeSession session)
        {
            int checks = 0;
            var budget = new FlowFieldBuildBudget(1d, () => checks++ < 2 ? 0d : 2d);
            ((IFlowFieldBuildOperation)session).PumpInternal(in budget);
        }

        internal static void Finish(FlowFieldVolumeSession session)
        {
            for (int i = 0; i < 500 && session.IsRebuilding; i++) session.Pump(10d);
            Assert.That(session.IsFaulted, Is.False, session.LastError);
            Assert.That(session.IsReady, Is.True, session.LastError);
            Assert.That(session.IsRebuilding, Is.False, "Build did not finish.");
        }

        private void Build(FlowFieldVolumeRequest? request = null)
        {
            _session.Submit(request ?? Request(Grid));
            Finish(_session);
        }

        private BoxCollider Obstacle(Vector3 position)
        {
            var obj = new GameObject("Safety regression obstacle");
            _objects.Add(obj);
            obj.layer = FlowFieldRegressionFixture.Layer;
            obj.transform.position = position;
            var collider = obj.AddComponent<BoxCollider>();
            Physics.SyncTransforms();
            return collider;
        }

        private void StartBuildAfterObstacleQueries()
        {
            _session.Submit(Request(Grid, goal: new Vector3(0.5f, 3.5f, 7.5f)));
            for (int i = 0; i < 100 && _session.BuildProgress < 1f / 3f; i++) PumpOneStep(_session);
            Assert.That(_session.IsRebuilding, Is.True);
            Assert.That(_session.BuildProgress, Is.GreaterThanOrEqualTo(1f / 3f));
        }

        private void AssertStopped(Vector3 position)
        {
            Assert.That(_session.TrySample(position, out var sample), Is.True);
            Assert.That(sample.HasCell, Is.True);
            Assert.That(sample.Direction, Is.EqualTo(Vector3.zero));
            Assert.That(sample.SpeedMultiplier, Is.Zero);
        }

        private bool IsBlocked(Vector3 position)
        {
            Assert.That(_session.TryExport(out var request, out var blocked, out _, out _, out _, out _, out _, out _, out _), Is.True);
            Assert.That(request.Grid.TryWorldToLocal(position, out int x, out int y, out int z), Is.True);
            return blocked[request.Grid.ToFlatIndex(x, y, z)];
        }

        [Test]
        public void UncertainRegionStopsImmediatelyAndRecoversAfterPublication()
        {
            Build();
            var before = _session.Sample(OldCell);
            var unaffected = new Vector3(0.5f, 0.5f, 7.5f);
            var previousUnaffected = _session.Sample(unaffected);
            int revision = _session.Revision;
            _session.NotifyObstacleRegionDirtyCommand(new Bounds(OldCell, Vector3.one * 0.1f));
            AssertStopped(OldCell);
            Assert.That(_session.Sample(unaffected).Direction, Is.EqualTo(previousUnaffected.Direction));
            Assert.That(_session.Sample(unaffected).SpeedMultiplier, Is.EqualTo(previousUnaffected.SpeedMultiplier));
            Assert.That(_session.Revision, Is.EqualTo(revision));
            Finish(_session);
            Assert.That(_session.Sample(OldCell).Direction, Is.EqualTo(before.Direction));
            Assert.That(_session.Sample(OldCell).SpeedMultiplier, Is.EqualTo(before.SpeedMultiplier));
        }

        [Test]
        public void MovingRegisteredObstacleDuringBuildProtectsOldAndNewRegions()
        {
            var collider = Obstacle(OldCell);
            _session.RegisterDynamicObstacle(collider);
            Build();
            StartBuildAfterObstacleQueries();
            int revision = _session.Revision;
            collider.transform.position = NewCell;
            Physics.SyncTransforms();
            PumpOneStep(_session);
            AssertStopped(OldCell);
            AssertStopped(NewCell);
            Assert.That(_session.Revision, Is.EqualTo(revision));
            int publications = 0;
            _session.FieldCommitted += () =>
            {
                if (++publications != 1) return;
                // The first publication still uses the obstacle snapshot from
                // before the move. Both regions must remain unsafe until the
                // queued replacement is published, including inside callbacks.
                Assert.That(IsBlocked(OldCell), Is.True);
                Assert.That(IsBlocked(NewCell), Is.False);
                AssertStopped(OldCell);
                AssertStopped(NewCell);
            };
            Finish(_session);
            Assert.That(publications, Is.EqualTo(2));
            Assert.That(IsBlocked(OldCell), Is.False);
            Assert.That(IsBlocked(NewCell), Is.True);
            Assert.That(_session.TryGetFieldInfo(out var info), Is.True);
            Assert.That(info.RequestedGoalWorld, Is.EqualTo(new Vector3(0.5f, 3.5f, 7.5f)));
        }

        [Test]
        public void DestroyingRegisteredObstacleDuringBuildPreservesItsCachedDirtyRegion()
        {
            var collider = Obstacle(OldCell);
            _session.RegisterDynamicObstacle(collider);
            Build();
            StartBuildAfterObstacleQueries();
            Object.DestroyImmediate(collider.gameObject);
            PumpOneStep(_session);
            AssertStopped(OldCell);
            Finish(_session);
            Assert.That(IsBlocked(OldCell), Is.False);
            Assert.That(_session.Sample(OldCell).SpeedMultiplier, Is.GreaterThan(0f));
        }

        [Test]
        public void DestroyedColliderCanBeUnregisteredUsingItsCachedBounds()
        {
            var collider = Obstacle(OldCell);
            _session.RegisterDynamicObstacle(collider);
            Build();
            Object.DestroyImmediate(collider.gameObject);
            Assert.That(_session.UnregisterDynamicObstacle(collider), Is.True);
            AssertStopped(OldCell);
            Finish(_session);
            Assert.That(IsBlocked(OldCell), Is.False);
        }

        [TestCase("disabled")]
        [TestCase("trigger")]
        [TestCase("layer")]
        [TestCase("inactive")]
        [TestCase("destroyed")]
        public void UnregisteredObstacleStateChangeInvalidatesPreviousRegion(string change)
        {
            var collider = Obstacle(OldCell);
            _session.Submit(Request(Grid));
            _session.DetectUnregisteredObstacleChanges(true);
            Finish(_session);
            Assert.That(IsBlocked(OldCell), Is.True);
            switch (change)
            {
                case "disabled": collider.enabled = false; break;
                case "trigger": collider.isTrigger = true; break;
                case "layer": collider.gameObject.layer = 30; break;
                case "inactive": collider.gameObject.SetActive(false); break;
                case "destroyed": Object.DestroyImmediate(collider.gameObject); break;
            }
            Physics.SyncTransforms();
            _session.DetectUnregisteredObstacleChanges(true);
            AssertStopped(OldCell);
            Finish(_session);
            Assert.That(IsBlocked(OldCell), Is.False);
        }

        [Test]
        public void GridReplacementDuringPartialBuildImmediatelyHidesOldPublication()
        {
            Build();
            StartBuildAfterObstacleQueries();
            var replacement = FlowFieldGridSpace.FromVolumeCellGrid(new Vector3(100f, 0f, 0f), 4, 4, 4, 1f);
            var publishedOrigins = new List<Vector3>();
            _session.FieldCommitted += () =>
            {
                Assert.That(_session.TryGetFieldInfo(out var info), Is.True);
                publishedOrigins.Add(info.Grid.Origin);
            };
            _session.Submit(Request(replacement, goal: replacement.Origin + Vector3.one * 3.5f));
            Assert.That(_session.TrySample(OldCell, out _), Is.False);
            Finish(_session);
            Assert.That(publishedOrigins, Is.EqualTo(new[] { replacement.Origin }));
            Assert.That(_session.TrySample(replacement.Origin + Vector3.one * 0.5f, out _), Is.True);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void DefaultDirectionStopsAtVolumeBoundary(int axis)
        {
            Vector3 direction = axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward;
            Build(Request(Grid, hasGoal: false, direction: direction));
            var interior = Vector3.one * 1.5f;
            Assert.That(_session.Sample(interior).SpeedMultiplier, Is.GreaterThan(0f));
            var edge = interior;
            edge[axis] = axis == 1 ? 3.5f : 7.5f;
            AssertStopped(edge);
        }

        [Test]
        public void EscapeFromBlockedCellStopsWhenOnlyItsNeighborBecomesUncertain()
        {
            Obstacle(OldCell);
            Build();
            Assert.That(IsBlocked(OldCell), Is.True);
            var escape = _session.Sample(OldCell);
            Assert.That(escape.SpeedMultiplier, Is.GreaterThan(0f));
            var step = new Vector3(Math.Sign(escape.Direction.x), Math.Sign(escape.Direction.y), Math.Sign(escape.Direction.z));
            // After the existing one-cell safety margin, this intersects the
            // escape neighbor but does not intersect the blocked source cell.
            _session.MarkObstacleRegionDirty(new Bounds(OldCell + step * 2f, Vector3.one * 0.1f));
            AssertStopped(OldCell);
            Finish(_session);
            Assert.That(_session.Sample(OldCell).Direction, Is.EqualTo(escape.Direction));
        }

        [Test]
        public void DirtyRegionOverflowRetainsFirstAndNewestRegionsConservatively()
        {
            var grid = FlowFieldGridSpace.FromVolumeCellGrid(Vector3.zero, 400, 1, 1, 1f);
            Build(Request(grid, goal: new Vector3(399.5f, 0.5f, 0.5f)));
            for (int i = 0; i < 70; i++)
                _session.MarkObstacleRegionDirty(new Bounds(new Vector3(2.5f + i * 5f, 0.5f, 0.5f), Vector3.one * 0.1f));
            AssertStopped(new Vector3(2.5f, 0.5f, 0.5f));
            AssertStopped(new Vector3(347.5f, 0.5f, 0.5f));
            Assert.That(_session.Sample(new Vector3(397.5f, 0.5f, 0.5f)).Direction, Is.EqualTo(Vector3.right));
            Finish(_session);
            Assert.That(_session.Sample(new Vector3(2.5f, 0.5f, 0.5f)).Direction, Is.EqualTo(Vector3.right));
        }

        [Test]
        public void StaticBakedSamplingIgnoresRuntimeSafetyRegionsAndPreservesBake()
        {
            Build();
            Assert.That(_session.TryExport(out var input, out var blocked, out var topology, out var flags,
                out var next, out var directions, out var speeds, out var escape, out int goal), Is.True);
            _bake = ScriptableObject.CreateInstance<FlowFieldVolumeBakeData>();
            _bake.Apply(Grid, input.WorldBounds, input.ObstacleLayer, 0f, true, Goal, 0f, goal,
                blocked, topology, flags, next, directions, speeds, escape);
            int bakeRevision = _bake.Revision;
            _session.Release();
            _session.Initialize(FlowFieldBakeMode.StaticBaked);
            Build(Request(Grid, bake: _bake));
            var previous = _session.Sample(OldCell);
            _session.MarkObstacleRegionDirty(new Bounds(OldCell, Vector3.one));
            Assert.That(_session.Sample(OldCell).Direction, Is.EqualTo(previous.Direction));
            Assert.That(_session.Sample(OldCell).SpeedMultiplier, Is.EqualTo(previous.SpeedMultiplier));
            Finish(_session);
            Assert.That(_bake.Revision, Is.EqualTo(bakeRevision));
        }

        [Test]
        public void SchedulerKeepsSafetyRegionsIsolatedBetweenSessions()
        {
            var other = new FlowFieldVolumeSession();
            try
            {
                other.Initialize(FlowFieldBakeMode.RuntimeDynamic);
                _session.AttachRuntimeScheduler();
                other.AttachRuntimeScheduler();
                _session.Submit(Request(Grid));
                other.Submit(Request(Grid));
                for (int i = 0; i < 100 && (_session.IsRebuilding || other.IsRebuilding); i++)
                    FlowFieldBuildScheduler.PumpAll(10d);
                Assert.That(_session.IsReady && other.IsReady, Is.True);
                var before = other.Sample(OldCell);
                _session.MarkObstacleRegionDirty(new Bounds(OldCell, Vector3.one * 0.1f));
                AssertStopped(OldCell);
                Assert.That(other.Sample(OldCell).Direction, Is.EqualTo(before.Direction));
                Assert.That(other.Sample(OldCell).SpeedMultiplier, Is.EqualTo(before.SpeedMultiplier));
                for (int i = 0; i < 100 && _session.IsRebuilding; i++) FlowFieldBuildScheduler.PumpAll(10d);
                Assert.That(_session.IsRebuilding, Is.False);
            }
            finally { other.Dispose(); }
        }
    }
}
