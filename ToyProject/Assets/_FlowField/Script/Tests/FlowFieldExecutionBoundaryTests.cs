using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    public sealed class FlowFieldExecutionBoundaryTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (Application.isPlaying)
                yield return new ExitPlayMode();
            foreach (GameObject created in _objects)
            {
                if (created != null)
                    UnityEngine.Object.DestroyImmediate(created);
            }
            _objects.Clear();
            Physics.SyncTransforms();
        }

        private GameObject MakeObject(string name)
        {
            var created = new GameObject(name);
            _objects.Add(created);
            return created;
        }

        private FlowFieldManager CreateManager(bool volume)
        {
            if (!volume)
            {
                GameObject ground = MakeObject("Boundary ground");
                ground.layer = FlowFieldRegressionFixture.Layer;
                ground.transform.position = new Vector3(1.5f, -.5f, 1.5f);
                ground.AddComponent<BoxCollider>().size = new Vector3(3f, 1f, 3f);
            }
            GameObject root = MakeObject("Boundary manager");
            root.SetActive(false);
            var manager = root.AddComponent<FlowFieldManager>();
            using (var settings = new SerializedObject(manager))
            {
                settings.FindProperty("_spaceMode").enumValueIndex =
                    (int)(volume ? FlowFieldSpaceMode.Volume3D : FlowFieldSpaceMode.Surface2D);
                settings.FindProperty("_bakeMode").enumValueIndex = (int)FlowFieldBakeMode.RuntimeDynamic;
                settings.FindProperty("_bakeBoundsLocal").boundsValue = volume
                    ? new Bounds(Vector3.one * 1.5f, Vector3.one * 3f)
                    : FlowFieldRegressionFixture.Settings.BakeBounds;
                settings.FindProperty("_cellSize").floatValue = 1f;
                settings.FindProperty("_groundBakeLayer").intValue = 1 << FlowFieldRegressionFixture.Layer;
                settings.FindProperty("_obstacleLayer").intValue = 1 << 28;
                settings.FindProperty("_obstacleCheckHeight").floatValue = 1f;
                settings.FindProperty("_obstacleCheckCenterOffset").floatValue = .5f;
                settings.FindProperty("_refreshRate").floatValue = .05f;
                settings.FindProperty("_showField").boolValue = false;
                settings.ApplyModifiedPropertiesWithoutUndo();
            }
            Physics.SyncTransforms();
            root.SetActive(true);
            return manager;
        }

        private HookModifier CreateModifier(int priority = 10)
        {
            GameObject area = MakeObject("Boundary modifier");
            area.transform.position = new Vector3(1.5f, .5f, 1.5f);
            var box = area.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = Vector3.one * 2f;
            Physics.SyncTransforms();
            return new HookModifier { Collider = box, Order = priority };
        }

        private static void Pump(FlowFieldManager manager)
        {
            if (manager.SpaceMode == FlowFieldSpaceMode.Volume3D)
                manager.VolumeSession.Pump(10000d);
            else
                manager.SurfaceSession.Pump();
        }

        private static void Build(FlowFieldManager manager)
        {
            manager.Init();
            Pump(manager);
            Assert.That(manager.IsReady, Is.True, manager.LastError);
        }

        // Runs the session work that reaches the hooked Modifier call.
        private static void ReachHook(FlowFieldManager manager, string hook)
        {
            if (manager.SpaceMode == FlowFieldSpaceMode.Volume3D)
                manager.VolumeSession.DetectModifierChanges();
            else
                manager.SurfaceSession.DetectModifierChanges();
            if (hook != "Observe")
                Pump(manager);
        }

        private static void SetHook(HookModifier modifier, string hook, Action action)
        {
            switch (hook)
            {
                case "Observe": modifier.OnObserve = action; break;
                case "Capture": modifier.OnCapture = action; break;
                case "Modify": modifier.OnModify = action; break;
            }
        }

        private static Action Command(FlowFieldManager manager, string command)
        {
            switch (command)
            {
                case "Init": return manager.Init;
                case "RequestRebuild": return manager.RequestRebuild;
                case "Release": return manager.Release;
                case "SetSpaceMode": return () => manager.SetSpaceMode(FlowFieldSpaceMode.Surface2D);
                case "SetGoal": return () => manager.SetGoal(FlowFieldGoalRequest.Position(Vector3.one));
                case "NotifyCellsDirty": return manager.NotifyCellsDirty;
                case "NotifyObstacleRegionDirty":
                    return () => manager.NotifyObstacleRegionDirty(new Bounds(Vector3.one, Vector3.one));
                default: throw new ArgumentException(command);
            }
        }

        [Test]
        public void ModifierCodeCannotStartAnotherOperation(
            [Values(false, true)] bool volume,
            [Values("Observe", "Capture", "Modify")] string hook,
            [Values("Init", "RequestRebuild", "Release", "SetSpaceMode", "SetGoal", "NotifyCellsDirty", "NotifyObstacleRegionDirty")]
            string command)
        {
            FlowFieldManager manager = CreateManager(volume);
            HookModifier modifier = CreateModifier();
            Build(manager);
            Assert.That(manager.RegisterVectorModifier(modifier), Is.True);
            Pump(manager);
            int revision = manager.Revision;
            Exception rejected = null;
            Action call = Command(manager, command);
            SetHook(modifier, hook, () =>
            {
                if (rejected != null)
                    return;
                try { call(); }
                catch (Exception exception) { rejected = exception; }
            });

            modifier.Bump();
            ReachHook(manager, hook);

            Assert.That(rejected, Is.TypeOf<InvalidOperationException>());
            Assert.That(manager.IsFaulted, Is.False, manager.LastError);
            Assert.That(manager.IsInitialized, Is.True);
            Assert.That(manager.Revision, Is.GreaterThanOrEqualTo(revision));
            manager.Release();
        }

        [Test]
        public void ARejectedCommandThatEscapesFaultsTheSessionAndARetryRecovers(
            [Values(false, true)] bool volume,
            [Values("Capture", "Modify")] string hook)
        {
            FlowFieldManager manager = CreateManager(volume);
            HookModifier modifier = CreateModifier();
            Build(manager);
            manager.RegisterVectorModifier(modifier);
            SetHook(modifier, hook, manager.Release);
            LogAssert.ignoreFailingMessages = true;

            Pump(manager);

            Assert.That(manager.IsFaulted, Is.True);
            Assert.That(manager.LastError, Does.Contain(nameof(FlowFieldManager.Release)));
            SetHook(modifier, hook, null);
            manager.RequestRebuild();
            Pump(manager);
            Assert.That(manager.IsReady, Is.True, manager.LastError);
            manager.Release();
        }

        [Test]
        public void RegistrationFromModifierCodeWaitsForTheOperationToFinish(
            [Values(false, true)] bool volume,
            [Values("Observe", "Capture", "Modify")] string hook)
        {
            FlowFieldManager manager = CreateManager(volume);
            HookModifier modifier = CreateModifier();
            HookModifier added = CreateModifier(20);
            Build(manager);
            manager.RegisterVectorModifier(modifier);
            Pump(manager);
            int addedObservationsDuringHook = -1;
            bool done = false;
            SetHook(modifier, hook, () =>
            {
                if (done)
                    return;
                done = true;
                Assert.That(manager.RegisterVectorModifier(added), Is.True);
                addedObservationsDuringHook = added.Observations;
            });

            modifier.Bump();
            ReachHook(manager, hook);

            Assert.That(done, Is.True);
            Assert.That(addedObservationsDuringHook, Is.Zero, "The registry changed while Modifier code ran.");
            Assert.That(added.Observations, Is.GreaterThan(0), "The queued registration never ran.");
            Pump(manager);
            Assert.That(manager.IsFaulted, Is.False, manager.LastError);
            Assert.That(added.Captures, Is.GreaterThan(0));
            manager.Release();
        }

        [Test]
        public void UnregisteringFromModifierCodeWaitsForTheOperationToFinish([Values(false, true)] bool volume)
        {
            FlowFieldManager manager = CreateManager(volume);
            HookModifier first = CreateModifier();
            HookModifier later = CreateModifier(20);
            Build(manager);
            manager.RegisterVectorModifier(first);
            manager.RegisterVectorModifier(later);
            first.OnCapture = () =>
            {
                first.OnCapture = null;
                Assert.That(manager.UnregisterVectorModifier(later), Is.True);
            };

            Pump(manager);

            // Removed only after this build took its snapshot.
            Assert.That(later.Captures, Is.EqualTo(1));
            Assert.That(manager.IsFaulted, Is.False, manager.LastError);
            Pump(manager);
            Assert.That(later.Captures, Is.EqualTo(1));
            manager.Release();
        }

        [Test]
        public void ObstacleRegistrationFromModifierCodeWaitsForTheOperationToFinish([Values(false, true)] bool volume)
        {
            FlowFieldManager manager = CreateManager(volume);
            HookModifier modifier = CreateModifier();
            Build(manager);
            manager.RegisterVectorModifier(modifier);
            GameObject obstacle = MakeObject("Boundary obstacle");
            obstacle.layer = 28;
            obstacle.transform.position = new Vector3(1.5f, .5f, 1.5f);
            var box = obstacle.AddComponent<BoxCollider>();
            Physics.SyncTransforms();
            bool registeredDuringBuild = false;
            modifier.OnCapture = () =>
            {
                modifier.OnCapture = null;
                manager.RegisterDynamicObstacle(box);
                // A second registration would report false if the first had already applied.
                registeredDuringBuild = !manager.RegisterDynamicObstacle(box);
            };

            Pump(manager);

            Assert.That(registeredDuringBuild, Is.False);
            Assert.That(manager.IsFaulted, Is.False, manager.LastError);
            Assert.That(manager.UnregisterDynamicObstacle(box), Is.True);
            manager.Release();
        }

        [Test]
        public void ADeferredCommandDoesNothingOnceTheSessionHasStopped([Values(false, true)] bool volume)
        {
            FlowFieldManager manager = CreateManager(volume);
            HookModifier modifier = CreateModifier();
            HookModifier added = CreateModifier(20);
            Build(manager);
            manager.RegisterVectorModifier(modifier);
            bool deferred = false;
            modifier.OnCapture = () =>
            {
                modifier.OnCapture = null;
                deferred = manager.RegisterVectorModifier(added);
                throw new InvalidOperationException("Intentional Modifier failure");
            };
            LogAssert.ignoreFailingMessages = true;

            Pump(manager);

            Assert.That(deferred, Is.True);
            Assert.That(manager.IsFaulted, Is.True);
            Assert.That(added.Observations, Is.Zero);
            manager.Release();
        }

        [Test]
        public void RetryingFromAFaultedHandlerStillLogsTheFailure([Values(false, true)] bool volume)
        {
            FlowFieldManager manager = CreateManager(volume);
            Build(manager);
            manager.StateChanged += state =>
            {
                if (state == FlowFieldRuntimeState.Faulted)
                    manager.RequestRebuild();
            };
            LogAssert.Expect(LogType.Error, "[FlowFieldManager] FlowField build failed: Intentional failure");

            FlowFieldSessionBase session = volume ? manager.VolumeSession : manager.SurfaceSession;
            session.ReportFault(new InvalidOperationException("Intentional failure"));

            Assert.That(manager.IsFaulted, Is.False);
            manager.Release();
        }

        [Test]
        public void SamplingIsAllowedInModifierCode([Values(false, true)] bool volume)
        {
            FlowFieldManager manager = CreateManager(volume);
            HookModifier modifier = CreateModifier();
            Build(manager);
            manager.RegisterVectorModifier(modifier);
            bool sampled = false;
            modifier.OnModify = () => sampled |= manager.TrySample(new Vector3(1.5f, .5f, 1.5f), out _);

            Pump(manager);

            Assert.That(manager.IsFaulted, Is.False, manager.LastError);
            Assert.That(sampled, Is.True);
            manager.Release();
        }

        [Test]
        public void HandlersRunAfterTheOperationAndSeeItsFinalState([Values(false, true)] bool volume)
        {
            FlowFieldManager manager = CreateManager(volume);
            var states = new List<FlowFieldRuntimeState>();
            int publications = 0;
            bool readyAtPublication = false;
            manager.StateChanged += states.Add;
            manager.FieldChanged += () =>
            {
                publications++;
                readyAtPublication = manager.State == FlowFieldRuntimeState.Ready;
            };

            Build(manager);

            Assert.That(publications, Is.EqualTo(1));
            Assert.That(readyAtPublication, Is.True, "The publication arrived before the operation had finished.");
            Assert.That(states[states.Count - 1], Is.EqualTo(FlowFieldRuntimeState.Ready));
            manager.Release();
        }

        [Test]
        public void AHandlersReleaseDropsTheOlderEventsButReleasedAlwaysArrives([Values(false, true)] bool volume)
        {
            FlowFieldManager manager = CreateManager(volume);
            var states = new List<FlowFieldRuntimeState>();
            int publications = 0;
            bool released = false;
            manager.StateChanged += state =>
            {
                states.Add(state);
                if (state == FlowFieldRuntimeState.Building && !released)
                {
                    released = true;
                    // The rest of this operation's events belong to the ended lifetime.
                    manager.Release();
                    Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Released));
                }
            };
            manager.FieldChanged += () => publications++;

            manager.Init();
            Pump(manager);

            Assert.That(released, Is.True);
            Assert.That(states[states.Count - 1], Is.EqualTo(FlowFieldRuntimeState.Released));
            Assert.That(states.FindAll(state => state == FlowFieldRuntimeState.Released), Has.Count.EqualTo(1));
            Assert.That(publications, Is.Zero);
        }

        [Test]
        public void AHandlerCanRestartTheManagerFromReleased([Values(false, true)] bool volume)
        {
            FlowFieldManager manager = CreateManager(volume);
            Build(manager);
            bool restarted = false;
            manager.StateChanged += state =>
            {
                if (state != FlowFieldRuntimeState.Released || restarted)
                    return;
                restarted = true;
                manager.Init();
            };

            manager.Release();

            Assert.That(restarted, Is.True);
            Pump(manager);
            Assert.That(manager.IsReady, Is.True, manager.LastError);
            manager.Release();
        }

        [Test]
        public void HandlersAreNeverNested([Values(false, true)] bool volume)
        {
            FlowFieldManager manager = CreateManager(volume);
            int depth = 0;
            int deepest = 0;
            bool restarted = false;
            manager.StateChanged += state =>
            {
                depth++;
                deepest = Math.Max(deepest, depth);
                if (state == FlowFieldRuntimeState.Ready && !restarted)
                {
                    restarted = true;
                    manager.Release();
                    manager.Init();
                }
                depth--;
            };

            manager.Init();
            Pump(manager);
            Pump(manager);

            Assert.That(restarted, Is.True);
            Assert.That(manager.IsReady, Is.True, manager.LastError);
            Assert.That(deepest, Is.EqualTo(1));
            manager.Release();
        }

        [UnityTest]
        public IEnumerator ANeverStartedSessionPublishesNoReleased()
        {
            yield return new EnterPlayMode();
            FlowFieldManager manager = CreateManager(volume: false);
            yield return WaitUntilReady(manager);
            var states = new List<FlowFieldRuntimeState>();
            manager.StateChanged += states.Add;

            UnityEngine.Object.DestroyImmediate(manager.gameObject);

            // Destroying also disables the Manager. Only the Surface session ever
            // ran, so the Volume session adds nothing.
            Assert.That(states, Is.EqualTo(new[] { FlowFieldRuntimeState.Suspended, FlowFieldRuntimeState.Released }));
        }

        [UnityTest]
        public IEnumerator DisablingTheManagerFromModifierCodeWaitsForTheOperationToFinish(
            [Values(false, true)] bool volume)
        {
            yield return new EnterPlayMode();
            FlowFieldManager manager = CreateManager(volume);
            HookModifier modifier = CreateModifier();
            yield return WaitUntilReady(manager);
            manager.RegisterVectorModifier(modifier);
            FlowFieldRuntimeState stateInsideHook = default;
            modifier.OnModify = () =>
            {
                modifier.OnModify = null;
                manager.gameObject.SetActive(false);
                stateInsideHook = manager.State;
            };

            float deadline = Time.realtimeSinceStartup + 5f;
            while (manager.State != FlowFieldRuntimeState.Suspended && Time.realtimeSinceStartup < deadline)
                yield return null;

            Assert.That(stateInsideHook, Is.Not.EqualTo(FlowFieldRuntimeState.Suspended));
            Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Suspended), manager.LastError);
            Assert.That(manager.IsFaulted, Is.False);

            manager.gameObject.SetActive(true);
            yield return WaitUntilReady(manager);
        }

        [UnityTest]
        public IEnumerator DestroyingTheManagerFromModifierCodeWaitsForTheOperationToFinish(
            [Values(false, true)] bool volume)
        {
            yield return new EnterPlayMode();
            FlowFieldManager manager = CreateManager(volume);
            HookModifier modifier = CreateModifier();
            yield return WaitUntilReady(manager);
            manager.RegisterVectorModifier(modifier);
            bool released = false;
            manager.StateChanged += state => released |= state == FlowFieldRuntimeState.Released;
            bool releasedInsideHook = false;
            modifier.OnModify = () =>
            {
                modifier.OnModify = null;
                UnityEngine.Object.DestroyImmediate(manager.gameObject);
                releasedInsideHook = released;
            };

            float deadline = Time.realtimeSinceStartup + 5f;
            while (!released && Time.realtimeSinceStartup < deadline)
                yield return null;

            Assert.That(released, Is.True);
            Assert.That(releasedInsideHook, Is.False);
        }

        private static IEnumerator WaitUntilReady(FlowFieldManager manager)
        {
            float deadline = Time.realtimeSinceStartup + 5f;
            while (!manager.IsReady && !manager.IsFaulted && Time.realtimeSinceStartup < deadline)
                yield return null;
            Assert.That(manager.IsReady, Is.True, manager.LastError);
        }

        [UnityTest]
        public IEnumerator ReleaseStopsAWorkerSolveWithoutBlockingOnItsResult(
            [Values(false, true)] bool volume)
        {
            yield return new EnterPlayMode();
            FlowFieldManager manager = CreateManager(volume);
            yield return WaitUntilReady(manager);
            int revision = manager.Revision;
            manager.SetGoal(FlowFieldGoalRequest.Position(new Vector3(0.5f, volume ? 0.5f : 0f, 0.5f)));
            yield return null;

            manager.Release();

            Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Released));
            Assert.That(manager.Revision, Is.LessThanOrEqualTo(revision + 1));
            manager.Init();
            yield return WaitUntilReady(manager);
            Assert.That(manager.IsFaulted, Is.False, manager.LastError);
        }

        [Test]
        public void BuildBudgetRejectsAnUnusableValue([Values(0d, -1d, double.NaN, double.PositiveInfinity)] double value)
        {
            double before = FlowFieldBuildSettings.BudgetMilliseconds;
            Assert.Throws<ArgumentOutOfRangeException>(() => FlowFieldBuildSettings.BudgetMilliseconds = value);
            Assert.That(FlowFieldBuildSettings.BudgetMilliseconds, Is.EqualTo(before));
        }

        [UnityTest]
        public IEnumerator SurfaceBuildsWhileTheGameTimeIsStopped()
        {
            yield return new EnterPlayMode();
            FlowFieldManager manager = CreateManager(volume: false);
            yield return WaitUntilReady(manager);
            int revision = manager.Revision;
            float timeScale = Time.timeScale;
            Time.timeScale = 0f;
            try
            {
                manager.SetGoal(FlowFieldGoalRequest.Position(new Vector3(0.5f, 0f, 0.5f)));
                float deadline = Time.realtimeSinceStartup + 5f;
                while (manager.Revision == revision && Time.realtimeSinceStartup < deadline)
                    yield return null;
                Assert.That(manager.Revision, Is.GreaterThan(revision), manager.LastError);
            }
            finally
            {
                Time.timeScale = timeScale;
            }
        }

        private sealed class HookModifier : IFlowFieldVectorModifier, IFlowFieldModifierSnapshot
        {
            internal Collider Collider;
            internal int Order;
            internal int Version;
            internal int Observations;
            internal int Captures;
            internal Action OnObserve;
            internal Action OnCapture;
            internal Action OnModify;

            internal void Bump() => Version++;

            public FlowFieldModifierObservation Observe()
            {
                Observations++;
                OnObserve?.Invoke();
                return new FlowFieldModifierObservation(Collider, Order, Version);
            }

            public IFlowFieldModifierSnapshot CaptureSnapshot()
            {
                Captures++;
                OnCapture?.Invoke();
                return this;
            }

            public FlowFieldVectorState Modify(in FlowFieldVectorState current, in FlowFieldVectorModifierContext context)
            {
                OnModify?.Invoke();
                return current;
            }
        }
    }
}
