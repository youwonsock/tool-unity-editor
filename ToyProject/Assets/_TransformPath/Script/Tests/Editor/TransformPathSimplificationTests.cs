using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Common.TransformPath.Tests
{
    public sealed class TransformPathSimplificationTests
    {
        private readonly List<Object> _objects = new List<Object>();
        private float _timeScale;
        private float _fixedDelta;

        [SetUp]
        public void SetUp()
        {
            _timeScale = Time.timeScale;
            _fixedDelta = Time.fixedDeltaTime;
            Time.timeScale = 1f;
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = _objects.Count - 1; i >= 0; i--)
                if (_objects[i] != null) Object.DestroyImmediate(_objects[i]);
            _objects.Clear();
            PathTimeScaleCoordinator.Shared.Reset();
            Time.timeScale = _timeScale;
            Time.fixedDeltaTime = _fixedDelta;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void UnifiedPlaybackContractKeepsCommandBehavior(bool queued)
        {
            PathData path = CreatePath();
            IPathFollower playback;
            if (queued)
            {
                GameObject parent = CreateObject("Manager");
                var manager = parent.AddComponent<QueuedPathManager>();
                manager.Init(); manager.ConfigureRoute(path);
                GameObject actor = CreateObject("Queue actor");
                actor.transform.SetParent(parent.transform);
                playback = actor.AddComponent<QueuedPathFollower>();
            }
            else playback = CreateObject("Actor").AddComponent<PathFollower>();
            IPathFollower control = playback;
            IPathPlaybackState state = playback;
            control.Init();
            PathCommandReceipt started = control.StartPlayback(PathPlaybackRequest.Single(path));
            Assert.That(started.PlaybackId, Is.GreaterThan(0UL));
            Assert.That(started.OwnerId, Is.GreaterThan(0UL));
            Assert.That(control.SetDuration(2f).OwnerId, Is.EqualTo(started.OwnerId));
            control.Seek(0.25f);
            Assert.That(state.NormalizedTime, Is.EqualTo(0.25f).Within(0.00001f));
            control.PauseMove();
            Assert.That(state.State, Is.EqualTo(EPathFollowerState.Paused));
            control.ResumeMove();
            Assert.That(state.PlaybackId, Is.EqualTo(started.PlaybackId));
            PathCommandReceipt stopped = control.StopMove();
            Assert.That(stopped.OwnerId, Is.EqualTo(started.OwnerId));
            Assert.That(stopped.PlaybackId, Is.Zero);
            Assert.That(stopped.StateRevision, Is.GreaterThan(started.StateRevision));
            control.Release();
            Assert.That(state.State, Is.EqualTo(EPathFollowerState.Uninitialized));
        }

        [Test]
        public void PlaybackIdentityIncreasesAcrossReplacementStopAndRelease()
        {
            PathData path = CreatePath();
            var follower = CreateObject("Actor").AddComponent<PathFollower>();
            follower.Init();
            PathCommandReceipt first = follower.StartPlayback(PathPlaybackRequest.Single(path));
            PathCommandReceipt second = follower.StartPlayback(PathPlaybackRequest.Single(path));
            Assert.That(second.OwnerId, Is.EqualTo(first.OwnerId));
            Assert.That(second.PlaybackId, Is.GreaterThan(first.PlaybackId));
            follower.StopMove(); follower.Release(); follower.Init();
            PathCommandReceipt third = follower.StartPlayback(PathPlaybackRequest.Single(path));
            Assert.That(third.OwnerId, Is.EqualTo(first.OwnerId));
            Assert.That(third.PlaybackId, Is.GreaterThan(second.PlaybackId));
            Assert.That(third.StateRevision, Is.GreaterThan(second.StateRevision));
        }

        [Test]
        public void StoppingOlderFollowerDoesNotReleaseNewerTimeScaleOwner()
        {
            PathData path = CreatePath();
            var first = CreateObject("First").AddComponent<PathFollower>();
            var second = CreateObject("Second").AddComponent<PathFollower>();
            first.Init(); second.Init();
            PathCommandReceipt a = first.StartPlayback(PathPlaybackRequest.Single(path));
            PathCommandReceipt b = second.StartPlayback(PathPlaybackRequest.Single(path));
            Assert.That(a.OwnerId, Is.Not.EqualTo(b.OwnerId));
            AdjustTime(first, 0.25f, 101);
            AdjustTime(second, 0.75f, 102);
            first.StopMove();
            Assert.That(Time.timeScale, Is.EqualTo(0.75f).Within(0.001f));
            second.StopMove();
            Assert.That(Time.timeScale, Is.EqualTo(1f).Within(0.001f));
        }

        [Test]
        public void QueueCallbackSnapshotSurvivesListenerRemoval()
        {
            QueuedPathFollower queue = CreateQueue();
            var calls = new List<string>();
            Action<EPathFollowerState> second = state => calls.Add("second:" + state);
            queue.StateChanged += state =>
            {
                calls.Add("first:" + state);
                if (state == EPathFollowerState.Moving) queue.StateChanged -= second;
            };
            queue.StateChanged += second;
            queue.StartFreePlayback(PathPlaybackRequest.Single(CreatePath()));
            queue.PauseMove();
            CollectionAssert.AreEqual(new[] { "first:Moving", "second:Moving", "first:Paused" }, calls);
        }

        [Test]
        public void QueueCallbackPauseInterruptsOldMovingNotification()
        {
            QueuedPathFollower queue = CreateQueue();
            var calls = new List<EPathFollowerState>();
            queue.StateChanged += state => { if (state == EPathFollowerState.Moving) queue.PauseMove(); };
            queue.StateChanged += state => calls.Add(state);
            queue.StartFreePlayback(PathPlaybackRequest.Single(CreatePath()));
            CollectionAssert.AreEqual(new[] { EPathFollowerState.Paused }, calls);
        }

        [Test]
        public void QueueInitReleaseCyclesDoNotDuplicatePublicNotifications()
        {
            QueuedPathFollower queue = CreateQueue();
            for (int i = 0; i < 3; i++) { queue.Release(); queue.Init(); }
            int moving = 0;
            queue.StateChanged += state => { if (state == EPathFollowerState.Moving) moving++; };
            queue.StartFreePlayback(PathPlaybackRequest.Single(CreatePath()));
            Assert.That(moving, Is.EqualTo(1));
        }

        [Test]
        public void DestroyedUnderlyingFollowerReturnsEmptyState()
        {
            QueuedPathFollower queue = CreateQueue();
            var external = CreateObject("External follower").AddComponent<PathFollower>();
            queue.Release();
            typeof(QueuedPathFollower).GetField("_pathFollower", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(queue, external);
            queue.Init();
            queue.StartFreePlayback(PathPlaybackRequest.Single(CreatePath()));
            Object.DestroyImmediate(external.gameObject);
            Assert.That(queue.CurrentProvider, Is.Null);
            Assert.That(queue.CurrentSequence, Is.Null);
            Assert.That(queue.State, Is.EqualTo(EPathFollowerState.Uninitialized));
            Assert.That(queue.CurrentSegmentIndex, Is.EqualTo(-1));
            Assert.That(queue.SnapshotRevision, Is.EqualTo(-1));
            Assert.That(queue.PlaybackId, Is.Zero);
            Assert.That(queue.Speed, Is.Zero);
            Assert.That(queue.IsMoving, Is.False);
        }

        [Test]
        public void PublicApiAndSerializedFieldsMatchBaseline()
        {
            var actual = new PublicApi { Symbols = BuildPublicApi() };
            string capture = Environment.GetEnvironmentVariable("TRANSFORMPATH_CAPTURE_PUBLIC_API");
            if (!string.IsNullOrEmpty(capture))
            {
                File.WriteAllText(capture, JsonUtility.ToJson(actual, true) + "\n");
                return;
            }
            string path = Path.Combine(Application.dataPath,
                "_TransformPath/Script/Tests/Editor/TransformPathPublicApi.json");
            Assert.That(File.Exists(path), Is.True, "Current public API baseline must be captured before verification.");
            var expected = JsonUtility.FromJson<PublicApi>(File.ReadAllText(path));
            CollectionAssert.AreEqual(expected.Symbols, actual.Symbols);
        }

        private static string[] BuildPublicApi()
        {
            var symbols = new List<string>();
            const BindingFlags declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (Type type in typeof(PathFollower).Assembly.GetExportedTypes())
            {
                string name = type.FullName;
                symbols.Add($"type {name} base={type.BaseType?.FullName} interface={type.IsInterface} sealed={type.IsSealed} abstract={type.IsAbstract}");
                foreach (Type contract in type.GetInterfaces()) symbols.Add($"implements {name} {contract.FullName}");
                foreach (ConstructorInfo constructor in type.GetConstructors(declared))
                    symbols.Add($"ctor {name}({Parameters(constructor.GetParameters())})");
                foreach (MethodInfo method in type.GetMethods(declared))
                    symbols.Add($"method {name}.{method.Name}({Parameters(method.GetParameters())}):{method.ReturnType.FullName} static={method.IsStatic}");
                foreach (PropertyInfo property in type.GetProperties(declared))
                    symbols.Add($"property {name}.{property.Name}:{property.PropertyType.FullName}");
                foreach (EventInfo evt in type.GetEvents(declared)) symbols.Add($"event {name}.{evt.Name}:{evt.EventHandlerType.FullName}");
                foreach (FieldInfo field in type.GetFields(declared))
                    symbols.Add($"field {name}.{field.Name}:{field.FieldType.FullName}" +
                        (field.IsLiteral ? "=" + Convert.ToString(field.GetRawConstantValue(), CultureInfo.InvariantCulture) : ""));
                foreach (FieldInfo field in type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    if (field.IsDefined(typeof(SerializeField), true))
                        symbols.Add($"serialized {name}.{field.Name}:{field.FieldType.FullName}");
            }
            return symbols.OrderBy(symbol => symbol, StringComparer.Ordinal).ToArray();
        }

        private static string Parameters(ParameterInfo[] parameters) => string.Join(",", parameters.Select(p =>
            p.ParameterType.FullName + " " + p.Name + (p.IsOptional ? "=" +
                Convert.ToString(p.DefaultValue, CultureInfo.InvariantCulture) : "")));

        [Serializable]
        private sealed class PublicApi { public string[] Symbols; }

        private void AdjustTime(PathFollower follower, float value, int frame)
        {
            var setting = ScriptableObject.CreateInstance<PathEventSettingSO>();
            _objects.Add(setting);
            setting.UseTimeScaleAdjust = true;
            setting.TimeScaleAdjustValue = value;
            setting.TimeScaleAdjustDuration = 0.1f;
            var handler = follower.GetComponent<PathEventHandler>();
            handler.HandleEvent(setting); handler.Tick(0.2f, 0.2f, frame);
        }

        private QueuedPathFollower CreateQueue()
        {
            var queue = CreateObject("Queue actor").AddComponent<QueuedPathFollower>();
            queue.Init();
            return queue;
        }

        private PathData CreatePath()
        {
            var path = CreateObject("Path").AddComponent<PathData>();
            path.ConfigureBuildSettings(new PathBuildSettings(EPathCurveType.Linear, 10));
            path.ConfigureMovementSettings(PathMovementSettings.Time(5f));
            path.ConfigureControlPoints(new[] { Vector3.zero, Vector3.right * 10f });
            path.Init();
            return path;
        }

        private GameObject CreateObject(string name)
        {
            var go = new GameObject(name); _objects.Add(go); return go;
        }
    }
}
