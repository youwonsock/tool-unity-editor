using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Common.FlowField.Tests
{
    public sealed class FlowFieldSchedulingOwnershipRegressionTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();
        private GameObject MakeObject(string name)
        {
            var result = new GameObject(name);
            _objects.Add(result);
            return result;
        }

        private BoxCollider MakeInfluence()
        {
            var owner = MakeObject("Scheduling influence");
            owner.layer = 27;
            owner.transform.position = new Vector3(1.5f, .5f, 1.5f);
            var collider = owner.AddComponent<BoxCollider>();
            collider.isTrigger = true;
            collider.size = new Vector3(3f, 2f, 3f);
            return collider;
        }

        private FlowFieldManager CreateManager(bool volume)
        {
            if (!volume)
            {
                var ground = MakeObject("Scheduling ground");
                ground.layer = FlowFieldRegressionFixture.Layer;
                ground.transform.position = new Vector3(1.5f, -.5f, 1.5f);
                ground.AddComponent<BoxCollider>().size = new Vector3(3f, 1f, 3f);
            }
            var root = MakeObject("Scheduling manager");
            root.SetActive(false);
            var manager = root.AddComponent<FlowFieldManager>();
            using (var settings = new SerializedObject(manager))
            {
                settings.FindProperty("_spaceMode").enumValueIndex = (int)(volume ? FlowFieldSpaceMode.Volume3D : FlowFieldSpaceMode.Surface2D);
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

        private static IEnumerator WaitForRevision(FlowFieldManager manager, int revision)
        {
            float deadline = Time.realtimeSinceStartup + 3f;
            while (manager.Revision < revision && !manager.IsFaulted && Time.realtimeSinceStartup < deadline)
                yield return null;
            Assert.That(manager.IsReady, Is.True, manager.LastError);
            Assert.That(manager.Revision, Is.EqualTo(revision));
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            for (int i = _objects.Count - 1; i >= 0; i--)
                if (_objects[i] != null) Object.DestroyImmediate(_objects[i]);
            _objects.Clear();
            if (Application.isPlaying) yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator BuildDriverIsUniqueInPlayModeAndEndsWithIt()
        {
            yield return new EnterPlayMode();
            yield return null;
            Assert.That(Resources.FindObjectsOfTypeAll<FlowFieldBuildDriver>().Length, Is.EqualTo(1));
            yield return new ExitPlayMode();
            yield return null;
            Assert.That(Resources.FindObjectsOfTypeAll<FlowFieldBuildDriver>(), Is.Empty);
        }

        [UnityTest] public IEnumerator SurfaceManagerRegistrationQueuesComposition()
            => CheckSurfaceUpdate("register");
        [UnityTest] public IEnumerator SurfaceManagerValueNotificationQueuesComposition()
            => CheckSurfaceUpdate("notify");
        [UnityTest] public IEnumerator SurfaceManagerUnregistrationQueuesComposition()
            => CheckSurfaceUpdate("unregister");

        private IEnumerator CheckSurfaceUpdate(string operation)
        {
            yield return new EnterPlayMode();
            var manager = CreateManager(false);
            yield return WaitForRevision(manager, 1);
            var modifier = new Modifier { Collider = MakeInfluence() };
            if (operation != "register")
            {
                manager.RegisterVectorModifier(modifier);
                manager.RequestRebuild();
                yield return WaitForRevision(manager, 2);
                Assert.That(manager.Sample(new Vector3(.5f, 0f, .5f)).SpeedMultiplier, Is.EqualTo(2f));
            }
            int baseline = manager.Revision;
            if (operation == "register") manager.RegisterVectorModifier(modifier);
            else if (operation == "unregister") manager.UnregisterVectorModifier(modifier);
            else
            {
                modifier.Speed = 3f;
                modifier.RevisionValue++;
                manager.NotifyModifierChanged(modifier, FlowFieldModifierChange.Value);
            }
            // Exercise the actual Manager FixedUpdate/LateUpdate path, with no extra RequestRebuild.
            float deadline = Time.realtimeSinceStartup + .4f;
            while (Time.realtimeSinceStartup < deadline) yield return null;
            float expected = operation == "unregister" ? 1f : operation == "notify" ? 3f : 2f;
            TestContext.WriteLine($"operation={operation}; revision={baseline}->{manager.Revision}; speed={manager.Sample(new Vector3(.5f, 0f, .5f)).SpeedMultiplier}; expected={expected}");
            Assert.That(manager.IsFaulted, Is.False, manager.LastError);
            Assert.That(manager.Sample(new Vector3(.5f, 0f, .5f)).SpeedMultiplier, Is.EqualTo(expected));
            Assert.That(manager.Revision, Is.EqualTo(baseline + 1));
        }

        [UnityTest]
        public IEnumerator SupportedModeSwitchKeepsManagerCallbacksAttachedToTheNewSession()
        {
            yield return new EnterPlayMode();
            var manager = CreateManager(false);
            yield return WaitForRevision(manager, 1);
            manager.Release();
            int publications = 0, states = 0;
            manager.FieldChanged += () => publications++;
            manager.StateChanged += _ => states++;
            manager.SetSpaceMode(FlowFieldSpaceMode.Volume3D);
            manager.Init();
            yield return WaitForRevision(manager, 1);
            Assert.That(manager.SpaceMode, Is.EqualTo(FlowFieldSpaceMode.Volume3D));
            TestContext.WriteLine($"ready={manager.IsReady}; revision={manager.Revision}; publications={publications}; stateEvents={states}");
            Assert.That(publications, Is.EqualTo(1), "Release/SetSpaceMode/Init must transfer the Manager callback subscription.");
            Assert.That(states, Is.GreaterThan(0));
        }

        [UnityTest] public IEnumerator SerializedModeDriftStillReleasesTheAcceptedSession()
            => CheckAcceptedMode("release");
        [UnityTest] public IEnumerator SerializedModeDriftStillSuspendsTheAcceptedSession()
            => CheckAcceptedMode("disable");

        private IEnumerator CheckAcceptedMode(string operation)
        {
            yield return new EnterPlayMode();
            var manager = CreateManager(true);
            yield return WaitForRevision(manager, 1);
            // Covers Debug Inspector, Undo, or another serialized editor tool;
            // the normal custom Inspector intentionally disables this field.
            using (var settings = new SerializedObject(manager))
            {
                settings.FindProperty("_spaceMode").enumValueIndex = (int)FlowFieldSpaceMode.Surface2D;
                settings.ApplyModifiedPropertiesWithoutUndo();
            }
            Assert.That(manager.SpaceMode, Is.EqualTo(FlowFieldSpaceMode.Volume3D));
            if (operation == "release") manager.Release(); else manager.enabled = false;
            TestContext.WriteLine($"operation={operation}; state={manager.State}; volumeState={manager.VolumeSession.State}");
            Assert.That(manager.VolumeSession.State, Is.EqualTo(operation == "release"
                ? FlowFieldRuntimeState.Released : FlowFieldRuntimeState.Suspended));
        }

        [UnityTest] public IEnumerator VolumeRebuildRequestRequeriesWhileTheGoalMoves()
            => CheckVolumeObstacleQuery("rebuild", expectQueried: true);
        [UnityTest] public IEnumerator VolumeCellsDirtyRequeriesWhileTheGoalMoves()
            => CheckVolumeObstacleQuery("cells", expectQueried: true);
        [UnityTest] public IEnumerator VolumeReactivationRequeriesAfterTheGoalMoved()
            => CheckVolumeObstacleQuery("reactivate", expectQueried: true);
        [UnityTest] public IEnumerator VolumeSetGoalKeepsReusingObstacles()
            => CheckVolumeObstacleQuery("setGoal", expectQueried: false);

        private IEnumerator CheckVolumeObstacleQuery(string operation, bool expectQueried)
        {
            yield return new EnterPlayMode();
            var goal = MakeObject("Scheduling goal");
            goal.transform.position = Vector3.one * 2.5f;
            var manager = CreateManager(true);
            using (var settings = new SerializedObject(manager))
            {
                settings.FindProperty("_goalTransform").objectReferenceValue = goal.transform;
                settings.ApplyModifiedPropertiesWithoutUndo();
            }
            yield return null;
            yield return WaitForVolumeBuild(manager);

            // A collider nothing tracks only appears when obstacles are queried.
            var wall = MakeObject("Scheduling wall");
            wall.layer = 28;
            wall.transform.position = Vector3.one * 1.5f;
            wall.AddComponent<BoxCollider>();
            Physics.SyncTransforms();

            // The Manager observes a moved Transform in LateUpdate, so the
            // explicit request below is the first to carry the new Goal.
            Vector3 moved = new Vector3(.5f, 2.5f, .5f);
            switch (operation)
            {
                case "rebuild":
                    goal.transform.position = moved;
                    manager.RequestRebuild();
                    break;
                case "cells":
                    goal.transform.position = moved;
                    manager.NotifyCellsDirty();
                    break;
                case "reactivate":
                    manager.enabled = false;
                    goal.transform.position = moved;
                    manager.enabled = true;
                    break;
                case "setGoal":
                    manager.SetGoal(FlowFieldGoalRequest.Position(moved));
                    break;
            }
            yield return WaitForVolumeBuild(manager);

            Assert.That(manager.TryGetFieldInfo(out FlowFieldFieldInfo info), Is.True);
            Assert.That(info.RequestedGoalWorld, Is.EqualTo(moved));
            Assert.That(manager.VolumeSession.TryGetView(out FlowFieldGridSpace grid, out bool[] blocked, out _, out _), Is.True);
            Assert.That(blocked[grid.ToFlatIndex(1, 1, 1)], Is.EqualTo(expectQueried));
        }

        private static IEnumerator WaitForVolumeBuild(FlowFieldManager manager)
        {
            float deadline = Time.realtimeSinceStartup + 3f;
            while ((manager.IsRebuilding || !manager.IsReady) && !manager.IsFaulted && Time.realtimeSinceStartup < deadline)
                yield return null;
            Assert.That(manager.IsReady, Is.True, manager.LastError);
            Assert.That(manager.IsRebuilding, Is.False);
        }

        [Test]
        public void SurfaceDirtyInputsAreProcessedWithoutResubmission(
            [Values("scene", "static", "bfs")] string path,
            [Values("register", "notify", "unregister", "observe")] string operation)
        {
            var source = FlowFieldRegressionFixture.Surface();
            var backend = new FlowFieldRegressionFixture.DeferredBackend();
            using var session = new FlowFieldSession(source, backend);
            var errors = new FlowFieldRegressionFixture.EventFailures(session);
            bool baked = path == "static", bfs = path == "bfs";
            session.Initialize(baked ? FlowFieldBakeMode.StaticBaked : FlowFieldBakeMode.RuntimeDynamic,
                baked ? FlowFieldSessionSourceKind.StaticSnapshot : FlowFieldSessionSourceKind.SceneBuild,
                bfs ? FlowFieldSolvePolicy.Worker : FlowFieldSolvePolicy.Inline);
            var modifier = new Modifier { Collider = MakeInfluence() };
            if (operation != "register") session.RegisterModifierCommand(modifier);
            session.Submit(baked ? StaticRequest(CreateSnapshot()) : FlowFieldRegressionFixture.Request(
                bfs ? (Vector3?)new Vector3(2.5f, 0f, 2.5f) : null));
            session.Pump();
            int revision = session.Revision;
            if (operation == "register") session.RegisterModifierCommand(modifier);
            else if (operation == "unregister") session.UnregisterModifierCommand(modifier);
            else
            {
                modifier.Speed = 3f; modifier.RevisionValue++;
                if (operation == "notify") session.NotifyModifierChangedCommand(modifier, FlowFieldModifierChange.Value);
                else session.DetectModifierChanges();
            }
            if (bfs) backend.Complete(0); else session.Pump();
            Assert.That(session.IsReady, Is.True, session.LastError);
            float expected = operation == "unregister" ? 1f : operation == "register" ? 2f : 3f;
            Assert.That(session.Sample(new Vector3(.5f, 0f, .5f)).SpeedMultiplier, Is.EqualTo(expected));
            Assert.That(session.Revision, Is.EqualTo(revision + 1));
            Assert.That(source.BuildCount, Is.EqualTo(baked ? 0 : 1));
            Assert.That(backend.Calls.Count, Is.EqualTo(bfs ? 1 : 0));
            int finalRevision = session.Revision;
            session.Pump();
            Assert.That(session.Revision, Is.EqualTo(finalRevision));
            Assert.That(session.HasPendingRequest, Is.False);
            errors.AssertNone();
        }

        [UnityTest]
        public IEnumerator RoundTripModeSwitchesKeepOnePublicationPerBuild()
        {
            yield return new EnterPlayMode();
            var manager = CreateManager(false);
            yield return WaitForRevision(manager, 1);
            int publications = 0;
            manager.FieldChanged += () => publications++;
            for (int cycle = 0; cycle < 4; cycle++)
            {
                manager.Release();
                manager.SetSpaceMode(cycle % 2 == 0 ? FlowFieldSpaceMode.Volume3D : FlowFieldSpaceMode.Surface2D);
                manager.Init();
                yield return WaitForRevision(manager, cycle / 2 + (cycle % 2 == 0 ? 1 : 2));
                Assert.That(publications, Is.EqualTo(cycle + 1));
            }
        }

        [UnityTest]
        public IEnumerator SurfaceManagerQueuesTheCurrentGoalAndDirectionInputs()
        {
            yield return new EnterPlayMode();
            var manager = CreateManager(false);
            yield return WaitForRevision(manager, 1);
            var goal = new Vector3(2.5f, 0f, 2.5f);
            manager.SetGoal(FlowFieldGoalRequest.Position(goal));
            yield return WaitForRevision(manager, 2);
            Assert.That(manager.TryGetFieldInfo(out var info), Is.True);
            Assert.That(info.ResolvedGoalWorld, Is.EqualTo(goal));
            manager.SetGoal(FlowFieldGoalRequest.None);
            yield return WaitForRevision(manager, 3);
            using (var settings = new SerializedObject(manager))
            {
                settings.FindProperty("_defaultFlowDirection").vector3Value = Vector3.right;
                settings.ApplyModifiedPropertiesWithoutUndo();
            }
            yield return WaitForRevision(manager, 4);
            Assert.That(manager.Sample(new Vector3(.5f, 0f, .5f)).Direction, Is.EqualTo(Vector3.right));
        }

        [UnityTest]
        public IEnumerator ReleasedSubscriberMayInitializeTheReplacementMode()
        {
            yield return new EnterPlayMode();
            var manager = CreateManager(false);
            yield return WaitForRevision(manager, 1);
            int publications = 0;
            bool replaced = false;
            manager.FieldChanged += () => publications++;
            manager.StateChanged += state =>
            {
                if (state != FlowFieldRuntimeState.Released || replaced) return;
                replaced = true;
                manager.SetSpaceMode(FlowFieldSpaceMode.Volume3D);
                manager.Init();
            };
            manager.Release();
            Assert.That(replaced, Is.True);
            yield return WaitForRevision(manager, 1);
            Assert.That(manager.SpaceMode, Is.EqualTo(FlowFieldSpaceMode.Volume3D));
            Assert.That(publications, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator FaultedAcceptedSessionRemainsVisibleAfterSerializedModeDrift()
        {
            yield return new EnterPlayMode();
            var manager = CreateManager(true);
            yield return WaitForRevision(manager, 1);
            LogAssert.Expect(LogType.Error, "[FlowFieldManager] FlowField build failed: Expected ownership fault");
            manager.VolumeSession.ReportFault(new InvalidOperationException("Expected ownership fault"));
            using (var settings = new SerializedObject(manager))
            {
                settings.FindProperty("_spaceMode").enumValueIndex = (int)FlowFieldSpaceMode.Surface2D;
                settings.ApplyModifiedPropertiesWithoutUndo();
            }
            Assert.That(manager.IsFaulted, Is.True);
            Assert.That(manager.SpaceMode, Is.EqualTo(FlowFieldSpaceMode.Volume3D));
            Assert.That(manager.LastError, Does.Contain("Expected ownership fault"));
            manager.Release();
            Assert.That(manager.VolumeSession.State, Is.EqualTo(FlowFieldRuntimeState.Released));
        }

        [Test]
        public void ModifierFaultReleaseAllowsValidReinitialization()
        {
            var root = MakeObject("Modifier fault state");
            root.SetActive(false);
            var modifier = root.AddComponent<FlowFieldSpeedModifier>();
            using (var settings = new SerializedObject(modifier))
            {
                settings.FindProperty("_speedMultiplier").floatValue = -1f;
                settings.ApplyModifiedPropertiesWithoutUndo();
            }
            Assert.Throws<ArgumentOutOfRangeException>(() => modifier.Init());
            Assert.That(modifier.IsFaulted, Is.True);
            Assert.That(modifier.IsInitialized, Is.False);
            modifier.Release();
            Assert.That(modifier.IsFaulted, Is.False);
            modifier.SetSpeedMultiplier(2f);
            modifier.Init();
            Assert.That(modifier.IsInitialized, Is.True);
            Assert.That(modifier.IsFaulted, Is.False);
        }

        [Test]
        public void DuplicatePriorityRejectionPreservesTheAcceptedOrder()
        {
            var registry = new FlowFieldModifierRegistry();
            var first = new Modifier { Collider = MakeInfluence() };
            var duplicate = new Modifier { Collider = first.Collider };
            registry.Register(first);
            Assert.Throws<InvalidOperationException>(() => registry.Register(duplicate));
            Assert.That(registry.Entries.Count, Is.EqualTo(1));
            Assert.That(registry.Entries[0].Modifier, Is.SameAs(first));
        }

        [Test]
        public void CurrentAreaGetterErrorStillFaultsExactlyOnce([Values("scene", "static", "bfs")] string path)
        {
            var backend = new FlowFieldRegressionFixture.DeferredBackend();
            using var session = new FlowFieldSession(FlowFieldRegressionFixture.Surface(), backend);
            bool baked = path == "static", bfs = path == "bfs";
            session.Initialize(baked ? FlowFieldBakeMode.StaticBaked : FlowFieldBakeMode.RuntimeDynamic,
                baked ? FlowFieldSessionSourceKind.StaticSnapshot : FlowFieldSessionSourceKind.SceneBuild,
                bfs ? FlowFieldSolvePolicy.Worker : FlowFieldSolvePolicy.Inline);
            var modifier = new Modifier { Collider = MakeInfluence() };
            session.RegisterModifierCommand(modifier);
            session.Submit(baked ? StaticRequest(CreateSnapshot()) : FlowFieldRegressionFixture.Request(
                bfs ? (Vector3?)new Vector3(2.5f, 0f, 2.5f) : null));
            if (bfs) { session.Pump(); session.NotifyModifierChangedCommand(modifier, FlowFieldModifierChange.Area); }
            int failures = 0;
            session.Failed += _ => failures++;
            modifier.ReadCollider = () => throw new InvalidOperationException("Current area failure");
            if (bfs) backend.Complete(0); else session.Pump();
            Assert.That(session.IsFaulted, Is.True);
            Assert.That(session.LastError, Does.Contain("Current area failure"));
            session.Pump();
            Assert.That(failures, Is.EqualTo(1));
        }

        private static FlowFieldStaticBakeSnapshot CreateSnapshot()
        {
            using var source = new FlowFieldSession(FlowFieldRegressionFixture.Surface());
            source.Initialize(FlowFieldBakeMode.RuntimeDynamic, FlowFieldSessionSourceKind.SceneBuild,
                FlowFieldSolvePolicy.Inline);
            source.Submit(FlowFieldRegressionFixture.Request()); source.Pump();
            var work = source.CommittedWorkspace;
            return new FlowFieldStaticBakeSnapshot(FlowFieldRegressionFixture.Settings, 1 << FlowFieldRegressionFixture.Layer,
                1f, .5f, 0f, 1, false, Vector3.zero, 0f, -1, source.CommittedSurface,
                work.Blocked, work.EscapeDirections, work.TopologyMasks, work.GoalFlags, work.GoalDirections, work.NextCells);
        }

        private static FlowFieldSessionRequest StaticRequest(FlowFieldStaticBakeSnapshot snapshot)
            => FlowFieldSessionRequest.ForStaticSnapshot(FlowFieldRegressionFixture.Settings, null, snapshot,
                1 << FlowFieldRegressionFixture.Layer, 1f, .5f, 0f, Vector3.forward, FlowFieldDirtyFlags.None);

        private sealed class CallbackSurfaceSource : IFlowFieldSurfaceSource
        {
            internal Action OnBuild;
            internal bool ReturnNullOnce, ReturnInvalidOnce, DynamicGrid;
            public FlowFieldSurfaceData Build(in FlowFieldSurfaceBakeSettings settings)
            {
                OnBuild?.Invoke();
                if (ReturnNullOnce) { ReturnNullOnce = false; return null; }
                if (ReturnInvalidOnce)
                {
                    ReturnInvalidOnce = false;
                    return FlowFieldSurfaceData.FromRuntime(settings, new FlowFieldSurfaceBakeResult(settings.Grid.CellCount), 1);
                }
                if (!DynamicGrid) return FlowFieldRegressionFixture.Surface().Build(settings);
                var result = new FlowFieldSurfaceBakeResult(settings.Grid.CellCount);
                for (int index = 0; index < settings.Grid.CellCount; index++) result.SetSurface(index, 0f, Vector3.up);
                for (int index = 0; index < settings.Grid.CellCount; index++)
                {
                    settings.Grid.FromFlatIndex(index, out int x, out int z);
                    FlowFieldSurfaceBaker.BuildNeighborMask(settings, result, index, x, z);
                }
                return FlowFieldSurfaceData.FromRuntime(settings, result, 1);
            }
        }

        private sealed class Modifier : IFlowFieldVectorModifier
        {
            internal Collider Collider;
            internal int RevisionValue;
            internal int Order = 1;
            internal float Speed = 2f;
            internal Action ReadCollider;
            public Collider InfluenceCollider { get { ReadCollider?.Invoke(); return Collider; } }
            public int Priority => Order;
            public int Revision => RevisionValue;
            public FlowFieldModifierObservation Observe() => new FlowFieldModifierObservation(InfluenceCollider, Priority, Revision);
            public IFlowFieldModifierSnapshot CaptureSnapshot() => new Snapshot(Speed);
        }
        private sealed class Snapshot : IFlowFieldModifierSnapshot
        {
            private readonly float _speed;
            internal Snapshot(float speed) => _speed = speed;
            public FlowFieldVectorState Modify(in FlowFieldVectorState state, in FlowFieldVectorModifierContext context)
                => new FlowFieldVectorState(state.Direction, state.SpeedMultiplier * _speed);
        }
    }
}
