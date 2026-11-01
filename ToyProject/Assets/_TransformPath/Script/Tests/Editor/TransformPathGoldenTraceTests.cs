using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Common.TransformPath.Tests
{
    /// <summary>
    /// Golden hashes of what playback does frame by frame: pose, progress, state
    /// and the notifications of each frame. Refactoring steps must keep these
    /// values; a step that intentionally changes them records the new value and
    /// the reason in its commit.
    /// </summary>
    public sealed class TransformPathGoldenTraceTests
    {
        private const float DeltaTime = 0.02f;

        private readonly List<Object> _objects = new List<Object>();
        private readonly List<string> _notifications = new List<string>();
        private PathRuntimeDriver _driver;
        private ulong _hash;
        private int _count;
        private int _frame;

        [SetUp]
        public void SetUp()
        {
            _driver = new PathRuntimeDriver();
            _hash = 14695981039346656037UL;
            _count = 0;
            _frame = 1;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (Object item in _objects)
                if (item != null) Object.DestroyImmediate(item);
            _objects.Clear();
            _notifications.Clear();
        }

        [TestCase("SingleSpeed", "2917:2bdd47bf623dbadd")]
        [TestCase("SingleTimeCurve", "1920:4f59a19b692fd297")]
        [TestCase("SingleLoop", "1412:f6322585788bc1a4")]
        [TestCase("SplineSpeed", "4912:52340c6f24aef267")]
        [TestCase("Aggregate", "1686:f46d886a23a84dce")]
        [TestCase("SequenceMixed", "2288:66452aeb01d00acd")]
        [TestCase("SequenceLoopShort", "2832:ddee91763e017928")]
        [TestCase("Commands", "2450:a5efe30ec0e5361f")]
        [TestCase("PathChange", "2917:02cfd0f1e505d3a8")]
        [TestCase("EventEffects", "5864:c39b13e54045f688")]
        [TestCase("Queue", "25777:6cf004659f94959a")]
        [TestCase("QueueCapAndHold", "11313:61a3fcb63d52621e")]
        [TestCase("QueueSequenceTime", "19956:7bd14b8cfd5d0de1")]
        public void TraceMatches(string scenario, string expected)
        {
            typeof(TransformPathGoldenTraceTests)
                .GetMethod(scenario, BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(this, null);
            Assert.That($"{_count}:{_hash:x16}", Is.EqualTo(expected));
        }

        private void SingleSpeed()
        {
            PathData path = CreatePath(EPathCurveType.Linear, PathMovementSettings.Speed(3f), Line(12f), (0.25f, "a"), (0.5f, "b"), (0.9f, "c"));
            PathFollower follower = CreateFollower();
            follower.StartPlayback(PathPlaybackRequest.Single(path));
            Run(260, follower);
        }

        private void SingleTimeCurve()
        {
            PathData path = CreatePath(EPathCurveType.Linear, new PathMovementSettings(EPathMoveType.TimeBased, 3f, AnimationCurve.EaseInOut(0f, 0f, 1f, 1f)), Line(12f), (0.3f, "a"), (0.7f, "b"));
            PathFollower follower = CreateFollower();
            follower.StartPlayback(PathPlaybackRequest.Single(path));
            Run(170, follower);
        }

        private void SingleLoop()
        {
            PathData path = CreatePath(EPathCurveType.Linear, PathMovementSettings.Speed(10f), Line(3f), (0.5f, "half"));
            PathFollower follower = CreateFollower();
            follower.StartPlayback(PathPlaybackRequest.Single(path, true));
            Run(120, follower);
        }

        private void SplineSpeed()
        {
            var points = new[] { Vector3.zero, new Vector3(4f, 0f, 2f), new Vector3(8f, 3f, 0f), new Vector3(12f, 0f, -4f) };
            PathData bSpline = CreatePath(EPathCurveType.SplineApproximating, PathMovementSettings.Speed(4f), points);
            PathData catmullRom = CreatePath(EPathCurveType.SplineInterpolating, PathMovementSettings.Speed(4f), points);
            PathFollower first = CreateFollower();
            PathFollower second = CreateFollower();
            first.StartPlayback(PathPlaybackRequest.Single(bSpline));
            second.StartPlayback(PathPlaybackRequest.Single(catmullRom));
            Run(220, first, second);
        }

        private void Aggregate()
        {
            MultiPathData route = CreateRoute(
                (CreatePath(EPathCurveType.Linear, PathMovementSettings.Speed(1f), Line(4f, 0f)), PathMovementSettings.Speed(1f), false),
                (CreatePath(EPathCurveType.Linear, PathMovementSettings.Speed(1f), Line(8f, 4f)), PathMovementSettings.Speed(1f), false));
            PathFollower follower = CreateFollower();
            follower.StartPlayback(PathPlaybackRequest.Aggregate(route, PathMovementSettings.Speed(5f)));
            Run(150, follower);
        }

        private void SequenceMixed()
        {
            PathData first = CreatePath(EPathCurveType.Linear, PathMovementSettings.Speed(1f), Line(4f, 0f), (0.5f, "first"));
            PathData second = CreatePath(EPathCurveType.Linear, PathMovementSettings.Speed(1f), Line(6f, 4f), (0.25f, "second"));
            PathData third = CreatePath(EPathCurveType.Linear, PathMovementSettings.Speed(1f), Line(2f, 10f), (0.75f, "third"));
            MultiPathData route = CreateRoute(
                (first, PathMovementSettings.Speed(4f), false),
                (second, new PathMovementSettings(EPathMoveType.TimeBased, 1.5f, AnimationCurve.EaseInOut(0f, 0f, 1f, 1f)), false),
                (third, PathMovementSettings.Speed(2f), true));
            PathFollower follower = CreateFollower();
            follower.StartPlayback(PathPlaybackRequest.Sequence(route));
            Run(200, follower);
        }

        // Faster than one frame per segment, so a frame crosses several of them.
        private void SequenceLoopShort()
        {
            MultiPathData route = CreateRoute(
                (CreatePath(EPathCurveType.Linear, PathMovementSettings.Speed(1f), Line(0.1f, 0f)), PathMovementSettings.Speed(20f), false),
                (CreatePath(EPathCurveType.Linear, PathMovementSettings.Speed(1f), Line(0.1f, 0.1f)), PathMovementSettings.Speed(20f), false),
                (CreatePath(EPathCurveType.Linear, PathMovementSettings.Speed(1f), Line(0.1f, 0.2f)), PathMovementSettings.Speed(20f), false));
            PathFollower follower = CreateFollower();
            follower.StartPlayback(PathPlaybackRequest.Sequence(route, true));
            Run(60, follower);
        }

        private void Commands()
        {
            PathData path = CreatePath(EPathCurveType.Linear, PathMovementSettings.Speed(2f), Line(20f), (0.2f, "a"), (0.6f, "b"));
            PathFollower follower = CreateFollower();
            follower.StartPlayback(PathPlaybackRequest.Single(path));
            Run(40, follower);
            follower.PauseMove();
            Run(10, follower);
            follower.ResumeMove();
            follower.SetSpeed(5f);
            Run(40, follower);
            follower.Seek(0.1f);
            Run(40, follower);
            follower.SetRotateToPathDirection(false);
            follower.Seek(0.8f);
            Run(80, follower);
            follower.StopMove();
            Run(5, follower);
        }

        private void PathChange()
        {
            PathData first = CreatePath(EPathCurveType.Linear, PathMovementSettings.Speed(1f), Line(4f, 0f));
            PathData second = CreatePath(EPathCurveType.Linear, PathMovementSettings.Speed(1f), Line(4f, 4f));
            MultiPathData route = CreateRoute((first, PathMovementSettings.Speed(2f), false), (second, PathMovementSettings.Speed(2f), false));
            PathFollower single = CreateFollower();
            PathFollower sequence = CreateFollower();
            PathData alone = CreatePath(EPathCurveType.Linear, PathMovementSettings.Speed(1f), Line(10f));
            single.StartPlayback(PathPlaybackRequest.Single(alone));
            sequence.StartPlayback(PathPlaybackRequest.Sequence(route));
            Run(30, single, sequence);
            alone.ConfigureControlPoints(new[] { Vector3.zero, new Vector3(5f, 5f, 0f) });
            first.ConfigureControlPoints(new[] { Vector3.zero, new Vector3(3f, 3f, 0f) });
            Run(30, single, sequence);
            single.PauseMove();
            alone.ConfigureControlPoints(Line(20f));
            Run(10, single, sequence);
            single.ResumeMove();
            Run(60, single, sequence);
        }

        // Speed change, Pause with a delayed Resume, and a Duration change.
        private void EventEffects()
        {
            PathEventEntry speedUp = Event(0.2f, "speedUp", setting =>
            {
                setting.UseModifyPathMoveSpeed = true;
                setting.MoveSpeedTargetValue = 6f;
                setting.MoveSpeedAdjustDuration = 0.2f;
            });
            PathEventSettingSO resume = Event(0f, "resume", setting =>
            {
                setting.UseModifyPathMoveSpeed = true;
                setting.MoveSpeedTargetValue = 2f;
            }).EventSetting;
            PathEventEntry pause = Event(0.6f, "pause", setting =>
            {
                setting.UseModifyPathMoveSpeed = true;
                setting.MoveSpeedTargetValue = 0f;
                setting.UseDelayedEvents = true;
                setting.DelayedEvents.Add(new PathEventSettingSO.DelayedEventEntry { Delay = 0.3f, EventSetting = resume });
            });
            PathData path = CreatePath(EPathCurveType.Linear, PathMovementSettings.Speed(3f), Line(12f));
            SetEvents(path, speedUp, pause);
            PathFollower speed = CreateFollower();
            speed.StartPlayback(PathPlaybackRequest.Single(path));

            PathData timed = CreatePath(EPathCurveType.Linear, new PathMovementSettings(EPathMoveType.TimeBased, 2f, AnimationCurve.Linear(0f, 0f, 1f, 1f)), Line(12f));
            SetEvents(timed, Event(0.5f, "longer", setting =>
            {
                setting.UseModifyPathMoveDuration = true;
                setting.MoveDurationTargetValue = 4f;
            }));
            PathFollower time = CreateFollower();
            time.StartPlayback(PathPlaybackRequest.Single(timed));
            Run(260, speed, time);
        }

        private void Queue()
        {
            PathData path = CreatePath(EPathCurveType.Linear, PathMovementSettings.Speed(4f), Line(30f), (0.5f, "half"));
            QueuedPathManager manager = CreateManager(path, 1.5f);
            QueuedPathFollower[] agents = { CreateAgent(), CreateAgent(), CreateAgent(), CreateAgent() };
            foreach (QueuedPathFollower agent in agents)
                agent.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            for (int i = 0; i < agents.Length; i++)
                agents[i].Seek((agents.Length - i) * 2f / 30f);
            Run(120, Followers(agents));
            agents[1].PauseMove();
            Run(60, Followers(agents));
            agents[1].ResumeMove();
            Run(400, Followers(agents));
        }

        private void QueueCapAndHold()
        {
            PathData path = CreatePath(EPathCurveType.Linear, new PathMovementSettings(EPathMoveType.TimeBased, 6f, AnimationCurve.EaseInOut(0f, 0f, 1f, 1f)), Line(20f));
            QueuedPathManager manager = CreateManager(path, 2f);
            QueuedPathFollower ahead = CreateAgent();
            QueuedPathFollower behind = CreateAgent();
            ahead.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path), 0.5f);
            behind.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            Run(200, Followers(ahead, behind));
            behind.ForceBlock();
            Run(30, Followers(ahead, behind));
            behind.ForceUnblock();
            Run(30, Followers(ahead, behind));
            ahead.DetachQueue();
            Run(250, Followers(ahead, behind));
        }

        private void QueueSequenceTime()
        {
            MultiPathData route = CreateRoute(
                (CreatePath(EPathCurveType.Linear, PathMovementSettings.Speed(1f), Line(6f, 0f)), new PathMovementSettings(EPathMoveType.TimeBased, 2f, AnimationCurve.EaseInOut(0f, 0f, 1f, 1f)), false),
                (CreatePath(EPathCurveType.Linear, PathMovementSettings.Speed(1f), Line(6f, 6f)), PathMovementSettings.Speed(3f), true));
            QueuedPathManager manager = CreateManager(route, 1f);
            QueuedPathFollower[] agents = { CreateAgent(), CreateAgent(), CreateAgent() };
            foreach (QueuedPathFollower agent in agents)
                agent.StartQueuedPlayback(manager, PathPlaybackRequest.Sequence(route));
            Run(600, Followers(agents));
        }

        private void Run(int frames, params PathFollower[] followers)
        {
            for (int i = 0; i < frames; i++)
            {
                _driver.Tick(DeltaTime, DeltaTime, _frame++);
                Assert.That(_driver.LastException, Is.Null);
                foreach (PathFollower follower in followers)
                {
                    Vector3 position = follower.transform.position;
                    Quaternion rotation = follower.transform.rotation;
                    Add(position.x); Add(position.y); Add(position.z);
                    Add(rotation.x); Add(rotation.y); Add(rotation.z); Add(rotation.w);
                    Add(follower.NormalizedTime); Add(follower.GlobalNormalizedTime);
                    Add(follower.CurrentSegmentIndex);
                    Add((int)follower.State);
                }
                foreach (string notification in _notifications)
                    foreach (char character in notification)
                        Add(character);
                _notifications.Clear();
            }
        }

        private void Add(float value) => Add((long)Mathf.Round(value * 10000f));

        private void Add(long value)
        {
            for (int shift = 0; shift < 64; shift += 8)
            {
                _hash ^= (byte)(value >> shift);
                _hash *= 1099511628211UL;
            }
            _count++;
        }

        private static PathFollower[] Followers(params QueuedPathFollower[] agents)
            => Array.ConvertAll(agents, agent => agent.UnderlyingFollower);

        private static Vector3[] Line(float length, float start = 0f)
            => new[] { Vector3.right * start, Vector3.right * (start + length) };

        private GameObject CreateObject(string name)
        {
            var item = new GameObject(name);
            _objects.Add(item);
            return item;
        }

        private PathData CreatePath(EPathCurveType curve, PathMovementSettings movement, Vector3[] points, params (float time, string name)[] events)
        {
            var path = CreateObject("Path").AddComponent<PathData>();
            path.ConfigureMovementSettings(movement);
            path.ConfigureBuildSettings(new PathBuildSettings(curve, 50));
            var entries = new PathEventEntry[events.Length];
            for (int i = 0; i < events.Length; i++)
                entries[i] = Event(events[i].time, events[i].name, null);
            SetEvents(path, entries);
            path.ConfigureControlPoints(points);
            path.Init();
            return path;
        }

        private static void SetEvents(PathData path, params PathEventEntry[] events)
        {
            typeof(PathData).GetField("_pathEvents", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(path, new List<PathEventEntry>(events));
            if (path.IsInitialized)
                path.Rebuild();
        }

        private PathEventEntry Event(float time, string name, Action<PathEventSettingSO> configure)
        {
            var setting = ScriptableObject.CreateInstance<PathEventSettingSO>();
            _objects.Add(setting);
            setting.EventName = name;
            configure?.Invoke(setting);
            return new PathEventEntry { NormalizedTime = time, EventSetting = setting };
        }

        private MultiPathData CreateRoute(params (PathData path, PathMovementSettings movement, bool preserve)[] segments)
        {
            var route = CreateObject("Route").AddComponent<MultiPathData>();
            var descriptors = new PathSegmentDescriptor[segments.Length];
            for (int i = 0; i < segments.Length; i++)
                descriptors[i] = new PathSegmentDescriptor(segments[i].path, segments[i].movement, segments[i].preserve);
            route.ConfigureSegments(descriptors);
            return route;
        }

        private PathFollower CreateFollower()
        {
            var follower = CreateObject("Follower").AddComponent<PathFollower>();
            follower.Init();
            Observe(follower);
            return follower;
        }

        private void Observe(PathFollower follower)
        {
            _driver.Register(follower);
            var handler = follower.GetComponent<PathEventHandler>();
            _driver.Register(handler);
            handler.OnEventDispatched += name => _notifications.Add("event:" + name);
            follower.StateChanged += state => _notifications.Add("state:" + state);
            follower.SegmentChanged += index => _notifications.Add("segment:" + index);
            follower.Completed += () => _notifications.Add("completed");
        }

        private QueuedPathManager CreateManager(IPathProvider route, float spacing)
        {
            var manager = CreateObject("Manager").AddComponent<QueuedPathManager>();
            manager.Init();
            manager.DefaultSpacing = spacing;
            manager.ConfigureRoute(route);
            _driver.Register(manager);
            return manager;
        }

        private QueuedPathFollower CreateAgent()
        {
            var agent = CreateObject("Agent").AddComponent<QueuedPathFollower>();
            agent.Init();
            _driver.Register(agent);
            Observe(agent.UnderlyingFollower);
            agent.OnBlocked += _ => _notifications.Add("blocked");
            agent.OnResumed += _ => _notifications.Add("resumed");
            return agent;
        }
    }
}
