using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using static Common.FlowField.Tests.FlowFieldEditSessions;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// Additional Goals on sessions the test pumps itself in Edit Mode. A
    /// Goal's field reads as the default Goal's field would with that Goal,
    /// after any mix of inputs; a published Goal field never changes; the
    /// nearest shared Goal answers; Stop, Restart, removal and Release drop
    /// the fields and a handle never names another Goal; the Goals build
    /// while the default Goal and a Group Goal move every pump; a Modifier
    /// registered while they build never faults them; a Goal read never
    /// follows its older field into cells the default Goal's newer field has
    /// blocked; and a Manager moved during a build builds them again on its
    /// new grid.
    /// </summary>
    [TestFixture, Category("Goal")]
    internal sealed class AdditionalGoalTests
    {
        private const int StormSteps = 24;
        private const int MaxGoals = 3;
        private const int MaxCubes = 3;
        private const int MovingPumps = 400;
        private const int WindowPumps = 400;

        private readonly FlowFieldTestLog _log = new FlowFieldTestLog();
        private readonly List<FlowFieldSession> _sessions = new List<FlowFieldSession>();

        private FlowFieldManager _manager;
        private FlowFieldManagerConfig _settings;
        private Bounds _bounds;

        private float Radius => _settings.Flow.GoalInfluenceRadius;

        [SetUp]
        public void StartLog() => _log.Start();

        [TearDown]
        public void CloseScene()
        {
            foreach (FlowFieldSession session in _sessions)
                session.Dispose();
            _sessions.Clear();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _log.StopAndAssertClean();
        }

        [TestCase(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic)]
        [TestCase(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.BakedBase)]
        [TestCase(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.RuntimeDynamic)]
        [TestCase(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.BakedBase)]
        public void GoalAtTheDefaultGoalReadsTheSame(string scene, FlowFieldBakeMode bakeMode)
        {
            FlowFieldSession session = Open(scene, bakeMode);
            Vector3 goal = StormTests.FinalGoal(_bounds);
            FlowFieldManagerConfig config = WithGoal(_manager, _settings, goal);
            session.SubmitConfig(in config, FlowFieldConfigChange.Goal);
            int id = session.AddGoalCommand(FlowFieldGoalKind.Shared, FlowFieldGoalRequest.Position(goal, Radius));
            Assert.That(session.IsGoalReady(id), Is.False, "A Goal builds after the default Goal's field.");
            Settle(session, "with the Goal");

            Assert.That(session.IsGoalReady(id), Is.True);
            FlowFieldField field = session.GoalField(id);
            FlowFieldField published = Published(session);
            Assert.That(Hash(field), Is.EqualTo(Hash(published)), "The Goal's field differs from the default Goal's.");
            Assert.That(field.Agent, Is.SameAs(published.Agent), "A Goal reads the published agent layer.");
            Assert.That(field.Goal.Distances, Is.Not.Null);
            Assert.That(field.Goal.Distances[field.Goal.CellIndex], Is.EqualTo(0), "The Goal cell is no step away.");
            FlowFieldFieldChecks.AssertInvariants(field);

            var random = new System.Random(5);
            for (int i = 0; i < 32; i++)
            {
                Vector3 position = StormTests.Pick(_bounds, random);
                bool sampled = session.TrySample(position, out FlowFieldSample expected);
                Assert.That(session.TrySampleGoal(id, position, out FlowFieldSample sample), Is.EqualTo(sampled));
                Assert.That(sample, Is.EqualTo(expected), "at " + position.ToString("F2"));
            }
        }

        // Goals added, moved and removed, the default Goal moved, obstacles
        // registered, moved and removed, region notices and rebuilds, each
        // pump stopped after a seeded number of budget checks. A published
        // Goal field must never change, and after the storm each Goal must
        // equal a fresh session's field with the default Goal there.
        [TestCase(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic, 1)]
        [TestCase(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic, 2)]
        [TestCase(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.BakedBase, 1)]
        [TestCase(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.RuntimeDynamic, 1)]
        [TestCase(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.RuntimeDynamic, 2)]
        [TestCase(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.BakedBase, 1)]
        public void Storm(string scene, FlowFieldBakeMode bakeMode, int seed)
        {
            FlowFieldSession session = Open(scene, bakeMode);
            var random = new System.Random(seed);
            var goals = new List<int>
            {
                session.AddGoalCommand(FlowFieldGoalKind.Shared, RandomRequest(random)),
                session.AddGoalCommand(FlowFieldGoalKind.Group, RandomRequest(random)),
            };
            var cubes = new List<Collider>();
            var seen = new Dictionary<int, KeyValuePair<FlowFieldField, ulong>>();
            int interrupted = 0;
            for (int step = 0; step < StormSteps; step++)
            {
                RunStep(session, goals, cubes, random);
                session.Pump(FlowFieldTestBudget.AfterChecks(random.Next(0, 8)), waitForWorker: true);
                Assert.That(session.IsFaulted, Is.False, Where(seed, step) + " faulted: " + session.LastError);
                if (session.HasActiveBuild)
                    interrupted++;
                AssertPublishedGoalsUnchanged(session, goals, seen, Where(seed, step));
            }
            Assert.That(interrupted, Is.GreaterThan(0), Where(seed, StormSteps) + " no step left a build running.");
            if (goals.Count == 0)
                goals.Add(session.AddGoalCommand(FlowFieldGoalKind.Shared, RandomRequest(random)));
            Settle(session, Where(seed, StormSteps));
            AssertPublishedGoalsUnchanged(session, goals, seen, Where(seed, StormSteps));

            foreach (int id in goals)
            {
                Assert.That(session.IsGoalReady(id), Is.True, Where(seed, StormSteps) + " Goal " + id + " has no field.");
                FlowFieldField field = session.GoalField(id);
                FlowFieldFieldChecks.AssertInvariants(field);
                Vector3 goal = session.Goals.Find(id).Request.WorldPosition;
                FlowFieldSession fresh = Track(CreateSettled(_manager, _settings, goal, cubes));
                Assert.That(Hash(field), Is.EqualTo(Hash(Published(fresh))),
                    Where(seed, StormSteps) + " Goal " + id + " at " + goal.ToString("F2") + " differs from a fresh field.");
            }
        }

        [TestCase(FlowFieldPlayScene.SampleScene)]
        [TestCase(FlowFieldPlayScene.VolumeScene)]
        public void NearestSharedGoalAnswers(string scene)
        {
            FlowFieldSession session = Open(scene, FlowFieldBakeMode.RuntimeDynamic);
            Vector3 nearA = Point(0.25f, 0.25f);
            Vector3 nearB = Point(0.75f, 0.75f);
            int a = session.AddGoalCommand(FlowFieldGoalKind.Shared, FlowFieldGoalRequest.Position(Point(0.2f, 0.2f)));
            int b = session.AddGoalCommand(FlowFieldGoalKind.Shared, FlowFieldGoalRequest.Position(Point(0.8f, 0.8f)));
            // A Group Goal right there never answers the nearest query.
            session.AddGoalCommand(FlowFieldGoalKind.Group, FlowFieldGoalRequest.Position(nearA));
            Settle(session, "with three Goals");

            int pickedA = AssertNearest(session, nearA, a, b);
            int pickedB = AssertNearest(session, nearB, a, b);
            Assert.That(pickedA, Is.Not.EqualTo(pickedB), "Each end of the grid has its own nearest Goal.");

            Assert.That(session.TrySampleNearestShared(_bounds.max + Vector3.one * 100f, out _, out int outside), Is.False);
            Assert.That(outside, Is.EqualTo(0));
            Assert.That(session.TrySampleNearestShared(new Vector3(float.NaN, 0f, 0f), out _, out _), Is.False);
        }

        [TestCase(FlowFieldPlayScene.SampleScene)]
        [TestCase(FlowFieldPlayScene.VolumeScene)]
        public void StopRemoveAndReleaseDropGoals(string scene)
        {
            FlowFieldSession session = Open(scene, FlowFieldBakeMode.RuntimeDynamic);
            Vector3 goal = Point(0.3f, 0.6f);
            int id = session.AddGoalCommand(FlowFieldGoalKind.Shared, FlowFieldGoalRequest.Position(goal));
            Settle(session, "with the Goal");
            string built = Hash(session.GoalField(id));

            session.Stop();
            Assert.That(session.IsGoalReady(id), Is.False, "A stopped session answers no Goal.");
            session.Restart();
            Assert.That(session.IsGoalReady(id), Is.False, "A restart drops the Goal's field.");
            Settle(session, "after the restart");
            Assert.That(session.IsGoalReady(id), Is.True);
            Assert.That(Hash(session.GoalField(id)), Is.EqualTo(built), "The restart built another field.");

            int other = session.AddGoalCommand(FlowFieldGoalKind.Group, FlowFieldGoalRequest.Position(Point(0.7f, 0.4f)));
            Settle(session, "with two Goals");
            Assert.That(session.RemoveGoalCommand(id), Is.True);
            Assert.That(session.IsGoalReady(id), Is.False);
            Assert.That(session.TrySampleGoal(id, goal, out FlowFieldSample sample), Is.False);
            Assert.That(sample, Is.EqualTo(FlowFieldSample.Stopped));
            Assert.That(session.RemoveGoalCommand(id), Is.False, "A removed Goal is gone.");
            Assert.That(session.IsGoalReady(other), Is.True, "Removing one Goal keeps the others.");
            Assert.That(IsIdle(session), Is.True, "A removal owes nothing.");

            session.Release();
            Assert.That(session.Goals.Count, Is.EqualTo(0), "Release removes every Goal.");
            Start(session, FlowFieldBakeMode.RuntimeDynamic, WithGoal(_manager, _settings, null));
            Settle(session, "after Release and Init");
            Assert.That(session.IsGoalReady(other), Is.False);
            Assert.That(session.SetGoalCommand(other, FlowFieldGoalRequest.Position(goal)), Is.False);

            // A handle never names a later Goal, of this session or another.
            int again = session.AddGoalCommand(FlowFieldGoalKind.Shared, FlowFieldGoalRequest.Position(goal));
            FlowFieldSession second = Track(CreateSettled(_manager, _settings, null));
            int elsewhere = second.AddGoalCommand(FlowFieldGoalKind.Shared, FlowFieldGoalRequest.Position(goal));
            Assert.That(new[] { id, other, again, elsewhere }, Is.Unique);
            Assert.That(second.RemoveGoalCommand(again), Is.False, "Another session's handle names no Goal here.");
        }

        // The default Goal and a Group Goal move before every pump, so the
        // default field and the Group Goal are always owed; the Goals take
        // turns with the default Goal, and the kinds take turns, so the
        // Shared Goal still builds.
        [TestCase(FlowFieldPlayScene.SampleScene)]
        [TestCase(FlowFieldPlayScene.VolumeScene)]
        public void GoalsBuildWhileTheDefaultGoalKeepsMoving(string scene)
        {
            FlowFieldSession session = Open(scene, FlowFieldBakeMode.RuntimeDynamic);
            int group = session.AddGoalCommand(FlowFieldGoalKind.Group, FlowFieldGoalRequest.Position(Point(0.5f, 0.5f)));
            int shared = session.AddGoalCommand(FlowFieldGoalKind.Shared, FlowFieldGoalRequest.Position(Point(0.5f, 0.8f)));
            int pumps = 0;
            while (pumps < MovingPumps && !(session.IsGoalReady(group) && session.IsGoalReady(shared)))
            {
                bool even = pumps % 2 == 0;
                FlowFieldManagerConfig config = WithGoal(_manager, _settings, Point(even ? 0.3f : 0.7f, 0.5f));
                session.SubmitConfig(in config, FlowFieldConfigChange.Goal);
                session.SetGoalCommand(group, FlowFieldGoalRequest.Position(Point(0.5f, even ? 0.4f : 0.6f)));
                session.Pump(FlowFieldTestBudget.AfterChecks(2), waitForWorker: true);
                Assert.That(session.IsFaulted, Is.False, "pump " + pumps + " faulted: " + session.LastError);
                pumps++;
            }
            Assert.That(session.IsGoalReady(group), Is.True, "The Group Goal never built while the default Goal moved.");
            Assert.That(session.IsGoalReady(shared), Is.True, "The Shared Goal never built while the Group Goal moved.");
        }

        // After a default build publishes a new obstacle, the uncertain
        // region it queried is gone while the Goal still reads its older
        // field. Volume3D has no build cells to fall back on, so the read
        // must take the default Goal's newer blocked cells: a newly blocked
        // cell escapes as the default field's does, and a cell whose Goal
        // path leads into one stops. Surface2D's build cells cover the same
        // window, except while a ground rescan builds: then its read takes
        // the published cells as well and escapes along their final
        // directions.
        [Test]
        public void VolumeGoalReadSeesTheDefaultGoalsNewerCells()
        {
            FlowFieldSession session = Open(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.RuntimeDynamic);
            int id = session.AddGoalCommand(FlowFieldGoalKind.Shared, FlowFieldGoalRequest.Position(Point(0.85f, 0.5f)));
            Settle(session, "with the Goal");

            Collider cube = CreateCube(Point(0.6f, 0.5f));
            Assert.That(session.RegisterObstacleCommand(cube), Is.True);
            FlowFieldField published = null;
            FlowFieldField goal = null;
            bool window = false;
            for (int pump = 0; pump < WindowPumps && !window; pump++)
            {
                session.Pump(FlowFieldTestBudget.AfterChecks(1), waitForWorker: true);
                Assert.That(session.IsFaulted, Is.False, session.LastError);
                published = session.TryGetPublishedField(out FlowFieldField field) ? field : null;
                goal = session.Goals.Find(id).Field;
                window = published != null
                    && goal != null
                    && !ReferenceEquals(published.Agent, goal.Agent)
                    && session.Safety.UncertainRegionCount == 0;
            }
            Assert.That(window, Is.True, "The default field never published the cube before the Goal rebuilt.");

            int newlyBlocked = 0;
            int ledIn = 0;
            FlowFieldGridSpace grid = published.Grid;
            for (int index = 0; index < grid.CellCount; index++)
            {
                Vector3 center = grid.CellCenter(index);
                if (published.Agent.Blocked[index] && !goal.Agent.Blocked[index])
                {
                    newlyBlocked++;
                    Assert.That(session.TrySample(center, out FlowFieldSample expected), Is.True);
                    Assert.That(session.TrySampleGoal(id, center, out FlowFieldSample sample), Is.True);
                    Assert.That(sample, Is.EqualTo(expected), "A newly blocked cell escapes as the default field's does.");
                    continue;
                }
                int next = goal.Goal.NextCells[index];
                if (published.Agent.Blocked[index] || next < 0 || next == index
                    || !published.Agent.Blocked[next] || goal.Agent.Blocked[next])
                    continue;
                ledIn++;
                Assert.That(session.TrySampleGoal(id, center, out FlowFieldSample stopped), Is.True);
                Assert.That(stopped.SpeedMultiplier, Is.EqualTo(0f),
                    "A Goal path into a newly blocked cell must stop at " + center.ToString("F2"));
            }
            Assert.That(newlyBlocked, Is.GreaterThan(0), "The cube blocked no open cell.");
            Assert.That(ledIn, Is.GreaterThan(0), "No Goal path led into the cube.");
            Settle(session, "after the window");
        }

        // A Modifier registers while two Goals build; Surface2D finds its
        // cells only in the default Goal's next build, so the Goals' build
        // leaves it out and the registration builds them again with it.
        [TestCase(FlowFieldPlayScene.SampleScene)]
        [TestCase(FlowFieldPlayScene.VolumeScene)]
        public void ModifierRegisteredDuringAGoalBuild(string scene)
        {
            FlowFieldSession session = Open(scene, FlowFieldBakeMode.RuntimeDynamic);
            int a = session.AddGoalCommand(FlowFieldGoalKind.Shared, FlowFieldGoalRequest.Position(Point(0.2f, 0.3f)));
            int b = session.AddGoalCommand(FlowFieldGoalKind.Shared, FlowFieldGoalRequest.Position(Point(0.8f, 0.7f)));
            Settle(session, "with two Goals");

            Vector3 goalA = Point(0.3f, 0.3f);
            Vector3 goalB = Point(0.7f, 0.7f);
            Assert.That(session.SetGoalCommand(a, FlowFieldGoalRequest.Position(goalA)), Is.True);
            Assert.That(session.SetGoalCommand(b, FlowFieldGoalRequest.Position(goalB)), Is.True);
            session.Pump(FlowFieldTestBudget.AfterChecks(1), waitForWorker: true);
            Assert.That(session.HasActiveBuild, Is.True, "The Goals' build must still be running.");

            Vector3 size = new Vector3(_bounds.size.x * 0.4f, _bounds.size.y, _bounds.size.z * 0.4f);
            FlowFieldProbeModifier probe = FlowFieldModifierScenarios.CreateProbe("Probe", _bounds.center, size);
            Assert.That(session.RegisterModifierCommand(probe), Is.True);
            Settle(session, "after the registration");

            var modifiers = new IFlowFieldVectorModifier[] { probe };
            FlowFieldSession freshA = Track(CreateSettled(_manager, _settings, goalA, modifiers: modifiers));
            FlowFieldSession freshB = Track(CreateSettled(_manager, _settings, goalB, modifiers: modifiers));
            Assert.That(Hash(session.GoalField(a)), Is.EqualTo(Hash(Published(freshA))), "Goal A left the Modifier out.");
            Assert.That(Hash(session.GoalField(b)), Is.EqualTo(Hash(Published(freshB))), "Goal B left the Modifier out.");
        }

        // The Manager moves while a default Goal build runs, so the SetGoal
        // that reads the new grid is owed and submitted when the next build
        // begins. That drops the published field, and the Goal builds again
        // on the new grid, as a fresh session with that Goal would.
        [TestCase(FlowFieldPlayScene.SampleScene)]
        [TestCase(FlowFieldPlayScene.VolumeScene)]
        public void MovedManagerBuildsTheGoalsAgain(string scene)
        {
            FlowFieldSession session = Open(scene, FlowFieldBakeMode.RuntimeDynamic);
            Vector3 goal = Point(0.8f, 0.7f);
            int id = session.AddGoalCommand(FlowFieldGoalKind.Shared, FlowFieldGoalRequest.Position(goal, Radius));
            Settle(session, "with the Goal");

            session.SubmitConfig(WithGoal(_manager, _settings, Point(0.3f, 0.3f)), FlowFieldConfigChange.Goal);
            for (int pump = 0; pump < WindowPumps && !session.HasActiveBuild; pump++)
                session.Pump(FlowFieldTestBudget.AfterChecks(1), waitForWorker: false);
            Assert.That(session.HasActiveBuild, Is.True, "No default Goal build was running.");

            _manager.transform.position += new Vector3(_settings.Grid.CellSize * 2f, 0f, 0f);
            FlowFieldManagerConfig moved = _manager.CaptureConfig(FlowFieldBakeMode.RuntimeDynamic);
            session.SubmitConfig(WithGoal(_manager, moved, Point(0.35f, 0.3f)), FlowFieldConfigChange.Goal);
            Settle(session, "after the move");

            Assert.That(session.TryGetFieldInfo(out FlowFieldFieldInfo info), Is.True);
            Assert.That(info.WorldBounds.min.x, Is.EqualTo(_bounds.min.x + _settings.Grid.CellSize * 2f).Within(0.001f),
                "The default field did not move with the Manager.");
            Assert.That(session.IsGoalReady(id), Is.True, "The Goal never built on the new grid.");
            FlowFieldSession fresh = Track(CreateSettled(_manager, moved, goal));
            Assert.That(Hash(session.GoalField(id)), Is.EqualTo(Hash(Published(fresh))));
        }

        // ----- Steps --------------------------------------------------------

        private void RunStep(FlowFieldSession session, List<int> goals, List<Collider> cubes, System.Random random)
        {
            switch (random.Next(7))
            {
                case 0:
                    FlowFieldManagerConfig config = WithGoal(_manager, _settings, StormTests.Pick(_bounds, random));
                    session.SubmitConfig(in config, FlowFieldConfigChange.Goal);
                    break;
                case 1:
                    if (goals.Count > 0)
                        session.SetGoalCommand(goals[random.Next(goals.Count)], RandomRequest(random));
                    break;
                case 2:
                    if (cubes.Count < MaxCubes)
                    {
                        Collider cube = CreateCube(random);
                        Assert.That(session.RegisterObstacleCommand(cube), Is.True);
                        cubes.Add(cube);
                    }
                    else
                    {
                        int index = random.Next(cubes.Count);
                        Assert.That(session.UnregisterObstacleCommand(cubes[index]), Is.True);
                        Object.DestroyImmediate(cubes[index].gameObject);
                        Physics.SyncTransforms();
                        cubes.RemoveAt(index);
                    }
                    break;
                case 3:
                    session.NotifyObstacleRegionDirtyCommand(new Bounds(StormTests.Pick(_bounds, random), new Vector3(6f, 6f, 6f)));
                    break;
                case 4:
                    if (goals.Count < MaxGoals)
                    {
                        FlowFieldGoalKind kind = random.Next(2) == 0 ? FlowFieldGoalKind.Shared : FlowFieldGoalKind.Group;
                        goals.Add(session.AddGoalCommand(kind, RandomRequest(random)));
                    }
                    else
                    {
                        int index = random.Next(goals.Count);
                        Assert.That(session.RemoveGoalCommand(goals[index]), Is.True);
                        Assert.That(session.IsGoalReady(goals[index]), Is.False);
                        goals.RemoveAt(index);
                    }
                    break;
                case 5:
                    FlowFieldManagerConfig current = WithGoal(_manager, _settings, null);
                    session.SubmitConfig(in current, FlowFieldConfigChange.Rebuild);
                    break;
                default:
                    if (cubes.Count > 0)
                    {
                        Collider moved = cubes[random.Next(cubes.Count)];
                        moved.transform.position = CubePosition(random);
                        Physics.SyncTransforms();
                        session.NotifyObstacleMovedCommand(moved);
                    }
                    break;
            }
        }

        // A field the session still publishes for a Goal reads the same as
        // when it was first seen.
        private static void AssertPublishedGoalsUnchanged(
            FlowFieldSession session,
            List<int> goals,
            Dictionary<int, KeyValuePair<FlowFieldField, ulong>> seen,
            string where)
        {
            foreach (int id in goals)
            {
                FlowFieldField field = session.Goals.Find(id).Field;
                if (field == null)
                {
                    seen.Remove(id);
                    continue;
                }
                ulong hash = FlowFieldFieldHash.Compute(field);
                if (seen.TryGetValue(id, out KeyValuePair<FlowFieldField, ulong> last) && ReferenceEquals(last.Key, field))
                    Assert.That(hash, Is.EqualTo(last.Value), where + " Goal " + id + "'s field changed while published.");
                seen[id] = new KeyValuePair<FlowFieldField, ulong>(field, hash);
            }
        }

        // The Goal whose field has the fewest steps to the position answers
        // with that field's own sample, the earlier Goal on a tie.
        private static int AssertNearest(FlowFieldSession session, Vector3 position, int a, int b)
        {
            FlowFieldField fieldA = session.GoalField(a);
            FlowFieldField fieldB = session.GoalField(b);
            Assert.That(FlowFieldSampler.TryLocate(fieldA, position, out int index), Is.True);
            int expected = fieldB.Goal.Distances[index] < fieldA.Goal.Distances[index] ? b : a;
            Assert.That(session.TrySampleNearestShared(position, out FlowFieldSample sample, out int picked), Is.True);
            Assert.That(picked, Is.EqualTo(expected), "at " + position.ToString("F2"));
            Assert.That(session.TrySampleGoal(expected, position, out FlowFieldSample own), Is.True);
            Assert.That(sample, Is.EqualTo(own));
            return picked;
        }

        // ----- Setup --------------------------------------------------------

        // The scene's Manager settings and a settled session without a Goal.
        private FlowFieldSession Open(string scene, FlowFieldBakeMode bakeMode)
        {
            EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);
            _manager = Object.FindObjectsByType<FlowFieldManager>(FindObjectsSortMode.None)[0];
            _settings = _manager.CaptureConfig(bakeMode);
            FlowFieldSession session = Track(CreateSettled(_manager, _settings, null));
            Assert.That(session.TryGetFieldInfo(out FlowFieldFieldInfo info), Is.True);
            _bounds = info.WorldBounds;
            return session;
        }

        private FlowFieldSession Track(FlowFieldSession session)
        {
            _sessions.Add(session);
            return session;
        }

        private Vector3 Point(float x, float z)
            => new Vector3(
                Mathf.Lerp(_bounds.min.x, _bounds.max.x, x),
                _bounds.center.y,
                Mathf.Lerp(_bounds.min.z, _bounds.max.z, z));

        private FlowFieldGoalRequest RandomRequest(System.Random random)
            => FlowFieldGoalRequest.Position(StormTests.Pick(_bounds, random), Radius);

        private Collider CreateCube(System.Random random) => CreateCube(CubePosition(random));

        // A cube on the Obstacle Layer, 1.6 cells wide; in Surface2D as tall
        // as the field.
        private Collider CreateCube(Vector3 position)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "Test Cube";
            cube.layer = FlowFieldObstacleProbe.ObstacleLayer(_manager);
            float width = _settings.Grid.CellSize * 1.6f;
            bool volume = _settings.SpaceMode == FlowFieldSpaceMode.Volume3D;
            cube.transform.localScale = new Vector3(width, volume ? width : _bounds.size.y, width);
            cube.transform.position = position;
            Physics.SyncTransforms();
            return cube.GetComponent<Collider>();
        }

        private Vector3 CubePosition(System.Random random)
        {
            Vector3 position = StormTests.Pick(_bounds, random);
            if (_settings.SpaceMode == FlowFieldSpaceMode.Surface2D)
                position.y = _bounds.center.y;
            return position;
        }

        private static string Where(int seed, int step) => $"seed {seed}, step {step}:";
    }
}
