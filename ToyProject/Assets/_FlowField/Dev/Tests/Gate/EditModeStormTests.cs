using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// A seeded input storm on a session the test pumps itself in Edit Mode.
    /// Each pump stops after a seeded number of budget checks and waits for
    /// the worker, so a run takes the same steps every time, and a failure
    /// names the seed and the step to replay. After the storm and one fixed
    /// Goal the field must equal a fresh session's field with that Goal.
    /// </summary>
    [TestFixture, Category("Gate")]
    internal sealed class EditModeStormTests
    {
        private const int Steps = 24;
        // Pumps that finish the owed builds; Surface2D begins one per pump.
        private const int MaxSettlePumps = 64;

        private readonly FlowFieldTestLog _log = new FlowFieldTestLog();

        [SetUp]
        public void StartLog() => _log.Start();

        [TearDown]
        public void CloseScene()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _log.StopAndAssertClean();
        }

        [TestCase(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic, 1)]
        [TestCase(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic, 2)]
        [TestCase(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic, 3)]
        [TestCase(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.BakedBase, 1)]
        [TestCase(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.BakedBase, 2)]
        [TestCase(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.BakedBase, 3)]
        [TestCase(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.RuntimeDynamic, 1)]
        [TestCase(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.RuntimeDynamic, 2)]
        [TestCase(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.RuntimeDynamic, 3)]
        [TestCase(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.BakedBase, 1)]
        [TestCase(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.BakedBase, 2)]
        [TestCase(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.BakedBase, 3)]
        public void Storm(string scene, FlowFieldBakeMode bakeMode, int seed)
        {
            EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);
            FlowFieldManager manager = Object.FindObjectsByType<FlowFieldManager>(FindObjectsSortMode.None)[0];
            FlowFieldManagerConfig settings = manager.CaptureConfig(bakeMode);
            FlowFieldSession session = Create(settings.SpaceMode);
            FlowFieldSession fresh = null;
            try
            {
                FlowFieldManagerConfig current = WithGoal(manager, settings, null);
                Start(session, bakeMode, current);
                Settle(session, seed, -1);
                Assert.That(session.TryGetFieldInfo(out FlowFieldFieldInfo info), Is.True, Where(seed, -1));
                Bounds bounds = info.WorldBounds;

                var random = new System.Random(seed);
                int interrupted = 0;
                for (int step = 0; step < Steps; step++)
                {
                    current = RunStep(session, manager, settings, current, bakeMode, bounds, random);
                    session.Pump(FlowFieldTestBudget.AfterChecks(random.Next(0, 8)), waitForWorker: true);
                    Assert.That(session.IsFaulted, Is.False, Where(seed, step) + " faulted: " + session.LastError);
                    if (session.HasActiveBuild)
                        interrupted++;
                }
                // The next step's input must meet builds still running.
                Assert.That(interrupted, Is.GreaterThan(0), Where(seed, Steps) + " no step left a build running.");

                FlowFieldManagerConfig final = WithGoal(manager, settings, StormTests.FinalGoal(bounds));
                session.SubmitConfig(in final, FlowFieldConfigChange.Goal);
                Settle(session, seed, Steps);
                string afterStorm = Hash(session);

                fresh = Create(settings.SpaceMode);
                Start(fresh, bakeMode, final);
                Settle(fresh, seed, Steps);
                Assert.That(afterStorm, Is.EqualTo(Hash(fresh)), Where(seed, Steps) + " the storm left a trace.");
            }
            finally
            {
                session.Dispose();
                fresh?.Dispose();
            }
        }

        // The kinds of the Play Mode storm, picked by the seed: Goals, a
        // region notice, two Goals at once, a rebuild, Release and Init, and
        // no Goal. Returns the settings the session now has.
        private static FlowFieldManagerConfig RunStep(
            FlowFieldSession session,
            FlowFieldManager manager,
            in FlowFieldManagerConfig settings,
            in FlowFieldManagerConfig current,
            FlowFieldBakeMode bakeMode,
            Bounds bounds,
            System.Random random)
        {
            switch (random.Next(6))
            {
                case 0:
                    return SubmitGoal(session, manager, settings, StormTests.Pick(bounds, random));
                case 1:
                    session.NotifyObstacleRegionDirtyCommand(new Bounds(StormTests.Pick(bounds, random), new Vector3(6f, 6f, 6f)));
                    return current;
                case 2:
                    SubmitGoal(session, manager, settings, StormTests.Pick(bounds, random));
                    return SubmitGoal(session, manager, settings, StormTests.Pick(bounds, random));
                case 3:
                    session.SubmitConfig(in current, FlowFieldConfigChange.Rebuild);
                    return current;
                case 4:
                    // Release clears the Goal, as the Manager's does.
                    FlowFieldManagerConfig released = WithGoal(manager, settings, null);
                    session.Release();
                    Start(session, bakeMode, released);
                    return released;
                default:
                    return SubmitGoal(session, manager, settings, null);
            }
        }

        private static FlowFieldManagerConfig SubmitGoal(
            FlowFieldSession session,
            FlowFieldManager manager,
            in FlowFieldManagerConfig settings,
            Vector3? goal)
        {
            FlowFieldManagerConfig config = WithGoal(manager, settings, goal);
            session.SubmitConfig(in config, FlowFieldConfigChange.Goal);
            return config;
        }

        private static FlowFieldSession Create(FlowFieldSpaceMode spaceMode)
            => spaceMode == FlowFieldSpaceMode.Volume3D ? FlowFieldSession.CreateVolume() : FlowFieldSession.CreateSurface();

        // Init's order: the bake mode, then the whole settings as a rebuild.
        private static void Start(FlowFieldSession session, FlowFieldBakeMode bakeMode, in FlowFieldManagerConfig config)
        {
            session.Initialize(bakeMode);
            session.SubmitConfig(in config, FlowFieldConfigChange.Rebuild);
        }

        private static FlowFieldManagerConfig WithGoal(FlowFieldManager manager, in FlowFieldManagerConfig settings, Vector3? goal)
            => new FlowFieldManagerConfig(
                settings.SpaceMode,
                settings.BakeMode,
                in settings.Grid,
                in settings.Surface,
                in settings.Obstacles,
                in settings.Flow,
                in settings.BakeAssets,
                manager.transform,
                goal.HasValue,
                goal ?? default);

        // Runs every owed build to its end, waiting for the worker.
        private static void Settle(FlowFieldSession session, int seed, int step)
        {
            for (int pump = 0; pump < MaxSettlePumps && (session.HasActiveBuild || session.HasOwedInput || session.HasPendingRequest); pump++)
                session.Pump();
            Assert.That(session.IsFaulted, Is.False, Where(seed, step) + " faulted: " + session.LastError);
            Assert.That(session.HasActiveBuild || session.HasOwedInput || session.HasPendingRequest, Is.False,
                Where(seed, step) + " never settled.");
            Assert.That(session.IsReady, Is.True, Where(seed, step) + " has no field.");
        }

        private static string Hash(FlowFieldSession session)
        {
            Assert.That(session.TryGetPublishedField(out FlowFieldField field), Is.True);
            return FlowFieldFieldHash.Format(FlowFieldFieldHash.Compute(field));
        }

        private static string Where(int seed, int step) => $"seed {seed}, step {step}:";
    }
}
