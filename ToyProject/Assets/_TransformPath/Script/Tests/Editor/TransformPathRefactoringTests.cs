using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Common.TransformPath.Tests
{
    public sealed class TransformPathRefactoringTests
    {
        private readonly List<Object> _objects = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            for (int i = _objects.Count - 1; i >= 0; i--)
                if (_objects[i] != null) Object.DestroyImmediate(_objects[i]);
            _objects.Clear();
        }

        [Test]
        public void AggregatePathDataRefreshKeepsOverrideAndPlaybackIdentity()
        {
            PathData path = CreatePath(0f, 10f);
            PathFollower follower = CreateFollower();
            follower.StartPlayback(PathPlaybackRequest.Aggregate(path, PathMovementSettings.Time(10f)));
            follower.Tick(2f);
            ulong id = follower.PlaybackId;
            path.ConfigureControlPoints(new[] { Vector3.zero, Vector3.right * 20f });
            follower.Tick(0f);
            Assert.That(follower.State, Is.EqualTo(EPathFollowerState.Moving));
            Assert.That(follower.PlaybackId, Is.EqualTo(id));
            Assert.That(follower.Duration, Is.EqualTo(10f));
            Assert.That(follower.GlobalNormalizedTime, Is.EqualTo(0.2f).Within(0.00001f));
            Assert.That(follower.transform.position.x, Is.EqualTo(4f).Within(0.001f));
        }

        [TestCase("Single", false)]
        [TestCase("Single", true)]
        [TestCase("Aggregate", false)]
        [TestCase("Aggregate", true)]
        [TestCase("Sequence", false)]
        [TestCase("Sequence", true)]
        public void GeometryRefreshPreservesLocalProgressAndDefersWhilePaused(string kind, bool pause)
        {
            PathData path = CreatePath(0f, 10f);
            PathFollower follower = CreateFollower();
            follower.StartPlayback(Request(kind, path));
            follower.Tick(1f);
            float progress = follower.NormalizedTime;
            ulong id = follower.PlaybackId;
            if (pause) follower.PauseMove();
            path.ConfigureControlPoints(new[] { Vector3.zero, Vector3.right * 20f });
            if (pause)
            {
                follower.Tick(0f);
                Assert.That(follower.transform.position.x, Is.EqualTo(progress * 10f).Within(0.001f));
                follower.ResumeMove();
            }
            follower.Tick(0f);
            Assert.That(follower.PlaybackId, Is.EqualTo(id));
            Assert.That(follower.NormalizedTime, Is.EqualTo(progress).Within(0.00001f));
            Assert.That(follower.transform.position.x, Is.EqualTo(progress * 20f).Within(0.001f));
            Assert.That(follower.SnapshotRevision, Is.EqualTo(follower.CurrentProvider.Revision));
        }

        [TestCase("Single", false)]
        [TestCase("Aggregate", true)]
        [TestCase("Sequence", true)]
        public void MovementChangeOnlyStopsPlaybackThatConsumesProviderSettings(string kind, bool keepMoving)
        {
            PathData path = CreatePath(0f, 10f);
            PathFollower follower = CreateFollower();
            follower.StartPlayback(Request(kind, path));
            ulong id = follower.PlaybackId;
            path.ConfigureMovementSettings(PathMovementSettings.Speed(7f));
            follower.Tick(0f);
            Assert.That(follower.IsMoving, Is.EqualTo(keepMoving));
            Assert.That(follower.PlaybackId, Is.EqualTo(keepMoving ? id : 0UL));
            if (keepMoving) Assert.That(follower.Duration, Is.EqualTo(kind == "Aggregate" ? 10f : 5f));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SequenceStopsWhenItsEffectiveDescriptorMovementChanges(bool authoringProvider)
        {
            PathData path = CreatePath(0f, 10f);
            MultiPathData route;
            if (authoringProvider)
            {
                route = CreateObject("Authoring route").AddComponent<MultiPathData>();
                var serialized = new SerializedObject(route);
                var segments = serialized.FindProperty("_segments");
                segments.arraySize = 1;
                segments.GetArrayElementAtIndex(0).FindPropertyRelative("_providerObject").objectReferenceValue = path;
                segments.GetArrayElementAtIndex(0).FindPropertyRelative("_movementSource").enumValueIndex = 0;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                route.Init();
            }
            else route = CreateRoute(path);
            PathFollower follower = CreateFollower();
            follower.StartPlayback(PathPlaybackRequest.Sequence(route));
            if (authoringProvider) path.ConfigureMovementSettings(PathMovementSettings.Speed(7f));
            else route.ConfigureSegments(new[] { new PathSegmentDescriptor(path, PathMovementSettings.Speed(7f), false) });
            follower.Tick(0f);
            Assert.That(follower.State, Is.EqualTo(EPathFollowerState.Ready));
            Assert.That(follower.PlaybackId, Is.Zero);
        }

        [TestCase("Single")]
        [TestCase("Aggregate")]
        [TestCase("Sequence")]
        public void EventDefinitionChangeStopsWithoutDispatchingNewEvents(string kind)
        {
            PathData path = CreatePath(0f, 10f);
            PathFollower follower = CreateFollower();
            follower.StartPlayback(Request(kind, path));
            int dispatched = 0;
            follower.GetComponent<PathEventHandler>().OnEventDispatched += _ => dispatched++;
            var setting = ScriptableObject.CreateInstance<PathEventSettingSO>();
            _objects.Add(setting);
            setting.EventName = "New definition";
            typeof(PathData).GetField("_pathEvents", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(path, new List<PathEventEntry> {
                    new PathEventEntry { NormalizedTime = 0.5f, EventSetting = setting }
                });
            path.Rebuild();
            follower.Tick(10f);
            Assert.That(follower.State, Is.EqualTo(EPathFollowerState.Ready));
            Assert.That(follower.PlaybackId, Is.Zero);
            Assert.That(dispatched, Is.Zero);
        }

        [TestCase("Single")]
        [TestCase("Aggregate")]
        [TestCase("Sequence")]
        public void InvalidProviderStopsAndRestoresController(string kind)
        {
            PathData path = CreatePath(0f, 10f);
            GameObject actor = CreateObject("Actor with controller");
            CharacterController controller = actor.AddComponent<CharacterController>();
            PathFollower follower = actor.AddComponent<PathFollower>();
            follower.Init();
            follower.StartPlayback(Request(kind, path));
            path.Release();
            follower.Tick(0f);
            Assert.That(follower.PlaybackId, Is.Zero);
            Assert.That(controller.enabled, Is.True);
        }

        [Test]
        public void SequenceProviderReplacementStopsBeforeUsingNewRoute()
        {
            PathData first = CreatePath(0f, 10f);
            MultiPathData route = CreateRoute(first);
            PathFollower follower = CreateFollower();
            follower.StartPlayback(PathPlaybackRequest.Sequence(route));
            route.ConfigureSegments(new[] {
                new PathSegmentDescriptor(CreatePath(100f, 110f), PathMovementSettings.Time(5f), false)
            });
            follower.Tick(0f);
            Assert.That(follower.State, Is.EqualTo(EPathFollowerState.Ready));
            Assert.That(follower.transform.position.x, Is.EqualTo(0f).Within(0.001f));
        }

        [Test]
        public void TimeStepConsumesOnlyTimeNeededToReachConstraint()
        {
            PathProgressStep step = PathProgressCalculator.AdvanceTime(0f, 10f, null, 20f, 0.5f, 0.6f);
            Assert.That(step.Position, Is.EqualTo(6f).Within(0.00001f));
            Assert.That(step.Progress, Is.EqualTo(0.6f).Within(0.00001f));
            Assert.That(step.ConsumedTime, Is.EqualTo(12f).Within(0.00001f));
        }

        [Test]
        public void DistanceStepConsumesOnlyTimeNeededToReachConstraint()
        {
            PathProgressStep step = PathProgressCalculator.AdvanceDistance(0f, 10f, 2f, 20f, 0.5f, 0.6f);
            Assert.That(step.Position, Is.EqualTo(6f).Within(0.00001f));
            Assert.That(step.Progress, Is.EqualTo(0.6f).Within(0.00001f));
            Assert.That(step.ConsumedTime, Is.EqualTo(6f).Within(0.00001f));
        }

        [TestCase(0.1f)]
        [TestCase(0.5f)]
        [TestCase(0.9f)]
        public void CurveSeekRoundTripsDistanceProgress(float progress)
        {
            AnimationCurve curve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
            float time = PathProgressCalculator.InverseTimeProgress(progress, curve);
            Assert.That(PathProgressCalculator.EvaluateTimeProgress(time, curve),
                Is.EqualTo(progress).Within(0.00001f));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SequenceCarriesFrameTimeAndHonorsPauseFromBoundaryCallback(bool pause)
        {
            var paths = new PathData[5];
            for (int i = 0; i < paths.Length; i++)
            {
                paths[i] = CreatePath(i, i + 1);
                paths[i].ConfigureMovementSettings(PathMovementSettings.Time(0.1f));
            }
            PathFollower follower = CreateFollower();
            var segments = new List<int>();
            follower.SegmentChanged += index =>
            {
                segments.Add(index);
                if (pause) follower.PauseMove();
            };
            follower.StartPlayback(PathPlaybackRequest.Sequence(CreateRoute(paths)));
            follower.Tick(0.35f);
            if (pause)
            {
                CollectionAssert.AreEqual(new[] { 1 }, segments);
                Assert.That(follower.State, Is.EqualTo(EPathFollowerState.Paused));
                Assert.That(follower.GlobalNormalizedTime, Is.EqualTo(0.2f).Within(0.00001f));
            }
            else
            {
                CollectionAssert.AreEqual(new[] { 1, 2, 3 }, segments);
                Assert.That(follower.CurrentSegmentIndex, Is.EqualTo(3));
                Assert.That(follower.NormalizedTime, Is.EqualTo(0.5f).Within(0.00001f));
            }
        }

        [Test]
        public void QueueHoldPolicyWaitsForBothDelayAndSpacing()
        {
            var policy = new PathQueueSession { IsBlocked = true, SpeedMultiplier = 0f };
            policy.Step(0.01f, false, default(PathQueueState), 1.5f, 0.1f, 0.3f, 0f);
            var near = new PathQueueState(null, 1.6f, false, 1f, 1f, 0, 1, 0);
            policy.Step(0.2f, true, near, 1.5f, 0.1f, 0.3f, 0f);
            Assert.That(policy.IsBlocked, Is.True);
            var far = new PathQueueState(null, 2f, false, 1f, 1f, 0, 1, 0);
            policy.Step(0.01f, true, far, 1.5f, 0.1f, 0.3f, 5f);
            Assert.That(policy.IsBlocked, Is.False);
            Assert.That(policy.SpeedMultiplier, Is.EqualTo(0.05f).Within(0.00001f));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void QueueHoldPolicyManualAndManagerHoldsKeepDelay(bool managerBlocked)
        {
            var policy = new PathQueueSession { ManualBlock = !managerBlocked };
            policy.Step(1f, true, new PathQueueState(null, null, managerBlocked, 1f, 1f, 0, 1, 0), 1.5f, 0.1f, 0.3f, 0f);
            Assert.That(policy.IsBlocked, Is.True);
            Assert.That(policy.SpeedMultiplier, Is.Zero);
            Assert.That(policy.ResumeDelayRemaining, Is.EqualTo(0.1f));
        }

        [Test]
        public void InvalidatedQueueCallbackCanAcquireNewRegistration()
        {
            PathData path = CreatePath(0f, 10f);
            var first = CreateObject("First manager").AddComponent<QueuedPathManager>();
            var next = CreateObject("Next manager").AddComponent<QueuedPathManager>();
            first.Init(); first.ConfigureRoute(path);
            next.Init(); next.ConfigureRoute(path);
            GameObject actor = CreateObject("Queue actor");
            actor.AddComponent<PathFollower>();
            var queue = actor.AddComponent<QueuedPathFollower>();
            queue.StartQueuedPlayback(first, PathPlaybackRequest.Single(path), 0.5f);
            queue.QueueInvalidated += (_, reason) => queue.StartQueuedPlayback(next, PathPlaybackRequest.Single(path));
            queue.Seek(0.75f);
            PathQueueRegistration token = first.Register(queue);
            queue.ApplyQueueState(token, new PathQueueState(null, null, false, 1f, 0.5f, token.RouteRevision, queue.PlaybackId, 1));
            ((IPathRuntimeTickable)queue).Tick(0.02f, 0.02f, 0);
            Assert.That(queue.IsRegistered, Is.True);
            Assert.That(queue.ActiveManager, Is.SameAs(next));
            Assert.That(next.AgentCount, Is.EqualTo(1));
            Assert.That(first.AgentCount, Is.Zero);
            Assert.That(queue.IsMoving, Is.True);
        }

        [TestCase(EPathMoveType.TimeBased)]
        [TestCase(EPathMoveType.SpeedBased)]
        public void ScalarAdjustmentUsesGameTimeAndCannotAddressReplacement(EPathMoveType mode)
        {
            PathData path = CreatePath(0f, 10f);
            path.ConfigureMovementSettings(new PathMovementSettings(mode, 5f));
            PathFollower follower = CreateFollower();
            follower.StartPlayback(PathPlaybackRequest.Single(path));
            var adjustment = new PathScalarAdjustment();
            EPathMoveControlChannel channel = mode == EPathMoveType.SpeedBased
                ? EPathMoveControlChannel.Speed : EPathMoveControlChannel.Duration;
            adjustment.Start(follower, channel, 10f, 1f, null, 1);
            adjustment.Tick(0.5f, 1);
            Assert.That(mode == EPathMoveType.SpeedBased ? follower.Speed : follower.Duration, Is.EqualTo(5f));
            adjustment.Tick(0.5f, 2);
            Assert.That(mode == EPathMoveType.SpeedBased ? follower.Speed : follower.Duration, Is.EqualTo(7.5f));
            follower.StartPlayback(PathPlaybackRequest.Single(path));
            adjustment.Tick(1f, 3);
            Assert.That(adjustment.Active, Is.False);
            Assert.That(mode == EPathMoveType.SpeedBased ? follower.Speed : follower.Duration, Is.EqualTo(5f));
        }

        private PathPlaybackRequest Request(string kind, PathData path)
        {
            if (kind == "Single") return PathPlaybackRequest.Single(path);
            if (kind == "Aggregate") return PathPlaybackRequest.Aggregate(path, PathMovementSettings.Time(10f));
            return PathPlaybackRequest.Sequence(CreateRoute(path));
        }

        private MultiPathData CreateRoute(params PathData[] paths)
        {
            var descriptors = new PathSegmentDescriptor[paths.Length];
            for (int i = 0; i < paths.Length; i++)
                descriptors[i] = new PathSegmentDescriptor(paths[i], paths[i].MovementSettings, false);
            var route = CreateObject("Route").AddComponent<MultiPathData>();
            route.ConfigureSegments(descriptors);
            return route;
        }

        private PathData CreatePath(float start, float end)
        {
            var path = CreateObject("Path").AddComponent<PathData>();
            path.ConfigureBuildSettings(new PathBuildSettings(EPathCurveType.Linear, 10));
            path.ConfigureMovementSettings(PathMovementSettings.Time(5f));
            path.ConfigureControlPoints(new[] { Vector3.right * start, Vector3.right * end });
            path.Init();
            return path;
        }

        private PathFollower CreateFollower()
        {
            var follower = CreateObject("Actor").AddComponent<PathFollower>();
            follower.Init();
            return follower;
        }

        private GameObject CreateObject(string name)
        {
            var go = new GameObject(name);
            _objects.Add(go);
            return go;
        }
    }
}
