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
    /// the fields; the Goals build while the default Goal moves every pump;
    /// and a Modifier registered while they build never faults them.
    /// </summary>
    [TestFixture, Category("Goal")]
    internal sealed class AdditionalGoalTests
    {
        private const int StormSteps = 24;
        private const int MaxGoals = 3;
        private const int MaxCubes = 3;
        private const int MovingPumps = 400;

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
        }

        // The default Goal moves before every pump, so its field is always
        // owed; the Goals take turns with it and still build.
        [TestCase(FlowFieldPlayScene.SampleScene)]
        [TestCase(FlowFieldPlayScene.VolumeScene)]
        public void GoalBuildsWhileTheDefaultGoalKeepsMoving(string scene)
        {
            FlowFieldSession session = Open(scene, FlowFieldBakeMode.RuntimeDynamic);
            int id = session.AddGoalCommand(FlowFieldGoalKind.Group, FlowFieldGoalRequest.Position(Point(0.5f, 0.5f)));
            int pumps = 0;
            while (pumps < MovingPumps && !session.IsGoalReady(id))
            {
                FlowFieldManagerConfig config = WithGoal(_manager, _settings, Point(pumps % 2 == 0 ? 0.3f : 0.7f, 0.5f));
                session.SubmitConfig(in config, FlowFieldConfigChange.Goal);
                session.Pump(FlowFieldTestBudget.AfterChecks(2), waitForWorker: true);
                Assert.That(session.IsFaulted, Is.False, "pump " + pumps + " faulted: " + session.LastError);
                pumps++;
            }
            Assert.That(session.IsGoalReady(id), Is.True, "The Goal never built while the default Goal moved.");
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

        // A cube on the Obstacle Layer, 1.6 cells wide; in Surface2D as tall
        // as the field.
        private Collider CreateCube(System.Random random)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "Storm Cube";
            cube.layer = FlowFieldObstacleProbe.ObstacleLayer(_manager);
            float width = _settings.Grid.CellSize * 1.6f;
            bool volume = _settings.SpaceMode == FlowFieldSpaceMode.Volume3D;
            cube.transform.localScale = new Vector3(width, volume ? width : _bounds.size.y, width);
            cube.transform.position = CubePosition(random);
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
