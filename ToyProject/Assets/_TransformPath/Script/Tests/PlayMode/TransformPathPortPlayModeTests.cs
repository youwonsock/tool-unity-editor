#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Common.TransformPath.Samples;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Common.TransformPath.Tests.PlayMode
{
    public sealed class TransformPathPortPlayModeTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();
        private float _previousTimeScale;
        private float _previousFixedDeltaTime;

        [SetUp]
        public void SetUp()
        {
            _previousTimeScale = Time.timeScale;
            _previousFixedDeltaTime = Time.fixedDeltaTime;
            Time.timeScale = 1f;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            for (int i = _objects.Count - 1; i >= 0; i--)
                if (_objects[i] != null)
                    Object.Destroy(_objects[i]);
            _objects.Clear();
            yield return null;
            Time.timeScale = _previousTimeScale;
            Time.fixedDeltaTime = _previousFixedDeltaTime;
        }

        [UnityTest]
        public IEnumerator ExistingShowcaseRunsAllLanesAndQueuePauseResume()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/_TransformPath/Prefab/TransformPathShowcase.prefab");
            GameObject instance = Object.Instantiate(prefab);
            _objects.Add(instance);
            yield return null;
            yield return null;

            TransformPathOverviewController overview =
                instance.GetComponentInChildren<TransformPathOverviewController>(true);
            Assert.That(overview.IsInitialized, Is.True);
            PathFollower normal = GetField<PathFollower>(overview, "_pathFollower");
            PathFollower multi = GetField<PathFollower>(overview, "_multiPathFollower");
            QueuedPathFollower[] queue = GetField<QueuedPathFollower[]>(overview, "_queueFollowers");

            overview.ShowNormalLane();
            float before = normal.GlobalNormalizedTime;
            yield return new WaitForSeconds(0.25f);
            Assert.That(normal.GlobalNormalizedTime, Is.GreaterThan(before));

            overview.ShowMultiPathLane();
            before = multi.GlobalNormalizedTime;
            yield return new WaitForSeconds(0.25f);
            Assert.That(multi.GlobalNormalizedTime, Is.GreaterThan(before));

            overview.ShowQueuedLane();
            Assert.That(queue.Length, Is.GreaterThan(0));
            before = queue[0].GlobalNormalizedTime;
            yield return new WaitForSeconds(0.35f);
            Assert.That(queue[0].GlobalNormalizedTime, Is.GreaterThan(before));
            overview.PauseAll();
            before = queue[0].GlobalNormalizedTime;
            yield return new WaitForSeconds(0.15f);
            Assert.That(queue[0].GlobalNormalizedTime, Is.EqualTo(before).Within(0.00001f));
            overview.ResumeAll();
            yield return new WaitForSeconds(0.35f);
            Assert.That(queue[0].IsRegistered, Is.True);
            Assert.That(queue[0].GlobalNormalizedTime, Is.GreaterThan(before));
        }

        [UnityTest]
        public IEnumerator DisablingActorRestoresCharacterControllerDuringActualPlayback()
        {
            PathData path = CreatePath(5f);
            GameObject actor = CreateObject("Actor");
            CharacterController controller = actor.AddComponent<CharacterController>();
            PathFollower follower = actor.AddComponent<PathFollower>();
            follower.StartPlayback(PathPlaybackRequest.Single(path));
            yield return null;
            Assert.That(controller.enabled, Is.False);
            actor.SetActive(false);
            Assert.That(controller.enabled, Is.True);
        }

        [UnityTest]
        public IEnumerator NaturalCompletionRestoresCharacterControllerAndFinalPosition()
        {
            PathData path = CreatePath(0.1f);
            GameObject actor = CreateObject("Actor");
            CharacterController controller = actor.AddComponent<CharacterController>();
            PathFollower follower = actor.AddComponent<PathFollower>();
            follower.StartPlayback(PathPlaybackRequest.Single(path));
            Assert.That(controller.enabled, Is.False);
            yield return new WaitForSeconds(0.25f);
            Assert.That(follower.State, Is.EqualTo(EPathFollowerState.Completed));
            Assert.That(controller.enabled, Is.True);
            Assert.That(Vector3.Distance(actor.transform.position, Vector3.right * 10f),
                Is.LessThan(0.001f));
        }

        [UnityTest]
        public IEnumerator AggregateMultiPathTraversesLastSegmentInActualFrames()
        {
            PathData first = CreatePath(5f);
            PathData second = CreatePath(5f);
            second.ConfigureControlPoints(new[] { Vector3.right * 10f, Vector3.right * 30f });
            MultiPathData route = CreateObject("Aggregate Route").AddComponent<MultiPathData>();
            route.ConfigureSegments(new[] {
                new PathSegmentDescriptor(first, first.MovementSettings, false),
                new PathSegmentDescriptor(second, second.MovementSettings, false)
            });
            GameObject actor = CreateObject("Aggregate Actor");
            CharacterController controller = actor.AddComponent<CharacterController>();
            PathFollower follower = actor.AddComponent<PathFollower>();
            follower.StartPlayback(PathPlaybackRequest.Aggregate(route, PathMovementSettings.Time(1f)));
            Assert.That(controller.enabled, Is.False);
            bool observedLastSegment = false;
            float deadline = Time.realtimeSinceStartup + 5f;
            while (follower.IsMoving && Time.realtimeSinceStartup < deadline)
            {
                float progress = follower.GlobalNormalizedTime;
                if (progress > 0.34f && progress < 0.99f)
                {
                    observedLastSegment = true;
                    Assert.That(actor.transform.position.x, Is.EqualTo(progress * 30f).Within(0.001f));
                }
                yield return null;
            }
            Assert.That(observedLastSegment, Is.True);
            Assert.That(follower.State, Is.EqualTo(EPathFollowerState.Completed));
            Assert.That(actor.transform.position.x, Is.EqualTo(30f).Within(0.001f));
            Assert.That(controller.enabled, Is.True);
        }

        [UnityTest]
        public IEnumerator AggregateRefreshPreservesOverrideAndPausedPoseAcrossActualFrames()
        {
            PathData path = CreatePath(5f);
            GameObject actor = CreateObject("Refreshing Aggregate");
            CharacterController controller = actor.AddComponent<CharacterController>();
            PathFollower follower = actor.AddComponent<PathFollower>();
            follower.StartPlayback(PathPlaybackRequest.Aggregate(path, PathMovementSettings.Time(5f)));
            yield return null;
            ulong id = follower.PlaybackId;
            path.ConfigureControlPoints(new[] { Vector3.zero, Vector3.right * 20f });
            path.ConfigureMovementSettings(PathMovementSettings.Speed(100f));
            yield return null;
            Assert.That(follower.PlaybackId, Is.EqualTo(id));
            Assert.That(follower.Duration, Is.EqualTo(5f));
            Assert.That(actor.transform.position.x, Is.EqualTo(follower.GlobalNormalizedTime * 20f).Within(0.001f));
            follower.PauseMove();
            Vector3 paused = actor.transform.position;
            path.ConfigureControlPoints(new[] { Vector3.zero, Vector3.right * 30f });
            yield return null;
            Assert.That(actor.transform.position, Is.EqualTo(paused));
            follower.ResumeMove();
            yield return null;
            Assert.That(follower.PlaybackId, Is.EqualTo(id));
            Assert.That(actor.transform.position.x, Is.EqualTo(follower.GlobalNormalizedTime * 30f).Within(0.001f));
            follower.StopMove();
            Assert.That(controller.enabled, Is.True);
        }

        [UnityTest]
        public IEnumerator QueueFacadeKeepsIdentityAndSingleNotificationsAcrossEnableCycles()
        {
            PathData path = CreatePath(5f);
            GameObject actor = CreateObject("Queue facade lifecycle");
            CharacterController controller = actor.AddComponent<CharacterController>();
            QueuedPathFollower queue = actor.AddComponent<QueuedPathFollower>();
            int moving = 0;
            queue.StateChanged += state => { if (state == EPathFollowerState.Moving) moving++; };
            PathCommandReceipt first = queue.StartFreePlayback(PathPlaybackRequest.Single(path));
            yield return null;
            Assert.That(queue.Playback, Is.SameAs(queue));
            Assert.That(controller.enabled, Is.False);
            actor.SetActive(false);
            Assert.That(queue.PlaybackId, Is.Zero);
            Assert.That(controller.enabled, Is.True);
            actor.SetActive(true);
            PathCommandReceipt second = queue.StartFreePlayback(PathPlaybackRequest.Single(path));
            yield return null;
            Assert.That(second.PlaybackId, Is.GreaterThan(first.PlaybackId));
            Assert.That(moving, Is.EqualTo(2));
            queue.PauseMove();
            Assert.That(controller.enabled, Is.False);
            queue.ResumeMove();
            Assert.That(moving, Is.EqualTo(3));
            queue.StopMove();
            Assert.That(queue.IsMoving, Is.False);
            Assert.That(controller.enabled, Is.True);
        }

        [UnityTest]
        public IEnumerator PauseCallbackReplacementRetainsRegistrationAndMovesAcrossFrames()
        {
            PathData path = CreatePath(5f);
            var manager = CreateObject("Replacement manager").AddComponent<QueuedPathManager>();
            manager.Init(); manager.ConfigureRoute(path);
            GameObject actor = CreateObject("Replacement actor");
            CharacterController controller = actor.AddComponent<CharacterController>();
            QueuedPathFollower queue = actor.AddComponent<QueuedPathFollower>();
            foreach (bool queuedReplacement in new[] { false, true })
            {
                queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
                ulong oldId = queue.PlaybackId;
                Action<EPathFollowerState> replace = state =>
                {
                    if (state != EPathFollowerState.Paused) return;
                    if (queuedReplacement) queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
                    else queue.StartFreePlayback(PathPlaybackRequest.Single(path));
                };
                queue.StateChanged += replace;
                queue.PauseMove();
                queue.StateChanged -= replace;
                Assert.That(queue.PlaybackId, Is.GreaterThan(oldId));
                Assert.That(queue.IsRegistered, Is.EqualTo(queuedReplacement));
                Assert.That(controller.enabled, Is.False);
                yield return new WaitForSeconds(0.25f);
                Assert.That(queue.State, Is.EqualTo(EPathFollowerState.Moving));
                Assert.That(queue.IsRegistered, Is.EqualTo(queuedReplacement));
                Assert.That(queue.GlobalNormalizedTime, Is.GreaterThan(0f), "queuedReplacement=" + queuedReplacement);
                queue.StopMove();
                Assert.That(manager.AgentCount, Is.Zero);
                Assert.That(controller.enabled, Is.True);
            }
        }

        [UnityTest]
        public IEnumerator StopCallbackReplacementKeepsQueueConstraintAndMovesAcrossFrames()
        {
            return VerifyStopOrReleaseReplacement(false);
        }

        [UnityTest]
        public IEnumerator ReleaseCallbackReplacementKeepsQueueConstraintAndMovesAcrossFrames()
        {
            return VerifyStopOrReleaseReplacement(true);
        }

        private IEnumerator VerifyStopOrReleaseReplacement(bool release)
        {
            PathData path = CreatePath(5f);
            var original = CreateObject("Original manager").AddComponent<QueuedPathManager>();
            original.Init(); original.ConfigureRoute(path);
            var other = CreateObject("Other manager").AddComponent<QueuedPathManager>();
            other.Init(); other.ConfigureRoute(path);
            GameObject actor = CreateObject("Stop replacement actor");
            CharacterController controller = actor.AddComponent<CharacterController>();
            QueuedPathFollower queue = actor.AddComponent<QueuedPathFollower>();
            foreach (string destination in new[] { "Free", "Same", "Other" })
            {
                QueuedPathManager next = destination == "Other" ? other : original;
                queue.StartQueuedPlayback(original, PathPlaybackRequest.Single(path));
                ulong replacementId = 0;
                bool replaced = false;
                Action<EPathFollowerState> replace = state =>
                {
                    if (state != EPathFollowerState.Ready || replaced) return;
                    replaced = true;
                    replacementId = (destination == "Free"
                        ? queue.StartFreePlayback(PathPlaybackRequest.Single(path))
                        : queue.StartQueuedPlayback(next, PathPlaybackRequest.Single(path))).PlaybackId;
                    var effect = ScriptableObject.CreateInstance<PathEventSettingSO>();
                    effect.UseTimeScaleAdjust = true;
                    effect.TimeScaleAdjustValue = 0.75f;
                    effect.TimeScaleAdjustDuration = 0.1f;
                    try { queue.GetComponent<PathEventHandler>().HandleEvent(effect); }
                    finally { Object.Destroy(effect); }
                };
                queue.StateChanged += replace;
                if (release) queue.Release(); else queue.StopMove();
                queue.StateChanged -= replace;
                Assert.That(queue.PlaybackId, Is.EqualTo(replacementId));
                Assert.That(controller.enabled, Is.False);
                if (destination != "Free") queue.ForceBlock();
                yield return new WaitForSecondsRealtime(0.2f);
                Assert.That(Time.timeScale, Is.EqualTo(0.75f).Within(0.001f));
                if (destination != "Free")
                {
                    Assert.That(queue.GlobalNormalizedTime, Is.Zero, "New queue constraint must remain attached.");
                    queue.ForceUnblock();
                }
                yield return new WaitForSeconds(0.3f);
                Assert.That(queue.PlaybackId, Is.EqualTo(replacementId));
                Assert.That(queue.State, Is.EqualTo(EPathFollowerState.Moving));
                Assert.That(queue.IsRegistered, Is.EqualTo(destination != "Free"));
                Assert.That(queue.GlobalNormalizedTime, Is.GreaterThan(0f), destination);
                queue.StopMove();
                Assert.That(original.AgentCount, Is.Zero);
                Assert.That(other.AgentCount, Is.Zero);
                Assert.That(controller.enabled, Is.True);
                Assert.That(Time.timeScale, Is.EqualTo(1f).Within(0.001f));
            }
        }

        [UnityTest]
        public IEnumerator MixedSequenceLoopPauseAndCompletionKeepBoundaryNotifications()
        {
            PathData first = CreatePath(0.1f);
            PathData second = CreatePath(0.1f);
            second.ConfigureControlPoints(new[] { Vector3.right * 10f, Vector3.right * 20f });
            second.ConfigureMovementSettings(PathMovementSettings.Speed(100f));
            var route = CreateObject("Mixed route").AddComponent<MultiPathData>();
            route.ConfigureSegments(new[] {
                new PathSegmentDescriptor(first, first.MovementSettings, false),
                new PathSegmentDescriptor(second, second.MovementSettings, false)
            });
            GameObject actor = CreateObject("Mixed actor");
            CharacterController controller = actor.AddComponent<CharacterController>();
            PathFollower follower = actor.AddComponent<PathFollower>();
            var segments = new List<int>();
            bool pauseOnLoop = true;
            follower.SegmentChanged += index =>
            {
                segments.Add(index);
                if (pauseOnLoop && index == 0) follower.PauseMove();
            };
            int completions = 0;
            follower.Completed += () => completions++;
            ulong id = follower.StartPlayback(PathPlaybackRequest.Sequence(route, true)).PlaybackId;
            float deadline = Time.realtimeSinceStartup + 5f;
            while (follower.State != EPathFollowerState.Paused && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(follower.State, Is.EqualTo(EPathFollowerState.Paused));
            Assert.That(follower.PlaybackId, Is.EqualTo(id));
            CollectionAssert.AreEqual(new[] { 1, 0 }, segments);
            Assert.That(controller.enabled, Is.False);
            Vector3 paused = actor.transform.position;
            yield return null;
            Assert.That(actor.transform.position, Is.EqualTo(paused));
            pauseOnLoop = false;
            follower.StartPlayback(PathPlaybackRequest.Sequence(route));
            deadline = Time.realtimeSinceStartup + 5f;
            while (follower.IsMoving && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(follower.State, Is.EqualTo(EPathFollowerState.Completed));
            Assert.That(completions, Is.EqualTo(1));
            Assert.That(controller.enabled, Is.True);
            Assert.That(actor.transform.position.x, Is.EqualTo(20f).Within(0.001f));
        }

        [UnityTest]
        public IEnumerator QueueFrameRemovalAndReplacementStayDeferredAcrossActualFrames()
        {
            foreach (bool restart in new[] { false, true })
            {
                PathData path = CreatePath(5f);
                var manager = CreateObject("Frame mutation manager").AddComponent<QueuedPathManager>();
                manager.Init(); manager.ConfigureRoute(path);
                var ahead = new FrameAgent(path, 0.8f);
                manager.Register(ahead);
                GameObject actor = CreateObject("Frame mutation actor");
                CharacterController controller = actor.AddComponent<CharacterController>();
                QueuedPathFollower queue = actor.AddComponent<QueuedPathFollower>();
                queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
                PathQueueRegistration oldToken = manager.Register(queue);
                ulong oldId = queue.PlaybackId;
                bool mutated = false;
                bool observed = false;
                bool staleState = false;
                float progressInMutationFrame = -1f;
                ahead.OnApply = () =>
                {
                    ahead.OnApply = null;
                    queue.StopMove();
                    if (restart) queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
                    mutated = true;
                };
                // The observer follows the actor in this frame's stable order.
                var observer = new FrameAgent(path, 0f);
                observer.OnApply = () =>
                {
                    if (observed) return;
                    observed = true;
                    staleState = manager.TryGetState(queue, out _);
                    progressInMutationFrame = queue.GlobalNormalizedTime;
                    // The observer must not become an obstacle for the new
                    // registration when the next frame sorts equal progress.
                    manager.Unregister(observer);
                };
                manager.Register(observer);
                float deadline = Time.realtimeSinceStartup + 5f;
                while (!observed && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.That(mutated && observed, Is.True);
                Assert.That(staleState, Is.False, "The old frame must not cache a result for the detached or replacement playback.");
                Assert.That(progressInMutationFrame, Is.Zero);
                Assert.That(manager.TryGetState(oldToken, out _), Is.False);
                yield return new WaitForSeconds(0.3f);
                Assert.That(queue.IsRegistered, Is.EqualTo(restart));
                Assert.That(controller.enabled, Is.EqualTo(!restart));
                Assert.That(manager.TryGetState(queue, out _), Is.EqualTo(restart));
                if (restart)
                {
                    Assert.That(queue.PlaybackId, Is.GreaterThan(oldId));
                    Assert.That(queue.GlobalNormalizedTime, Is.GreaterThan(0f));
                }
                else Assert.That(queue.PlaybackId, Is.Zero);
                queue.StopMove(); manager.Release();
            }
        }

        [UnityTest]
        public IEnumerator NestedManagerReleaseDetachesActualFollowersAndRestoresControllers()
        {
            PathData path = CreatePath(5f);
            var manager = CreateObject("Nested release manager").AddComponent<QueuedPathManager>();
            manager.Init(); manager.ConfigureRoute(path);
            var queues = new QueuedPathFollower[2];
            var controllers = new CharacterController[2];
            var notifications = new int[2];
            for (int i = 0; i < 2; i++)
            {
                var actor = CreateObject("Nested release actor");
                controllers[i] = actor.AddComponent<CharacterController>();
                queues[i] = actor.AddComponent<QueuedPathFollower>();
                queues[i].StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
                int index = i;
                queues[i].QueueInvalidated += (_, reason) =>
                {
                    Assert.That(reason, Is.EqualTo(EPathQueueDetachReason.ManagerReleased));
                    notifications[index]++;
                    if (index == 0) manager.Release();
                };
            }
            yield return null;
            Assert.DoesNotThrow(() => manager.Release());
            yield return null;
            Assert.That(manager.AgentCount, Is.Zero);
            Assert.That(notifications, Is.EqualTo(new[] { 1, 1 }));
            for (int i = 0; i < 2; i++)
            {
                Assert.That(queues[i].IsRegistered, Is.False);
                Assert.That(queues[i].IsMoving, Is.False);
                Assert.That(controllers[i].enabled, Is.True);
            }
        }

        [UnityTest]
        public IEnumerator CompletionReleaseAndReplacementAreSafeAcrossActualFrames()
        {
            foreach (bool release in new[] { true, false })
            {
                PathData path = CreatePath(0.1f);
                PathData replacement = CreatePath(5f);
                var actor = CreateObject("Completion callback actor");
                var controller = actor.AddComponent<CharacterController>();
                var queue = actor.AddComponent<QueuedPathFollower>();
                queue.Init();
                int completions = 0;
                int staleNotifications = 0;
                queue.Completed += () =>
                {
                    completions++;
                    if (release) queue.Release();
                    else queue.StartFreePlayback(PathPlaybackRequest.Single(replacement));
                };
                queue.OnCompleted += _ => staleNotifications++;
                queue.StartFreePlayback(PathPlaybackRequest.Single(path));
                ulong previousId = queue.PlaybackId;
                float deadline = Time.realtimeSinceStartup + 5f;
                while (completions == 0 && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.That(completions, Is.EqualTo(1));
                Assert.That(staleNotifications, Is.Zero);
                Assert.That(controller.enabled, Is.EqualTo(release));
                Assert.That(queue.IsInitialized, Is.EqualTo(!release));
                if (!release)
                {
                    Assert.That(queue.PlaybackId, Is.GreaterThan(previousId));
                    yield return new WaitForSeconds(0.1f);
                    Assert.That(queue.GlobalNormalizedTime, Is.GreaterThan(0f));
                }
                queue.StopMove();
            }
        }

        [UnityTest]
        public IEnumerator ReplacementCallbackCannotAddAnAgentToReleasedOrChangedManager()
        {
            foreach (bool changeRoute in new[] { false, true })
            {
                PathData path = CreatePath(5f);
                PathData other = CreatePath(5f);
                var manager = CreateObject("Replacement manager").AddComponent<QueuedPathManager>();
                manager.Init(); manager.ConfigureRoute(path);
                var agent = new FrameAgent(path, 0.5f);
                manager.Register(agent);
                agent.OnDetach = () =>
                {
                    if (changeRoute) manager.ConfigureRoute(other);
                    else manager.Release();
                };
                bool observed = false;
                agent.OnApply = () =>
                {
                    agent.OnApply = null;
                    agent.PlaybackId++;
                    Assert.Throws<InvalidOperationException>(() => manager.Register(agent));
                    observed = true;
                };
                float deadline = Time.realtimeSinceStartup + 5f;
                while (!observed && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.That(observed, Is.True);
                yield return null;
                Assert.That(manager.AgentCount, Is.Zero);
                Assert.That(manager.TryGetState(agent, out _), Is.False);
                manager.Release();
            }
        }

        [UnityTest]
        public IEnumerator GeometryRevisionKeepsQueueBlockingAndCanResumeSameRegistration()
        {
            PathData path = CreatePath(10f);
            var manager = CreateObject("Revision manager").AddComponent<QueuedPathManager>();
            manager.ConfigureRoute(path);
            var ahead = new FrameAgent(path, 0.8f);
            manager.Register(ahead);
            var queue = CreateObject("Revision queue").AddComponent<QueuedPathFollower>();
            queue.EnableOvertakeProtection = false;
            queue.EnableGradualSlowdown = false;
            queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            PathQueueRegistration registration = manager.Register(queue);
            yield return new WaitForSeconds(0.25f);
            Assert.That(queue.GlobalNormalizedTime, Is.GreaterThan(0f));
            ahead.GlobalNormalizedTime = queue.GlobalNormalizedTime + 0.01f;
            path.ConfigureControlPoints(new[] { Vector3.zero, Vector3.right * 20f });
            yield return null;
            yield return null;
            float blockedProgress = queue.GlobalNormalizedTime;
            Assert.That(queue.SnapshotRevision, Is.EqualTo(path.Revision));
            Assert.That(queue.IsBlocked, Is.True);
            yield return new WaitForSeconds(0.1f);
            Assert.That(queue.GlobalNormalizedTime, Is.EqualTo(blockedProgress).Within(0.00001f));
            ahead.GlobalNormalizedTime = 0.8f;
            yield return new WaitForSeconds(0.25f);
            Assert.That(queue.GlobalNormalizedTime, Is.GreaterThan(blockedProgress));
            Assert.That(manager.Register(queue).RegistrationId, Is.EqualTo(registration.RegistrationId));
            Assert.That(manager.Unregister(registration), Is.True);
            Assert.That(queue.IsRegistered, Is.False);
        }

        [UnityTest]
        public IEnumerator StartingCallbackPauseStaysDetachedUntilExplicitResume()
        {
            PathData path = CreatePath(5f);
            var manager = CreateObject("Pause manager").AddComponent<QueuedPathManager>();
            manager.ConfigureRoute(path);
            foreach (bool queued in new[] { true, false })
            {
                var queue = CreateObject("Start pause actor").AddComponent<QueuedPathFollower>();
                Action<EPathFollowerState> pause = state => { if (state == EPathFollowerState.Moving) queue.PauseMove(); };
                queue.StateChanged += pause;
                if (queued) queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
                else queue.StartFreePlayback(PathPlaybackRequest.Single(path));
                yield return null;
                Assert.That(queue.State, Is.EqualTo(EPathFollowerState.Paused));
                Assert.That(queue.IsRegistered, Is.False);
                Assert.That(manager.AgentCount, Is.Zero);
                Assert.That(queue.IsBlocked, Is.True);
                Assert.That(queue.CurrentSpeedMultiplier, Is.Zero);
                Assert.That(queue.GlobalNormalizedTime, Is.Zero);
                queue.StateChanged -= pause;
                queue.ResumeMove();
                yield return new WaitForSeconds(0.25f);
                Assert.That(queue.IsRegistered, Is.EqualTo(queued));
                Assert.That(queue.GlobalNormalizedTime, Is.GreaterThan(0f));
                queue.StopMove();
            }
        }

        [UnityTest]
        public IEnumerator ReentrantPauseResumeKeepsAnimatorWithCurrentPlayback()
        {
            PathData path = CreatePath(5f);
            GameObject actor = CreateObject("Animator boundary actor");
            Animator animator = actor.AddComponent<Animator>();
            var view = actor.AddComponent<PathFollowerAnimatorView>();
            typeof(PathFollowerAnimatorView).GetField("_animator", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(view, animator);
            var follower = actor.AddComponent<PathFollower>();
            follower.StartPlayback(PathPlaybackRequest.Single(path));
            Action<EPathFollowerState> restart = state => { if (state == EPathFollowerState.Paused) follower.StartPlayback(PathPlaybackRequest.Single(path)); };
            follower.StateChanged += restart;
            follower.PauseMove();
            yield return null;
            Assert.That(follower.State, Is.EqualTo(EPathFollowerState.Moving));
            Assert.That(animator.speed, Is.EqualTo(1f));
            follower.StateChanged -= restart;
            follower.PauseMove();
            follower.StateChanged += state => { if (state == EPathFollowerState.Moving) follower.PauseMove(); };
            follower.ResumeMove();
            float progress = follower.GlobalNormalizedTime;
            yield return null;
            Assert.That(follower.State, Is.EqualTo(EPathFollowerState.Paused));
            Assert.That(animator.speed, Is.Zero);
            Assert.That(follower.GlobalNormalizedTime, Is.EqualTo(progress));
        }

        [UnityTest]
        public IEnumerator InvalidationReplacementDoesNotReceiveOldNotificationOnFollowingFrames()
        {
            PathData path = CreatePath(5f);
            foreach (bool fromReady in new[] { true, false })
            {
                var manager = CreateObject("Invalidation manager").AddComponent<QueuedPathManager>();
                manager.ConfigureRoute(path);
                var queue = CreateObject("Invalidation actor").AddComponent<QueuedPathFollower>();
                queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
                int staleNotifications = 0;
                if (fromReady)
                    queue.StateChanged += state => { if (state == EPathFollowerState.Ready) queue.StartFreePlayback(PathPlaybackRequest.Single(path)); };
                else
                    queue.QueueInvalidated += (_, __) => queue.StartFreePlayback(PathPlaybackRequest.Single(path));
                queue.QueueInvalidated += (_, __) => staleNotifications++;
                manager.Release();
                yield return null;
                yield return null;
                Assert.That(staleNotifications, Is.Zero);
                Assert.That(queue.State, Is.EqualTo(EPathFollowerState.Moving));
                Assert.That(queue.GlobalNormalizedTime, Is.GreaterThan(0f));
            }
        }

        [UnityTest]
        public IEnumerator DurationEventsKeepAllPlaybackKindsMovingOnRealFrames()
        {
            foreach (string kind in new[] { "Single", "Aggregate", "Sequence" })
            {
                PathData path = CreatePath(2f);
                var setting = ScriptableObject.CreateInstance<PathEventSettingSO>();
                try
                {
                    setting.EventName = "Extend duration";
                    setting.UseModifyPathMoveDuration = true;
                    setting.MoveDurationTargetValue = 20f;
                    typeof(PathData).GetField("_pathEvents", BindingFlags.NonPublic | BindingFlags.Instance)
                        .SetValue(path, new List<PathEventEntry> { new PathEventEntry { NormalizedTime = 0.15f, EventSetting = setting } });
                    path.Rebuild();
                    var follower = CreateObject("Duration boundary " + kind).AddComponent<PathFollower>();
                    bool fired = false;
                    follower.GetComponent<PathEventHandler>().OnEventDispatched += _ => fired = true;
                    PathPlaybackRequest request;
                    if (kind == "Single") request = PathPlaybackRequest.Single(path);
                    else if (kind == "Aggregate") request = PathPlaybackRequest.Aggregate(path, PathMovementSettings.Time(2f));
                    else
                    {
                        var route = CreateObject("Duration sequence").AddComponent<MultiPathData>();
                        route.ConfigureSegments(new[] { new PathSegmentDescriptor(path, path.MovementSettings, false) });
                        request = PathPlaybackRequest.Sequence(route);
                    }
                    follower.StartPlayback(request);
                    float deadline = Time.realtimeSinceStartup + 3f;
                    while (!fired && Time.realtimeSinceStartup < deadline) yield return null;
                    Assert.That(fired, Is.True, kind);
                    Assert.That(follower.Duration, Is.EqualTo(20f));
                    Assert.That(follower.State, Is.EqualTo(EPathFollowerState.Moving), kind);
                    Assert.That(follower.GlobalNormalizedTime, Is.LessThan(0.8f), kind);
                    float before = follower.GlobalNormalizedTime;
                    yield return null;
                    yield return null;
                    Assert.That(follower.State, Is.EqualTo(EPathFollowerState.Moving), kind);
                    Assert.That(follower.GlobalNormalizedTime, Is.GreaterThan(before), kind);
                    follower.StopMove();
                }
                finally { Object.Destroy(setting); }
            }
        }

        [UnityTest]
        public IEnumerator DirectUnderlyingReplacementKeepsQueueHoldAndThenResumes()
        {
            PathData path = CreatePath(5f);
            var manager = CreateObject("Ownership manager").AddComponent<QueuedPathManager>(); manager.ConfigureRoute(path);
            var queue = CreateObject("Ownership actor").AddComponent<QueuedPathFollower>();
            queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path), 0.4f);
            PathQueueRegistration old = manager.Register(queue);
            queue.ForceBlock();
            queue.UnderlyingFollower.StartPlayback(PathPlaybackRequest.Single(path));
            yield return null;
            yield return null;
            PathQueueRegistration current = manager.Register(queue);
            Assert.That(queue.IsBlocked, Is.True);
            Assert.That(queue.GlobalNormalizedTime, Is.Zero);
            Assert.That(current.PlaybackId, Is.EqualTo(queue.PlaybackId));
            Assert.That(current.RegistrationId, Is.Not.EqualTo(old.RegistrationId));
            Assert.That(manager.AgentCount, Is.EqualTo(1));
            Assert.That(manager.Unregister(old), Is.False);
            queue.ForceUnblock();
            yield return new WaitForSeconds(0.25f);
            Assert.That(queue.GlobalNormalizedTime, Is.GreaterThan(0f));
            Assert.That(queue.GlobalNormalizedTime, Is.LessThanOrEqualTo(0.4f));
        }

        [UnityTest]
        public IEnumerator DirectRouteReplacementInvalidatesAndPreservesCallbackChoice()
        {
            foreach (bool chooseFree in new[] { false, true })
            {
                PathData path = CreatePath(5f); PathData other = CreatePath(5f);
                var manager = CreateObject("Route ownership manager").AddComponent<QueuedPathManager>(); manager.ConfigureRoute(path);
                var queue = CreateObject("Route ownership actor").AddComponent<QueuedPathFollower>();
                queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
                int invalidations = 0;
                queue.QueueInvalidated += (_, reason) =>
                {
                    Assert.That(reason, Is.EqualTo(EPathQueueDetachReason.ProviderInvalid)); invalidations++;
                    if (chooseFree) queue.StartFreePlayback(PathPlaybackRequest.Single(other));
                };
                queue.StateChanged += state => Assert.That(state, Is.EqualTo(queue.State));
                queue.UnderlyingFollower.StartPlayback(PathPlaybackRequest.Single(other));
                yield return null;
                yield return null;
                Assert.That(invalidations, Is.EqualTo(1));
                Assert.That(queue.IsRegistered, Is.False);
                Assert.That(manager.AgentCount, Is.Zero);
                Assert.That(queue.State, Is.EqualTo(chooseFree ? EPathFollowerState.Moving : EPathFollowerState.Ready));
                if (chooseFree) Assert.That(queue.GlobalNormalizedTime, Is.GreaterThan(0f));
            }
        }

        [UnityTest]
        public IEnumerator BackwardEventSeekStopsOldDispatchOnRealFrames()
        {
            foreach (string kind in new[] { "Single", "Aggregate", "Sequence" })
            {
                var settings = new List<PathEventSettingSO>();
                try
                {
                    PathData path = CreateEventPath(2f, new[] { 0.4f, 0.41f }, new[] { "A", "B" }, settings);
                    var follower = CreateObject("Event seek " + kind).AddComponent<PathFollower>();
                    var calls = new List<string>(); bool seek = true;
                    follower.GetComponent<PathEventHandler>().OnEventDispatched += name =>
                    {
                        calls.Add(name);
                        if (name == "A" && seek) { seek = false; follower.Seek(0.1f); }
                    };
                    PathPlaybackRequest request;
                    if (kind == "Single") request = PathPlaybackRequest.Single(path);
                    else if (kind == "Aggregate") request = PathPlaybackRequest.Aggregate(path, PathMovementSettings.Time(2f));
                    else
                    {
                        var route = CreateObject("Event seek sequence").AddComponent<MultiPathData>();
                        route.ConfigureSegments(new[] { new PathSegmentDescriptor(path, path.MovementSettings, false) });
                        request = PathPlaybackRequest.Sequence(route);
                    }
                    follower.StartPlayback(request); ulong id = follower.PlaybackId;
                    float deadline = Time.realtimeSinceStartup + 3f;
                    while (calls.Count == 0 && Time.realtimeSinceStartup < deadline) yield return null;
                    CollectionAssert.AreEqual(new[] { "A" }, calls, kind);
                    Assert.That(follower.PlaybackId, Is.EqualTo(id));
                    Assert.That(follower.State, Is.EqualTo(EPathFollowerState.Moving));
                    Assert.That(follower.GlobalNormalizedTime, Is.LessThan(0.4f));
                    float before = follower.GlobalNormalizedTime;
                    yield return null;
                    Assert.That(follower.GlobalNormalizedTime, Is.GreaterThan(before));
                    follower.StopMove();
                }
                finally { foreach (var setting in settings) Object.Destroy(setting); }
            }
        }

        [UnityTest]
        public IEnumerator EventSeekSegmentContinuesWithOnlyTheNewSegmentsEvents()
        {
            var settings = new List<PathEventSettingSO>();
            try
            {
                PathData first = CreateEventPath(2f, new[] { 0.4f, 0.41f }, new[] { "A", "B" }, settings);
                PathData second = CreateEventPath(2f, new[] { 0.4f }, new[] { "C" }, settings);
                var route = CreateObject("Segment event seek route").AddComponent<MultiPathData>();
                route.ConfigureSegments(new[]
                {
                    new PathSegmentDescriptor(first, first.MovementSettings, false),
                    new PathSegmentDescriptor(second, second.MovementSettings, false)
                });
                var follower = CreateObject("Segment event seek actor").AddComponent<PathFollower>();
                var calls = new List<string>(); bool seek = true;
                follower.GetComponent<PathEventHandler>().OnEventDispatched += name =>
                {
                    calls.Add(name);
                    if (name == "A" && seek) { seek = false; follower.SeekSegment(1, 0.1f); }
                };
                follower.StartPlayback(PathPlaybackRequest.Sequence(route)); ulong id = follower.PlaybackId;
                float deadline = Time.realtimeSinceStartup + 3f;
                while (calls.Count < 2 && Time.realtimeSinceStartup < deadline) yield return null;
                CollectionAssert.AreEqual(new[] { "A", "C" }, calls);
                Assert.That(follower.CurrentSegmentIndex, Is.EqualTo(1));
                Assert.That(follower.PlaybackId, Is.EqualTo(id));
            }
            finally { foreach (var setting in settings) Object.Destroy(setting); }
        }

        [UnityTest]
        public IEnumerator SeekSegmentDiscardsCappedFrameBacklogBeforeNextDriverFrame()
        {
            float previousCapture = Time.captureDeltaTime;
            try
            {
                Time.captureDeltaTime = 0.1f;
                PathData first = CreatePath(0.001f); PathData second = CreatePath(0.001f);
                var route = CreateObject("Capped seek route").AddComponent<MultiPathData>();
                route.ConfigureSegments(new[]
                {
                    new PathSegmentDescriptor(first, first.MovementSettings, false),
                    new PathSegmentDescriptor(second, second.MovementSettings, false)
                });
                var follower = CreateObject("Capped seek actor").AddComponent<PathFollower>();
                follower.StartPlayback(PathPlaybackRequest.Sequence(route, true));
                yield return null;
                follower.SeekSegment(0, 0.2f);
                follower.SetDuration(10f);
                yield return null;
                Assert.That(follower.CurrentSegmentIndex, Is.Zero);
                Assert.That(follower.NormalizedTime, Is.EqualTo(0.21f).Within(0.0001f),
                    "The new driver frame must consume 0.1 seconds without the previous frame's transition backlog");
                follower.StopMove();
            }
            finally { Time.captureDeltaTime = previousCapture; }
        }

        private PathData CreateEventPath(float duration, float[] positions, string[] names, List<PathEventSettingSO> settings)
        {
            PathData path = CreatePath(duration); var events = new List<PathEventEntry>();
            for (int i = 0; i < positions.Length; i++)
            {
                var setting = ScriptableObject.CreateInstance<PathEventSettingSO>(); settings.Add(setting); setting.EventName = names[i];
                events.Add(new PathEventEntry { NormalizedTime = positions[i], EventSetting = setting });
            }
            typeof(PathData).GetField("_pathEvents", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(path, events);
            path.Rebuild(); return path;
        }

        [UnityTest]
        public IEnumerator StartingSettingsKeepPoseRotationAndControllerBehaviorOnRealFrames()
        {
            foreach (string kind in new[] { "Single", "Aggregate", "Sequence" })
            {
                PathData path = CreatePath(5f);
                GameObject actor = CreateObject("Initial pose " + kind);
                CharacterController controller = actor.AddComponent<CharacterController>();
                var follower = actor.AddComponent<PathFollower>();
                follower.transform.position = new Vector3(-9f, 2f, 4f);
                follower.transform.rotation = Quaternion.identity;
                follower.StateChanged += state =>
                {
                    if (state != EPathFollowerState.Moving) return;
                    follower.SetDuration(7f); follower.SetRotateToPathDirection(false);
                };
                PathPlaybackRequest request;
                if (kind == "Single") request = PathPlaybackRequest.Single(path);
                else if (kind == "Aggregate") request = PathPlaybackRequest.Aggregate(path, PathMovementSettings.Time(5f));
                else
                {
                    var route = CreateObject("Initial pose sequence").AddComponent<MultiPathData>();
                    route.ConfigureSegments(new[] { new PathSegmentDescriptor(path, path.MovementSettings, false) });
                    request = PathPlaybackRequest.Sequence(route);
                }
                follower.StartPlayback(request);
                Assert.That(Vector3.Distance(follower.transform.position, path.Sample(0f)), Is.LessThan(0.0001f), kind);
                Assert.That(Quaternion.Angle(follower.transform.rotation, Quaternion.identity), Is.LessThan(0.001f));
                Assert.That(controller.enabled, Is.False);
                yield return null;
                Assert.That(follower.GlobalNormalizedTime, Is.GreaterThan(0f));
                follower.StopMove(); Assert.That(controller.enabled, Is.True);
            }
        }

        [UnityTest]
        public IEnumerator ReentrantQueueCommandsCancelOldNotificationsAndLaterFramesResume()
        {
            PathData path = CreatePath(5f);
            var manager = CreateObject("Queue notification manager").AddComponent<QueuedPathManager>(); manager.ConfigureRoute(path);
            var queue = CreateObject("Queue notification actor").AddComponent<QueuedPathFollower>();
            typeof(QueuedPathFollower).GetField("_speedSmoothRate", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(queue, 1f);
            bool once = true; int mutationFrame = -1; int later = 0;
            var speeds = new List<KeyValuePair<int, float>>();
            queue.OnResumed += q => { if (once) { once = false; mutationFrame = Time.frameCount; q.ForceBlock(); } };
            queue.OnResumed += _ => later++;
            queue.OnSpeedChanged += (_, speed) => speeds.Add(new KeyValuePair<int, float>(Time.frameCount, speed));
            queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            ulong id = queue.PlaybackId; ulong revision = queue.StateRevision;
            float deadline = Time.realtimeSinceStartup + 3f;
            while (mutationFrame < 0 && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(mutationFrame, Is.GreaterThanOrEqualTo(0));
            Assert.That(later, Is.Zero); Assert.That(queue.IsBlocked, Is.True);
            foreach (var speed in speeds) Assert.That(speed.Key, Is.Not.EqualTo(mutationFrame), "Old resumed batch must not continue with a speed notification");
            queue.ForceUnblock(); deadline = Time.realtimeSinceStartup + 3f;
            while (later == 0 && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(later, Is.EqualTo(1)); Assert.That(queue.IsBlocked, Is.False);
            Assert.That(queue.PlaybackId, Is.EqualTo(id)); Assert.That(queue.StateRevision, Is.EqualTo(revision));
            Assert.That(queue.GlobalNormalizedTime, Is.GreaterThan(0f));
        }

        [UnityTest]
        public IEnumerator QueueCommandsStopOldCompletionAndInvalidationGroupsOnRealFrames()
        {
            foreach (bool complete in new[] { true, false })
            {
                PathData path = CreatePath(0.2f);
                var manager = CreateObject("Queue group manager").AddComponent<QueuedPathManager>(); manager.ConfigureRoute(path);
                var queue = CreateObject("Queue group actor").AddComponent<QueuedPathFollower>();
                int later = 0; int queueCompleted = 0;
                if (complete)
                {
                    queue.Completed += () => queue.ForceBlock();
                    queue.Completed += () => later++;
                    queue.OnCompleted += _ => queueCompleted++;
                }
                else
                {
                    queue.QueueInvalidated += (q, _) => q.ForceBlock();
                    queue.QueueInvalidated += (_, __) => later++;
                }
                queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
                if (complete)
                {
                    float deadline = Time.realtimeSinceStartup + 4f;
                    while (queue.State != EPathFollowerState.Completed && Time.realtimeSinceStartup < deadline) yield return null;
                    Assert.That(queue.State, Is.EqualTo(EPathFollowerState.Completed));
                }
                else { manager.Release(); yield return null; Assert.That(queue.State, Is.EqualTo(EPathFollowerState.Ready)); }
                Assert.That(later, Is.Zero); Assert.That(queueCompleted, Is.Zero);
                Assert.That(queue.IsBlocked, Is.True); Assert.That(queue.IsRegistered, Is.False);
            }
        }

        private sealed class FrameAgent : IQueuedPathAgent
        {
            public FrameAgent(IPathProvider provider, float progress) { QueueProvider = provider; GlobalNormalizedTime = progress; }
            public ulong PlaybackId { get; set; } = 1;
            public IPathProvider QueueProvider { get; }
            public bool IsMoving => true;
            public float GlobalNormalizedTime { get; set; }
            public int SnapshotRevision => QueueProvider.Revision;
            public PathQueueAgentSettings QueueSettings => new PathQueueAgentSettings(1f, true, true, true);
            public Action OnApply;
            public Action OnDetach;
            public void ApplyQueueState(PathQueueRegistration registration, PathQueueState state) => OnApply?.Invoke();
            public void OnQueueDetached(PathQueueRegistration registration, EPathQueueDetachReason reason) => OnDetach?.Invoke();
        }

        private PathData CreatePath(float duration)
        {
            PathData path = CreateObject("Path").AddComponent<PathData>();
            path.ConfigureMovementSettings(PathMovementSettings.Time(duration));
            path.ConfigureControlPoints(new[] { Vector3.zero, Vector3.right * 10f });
            path.Init();
            return path;
        }

        private GameObject CreateObject(string name)
        {
            var gameObject = new GameObject(name);
            _objects.Add(gameObject);
            return gameObject;
        }

        private static T GetField<T>(object target, string name)
        {
            return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(target);
        }
    }
}
#endif
