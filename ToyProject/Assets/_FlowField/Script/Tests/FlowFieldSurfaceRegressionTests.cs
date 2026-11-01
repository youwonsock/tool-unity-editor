using NUnit.Framework;
using UnityEngine;

namespace Common.FlowField.Tests
{
    public sealed class FlowFieldSurfaceRegressionTests
    {
        private FlowFieldSession _session;
        private FlowFieldRegressionFixture.EventFailures _eventFailures;
        private FlowFieldRegressionFixture.DeferredBackend _backend;
        private FlowFieldRegressionFixture.CountingSurfaceSource _source;
        private GameObject _colliderObject;
        private static readonly Vector3 OriginCell = new Vector3(0.5f, 0f, 0.5f);
        private static readonly Vector3 GoalA = new Vector3(2.5f, 0f, 2.5f);
        private static readonly Vector3 GoalB = new Vector3(0.5f, 0f, 2.5f);

        [SetUp]
        public void SetUp()
        {
            _backend = new FlowFieldRegressionFixture.DeferredBackend();
            _source = FlowFieldRegressionFixture.Surface();
            _session = new FlowFieldSession(_source, _backend);
            _eventFailures = new FlowFieldRegressionFixture.EventFailures(_session);
            _session.Initialize(FlowFieldBakeMode.RuntimeDynamic, FlowFieldSessionSourceKind.SceneBuild,
                FlowFieldSolvePolicy.Worker);
        }

        [TearDown]
        public void TearDown()
        {
            _session?.Dispose();
            if (_colliderObject != null) Object.DestroyImmediate(_colliderObject);
            _eventFailures?.AssertNone();
        }

        private void Build(Vector3? goal = null)
        {
            _session.Submit(FlowFieldRegressionFixture.Request(goal));
            _session.Pump();
            if (_session.IsRebuilding) _backend.Complete(_backend.Calls.Count - 1);
            Assert.That(_session.IsReady, Is.True, _session.LastError);
        }

        [Test]
        public void SubmissionDoesNotCalculateUntilOwnerPumps()
        {
            _session.Submit(FlowFieldRegressionFixture.Request(GoalA));
            Assert.That(_source.BuildCount, Is.Zero);
            Assert.That(_backend.Calls, Is.Empty);
            _session.Pump();
            Assert.That(_backend.Calls.Count, Is.EqualTo(1));
        }

        [Test]
        public void InitialFieldIsUnavailableUntilBfsCompletes()
        {
            _session.Submit(FlowFieldRegressionFixture.Request(GoalA));
            _session.Pump();
            Assert.That(_session.IsRebuilding, Is.True);
            Assert.That(_session.TrySample(OriginCell, out _), Is.False);
            _backend.Complete(0);
            Assert.That(_session.TrySample(OriginCell, out var sample), Is.True);
            Assert.That(sample.Direction.x, Is.GreaterThan(0f));
            Assert.That(sample.Direction.z, Is.GreaterThan(0f));
        }

        [Test]
        public void CompatiblePublicationRemainsReadableWhileGoalRebuilds()
        {
            Build();
            int revision = _session.Revision;
            _session.Submit(FlowFieldRegressionFixture.Request(GoalA));
            _session.Pump();
            Assert.That(_session.IsRebuilding && _session.IsReady, Is.True);
            Assert.That(_session.TrySample(OriginCell, out var previous), Is.True);
            Assert.That(previous.Direction, Is.EqualTo(Vector3.forward));
            Assert.That(_session.Revision, Is.EqualTo(revision));
            _backend.Complete(0);
            Assert.That(_session.Revision, Is.GreaterThan(revision));
        }

        [Test]
        public void ASupersededSolvePublishesAndTheLatestGoalFollows()
        {
            Build();
            int revision = _session.Revision;
            _session.Submit(FlowFieldRegressionFixture.Request(GoalA));
            _session.Pump();
            _session.Submit(FlowFieldRegressionFixture.Request(GoalB));
            _backend.Complete(0);
            Assert.That(_session.Revision, Is.EqualTo(revision + 1));
            Assert.That(_session.TryGetFieldInfo(out var interim), Is.True);
            Assert.That(interim.ResolvedGoalWorld, Is.EqualTo(GoalA));
            _session.Pump();
            _backend.Complete(1);
            Assert.That(_session.TryGetFieldInfo(out var info), Is.True);
            Assert.That(info.ResolvedGoalWorld, Is.EqualTo(GoalB));
            Assert.That(_session.Sample(OriginCell).Direction, Is.EqualTo(Vector3.forward));
        }

        [Test]
        public void RemovingTheGoalDiscardsTheRunningSolve()
        {
            Build(GoalA);
            int revision = _session.Revision;
            StartSolve(GoalB);
            _session.Submit(FlowFieldRegressionFixture.Request());
            _backend.Complete(_backend.Calls.Count - 1);
            Assert.That(_session.Revision, Is.EqualTo(revision), "A solve for a removed Goal must not publish.");
            Assert.That(_session.TryGetFieldInfo(out var info), Is.True);
            Assert.That(info.ResolvedGoalWorld, Is.EqualTo(GoalA));
            _session.Pump();
            Assert.That(_session.TryGetFieldInfo(out info), Is.True);
            Assert.That(info.HasResolvedGoal, Is.False);
        }

        [Test]
        public void ObservedGoalRemovalDiscardsTheRunningSolveBeforeItsSubmit()
        {
            Build(GoalA);
            int revision = _session.Revision;
            StartSolve(GoalB);
            _session.ObserveGoalRemoved();
            _backend.Complete(_backend.Calls.Count - 1);
            Assert.That(_session.Revision, Is.EqualTo(revision));
        }

        [Test]
        public void AGridChangeDiscardsTheRunningSolve()
        {
            Build(GoalA);
            int revision = _session.Revision;
            StartSolve(GoalB);
            _session.MarkDirty(FlowFieldDirtyFlags.Grid, FlowFieldCellRect.Invalid, FlowFieldCellRect.Invalid);
            _backend.Complete(_backend.Calls.Count - 1);
            Assert.That(_session.Revision, Is.EqualTo(revision));
        }

        [Test]
        public void AGoalMoveWithinItsCellDoesNotStartAnotherSolve()
        {
            Build(GoalA);
            int calls = _backend.Calls.Count;
            int revision = _session.Revision;
            StartSolve(GoalB);
            _session.Submit(FlowFieldRegressionFixture.Request(GoalB + new Vector3(0.2f, 0f, 0.2f)));
            _backend.Complete(_backend.Calls.Count - 1);
            Assert.That(_session.Revision, Is.EqualTo(revision + 1));
            _session.Pump();
            Assert.That(_backend.Calls.Count, Is.EqualTo(calls + 1), "The moved Goal still resolves to the solved cell.");
            Assert.That(_session.IsRebuilding, Is.False);
        }

        [Test]
        public void CompositionSpreadsOverFramesAndEndsWithTheSameField()
        {
            Build(GoalA);
            FlowFieldVector3Field expected = FlowFieldVector3Field.Capture(_session, FlowFieldRegressionFixture.Grid);
            _session.Submit(FlowFieldRegressionFixture.Request(GoalA, Vector3.left));

            // A budget that is already spent lets each frame do one piece.
            int frames = 0;
            var spent = new FlowFieldBuildBudget(0d, () => 1d);
            var operation = (IFlowFieldBuildOperation)_session;
            operation.PumpInternal(in spent);
            for (; frames < 1000 && _session.IsRebuilding; frames++)
                operation.PumpInternal(in spent);

            Assert.That(_session.IsReady, Is.True, _session.LastError);
            Assert.That(_session.IsFaulted, Is.False);
            expected.AssertEqual(FlowFieldVector3Field.Capture(_session, FlowFieldRegressionFixture.Grid));
        }

        private readonly struct FlowFieldVector3Field
        {
            private readonly Vector3[] _directions;

            private FlowFieldVector3Field(Vector3[] directions) => _directions = directions;

            internal static FlowFieldVector3Field Capture(FlowFieldSession session, FlowFieldGridSpace grid)
            {
                var directions = new Vector3[grid.CellCount];
                for (int i = 0; i < directions.Length; i++)
                {
                    grid.FromFlatIndex(i, out int x, out int z);
                    directions[i] = session.Sample(grid.LocalToWorldCenter(x, z)).Direction;
                }
                return new FlowFieldVector3Field(directions);
            }

            internal void AssertEqual(in FlowFieldVector3Field other)
                => Assert.That(other._directions, Is.EqualTo(_directions));
        }

        [Test]
        public void GoalMovingDuringEverySolveKeepsPublishing()
        {
            // Three Goals, so each published solve differs from the previous one.
            Vector3[] goals = { GoalB, new Vector3(2.5f, 0f, 0.5f), new Vector3(1.5f, 0f, 2.5f) };
            Build(GoalA);
            int revision = _session.Revision;

            Vector3 latest = GoalB;
            _session.Submit(FlowFieldRegressionFixture.Request(latest));
            int solves = 0;
            for (int cycle = 0; cycle < 8; cycle++)
            {
                _session.Pump();
                Assert.That(_session.IsRebuilding, Is.True, $"Cycle {cycle} did not start a solve.");
                solves++;
                latest = goals[(cycle + 1) % goals.Length];
                _session.Submit(FlowFieldRegressionFixture.Request(latest));
                _backend.Complete(_backend.Calls.Count - 1);
                Assert.That(_session.IsFaulted, Is.False, _session.LastError);
            }

            Assert.That(solves, Is.EqualTo(8));
            Assert.That(_session.Revision - revision, Is.EqualTo(8), "Every finished solve publishes.");
            Assert.That(_session.HasPendingRequest, Is.True);

            _session.Pump();
            Assert.That(_session.IsRebuilding, Is.True);
            _backend.Complete(_backend.Calls.Count - 1);
            AssertResolvedGoal(latest);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ReleasedOrSuspendedSessionIgnoresLateCompletion(bool release)
        {
            _session.Submit(FlowFieldRegressionFixture.Request(GoalA));
            _session.Pump();
            if (release) _session.Release(); else _session.Suspend();
            int revision = _session.Revision;
            int publications = 0;
            _session.FieldCommitted += () => publications++;
            Assert.DoesNotThrow(() => _backend.Complete(0, false));
            Assert.That(_session.IsReady, Is.False);
            Assert.That(_session.Revision, Is.EqualTo(revision));
            Assert.That(publications, Is.Zero);
        }

        [Test]
        public void PublicationListenerCanReleaseWithoutOldCompletionReactivatingSession()
        {
            _session.FieldCommitted += () => _session.Release();
            _session.Submit(FlowFieldRegressionFixture.Request(GoalA));
            _session.Pump();
            _backend.Complete(0);
            Assert.That(_session.State, Is.EqualTo(FlowFieldRuntimeState.Released));
            Assert.That(_session.TrySample(OriginCell, out _), Is.False);
        }

        [Test]
        public void IdenticalGoalDoesNotRebuildOrRepublish()
        {
            Build(GoalA);
            int revision = _session.Revision;
            int publications = 0;
            _session.FieldCommitted += () => publications++;
            _session.Submit(FlowFieldRegressionFixture.Request(GoalA));
            _session.Pump();
            Assert.That(_backend.Calls.Count, Is.EqualTo(1));
            Assert.That(_session.Revision, Is.EqualTo(revision));
            Assert.That(publications, Is.Zero);
        }

        [Test]
        public void ModifierValueChangeReusesNavigationAndChangesOnlySpeed()
        {
            Build(GoalA);
            var before = _session.Sample(OriginCell);
            _colliderObject = new GameObject("Modifier regression");
            _colliderObject.layer = FlowFieldRegressionFixture.Layer;
            var collider = _colliderObject.AddComponent<BoxCollider>();
            collider.isTrigger = true;
            collider.center = new Vector3(1.5f, 0.5f, 1.5f);
            collider.size = new Vector3(3f, 2f, 3f);
            var modifier = new FlowFieldRegressionFixture.Modifier { InfluenceCollider = collider, Priority = 100 };
            _session.RegisterModifierCommand(modifier);
            _session.Submit(FlowFieldRegressionFixture.Request(GoalA));
            _session.Pump();
            Assert.That(_session.Sample(OriginCell).SpeedMultiplier, Is.EqualTo(2f));
            modifier.Speed = 3f;
            modifier.Revision++;
            _session.NotifyModifierChangedCommand(modifier, FlowFieldModifierChange.Value);
            _session.Submit(FlowFieldRegressionFixture.Request(GoalA));
            _session.Pump();
            var after = _session.Sample(OriginCell);
            Assert.That(after.SpeedMultiplier, Is.EqualTo(3f));
            Assert.That(after.Direction, Is.EqualTo(before.Direction));
            Assert.That(_source.BuildCount, Is.EqualTo(1));
            Assert.That(_backend.Calls.Count, Is.EqualTo(1));
        }

        private static float[] CellSpeeds(FlowFieldSession session)
        {
            var speeds = new float[FlowFieldRegressionFixture.Grid.CellCount];
            for (int z = 0; z < 3; z++)
            for (int x = 0; x < 3; x++)
                speeds[FlowFieldRegressionFixture.Grid.ToFlatIndex(x, z)] =
                    session.Sample(new Vector3(x + .5f, 0f, z + .5f)).SpeedMultiplier;
            return speeds;
        }

        // A collider aligned with the cell edges must not leave a cell outside
        // the region that a later partial composition rewrites.
        private FlowFieldRegressionFixture.Modifier BuildWithAlignedModifier()
        {
            _colliderObject = new GameObject("Aligned modifier regression") { layer = FlowFieldRegressionFixture.Layer };
            _colliderObject.transform.position = new Vector3(1.5f, .5f, 1.5f);
            var collider = _colliderObject.AddComponent<BoxCollider>();
            collider.isTrigger = true;
            collider.size = new Vector3(1f, 2f, 1f);
            Physics.SyncTransforms();
            var modifier = new FlowFieldRegressionFixture.Modifier { InfluenceCollider = collider, Priority = 100 };
            _session.RegisterModifierCommand(modifier);
            Build();
            Assert.That(CellSpeeds(_session), Has.Some.EqualTo(2f));
            return modifier;
        }

        [Test]
        public void ValueChangeRewritesEveryCellOfAnAlignedModifier()
        {
            var modifier = BuildWithAlignedModifier();
            modifier.Speed = 3f;
            modifier.Revision++;
            _session.NotifyModifierChangedCommand(modifier, FlowFieldModifierChange.Value);
            Build();
            Assert.That(CellSpeeds(_session), Has.None.EqualTo(2f));
            Assert.That(CellSpeeds(_session), Has.Some.EqualTo(3f));
        }

        [Test]
        public void MovingAnAlignedModifierAwayRestoresEveryCell()
        {
            var modifier = BuildWithAlignedModifier();
            _colliderObject.transform.position = Vector3.one * 100f;
            Physics.SyncTransforms();
            _session.NotifyModifierChangedCommand(modifier, FlowFieldModifierChange.Area);
            Build();
            Assert.That(CellSpeeds(_session), Has.All.EqualTo(1f));
        }

        [Test]
        public void ModifierAlignedWithACellOnlyInfluencesThatCell()
        {
            BuildWithAlignedModifier();
            int influenced = 0;
            foreach (float speed in CellSpeeds(_session))
                if (speed > 1.5f) influenced++;
            Assert.That(influenced, Is.EqualTo(1));
        }

        private static int BlockedCells(FlowFieldSession session)
        {
            int blocked = 0;
            foreach (bool cell in session.CommittedWorkspace.Blocked)
                if (cell) blocked++;
            return blocked;
        }

        private static FlowFieldSessionRequest RequestWithClearance(float clearance)
            => FlowFieldSessionRequest.ForSceneBuild(FlowFieldRegressionFixture.Settings,
                1 << FlowFieldRegressionFixture.Layer, 1f, 0.5f, clearance, false, FlowFieldGoalResolution.None,
                Vector3.forward, FlowFieldDirtyFlags.None, FlowFieldCellRect.Invalid, FlowFieldCellRect.Invalid);

        [TestCase(false, 0f, 1)]
        [TestCase(true, 0f, 1)]
        [TestCase(false, 0.25f, 9)]
        [TestCase(true, 0.25f, 9)]
        public void ObstacleAlignedWithACellOnlyBlocksCellsWithinItsClearance(bool registered, float clearance, int expected)
        {
            _colliderObject = new GameObject("Aligned obstacle regression")
            {
                layer = FlowFieldRegressionFixture.Layer,
            };
            _colliderObject.transform.position = new Vector3(1.5f, .5f, 1.5f);
            var collider = _colliderObject.AddComponent<BoxCollider>();
            Physics.SyncTransforms();
            if (registered) _session.RegisterObstacle(collider, FlowFieldCellRect.Invalid);
            _session.Submit(RequestWithClearance(clearance));
            _session.Pump();
            Assert.That(_session.IsReady, Is.True, _session.LastError);
            Assert.That(BlockedCells(_session), Is.EqualTo(expected));
        }

        // A Player reports GameObject.isStatic as false for every object, so
        // this is the state each of these Colliders has there.
        [Test]
        public void ObstacleWithoutARigidbodyBlocksWithoutARegistration()
        {
            CreateCellObstacle();
            _session.Submit(RequestWithClearance(0f));
            _session.Pump();
            Assert.That(_session.IsReady, Is.True, _session.LastError);
            Assert.That(BlockedCells(_session), Is.EqualTo(1));
        }

        [Test]
        public void ObstacleWithARigidbodyIsNotPartOfTheStaticMask()
        {
            CreateCellObstacle().gameObject.AddComponent<Rigidbody>().isKinematic = true;
            _session.Submit(RequestWithClearance(0f));
            _session.Pump();
            Assert.That(_session.IsReady, Is.True, _session.LastError);
            Assert.That(BlockedCells(_session), Is.EqualTo(0));
        }

        [Test]
        public void RegisteringAnObstacleTakesItOutOfTheStaticMaskAndUnregisteringReturnsIt()
        {
            BoxCollider collider = CreateCellObstacle();
            _session.Submit(RequestWithClearance(0f));
            _session.Pump();
            Assert.That(BlockedCells(_session), Is.EqualTo(1));

            _session.RegisterObstacle(collider, FlowFieldCellRect.Invalid);
            collider.transform.position = new Vector3(2.5f, .5f, 2.5f);
            Physics.SyncTransforms();
            _session.Submit(RequestWithClearance(0f));
            _session.Pump();
            Assert.That(_session.IsReady, Is.True, _session.LastError);
            Assert.That(BlockedCells(_session), Is.EqualTo(1), "The registered Collider must leave no static cell behind.");

            _session.UnregisterObstacle(collider, FlowFieldCellRect.Invalid);
            _session.Submit(RequestWithClearance(0f));
            _session.Pump();
            Assert.That(_session.IsReady, Is.True, _session.LastError);
            Assert.That(BlockedCells(_session), Is.EqualTo(1));
        }

        private BoxCollider CreateCellObstacle()
        {
            _colliderObject = new GameObject("Unflagged obstacle regression") { layer = FlowFieldRegressionFixture.Layer };
            _colliderObject.transform.position = new Vector3(1.5f, .5f, 1.5f);
            var collider = _colliderObject.AddComponent<BoxCollider>();
            Physics.SyncTransforms();
            return collider;
        }

        [Test]
        public void ValueNotificationAppliesWithoutARevisionChange()
        {
            _colliderObject = new GameObject("Notified modifier regression") { layer = FlowFieldRegressionFixture.Layer };
            _colliderObject.transform.position = new Vector3(1.5f, .5f, 1.5f);
            var collider = _colliderObject.AddComponent<BoxCollider>();
            collider.isTrigger = true;
            collider.size = Vector3.one * 8f;
            Physics.SyncTransforms();
            var modifier = new FlowFieldRegressionFixture.Modifier { InfluenceCollider = collider, Priority = 100 };
            _session.RegisterModifierCommand(modifier);
            Build();
            modifier.Speed = 3f;
            _session.NotifyModifierChangedCommand(modifier, FlowFieldModifierChange.Value);
            Build();
            Assert.That(_session.Sample(OriginCell).SpeedMultiplier, Is.EqualTo(3f));
        }

        [Test]
        public void DefaultDirectionObservedDuringSolveDoesNotInvalidateGoal()
        {
            _session.Submit(FlowFieldRegressionFixture.Request(GoalA));
            _session.Pump();
            _session.ObserveDefaultDirection(Vector3.left);
            _backend.Complete(0);
            Assert.That(_session.IsReady, Is.True);
            Assert.That(_session.TryGetFieldInfo(out var info), Is.True);
            Assert.That(info.ResolvedGoalWorld, Is.EqualTo(GoalA));
        }

        [Test]
        public void PendingSolveDoesNotComposeModifiersUntilItCompletes()
        {
            Build(GoalA);
            _colliderObject = new GameObject("Compose count regression") { layer = FlowFieldRegressionFixture.Layer };
            var collider = _colliderObject.AddComponent<BoxCollider>();
            collider.isTrigger = true;
            collider.center = new Vector3(1.5f, 0.5f, 1.5f);
            collider.size = new Vector3(3f, 2f, 3f);
            var modifier = new CountingModifier { InfluenceCollider = collider, Priority = 100 };
            _session.RegisterModifierCommand(modifier);
            _session.Submit(FlowFieldRegressionFixture.Request(GoalA));
            _session.Pump();
            Assert.That(modifier.Calls, Is.GreaterThan(0));

            modifier.Calls = 0;
            _session.Submit(FlowFieldRegressionFixture.Request(GoalB));
            _session.Pump();
            Assert.That(_session.IsRebuilding, Is.True);
            Assert.That(modifier.Calls, Is.Zero, "The field is composed once, after the solve.");
            _backend.Complete(_backend.Calls.Count - 1);
            Assert.That(_session.IsReady, Is.True, _session.LastError);
            Assert.That(modifier.Calls, Is.GreaterThan(0));
            Assert.That(_session.Sample(OriginCell).SpeedMultiplier, Is.EqualTo(2f));
        }

        [Test]
        public void GoalChangePublishesWhenAnObstacleMovesDuringTheSolve()
        {
            Build(GoalA);
            RegisterObstacle(new Vector3(1.5f, 100f, 1.5f));
            RunOwnerTicks(GoalA);
            int revision = _session.Revision;

            StartSolve(GoalB);
            ObserveObstacleMove(new Vector3(1.5f, 101f, 1.5f));
            _backend.Complete(_backend.Calls.Count - 1);

            Assert.That(_session.Revision, Is.GreaterThan(revision));
            AssertResolvedGoal(GoalB);
            Assert.That(_session.HasPendingObstacleProbe, Is.True);

            int solves = _backend.Calls.Count;
            RunOwnerTicks(GoalB);
            AssertResolvedGoal(GoalB);
            Assert.That(_session.HasPendingObstacleProbe, Is.False);
            Assert.That(_backend.Calls.Count, Is.EqualTo(solves), "The same mask needs no further solve.");
        }

        [Test]
        public void ObstacleDirtiedDuringTheSolveIsPublishedAndRebuiltNext()
        {
            Vector3 cell = new Vector3(1.5f, 0.5f, 1.5f);
            Build(GoalA);
            RegisterObstacle(new Vector3(1.5f, 100f, 1.5f));
            RunOwnerTicks(GoalA);
            int revision = _session.Revision;

            StartSolve(GoalB);
            _colliderObject.transform.position = cell;
            Physics.SyncTransforms();
            FlowFieldCellRect dirty = FlowFieldCellRect.Full(FlowFieldRegressionFixture.Grid);
            _session.MarkObstacleDirty(dirty);
            int solves = _backend.Calls.Count;
            _backend.Complete(_backend.Calls.Count - 1);

            Assert.That(_session.Revision, Is.GreaterThan(revision));
            AssertResolvedGoal(GoalB);
            Assert.That(_session.HasPendingRequest, Is.True, "The obstacle request must survive the publication.");

            RunOwnerTicks(GoalB);
            Assert.That(_backend.Calls.Count, Is.GreaterThan(solves));
            Assert.That(_session.IsFaulted, Is.False, _session.LastError);
            int index = FlowFieldRegressionFixture.Grid.ToFlatIndex((int)cell.x, (int)cell.z);
            Assert.That(_session.CommittedWorkspace.Blocked[index], Is.True);
            AssertResolvedGoal(GoalB);
        }

        [Test]
        public void GoalForgottenByARejectedSolveKeepsItsNoWalkableWarningState()
        {
            var tracker = new FlowFieldGoalTracker();
            Assert.That(tracker.TryConsumeMissingWalkableWarning(), Is.True);
            tracker.ForgetLastBuild();
            Assert.That(tracker.TryConsumeMissingWalkableWarning(), Is.False);
        }

        [Test]
        public void ContinuouslyMovingObstacleDoesNotStarvePublication()
        {
            Vector3 first = new Vector3(2.5f, 0.5f, 0.5f);
            Vector3 second = new Vector3(1.5f, 0.5f, 1.5f);
            Build(GoalA);
            RegisterObstacle(first);
            RunOwnerTicks(GoalA);
            int revision = _session.Revision;

            _session.Submit(FlowFieldRegressionFixture.Request(GoalB));
            int solves = 0;
            Vector3 latest = first;
            for (int cycle = 0; cycle < 6; cycle++)
            {
                _session.Pump();
                Assert.That(_session.IsRebuilding, Is.True, $"Cycle {cycle} did not start a solve.");
                solves++;
                latest = cycle % 2 == 0 ? second : first;
                ObserveObstacleMove(latest);
                _backend.Complete(_backend.Calls.Count - 1);
                Assert.That(_session.IsFaulted, Is.False, _session.LastError);
                AssertResolvedGoal(GoalB);
                _session.ProbeObstacleChanges(FlowFieldRegressionFixture.Request(GoalB));
                _session.Submit(FlowFieldRegressionFixture.Request(GoalB));
            }

            Assert.That(solves, Is.EqualTo(6));
            Assert.That(_session.Revision, Is.GreaterThan(revision));
            RunOwnerTicks(GoalB);
            bool[] blocked = _session.CommittedWorkspace.Blocked;
            Assert.That(blocked[FlowFieldRegressionFixture.Grid.ToFlatIndex((int)latest.x, (int)latest.z)], Is.True);
            AssertResolvedGoal(GoalB);
        }

        [Test]
        public void GoalRejectedByAGridRequestIsSolvedAgain()
        {
            Build(GoalA);
            StartSolve(GoalB);
            _session.MarkDirty(
                FlowFieldDirtyFlags.Grid,
                FlowFieldCellRect.Invalid,
                FlowFieldCellRect.Invalid);
            _backend.Complete(_backend.Calls.Count - 1);
            AssertResolvedGoal(GoalA);

            Assert.That(_session.HasPendingRequest, Is.True);
            RunOwnerTicks(GoalB);
            AssertResolvedGoal(GoalB);
        }

        private void RegisterObstacle(Vector3 position)
        {
            _colliderObject = new GameObject("Moving obstacle regression");
            _colliderObject.layer = FlowFieldRegressionFixture.Layer;
            _colliderObject.transform.position = position;
            _colliderObject.transform.localScale = Vector3.one * 0.5f;
            BoxCollider collider = _colliderObject.AddComponent<BoxCollider>();
            Physics.SyncTransforms();
            _session.RegisterObstacle(collider, FlowFieldCellRect.Invalid);
        }

        private void StartSolve(Vector3 goal)
        {
            _session.Submit(FlowFieldRegressionFixture.Request(goal));
            _session.Pump();
            Assert.That(_session.IsRebuilding, Is.True);
        }

        // The same order as Manager.LateUpdate while a solve is in flight.
        private void ObserveObstacleMove(Vector3 position)
        {
            _colliderObject.transform.position = position;
            Physics.SyncTransforms();
            Assert.That(_session.DetectObstacleTransformsChanged(), Is.True);
            _session.RecordObstacleObservation(_session.DirtyObstacleRegion);
        }

        // Manager.LateUpdate probes and Manager.FixedUpdate submits and pumps.
        private void RunOwnerTicks(Vector3 goal)
        {
            for (int tick = 0; tick < 8; tick++)
            {
                if (_session.HasPendingObstacleProbe)
                    _session.ProbeObstacleChanges(FlowFieldRegressionFixture.Request(goal));
                if (!_session.HasPendingRequest)
                    return;
                _session.Submit(FlowFieldRegressionFixture.Request(goal));
                _session.Pump();
                if (_session.IsRebuilding)
                    _backend.Complete(_backend.Calls.Count - 1);
            }
            Assert.Fail("The session did not settle.");
        }

        private void AssertResolvedGoal(Vector3 goal)
        {
            Assert.That(_session.TryGetFieldInfo(out FlowFieldFieldInfo info), Is.True);
            Assert.That(info.HasResolvedGoal, Is.True);
            Assert.That(info.ResolvedGoalWorld.x, Is.EqualTo(goal.x).Within(0.01f));
            Assert.That(info.ResolvedGoalWorld.z, Is.EqualTo(goal.z).Within(0.01f));
        }

        private sealed class CountingModifier : IFlowFieldVectorModifier
        {
            public Collider InfluenceCollider { get; set; }
            public int Priority { get; set; }
            public int Revision { get; set; }
            public FlowFieldModifierObservation Observe() => new FlowFieldModifierObservation(InfluenceCollider, Priority, Revision);
            internal int Calls;
            public IFlowFieldModifierSnapshot CaptureSnapshot() => new Snapshot(this);

            private sealed class Snapshot : IFlowFieldModifierSnapshot
            {
                private readonly CountingModifier _owner;
                internal Snapshot(CountingModifier owner) { _owner = owner; }
                public FlowFieldVectorState Modify(in FlowFieldVectorState current, in FlowFieldVectorModifierContext context)
                {
                    _owner.Calls++;
                    return new FlowFieldVectorState(current.Direction, current.SpeedMultiplier * 2f);
                }
            }
        }
    }
}
