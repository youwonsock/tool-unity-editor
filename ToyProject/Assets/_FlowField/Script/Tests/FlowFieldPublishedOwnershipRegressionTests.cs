using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Common.FlowField.Tests
{
    public sealed class FlowFieldPublishedOwnershipRegressionTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();
        private GameObject Make(string name)
        {
            var result = new GameObject(name); _objects.Add(result); return result;
        }
        [UnityTearDown]
        public IEnumerator TearDown()
        {
            DestroyObjects();
            if (Application.isPlaying) yield return new ExitPlayMode();
        }
        private void DestroyObjects()
        {
            for (int i = _objects.Count - 1; i >= 0; i--)
            {
                if (_objects[i] == null) continue;
                var manager = _objects[i].GetComponent<FlowFieldManager>();
                if (manager != null)
                {
                    // Init is called explicitly in EditMode; dispose its owned
                    // sessions even when Unity does not dispatch OnDestroy.
                    Surface(manager).Dispose();
                    manager.VolumeSession.Dispose();
                }
                Object.DestroyImmediate(_objects[i]);
            }
            _objects.Clear();
        }
        private BoxCollider Influence()
        {
            var root = Make("Review influence"); root.layer = 27;
            root.transform.position = new Vector3(1.5f, 0f, 1.5f);
            var collider = root.AddComponent<BoxCollider>();
            collider.isTrigger = true; collider.size = new Vector3(3f, 2f, 3f); return collider;
        }
        private FlowFieldManager Manager(bool volume, bool activate = true)
        {
            var ground = Make("Review ground"); ground.layer = FlowFieldRegressionFixture.Layer;
            ground.transform.position = new Vector3(1.5f, -.5f, 1.5f);
            ground.AddComponent<BoxCollider>().size = new Vector3(3f, 1f, 3f);
            var root = Make("Review manager"); root.SetActive(false);
            var manager = root.AddComponent<FlowFieldManager>();
            using (var settings = new SerializedObject(manager))
            {
                settings.FindProperty("_spaceMode").enumValueIndex = (int)(volume ? FlowFieldSpaceMode.Volume3D : FlowFieldSpaceMode.Surface2D);
                settings.FindProperty("_bakeMode").enumValueIndex = (int)FlowFieldBakeMode.RuntimeDynamic;
                settings.FindProperty("_bakeBoundsLocal").boundsValue = FlowFieldRegressionFixture.Settings.BakeBounds;
                settings.FindProperty("_cellSize").floatValue = 1f;
                settings.FindProperty("_groundBakeLayer").intValue = 1 << FlowFieldRegressionFixture.Layer;
                settings.FindProperty("_obstacleLayer").intValue = 1 << 28;
                settings.FindProperty("_showField").boolValue = false;
                settings.FindProperty("_frontierComputeShader").objectReferenceValue = null;
                settings.ApplyModifiedPropertiesWithoutUndo();
            }
            if (activate) root.SetActive(true);
            Physics.SyncTransforms(); return manager;
        }
        private static FlowFieldSession Surface(FlowFieldManager manager)
            => (FlowFieldSession)typeof(FlowFieldManager).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);

        [TestCase(false)]
        [TestCase(true)]
        public void ReleaseFromInitialStateDoesNotResumeOldManagerInitialization(bool volume)
        {
            var manager = Manager(volume);
            int scheduled = FlowFieldBuildScheduler.Count;
            bool entered = false;
            manager.StateChanged += _ =>
            {
                if (entered) return; entered = true; manager.Release();
            };
            Exception error = null;
            try { manager.Init(); } catch (Exception exception) { error = exception; }
            TestContext.WriteLine($"volume={volume}; callback={entered}; state={manager.State}; error={error?.Message}; scheduler={scheduled}->{FlowFieldBuildScheduler.Count}");
            Assert.That(entered, Is.True);
            Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Released));
            Assert.That(error, Is.Null, "The old Init continuation must stop after its owner is released.");
            Assert.That(FlowFieldBuildScheduler.Count, Is.EqualTo(scheduled));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ModeReplacementDuringInitialStateDoesNotScheduleTheReleasedOwner(bool volume)
        {
            var manager = Manager(volume);
            int scheduled = FlowFieldBuildScheduler.Count;
            bool replaced = false;
            manager.StateChanged += _ =>
            {
                if (replaced) return; replaced = true;
                manager.Release();
                manager.SetSpaceMode(volume ? FlowFieldSpaceMode.Surface2D : FlowFieldSpaceMode.Volume3D);
                manager.Init();
            };
            Assert.DoesNotThrow(() => manager.Init());
            if (volume) Surface(manager).Pump();
            else FlowFieldVolumeSafetyRegressionTests.Finish(manager.VolumeSession);
            TestContext.WriteLine($"startVolume={volume}; mode={manager.SpaceMode}; ready={manager.IsReady}; scheduler={scheduled}->{FlowFieldBuildScheduler.Count}");
            Assert.That(replaced, Is.True);
            Assert.That(manager.IsReady, Is.True, manager.LastError);
            Assert.That(FlowFieldBuildScheduler.Count, Is.EqualTo(scheduled + (volume ? 0 : 1)),
                "Only the replacement Volume session may remain scheduled.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SurfaceModifierReentrantSamplingReadsOnlyPublishedValues(bool baked)
        {
            using var session = new FlowFieldSession(FlowFieldRegressionFixture.Surface());
            session.Initialize(baked ? FlowFieldBakeMode.StaticBaked : FlowFieldBakeMode.RuntimeDynamic,
                baked ? FlowFieldSessionSourceKind.StaticSnapshot : FlowFieldSessionSourceKind.SceneBuild,
                FlowFieldBfsBackendPolicy.ManagedOnly, null);
            var modifier = new Modifier { Collider = Influence() };
            session.RegisterModifierCommand(modifier);
            session.Submit(baked ? StaticRequest(Snapshot(false)) : FlowFieldRegressionFixture.Request());
            session.Pump();
            Assert.That(session.Sample(new Vector3(.5f, 0f, .5f)).SpeedMultiplier, Is.EqualTo(2f));
            int revision = session.Revision;
            float observed = -1f, published = -1f;
            int observedRevision = -1;
            bool entered = false;
            modifier.Speed = 3f; modifier.Version++;
            modifier.Callback = () =>
            {
                if (entered) return; entered = true;
                observed = session.Sample(new Vector3(.5f, 0f, .5f)).SpeedMultiplier;
                observedRevision = session.Revision;
                session.CommittedView.TryGetCell(0, out bool _, out FlowFieldGoalFlags _, out Vector3 _, out published, out bool _);
            };
            session.NotifyModifierChangedCommand(modifier, FlowFieldModifierChange.Value);
            session.Pump();
            Assert.That(entered, Is.True);
            Assert.That(session.Sample(new Vector3(.5f, 0f, .5f)).SpeedMultiplier, Is.EqualTo(3f));
            TestContext.WriteLine($"baked={baked}; callback Sample={observed}; committed view={published}; callback revision={observedRevision}; old revision={revision}");
            Assert.That(published, Is.EqualTo(2f));
            Assert.That(observedRevision, Is.EqualTo(revision));
            Assert.That(observed, Is.EqualTo(2f), "Public sampling must not expose a reset/partially composed staging array.");
        }

        [Test]
        public void PendingGoalMetadataStillDescribesThePublishedResolvedGoal()
        {
            var a = new Vector3(2.5f, 0f, 2.5f); var b = new Vector3(.5f, 0f, 2.5f);
            var backend = new FlowFieldRegressionFixture.DeferredBackend();
            using var session = new FlowFieldSession(FlowFieldRegressionFixture.Surface(), backend);
            session.Initialize(FlowFieldBakeMode.RuntimeDynamic, FlowFieldSessionSourceKind.SceneBuild,
                FlowFieldBfsBackendPolicy.RequireGpu, null);
            session.Submit(FlowFieldRegressionFixture.Request(a)); session.Pump(); backend.Complete(0);
            int revision = session.Revision;
            session.Submit(FlowFieldRegressionFixture.Request(b)); session.Pump();
            Assert.That(session.IsRebuilding, Is.True);
            Assert.That(session.TryGetFieldInfo(out var pending), Is.True);
            int committedGoalIndex = session.CommittedWorkspace.ResolvedGoalIndex;
            backend.Complete(1);
            Assert.That(session.TryGetFieldInfo(out var after), Is.True);
            Assert.That(after.ResolvedGoalWorld, Is.EqualTo(b));
            TestContext.WriteLine($"pending revision={pending.Revision}; resolved={pending.ResolvedGoalWorld}; committedGoalIndex={committedGoalIndex}; expected={a}");
            Assert.That(pending.Revision, Is.EqualTo(revision));
            Assert.That(pending.HasResolvedGoal, Is.True);
            Assert.That(pending.ResolvedGoalWorld, Is.EqualTo(a), "Resolved metadata must follow the published result, independently of the requested goal.");
        }

        [Test]
        public void StaticBakedMetadataIncludesItsResolvedGoal()
        {
            using var session = new FlowFieldSession(FlowFieldRegressionFixture.Surface());
            session.Initialize(FlowFieldBakeMode.StaticBaked, FlowFieldSessionSourceKind.StaticSnapshot,
                FlowFieldBfsBackendPolicy.ManagedOnly, null);
            session.Submit(StaticRequest(Snapshot(true))); session.Pump();
            Assert.That(session.IsReady, Is.True, session.LastError);
            Assert.That(session.TryGetFieldInfo(out var info), Is.True);
            TestContext.WriteLine($"requested={info.HasRequestedGoal}; resolved={info.HasResolvedGoal}; committedHasGoal={session.CommittedWorkspace.HasActiveGoal}; goalIndex={session.CommittedWorkspace.ResolvedGoalIndex}");
            Assert.That(info.HasRequestedGoal, Is.True);
            Assert.That(info.HasResolvedGoal, Is.True);
            Assert.That(info.ResolvedGoalWorld, Is.EqualTo(new Vector3(2.5f, 0f, 2.5f)));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NonRunnableVolumeWorkDoesNotSpinWithinTheSchedulerBudget(bool suspended)
        {
            using var session = new FlowFieldVolumeSession();
            session.Initialize(FlowFieldBakeMode.RuntimeDynamic);
            var request = FlowFieldVolumeSafetyRegressionTests.Request(
                FlowFieldGridSpace.FromVolumeCellGrid(Vector3.zero, 3, 3, 3, 1f));
            if (suspended) { session.Suspend(); session.Submit(request); }
            else session.Release();
            var counter = new CountingOperation(session);
            FlowFieldBuildScheduler.Register(counter);
            try { FlowFieldBuildScheduler.PumpAll(5d); }
            finally { FlowFieldBuildScheduler.Unregister(counter); }
            TestContext.WriteLine($"suspended={suspended}; state={session.State}; pending={session.HasPendingRequest}; isBuilding={counter.IsBuilding}; calls={counter.Calls}");
            Assert.That(session.State, Is.EqualTo(suspended ? FlowFieldRuntimeState.Suspended : FlowFieldRuntimeState.Released));
            Assert.That(counter.Calls, Is.LessThanOrEqualTo(1), "An operation unable to pump must not consume repeated turns until the budget expires.");
        }

        [Test]
        public void GoalRemovalDoesNotChangePublishedMetadataDuringComposition()
        {
            using var session = new FlowFieldSession(FlowFieldRegressionFixture.Surface());
            session.Initialize(FlowFieldBakeMode.RuntimeDynamic, FlowFieldSessionSourceKind.SceneBuild,
                FlowFieldBfsBackendPolicy.ManagedOnly, null);
            var modifier = new Modifier { Collider = Influence(), Speed = 1f };
            session.RegisterModifierCommand(modifier);
            var goal = new Vector3(2.5f, 0f, 2.5f);
            session.Submit(FlowFieldRegressionFixture.Request(goal)); session.Pump();
            int revision = session.Revision;
            bool entered = false;
            FlowFieldFieldInfo observed = default;
            modifier.Callback = () =>
            {
                if (entered) return; entered = true;
                session.TryGetFieldInfo(out observed);
            };
            session.Submit(FlowFieldRegressionFixture.Request()); session.Pump();
            Assert.That(entered, Is.True);
            Assert.That(session.TryGetFieldInfo(out var after), Is.True);
            Assert.That(after.HasResolvedGoal, Is.False);
            Assert.That(observed.Revision, Is.EqualTo(revision));
            Assert.That(observed.HasRequestedGoal, Is.False, "Requested input may already describe the removal.");
            Assert.That(observed.HasResolvedGoal, Is.True, "Resolved metadata still belongs to the old published field.");
            Assert.That(observed.ResolvedGoalWorld, Is.EqualTo(goal));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void EquivalentSnapshotKeepsGeometryContractWithoutRepublishingData(bool explicitSurfaceRebuild)
        {
            var first = Snapshot(false);
            first.Surface.CopyToArrays(out var heights, out var normals, out var flags, out var neighbors);
            var nextSurface = FlowFieldSurfaceData.FromSnapshot(FlowFieldRegressionFixture.Settings,
                heights, normals, flags, neighbors, first.Surface.ValidCellCount, 42);
            using var builder = new FlowFieldSession(FlowFieldRegressionFixture.Surface());
            builder.Initialize(FlowFieldBakeMode.RuntimeDynamic, FlowFieldSessionSourceKind.SceneBuild,
                FlowFieldBfsBackendPolicy.ManagedOnly, null);
            builder.Submit(FlowFieldRegressionFixture.Request()); builder.Pump();
            var work = builder.CommittedWorkspace;
            var replacement = new FlowFieldStaticBakeSnapshot(FlowFieldRegressionFixture.Settings,
                1 << FlowFieldRegressionFixture.Layer, 1f, .5f, 0f, 2, false, Vector3.zero, 0f, -1, nextSurface,
                work.Blocked, work.EscapeDirections, work.TopologyMasks, work.GoalFlags, work.GoalDirections, work.NextCells);
            using var session = new FlowFieldSession(FlowFieldRegressionFixture.Surface());
            session.Initialize(FlowFieldBakeMode.StaticBaked, FlowFieldSessionSourceKind.StaticSnapshot,
                FlowFieldBfsBackendPolicy.ManagedOnly, null);
            session.Submit(StaticRequest(first)); session.Pump();
            int revision = session.Revision;
            session.Submit(FlowFieldSessionRequest.ForStaticSnapshot(FlowFieldRegressionFixture.Settings, null, replacement,
                1 << FlowFieldRegressionFixture.Layer, 1f, .5f, 0f, Vector3.forward,
                explicitSurfaceRebuild ? FlowFieldDirtyFlags.All : FlowFieldDirtyFlags.None, 1024));
            Assert.That(session.Sample(new Vector3(.5f, 0f, .5f)).SpeedMultiplier, Is.EqualTo(1f));
            session.Pump();
            Assert.That(session.IsReady, Is.True, session.LastError);
            Assert.That(session.Revision, Is.EqualTo(revision), "Equivalent cell data must not republish.");
            var expectedSurface = explicitSurfaceRebuild ? replacement.Surface : first.Surface;
            Assert.That(session.CommittedSurface, Is.SameAs(expectedSurface));
            Assert.That(session.CommittedView.Surface, Is.SameAs(expectedSurface));
            Assert.That(session.CommittedSurface.Revision, Is.EqualTo(expectedSurface.Revision));
        }

        [Test]
        public void SchedulerPreservesSuspendedInputWithoutStarvingRunnableWork()
        {
            var grid = FlowFieldGridSpace.FromVolumeCellGrid(Vector3.zero, 3, 3, 3, 1f);
            FlowFieldVolumeRequest Request(Vector3 direction) => new FlowFieldVolumeRequest(grid,
                new Bounds(Vector3.one * 1.5f, Vector3.one * 3f), FlowFieldBakeMode.RuntimeDynamic,
                1 << FlowFieldRegressionFixture.Layer, 0f, false, Vector3.zero, 0f, direction, 1024, null, null);
            using var paused = new FlowFieldVolumeSession();
            using var runnable = new FlowFieldVolumeSession();
            paused.Initialize(FlowFieldBakeMode.RuntimeDynamic); runnable.Initialize(FlowFieldBakeMode.RuntimeDynamic);
            paused.Suspend(); paused.Submit(Request(Vector3.right)); runnable.Submit(Request(Vector3.forward));
            var waiting = new CountingOperation(paused); var active = new CountingOperation(runnable);
            FlowFieldBuildScheduler.Register(waiting); FlowFieldBuildScheduler.Register(active);
            try
            {
                FlowFieldBuildScheduler.PumpAll(10d);
                Assert.That(runnable.IsReady, Is.True, runnable.LastError);
                Assert.That(waiting.Calls, Is.LessThanOrEqualTo(1));
                Assert.That(paused.HasPendingRequest, Is.True);
                paused.Resume(); FlowFieldBuildScheduler.PumpAll(10d);
                Assert.That(paused.IsReady, Is.True, paused.LastError);
                Assert.That(paused.Sample(Vector3.one * .5f).Direction, Is.EqualTo(Vector3.right));
            }
            finally { FlowFieldBuildScheduler.Unregister(waiting); FlowFieldBuildScheduler.Unregister(active); }
        }

        [Test]
        public void StaleSurfaceConfigurationStillAllowsUnregistration()
        {
            var manager = Manager(false);
            manager.Init();
            var modifier = new Modifier { Collider = Influence() };
            var obstacle = Make("Review stale obstacle").AddComponent<BoxCollider>();
            Assert.That(manager.RegisterVectorModifier(modifier), Is.True);
            Assert.That(manager.RegisterDynamicObstacle(obstacle), Is.True);
            manager.MarkConfigurationStale();
            Assert.Throws<InvalidOperationException>(() => manager.RegisterVectorModifier(new Modifier { Collider = modifier.Collider }));
            Assert.That(manager.UnregisterVectorModifier(modifier), Is.True);
            Assert.That(manager.UnregisterDynamicObstacle(obstacle), Is.True);
        }

        [UnityTest]
        public IEnumerator DisablingAModifierOnAStaleSurfaceManagerDetachesIt()
        {
            yield return new EnterPlayMode();
            var manager = Manager(false);
            yield return WaitReady(manager);
            var owner = Make("Review stale modifier");
            owner.SetActive(false);
            owner.layer = 27;
            owner.transform.position = new Vector3(1.5f, 0f, 1.5f);
            var collider = owner.AddComponent<BoxCollider>();
            collider.isTrigger = true;
            collider.size = Vector3.one * 8f;
            var modifier = owner.AddComponent<FlowFieldSpeedModifier>();
            using (var settings = new SerializedObject(modifier))
            {
                settings.FindProperty("_flowFieldManager").objectReferenceValue = manager;
                settings.FindProperty("_influenceCollider").objectReferenceValue = collider;
                settings.FindProperty("_priority").intValue = 100;
                settings.ApplyModifiedPropertiesWithoutUndo();
            }
            owner.SetActive(true);
            manager.MarkConfigurationStale();
            // An exception from OnDisable reaches the log and fails this test.
            modifier.enabled = false;
            manager.RequestRebuild();
            yield return WaitReady(manager);
            Assert.That(manager.Sample(new Vector3(.5f, 0f, .5f)).SpeedMultiplier, Is.EqualTo(1f),
                "The disabled Modifier must have left the registry.");
        }

        [UnityTest] public IEnumerator SurfaceInitializationCallbacksPreserveSelectedLifetime() => ManagerBoundary(false, false);
        [UnityTest] public IEnumerator VolumeInitializationCallbacksPreserveSelectedLifetime() => ManagerBoundary(true, false);
        [UnityTest] public IEnumerator SurfaceReactivationCallbacksPreserveSelectedLifetime() => ManagerBoundary(false, true);
        [UnityTest] public IEnumerator VolumeReactivationCallbacksPreserveSelectedLifetime() => ManagerBoundary(true, true);

        private IEnumerator ManagerBoundary(bool volume, bool resume)
        {
            yield return new EnterPlayMode();
            foreach (string transition in new[] { "release", "restart", "replace", "disable", "fault", "dispose" })
            {
                int scheduledBefore = FlowFieldBuildScheduler.Count;
                var manager = Manager(volume, activate: false);
                if (resume)
                {
                    manager.gameObject.SetActive(true);
                    yield return WaitReady(manager);
                    manager.enabled = false;
                }
                bool entered = false;
                int publications = 0;
                manager.FieldChanged += () => publications++;
                manager.StateChanged += _ =>
                {
                    if (entered) return; entered = true;
                    if (transition == "disable") manager.enabled = false;
                    else if (transition == "dispose") Object.DestroyImmediate(manager.gameObject);
                    else if (transition == "fault")
                    {
                        LogAssert.Expect(LogType.Error, "[FlowFieldManager] FlowField build failed: Expected manager boundary fault");
                        if (volume) manager.VolumeSession.ReportFault(new InvalidOperationException("Expected manager boundary fault"));
                        else Surface(manager).ReportFault(new InvalidOperationException("Expected manager boundary fault"));
                    }
                    else
                    {
                        manager.Release();
                        if (transition != "release")
                        {
                            if (transition == "replace") manager.SetSpaceMode(volume ? FlowFieldSpaceMode.Surface2D : FlowFieldSpaceMode.Volume3D);
                            manager.Init();
                        }
                    }
                };
                if (resume) manager.enabled = true; else manager.gameObject.SetActive(true);
                Assert.That(entered, Is.True, $"{volume}/{resume}/{transition}");
                if (transition == "restart" || transition == "replace")
                {
                    yield return WaitReady(manager);
                    Assert.That(publications, Is.EqualTo(1), transition);
                    Assert.That(manager.SpaceMode, Is.EqualTo(transition == "replace"
                        ? volume ? FlowFieldSpaceMode.Surface2D : FlowFieldSpaceMode.Volume3D
                        : volume ? FlowFieldSpaceMode.Volume3D : FlowFieldSpaceMode.Surface2D));
                    Assert.That(FlowFieldBuildScheduler.Count, Is.EqualTo(scheduledBefore + (manager.SpaceMode == FlowFieldSpaceMode.Volume3D ? 1 : 0)));
                }
                else if (transition == "dispose")
                {
                    Assert.That(manager == null, Is.True);
                    Assert.That(FlowFieldBuildScheduler.Count, Is.EqualTo(scheduledBefore));
                }
                else
                {
                    Assert.That(manager.State, Is.EqualTo(transition == "release" ? FlowFieldRuntimeState.Released
                        : transition == "fault" ? FlowFieldRuntimeState.Faulted : FlowFieldRuntimeState.Suspended), transition);
                    Assert.That(publications, Is.Zero, transition);
                    if (transition == "fault")
                    {
                        manager.RequestRebuild(); yield return WaitReady(manager);
                        Assert.That(publications, Is.EqualTo(1), "Explicit retry must remain schedulable.");
                    }
                    else if (transition == "disable")
                    {
                        manager.enabled = true; yield return WaitReady(manager);
                        Assert.That(publications, Is.EqualTo(1), "Reactivation must publish the preserved input once.");
                    }
                }
                TestContext.WriteLine($"Manager boundary passed: volume={volume}, resume={resume}, transition={transition}");
                DestroyObjects();
                yield return null;
            }
        }
        private static IEnumerator WaitReady(FlowFieldManager manager)
        {
            float deadline = Time.realtimeSinceStartup + 3f;
            while (!manager.IsReady && !manager.IsFaulted && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(manager.IsReady, Is.True, manager.LastError);
        }

        [UnityTest]
        public IEnumerator InlineVolumePumpDoesNotInvalidateLegitimateInitialization()
        {
            yield return new EnterPlayMode();
            int scheduled = FlowFieldBuildScheduler.Count;
            var manager = Manager(true, activate: false);
            bool pumped = false;
            int publications = 0;
            manager.FieldChanged += () => publications++;
            manager.StateChanged += state =>
            {
                if (state != FlowFieldRuntimeState.Building || pumped) return;
                pumped = true; manager.VolumeSession.Pump(100d);
            };
            manager.gameObject.SetActive(true);
            yield return WaitReady(manager);
            Assert.That(pumped, Is.True);
            Assert.That(publications, Is.EqualTo(1));
            Assert.That(FlowFieldBuildScheduler.Count, Is.EqualTo(scheduled + 1));
            manager.RequestRebuild();
            yield return WaitReady(manager);
            Assert.That(publications, Is.EqualTo(2));
        }

        private static FlowFieldStaticBakeSnapshot Snapshot(bool goal)
        {
            using var source = new FlowFieldSession(FlowFieldRegressionFixture.Surface());
            source.Initialize(FlowFieldBakeMode.RuntimeDynamic, FlowFieldSessionSourceKind.SceneBuild,
                FlowFieldBfsBackendPolicy.ManagedOnly, null);
            var position = new Vector3(2.5f, 0f, 2.5f);
            source.Submit(FlowFieldRegressionFixture.Request(goal ? (Vector3?)position : null)); source.Pump();
            var work = source.CommittedWorkspace;
            return new FlowFieldStaticBakeSnapshot(FlowFieldRegressionFixture.Settings, 1 << FlowFieldRegressionFixture.Layer,
                1f, .5f, 0f, 1, goal, goal ? position : Vector3.zero, 0f, work.ResolvedGoalIndex, source.CommittedSurface,
                work.Blocked, work.EscapeDirections, work.TopologyMasks, work.GoalFlags, work.GoalDirections, work.NextCells);
        }
        private static FlowFieldSessionRequest StaticRequest(FlowFieldStaticBakeSnapshot snapshot)
            => FlowFieldSessionRequest.ForStaticSnapshot(FlowFieldRegressionFixture.Settings, null, snapshot,
                1 << FlowFieldRegressionFixture.Layer, 1f, .5f, 0f, Vector3.forward, FlowFieldDirtyFlags.None, 1024);
        private sealed class Modifier : IFlowFieldVectorModifier
        {
            internal Collider Collider; internal float Speed = 2f; internal int Version; internal Action Callback;
            public Collider InfluenceCollider => Collider;
            public int Priority => 1;
            public int Revision => Version;
            public IFlowFieldModifierSnapshot CaptureSnapshot() => new ValueSnapshot(Speed, Callback);
        }
        private sealed class ValueSnapshot : IFlowFieldModifierSnapshot
        {
            private readonly float _speed; private readonly Action _callback;
            internal ValueSnapshot(float speed, Action callback) { _speed = speed; _callback = callback; }
            public FlowFieldVectorState Modify(in FlowFieldVectorState state, in FlowFieldVectorModifierContext context)
            { _callback?.Invoke(); return new FlowFieldVectorState(state.Direction, state.SpeedMultiplier * _speed); }
        }
        private sealed class CountingOperation : IFlowFieldBuildOperation
        {
            private readonly IFlowFieldBuildOperation _target; internal int Calls;
            internal CountingOperation(IFlowFieldBuildOperation target) => _target = target;
            public bool IsWaitingForExternalCompletion => _target.IsWaitingForExternalCompletion;
            public bool IsBuilding => _target.IsBuilding;
            public void PumpInternal(in FlowFieldBuildBudget budget) { Calls++; _target.PumpInternal(in budget); }
        }
    }
}
