using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Common.TransformPath.Tests
{
    /// <summary>
    /// Public listeners run after the operation that raised them has returned,
    /// in order and without nesting, and a failing one changes nothing.
    /// </summary>
    public sealed class TransformPathListenerBoundaryTests
    {
        private readonly List<Object> _objects = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (Object item in _objects)
                if (item != null) Object.DestroyImmediate(item);
            _objects.Clear();
        }

        [Test]
        public void ListenersRunAfterTheTickThatRaisedThem()
        {
            var seen = new List<float>();
            PathFollower follower = CreateFollower();
            follower.GetComponent<PathEventHandler>().OnEventDispatched += _ => seen.Add(follower.NormalizedTime);
            follower.StartPlayback(PathPlaybackRequest.Single(CreatePath(10f, 0.1f, 0.2f)));

            follower.Tick(5f);

            Assert.That(seen, Is.EqualTo(new[] { 0.5f, 0.5f }), "Both listeners saw the finished tick, not the event positions.");
        }

        [Test]
        public void ACommandFromAListenerAppliesAfterTheTick()
        {
            var names = new List<string>();
            PathFollower follower = CreateFollower();
            var handler = follower.GetComponent<PathEventHandler>();
            handler.OnEventDispatched += name =>
            {
                names.Add(name);
                if (name == "t0.1")
                    follower.Seek(0.05f);
            };
            follower.StartPlayback(PathPlaybackRequest.Single(CreatePath(10f, 0.1f, 0.2f)));

            follower.Tick(3f);
            Assert.That(names, Is.EqualTo(new[] { "t0.1", "t0.2" }), "The tick finished its events before the Seek.");
            Assert.That(follower.NormalizedTime, Is.EqualTo(0.05f).Within(0.0001f));

            follower.Tick(1f);
            Assert.That(names, Is.EqualTo(new[] { "t0.1", "t0.2", "t0.1" }), "Passing the event again runs it again.");
        }

        [Test]
        public void EveryListenerHearsWhatHappenedWhateverAnEarlierOneDid()
        {
            PathData path = CreatePath(1f);
            var queue = Create<QueuedPathFollower>("Agent");
            queue.Init();
            int laterCompleted = 0;
            int queueCompleted = 0;
            queue.Completed += () => queue.StartFreePlayback(PathPlaybackRequest.Single(CreatePath(100f)));
            queue.Completed += () => laterCompleted++;
            queue.OnCompleted += _ => queueCompleted++;
            queue.StartFreePlayback(PathPlaybackRequest.Single(path));

            queue.UnderlyingFollower.Tick(5f);

            Assert.That(laterCompleted, Is.EqualTo(1));
            Assert.That(queueCompleted, Is.EqualTo(1));
            Assert.That(queue.State, Is.EqualTo(EPathFollowerState.Moving), "The first listener's new playback stands.");
        }

        [Test]
        public void ListenersDoNotNest()
        {
            var calls = new List<string>();
            PathFollower follower = CreateFollower();
            follower.StateChanged += state =>
            {
                calls.Add("enter:" + state);
                if (state == EPathFollowerState.Moving)
                    follower.PauseMove();
                calls.Add("exit:" + state);
            };
            follower.StartPlayback(PathPlaybackRequest.Single(CreatePath(10f)));

            Assert.That(calls, Is.EqualTo(new[] { "enter:Moving", "exit:Moving", "enter:Paused", "exit:Paused" }));
        }

        [Test]
        public void AFailingListenerIsLoggedAndChangesNothing()
        {
            GameObject actor = CreateObject("Actor");
            CharacterController controller = actor.AddComponent<CharacterController>();
            var follower = actor.AddComponent<PathFollower>();
            Animator animator = actor.AddComponent<Animator>();
            var view = actor.AddComponent<PathFollowerAnimatorView>();
            typeof(PathFollowerAnimatorView).GetField("_animator", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(view, animator);
            follower.Init();
            PathData first = CreatePath(1f);
            PathData second = CreatePath(100f);
            var route = Create<MultiPathData>("Route");
            route.ConfigureSegments(new[]
            {
                new PathSegmentDescriptor(first, first.MovementSettings, false),
                new PathSegmentDescriptor(second, second.MovementSettings, false),
            });
            int later = 0;
            follower.SegmentChanged += _ => throw new InvalidOperationException("Listener failure");
            follower.SegmentChanged += _ => later++;
            follower.StartPlayback(PathPlaybackRequest.Sequence(route));

            LogAssert.Expect(LogType.Exception, new Regex("Listener failure"));
            Assert.DoesNotThrow(() => follower.Tick(2f));

            Assert.That(later, Is.EqualTo(1));
            Assert.That(follower.State, Is.EqualTo(EPathFollowerState.Moving));
            Assert.That(controller.enabled, Is.False);
            Assert.That(animator.speed, Is.EqualTo(1f));
        }

        [Test]
        public void AFailedCommandLeavesListenersWorking()
        {
            int notified = 0;
            PathFollower follower = CreateFollower();
            follower.StateChanged += _ => notified++;
            PathData notReady = Create<PathData>("Path");

            Assert.Throws<InvalidOperationException>(() => follower.StartPlayback(PathPlaybackRequest.Single(notReady)));
            Assert.That(notified, Is.Zero);

            follower.StartPlayback(PathPlaybackRequest.Single(CreatePath(10f)));
            Assert.That(notified, Is.EqualTo(1), "The boundary must not stay inside an operation after an exception.");
        }

        [Test]
        public void AnEventListenerSeesTheEffectOfThatEvent()
        {
            PathData path = CreatePausingPath(out _);
            PathFollower follower = CreateFollower();
            var seen = new List<EPathFollowerState>();
            follower.GetComponent<PathEventHandler>().OnEventDispatched += _ => seen.Add(follower.State);
            follower.StartPlayback(PathPlaybackRequest.Single(path));

            follower.Tick(2f);

            Assert.That(seen, Is.EqualTo(new[] { EPathFollowerState.Paused }));
        }

        [Test]
        public void AManagerFaultDetachesEveryAgentBeforeListenersRun()
        {
            PathData path = CreatePath(100f);
            QueuedPathManager manager = CreateManager(path);
            QueuedPathFollower first = CreateQueue();
            QueuedPathFollower second = CreateQueue();
            first.StateChanged += state =>
            {
                if (state == EPathFollowerState.Ready)
                    manager.Release();
            };
            first.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            second.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            manager.Register(new ScriptedAgent(path) { OnApply = () => throw new InvalidOperationException("Agent failure") });
            var driver = new PathRuntimeDriver();
            driver.Register(manager);

            driver.Tick(0.1f, 0.1f, 1);

            Assert.That(driver.Faults, Has.Count.EqualTo(1));
            Assert.That(first.State, Is.EqualTo(EPathFollowerState.Ready));
            Assert.That(second.State, Is.EqualTo(EPathFollowerState.Ready), "A listener's Release must not cut the detach loop short.");
        }

        [TestCase("Unregister")]
        [TestCase("Register")]
        [TestCase("ConfigureRoute")]
        [TestCase("Release")]
        public void AnAgentCannotChangeTheQueueDuringTheManagerFrame(string command)
        {
            PathData path = CreatePath(100f);
            PathData otherPath = CreatePath(50f);
            QueuedPathManager manager = CreateManager(path);
            var acting = new ScriptedAgent(path) { Progress = 0.5f };
            var behind = new ScriptedAgent(path);
            Exception refused = null;
            acting.OnApply = () => refused = Assert.Catch(() => RunCommand(command, manager, acting, new ScriptedAgent(path), otherPath));
            manager.Register(acting);
            manager.Register(behind);
            var driver = new PathRuntimeDriver();
            driver.Register(manager);

            driver.Tick(0.1f, 0.1f, 1);

            Assert.That(refused, Is.TypeOf<InvalidOperationException>());
            Assert.That(driver.Faults, Is.Empty);
            Assert.That(manager.AgentCount, Is.EqualTo(2));
            Assert.That(behind.Applied, Is.EqualTo(1), "The agent behind still gets its result.");
        }

        [Test]
        public void AnAgentCannotChangeTheQueueWhileBeingDetached()
        {
            PathData path = CreatePath(100f);
            QueuedPathManager manager = CreateManager(path);
            var acting = new ScriptedAgent(path);
            var other = new ScriptedAgent(path);
            Exception refused = null;
            acting.OnDetach = () => refused = Assert.Catch(manager.Release);
            manager.Register(acting);
            manager.Register(other);

            manager.Release();

            Assert.That(refused, Is.TypeOf<InvalidOperationException>());
            Assert.That(other.Detached, Is.EqualTo(1));
        }

        [Test]
        public void AnAgentCanReadTheQueueDuringTheManagerFrame()
        {
            PathData path = CreatePath(100f);
            QueuedPathManager manager = CreateManager(path);
            var agent = new ScriptedAgent(path);
            IQueuedPathAgent first = null;
            bool hasState = false;
            agent.OnApply = () =>
            {
                first = manager.GetAgent(0);
                hasState = manager.TryGetState(agent, out _);
            };
            manager.Register(agent);
            var driver = new PathRuntimeDriver();
            driver.Register(manager);

            driver.Tick(0.1f, 0.1f, 1);

            Assert.That(driver.Faults, Is.Empty);
            Assert.That(first, Is.SameAs(agent));
            Assert.That(hasState, Is.True);
        }

        [Test]
        public void ListenersThatKeepRaisingEachOtherAreStopped()
        {
            PathFollower follower = CreateFollower();
            PathPlaybackRequest request = PathPlaybackRequest.Single(CreatePath(10f));
            int calls = 0;
            Action<EPathFollowerState> listener = null;
            listener = state =>
            {
                // Keeps a build without the limit from hanging the test run.
                if (++calls > 300000)
                {
                    follower.StateChanged -= listener;
                    return;
                }
                if (state == EPathFollowerState.Moving)
                    follower.StopMove();
                else if (state == EPathFollowerState.Ready)
                    follower.StartPlayback(request);
            };
            follower.StateChanged += listener;

            LogAssert.Expect(LogType.Error, new Regex("notifications"));
            follower.StartPlayback(request);

            Assert.That(calls, Is.LessThanOrEqualTo(100000));
            int later = 0;
            follower.StateChanged -= listener;
            follower.StateChanged += _ => later++;
            follower.StopMove();
            follower.StartPlayback(request);
            Assert.That(later, Is.GreaterThan(0), "Notifications work again after the limit.");
        }

        [Test]
        public void AListenerRemovedBeforeItsNotificationIsNotCalled()
        {
            PathFollower follower = CreateFollower();
            int completed = 0;
            Action onCompleted = () => completed++;
            follower.Completed += onCompleted;
            follower.StateChanged += state =>
            {
                if (state == EPathFollowerState.Completed)
                    follower.Completed -= onCompleted;
            };
            follower.StartPlayback(PathPlaybackRequest.Single(CreatePath(1f)));

            follower.Tick(5f);

            Assert.That(follower.State, Is.EqualTo(EPathFollowerState.Completed));
            Assert.That(completed, Is.Zero);
        }

        [Test]
        public void AListenerRemovedByAnEarlierOneOfTheSameEventIsNotCalled()
        {
            PathFollower follower = CreateFollower();
            int later = 0;
            Action second = () => later++;
            follower.Completed += () => follower.Completed -= second;
            follower.Completed += second;
            follower.StartPlayback(PathPlaybackRequest.Single(CreatePath(1f)));

            follower.Tick(5f);

            Assert.That(later, Is.Zero);
        }

        [Test]
        public void AListenerAddedAfterANotificationWasRaisedDoesNotHearIt()
        {
            PathFollower follower = CreateFollower();
            int late = 0;
            int completed = 0;
            follower.Completed += () => completed++;
            follower.StateChanged += state =>
            {
                if (state == EPathFollowerState.Completed)
                    follower.Completed += () => late++;
            };
            follower.StartPlayback(PathPlaybackRequest.Single(CreatePath(1f)));

            follower.Tick(5f);

            Assert.That(completed, Is.EqualTo(1));
            Assert.That(late, Is.Zero);
        }

        [Test]
        public void AFailingPathChangedListenerLeavesTheRouteBuilt()
        {
            PathData child = CreatePath(10f);
            PathData other = CreatePath(10f);
            int later = 0;
            child.PathChanged += () => throw new InvalidOperationException("Path listener failure");
            var route = Create<MultiPathData>("Route");
            route.ConfigureSegments(new[]
            {
                new PathSegmentDescriptor(child, child.MovementSettings, false),
                new PathSegmentDescriptor(other, other.MovementSettings, false),
            });
            child.PathChanged += () => later++;
            int revision = route.Revision;
            PathFollower follower = CreateFollower();
            follower.StartPlayback(PathPlaybackRequest.Sequence(route));

            LogAssert.Expect(LogType.Exception, new Regex("Path listener failure"));
            Assert.DoesNotThrow(() => child.ConfigureControlPoints(new[] { Vector3.zero, Vector3.right * 20f }));

            Assert.That(later, Is.EqualTo(1));
            Assert.That(route.IsReady, Is.True, "The failure must not keep the route from rebuilding.");
            Assert.That(route.Revision, Is.GreaterThan(revision));
            follower.Tick(0.1f);
            Assert.That(follower.State, Is.EqualTo(EPathFollowerState.Moving));
        }

        [Test]
        public void BlockedAndResumedFollowIsBlockedInPairs()
        {
            PathData path = CreatePath(100f);
            QueuedPathManager manager = CreateManager(path);
            QueuedPathFollower queue = CreateQueue();
            var heard = new List<string>();
            queue.OnBlocked += _ => heard.Add("blocked");
            queue.OnResumed += _ => heard.Add("resumed");
            var driver = new PathRuntimeDriver();
            driver.Register(manager);
            driver.Register(queue);
            driver.Register(queue.UnderlyingFollower);
            int frame = 1;
            void Run(int frames)
            {
                for (int i = 0; i < frames; i++)
                    driver.Tick(0.02f, 0.02f, frame++);
            }

            queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            Run(10);
            Assert.That(queue.IsBlocked, Is.False);
            Assert.That(heard, Is.EqualTo(new[] { "blocked", "resumed" }), "Waiting for the first result counts as blocked.");

            heard.Clear();
            queue.ForceBlock();
            Run(1);
            Assert.That(heard, Is.EqualTo(new[] { "blocked" }));

            queue.ForceUnblock();
            Run(10);
            Assert.That(queue.IsBlocked, Is.False);
            Assert.That(heard, Is.EqualTo(new[] { "blocked", "resumed" }));
        }

        [Test]
        public void AHoldDoesNotFreezeAPlaybackOutsideTheQueue()
        {
            QueuedPathFollower queue = CreateQueue();
            queue.StartFreePlayback(PathPlaybackRequest.Single(CreatePath(100f)));

            queue.ForceBlock();

            Assert.That(queue.IsBlocked, Is.False);
            Assert.That(queue.IsActuallyMoving, Is.True);
            Assert.That(queue.CurrentSpeedMultiplier, Is.EqualTo(1f));
            Assert.That(queue.IsQueueHeld, Is.False);
            queue.UnderlyingFollower.Tick(1f);
            Assert.That(queue.GlobalNormalizedTime, Is.GreaterThan(0f));
            queue.ForceUnblock();
            Assert.That(queue.IsBlocked, Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AHoldMadeOutsideTheQueueAppliesToTheNextQueuedStart(bool whileMoving)
        {
            PathData path = CreatePath(100f);
            QueuedPathManager manager = CreateManager(path);
            QueuedPathFollower queue = CreateQueue();
            var driver = new PathRuntimeDriver();
            driver.Register(manager);
            driver.Register(queue);
            driver.Register(queue.UnderlyingFollower);
            if (whileMoving)
                queue.StartFreePlayback(PathPlaybackRequest.Single(path));

            queue.ForceBlock();
            Assert.That(queue.IsBlocked, Is.False);
            queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            for (int frame = 1; frame <= 10; frame++)
                driver.Tick(0.02f, 0.02f, frame);

            Assert.That(queue.IsQueueHeld, Is.True);
            Assert.That(queue.GlobalNormalizedTime, Is.Zero);
            queue.ForceUnblock();
            for (int frame = 11; frame <= 40; frame++)
                driver.Tick(0.02f, 0.02f, frame);
            Assert.That(queue.GlobalNormalizedTime, Is.GreaterThan(0f));
        }

        [Test]
        public void AStopWhileBlockedDoesNotCarryOverToTheNextPlayback()
        {
            PathData path = CreatePath(100f);
            QueuedPathManager manager = CreateManager(path);
            QueuedPathFollower queue = CreateQueue();
            var heard = new List<string>();
            queue.OnBlocked += _ => heard.Add("blocked");
            queue.OnResumed += _ => heard.Add("resumed");
            var driver = new PathRuntimeDriver();
            driver.Register(manager);
            driver.Register(queue);
            driver.Register(queue.UnderlyingFollower);

            queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            driver.Tick(0.01f, 0.01f, 1);
            Assert.That(heard, Is.EqualTo(new[] { "blocked" }));
            queue.StopMove();

            heard.Clear();
            queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            driver.Tick(0.01f, 0.01f, 2);
            Assert.That(heard, Is.EqualTo(new[] { "blocked" }), "The new playback reports its own wait.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ReleasingAQueueFollowerTellsItsListeners(bool paused)
        {
            QueuedPathFollower queue = CreateQueue();
            queue.StartFreePlayback(PathPlaybackRequest.Single(CreatePath(100f)));
            if (paused)
                queue.PauseMove();
            var states = new List<EPathFollowerState>();
            queue.StateChanged += states.Add;

            queue.Release();

            Assert.That(states, Is.EqualTo(new[] { EPathFollowerState.Ready, EPathFollowerState.Uninitialized }));
        }

        [Test]
        public void ReleasingARegisteredQueueFollowerLeavesTheManager()
        {
            PathData path = CreatePath(100f);
            QueuedPathManager manager = CreateManager(path);
            QueuedPathFollower queue = CreateQueue();
            queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            var states = new List<EPathFollowerState>();
            queue.StateChanged += states.Add;

            queue.Release();

            Assert.That(manager.AgentCount, Is.Zero);
            Assert.That(queue.IsRegistered, Is.False);
            Assert.That(states, Is.EqualTo(new[] { EPathFollowerState.Ready, EPathFollowerState.Uninitialized }));
        }

        [Test]
        public void APauseEventLeavesTheFollowerWhereItReportsToBe()
        {
            PathData path = CreatePausingPath(out _);
            PathFollower follower = CreateFollower();
            float heardAt = -1f;
            follower.GetComponent<PathEventHandler>().OnEventDispatched += _ => heardAt = follower.transform.position.x;
            follower.StartPlayback(PathPlaybackRequest.Single(path));

            follower.Tick(2f);

            Assert.That(follower.State, Is.EqualTo(EPathFollowerState.Paused));
            Assert.That(follower.NormalizedTime, Is.EqualTo(0.2f).Within(0.0001f));
            Assert.That(follower.transform.position.x, Is.EqualTo(2f).Within(0.001f));
            Assert.That(heardAt, Is.EqualTo(2f).Within(0.001f), "The listener sees the same position.");
        }

        // Each test below fails if the named entry point stops opening an operation.

        [TestCase("Pause")]
        [TestCase("Resume")]
        [TestCase("Stop")]
        [TestCase("Init")]
        public void AFollowerListenerHearsAStateAQueueListenerMovesOnFrom(string command)
        {
            QueuedPathFollower queue = CreateQueue();
            PathFollower follower = queue.UnderlyingFollower;
            PathPlaybackRequest request = PathPlaybackRequest.Single(CreatePath(100f));
            EPathFollowerState target = EPathFollowerState.Ready;
            Action run = () => follower.StopMove();
            Action moveOn = () => queue.StartFreePlayback(request);
            switch (command)
            {
                case "Pause":
                    queue.StartFreePlayback(request);
                    target = EPathFollowerState.Paused;
                    run = () => queue.PauseMove();
                    moveOn = () => queue.ResumeMove();
                    break;
                case "Resume":
                    queue.StartFreePlayback(request);
                    queue.PauseMove();
                    target = EPathFollowerState.Moving;
                    run = () => queue.ResumeMove();
                    moveOn = () => queue.PauseMove();
                    break;
                case "Stop":
                    queue.StartFreePlayback(request);
                    break;
                case "Init":
                    follower.Release();
                    run = () => follower.Init();
                    break;
            }
            var heard = new List<EPathFollowerState>();
            follower.StateChanged += heard.Add;
            bool moved = false;
            queue.StateChanged += state =>
            {
                if (state == target && !moved)
                {
                    moved = true;
                    moveOn();
                }
            };

            run();

            Assert.That(heard, Does.Contain(target));
        }

        [Test]
        public void AListenerSeesAFollowerReleaseFinished()
        {
            PathFollower follower = CreateFollower();
            follower.StartPlayback(PathPlaybackRequest.Single(CreatePath(100f)));
            bool? initializedWhenHeard = null;
            follower.StateChanged += state =>
            {
                if (state == EPathFollowerState.Ready)
                    initializedWhenHeard = follower.IsInitialized;
            };

            follower.Release();

            Assert.That(initializedWhenHeard, Is.False);
        }

        [Test]
        public void AListenerSeesAQueueInitFinished()
        {
            var queue = Create<QueuedPathFollower>("Agent");
            bool? initializedWhenHeard = null;
            queue.StateChanged += _ => initializedWhenHeard = queue.IsInitialized;

            queue.Init();

            Assert.That(initializedWhenHeard, Is.True);
        }

        [Test]
        public void AListenerSeesAFreeStartThatHadToInitFirst()
        {
            var queue = Create<QueuedPathFollower>("Agent");
            var states = new List<EPathFollowerState>();
            queue.StateChanged += _ => states.Add(queue.State);

            queue.StartFreePlayback(PathPlaybackRequest.Single(CreatePath(100f)));

            Assert.That(states[0], Is.EqualTo(EPathFollowerState.Moving));
        }

        [Test]
        public void AListenerCanDetachAQueuedStart()
        {
            PathData path = CreatePath(100f);
            QueuedPathManager manager = CreateManager(path);
            QueuedPathFollower queue = CreateQueue();
            queue.StateChanged += state =>
            {
                if (state == EPathFollowerState.Moving)
                    queue.DetachQueue();
            };

            Assert.DoesNotThrow(() => queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path)));

            Assert.That(queue.IsRegistered, Is.False);
            Assert.That(queue.State, Is.EqualTo(EPathFollowerState.Moving));
        }

        [Test]
        public void AQueueTickRaisesItsNotificationsTogether()
        {
            PathData path = CreatePath(100f);
            QueuedPathManager manager = CreateManager(path);
            QueuedPathFollower queue = CreateQueue();
            var speeds = new List<float>();
            queue.OnBlocked += _ => queue.StopMove();
            queue.OnSpeedChanged += (_, speed) => speeds.Add(speed);
            queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            var driver = new PathRuntimeDriver();
            driver.Register(manager);
            driver.Register(queue);

            driver.Tick(0.01f, 0.01f, 1);

            Assert.That(queue.State, Is.EqualTo(EPathFollowerState.Ready));
            Assert.That(speeds, Has.Count.EqualTo(1), "The stop came after the tick had raised both.");
        }

        [Test]
        public void AManagerReleaseDetachesEveryAgentBeforeListenersRun()
        {
            PathData path = CreatePath(100f);
            QueuedPathManager manager = CreateManager(path);
            QueuedPathFollower first = StartQueued(manager, path);
            QueuedPathFollower second = StartQueued(manager, path);
            first.StateChanged += state =>
            {
                if (state == EPathFollowerState.Ready)
                    manager.Release();
            };

            manager.Release();

            Assert.That(first.State, Is.EqualTo(EPathFollowerState.Ready));
            Assert.That(second.State, Is.EqualTo(EPathFollowerState.Ready));
        }

        [Test]
        public void ARouteChangeDetachesEveryAgentBeforeListenersRun()
        {
            PathData path = CreatePath(100f);
            QueuedPathManager manager = CreateManager(path);
            QueuedPathFollower first = StartQueued(manager, path);
            QueuedPathFollower second = StartQueued(manager, path);
            first.StateChanged += state =>
            {
                if (state == EPathFollowerState.Ready)
                    manager.Release();
            };

            manager.ConfigureRoute(CreatePath(50f));

            Assert.That(second.State, Is.EqualTo(EPathFollowerState.Ready));
            Assert.That(manager.RouteProvider, Is.Null, "The listener's Release came after the new route.");
        }

        [Test]
        public void AManagerFrameDetachesEveryAgentBeforeListenersRun()
        {
            PathData path = CreatePath(100f);
            QueuedPathManager manager = CreateManager(path);
            QueuedPathFollower first = StartQueued(manager, path);
            QueuedPathFollower second = StartQueued(manager, path);
            first.StateChanged += state =>
            {
                if (state == EPathFollowerState.Ready)
                    manager.Release();
            };
            var driver = new PathRuntimeDriver();
            driver.Register(manager);
            path.Release();

            driver.Tick(0.01f, 0.01f, 1);

            Assert.That(driver.Faults, Is.Empty);
            Assert.That(second.State, Is.EqualTo(EPathFollowerState.Ready));
        }

        [Test]
        public void ARegisterThatSeesANewRouteDetachesEveryAgentBeforeListenersRun()
        {
            PathData firstPath = CreatePath(50f);
            PathData secondPath = CreatePath(50f);
            var route = Create<MultiPathData>("Route");
            route.ConfigureSegments(new[]
            {
                new PathSegmentDescriptor(firstPath, firstPath.MovementSettings, false),
                new PathSegmentDescriptor(secondPath, secondPath.MovementSettings, false),
            });
            var manager = Create<QueuedPathManager>("Manager");
            manager.Init();
            manager.ConfigureRoute(route);
            QueuedPathFollower first = CreateQueue();
            QueuedPathFollower second = CreateQueue();
            first.StartQueuedPlayback(manager, PathPlaybackRequest.Sequence(route));
            second.StartQueuedPlayback(manager, PathPlaybackRequest.Sequence(route));
            first.StateChanged += state =>
            {
                if (state == EPathFollowerState.Ready)
                    manager.Release();
            };
            route.ConfigureSegments(new[]
            {
                new PathSegmentDescriptor(secondPath, secondPath.MovementSettings, false),
                new PathSegmentDescriptor(firstPath, firstPath.MovementSettings, false),
            });
            var newcomer = new ScriptedAgent(route);

            manager.Register(newcomer);

            Assert.That(second.State, Is.EqualTo(EPathFollowerState.Ready));
            Assert.That(newcomer.Detached, Is.EqualTo(1), "The listener's Release came after the registration.");
        }

        [Test]
        public void AListenerCanRegisterAgainAfterAnUnregister()
        {
            PathData path = CreatePath(100f);
            QueuedPathManager manager = CreateManager(path);
            QueuedPathFollower queue = StartQueued(manager, path);
            PathPlaybackRequest request = PathPlaybackRequest.Single(path);
            queue.StateChanged += state =>
            {
                if (state == EPathFollowerState.Ready)
                    queue.StartQueuedPlayback(manager, request);
            };

            manager.Unregister(queue);

            Assert.That(queue.IsRegistered, Is.True);
            Assert.That(manager.AgentCount, Is.EqualTo(1));
        }

        [Test]
        public void AListenerSeesTheEffectOfADelayedEvent()
        {
            PathData path = CreatePausingPath(out PathEventSettingSO pause);
            pause.DelayedEvents[0].EventSetting.EventName = "resume";
            path.Rebuild();
            PathFollower follower = CreateFollower();
            var handler = follower.GetComponent<PathEventHandler>();
            var seen = new List<EPathFollowerState>();
            handler.OnEventDispatched += name =>
            {
                if (name == "resume")
                    seen.Add(follower.State);
            };
            follower.StartPlayback(PathPlaybackRequest.Single(path));
            follower.Tick(2f);

            handler.Tick(1.5f, 1.5f, Time.frameCount + 1);

            Assert.That(seen, Is.EqualTo(new[] { EPathFollowerState.Moving }));
        }

        [Test]
        public void AListenerSeesTheEffectOfAManualEvent()
        {
            CreatePausingPath(out PathEventSettingSO pause);
            PathFollower follower = CreateFollower();
            var handler = follower.GetComponent<PathEventHandler>();
            var seen = new List<EPathFollowerState>();
            handler.OnEventDispatched += _ => seen.Add(follower.State);
            follower.StartPlayback(PathPlaybackRequest.Single(CreatePath(100f)));

            handler.HandleEvent(pause);

            Assert.That(seen, Is.EqualTo(new[] { EPathFollowerState.Paused }));
        }

        private QueuedPathFollower StartQueued(QueuedPathManager manager, PathData path)
        {
            QueuedPathFollower queue = CreateQueue();
            queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            return queue;
        }

        private PathData CreatePausingPath(out PathEventSettingSO pause)
        {
            var resume = ScriptableObject.CreateInstance<PathEventSettingSO>();
            _objects.Add(resume);
            resume.UseModifyPathMoveSpeed = true;
            resume.MoveSpeedTargetValue = 2f;
            pause = ScriptableObject.CreateInstance<PathEventSettingSO>();
            _objects.Add(pause);
            pause.EventName = "pause";
            pause.UseModifyPathMoveSpeed = true;
            pause.MoveSpeedTargetValue = 0f;
            pause.UseDelayedEvents = true;
            pause.DelayedEvents.Add(new PathEventSettingSO.DelayedEventEntry { Delay = 1f, EventSetting = resume });
            PathData path = CreatePath(10f);
            typeof(PathData).GetField("_pathEvents", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(path, new List<PathEventEntry> { new PathEventEntry { NormalizedTime = 0.1f, EventSetting = pause } });
            path.Rebuild();
            return path;
        }

        private static void RunCommand(
            string command,
            QueuedPathManager manager,
            IQueuedPathAgent acting,
            IQueuedPathAgent newcomer,
            PathData otherPath)
        {
            switch (command)
            {
                case "Unregister": manager.Unregister(acting); break;
                case "Register": manager.Register(newcomer); break;
                case "ConfigureRoute": manager.ConfigureRoute(otherPath); break;
                case "Release": manager.Release(); break;
            }
        }

        private sealed class ScriptedAgent : IQueuedPathAgent
        {
            public ScriptedAgent(IPathProvider provider) { QueueProvider = provider; }
            public Action OnApply;
            public Action OnDetach;
            public int Applied;
            public int Detached;
            public float Progress;
            public ulong PlaybackId => 1UL;
            public IPathProvider QueueProvider { get; }
            public bool IsMoving => true;
            public float GlobalNormalizedTime => Progress;
            public int SnapshotRevision => QueueProvider.Revision;
            public PathQueueAgentSettings QueueSettings => new PathQueueAgentSettings(1.5f, true, true, true);
            public void ApplyQueueState(PathQueueRegistration registration, PathQueueState state) { Applied++; OnApply?.Invoke(); }
            public void OnQueueDetached(PathQueueRegistration registration, EPathQueueDetachReason reason) { Detached++; OnDetach?.Invoke(); }
        }

        private QueuedPathManager CreateManager(PathData path)
        {
            var manager = Create<QueuedPathManager>("Manager");
            manager.Init();
            manager.ConfigureRoute(path);
            return manager;
        }

        private QueuedPathFollower CreateQueue()
        {
            var queue = Create<QueuedPathFollower>("Agent");
            queue.Init();
            return queue;
        }

        private GameObject CreateObject(string name)
        {
            var item = new GameObject(name);
            _objects.Add(item);
            return item;
        }

        private T Create<T>(string name) where T : Component => CreateObject(name).AddComponent<T>();

        private PathFollower CreateFollower()
        {
            var follower = Create<PathFollower>("Follower");
            follower.Init();
            return follower;
        }

        private PathData CreatePath(float length, params float[] events)
        {
            var path = Create<PathData>("Path");
            path.ConfigureMovementSettings(PathMovementSettings.Speed(1f));
            path.ConfigureBuildSettings(new PathBuildSettings(EPathCurveType.Linear, 10));
            var entries = new List<PathEventEntry>();
            foreach (float time in events)
            {
                var setting = ScriptableObject.CreateInstance<PathEventSettingSO>();
                _objects.Add(setting);
                setting.EventName = "t" + time;
                entries.Add(new PathEventEntry { NormalizedTime = time, EventSetting = setting });
            }
            typeof(PathData).GetField("_pathEvents", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(path, entries);
            path.ConfigureControlPoints(new[] { Vector3.zero, Vector3.right * length });
            path.Init();
            return path;
        }
    }
}
