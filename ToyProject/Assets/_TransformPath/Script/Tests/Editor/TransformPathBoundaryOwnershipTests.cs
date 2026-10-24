using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Common.TransformPath.Tests
{
    public sealed class TransformPathBoundaryOwnershipTests
    {
        private readonly List<GameObject> objects = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            for (int i = objects.Count - 1; i >= 0; i--)
                if (objects[i] != null) Object.DestroyImmediate(objects[i]);
            objects.Clear();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MovingReplacementKeepsManualQueueHold(bool throughUnderlying)
        {
            PathData path = Path();
            var manager = Actor().AddComponent<QueuedPathManager>(); manager.Init(); manager.ConfigureRoute(path);
            var queue = Actor().AddComponent<QueuedPathFollower>(); queue.Init();
            queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            Tick(manager, queue, 1, 0.2f);
            queue.ForceBlock();
            ulong old = queue.PlaybackId;
            if (throughUnderlying) queue.UnderlyingFollower.StartPlayback(PathPlaybackRequest.Single(path));
            else queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            Assert.That(queue.PlaybackId, Is.Not.EqualTo(old));
            Assert.That(queue.IsBlocked, Is.True);
            Tick(manager, queue, 2, 0.2f);
            Assert.That(queue.IsRegistered, Is.True);
            Assert.That(queue.GlobalNormalizedTime, Is.Zero,
                $"Manual queue hold was bypassed; reported blocked={queue.IsBlocked}, registered={queue.IsRegistered}, manager count={manager.AgentCount}");
        }

        [UnityTest]
        public IEnumerator PreviewCacheReleasesDestroyedPaths()
        {
            var cache = (IDictionary)typeof(PathDataScenePreviewCache).GetField("CACHES", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            var keys = new HashSet<object>();
            foreach (object key in cache.Keys) keys.Add(key);
            var added = new List<GameObject>();
            var addedIds = new List<int>();
            try
            {
                for (int i = 0; i < 24; i++)
                {
                    PathData path = Path(); added.Add(path.gameObject);
                    addedIds.Add(path.GetInstanceID());
                    Assert.That(PathDataScenePreviewCache.TryGet(path, out _), Is.True);
                }
                foreach (var obj in added) Object.DestroyImmediate(obj);
                double deadline = EditorApplication.timeSinceStartup + 0.15d;
                while (EditorApplication.timeSinceStartup < deadline) yield return null;
                int retained = 0;
                foreach (int id in addedIds) if (cache.Contains(id)) retained++;
                Assert.That(retained, Is.Zero,
                    $"Destroyed paths retained {retained} sampled-geometry cache entries");
            }
            finally
            {
                var remove = new List<object>();
                foreach (object key in cache.Keys) if (!keys.Contains(key)) remove.Add(key);
                foreach (object key in remove) cache.Remove(key);
            }
        }

        [TestCase("Single", 0.5f, 20f, 0.5f)]
        [TestCase("Aggregate", 0.5f, 20f, 0.5f)]
        [TestCase("Sequence", 0.5f, 20f, 0.5f)]
        [TestCase("Single", 0.5f, 2.5f, 0.5f)]
        [TestCase("Aggregate", 0.5f, 2.5f, 0.5f)]
        [TestCase("Sequence", 0.5f, 2.5f, 0.5f)]
        [TestCase("Single", 0.9f, 2.5f, 1f)]
        [TestCase("Aggregate", 0.9f, 2.5f, 1f)]
        [TestCase("Sequence", 0.9f, 2.5f, 1f)]
        public void DurationEventKeepsBoundaryPolicyConsistent(string kind, float eventPosition, float targetDuration, float tickProgress)
        {
            PathData path = Path();
            var setting = ScriptableObject.CreateInstance<PathEventSettingSO>();
            try
            {
                setting.EventName = "Change duration";
                setting.UseModifyPathMoveDuration = true;
                setting.MoveDurationTargetValue = targetDuration;
                setting.MoveDurationAdjustDuration = 0f;
                typeof(PathData).GetField("_pathEvents", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(path, new List<PathEventEntry> { new PathEventEntry { NormalizedTime = eventPosition, EventSetting = setting } });
                path.Rebuild();
                var follower = Actor().AddComponent<PathFollower>(); follower.Init();
                PathPlaybackRequest request;
                if (kind == "Single") request = PathPlaybackRequest.Single(path);
                else if (kind == "Aggregate") request = PathPlaybackRequest.Aggregate(path, PathMovementSettings.Time(5f));
                else
                {
                    var route = Actor().AddComponent<MultiPathData>();
                    route.ConfigureSegments(new[] { new PathSegmentDescriptor(path, path.MovementSettings, false) });
                    request = PathPlaybackRequest.Sequence(route);
                }
                follower.StartPlayback(request);
                follower.Tick(5f * tickProgress);
                Assert.That(follower.Duration, Is.EqualTo(targetDuration));
                Assert.That(follower.State, Is.EqualTo(tickProgress < 1f ? EPathFollowerState.Moving : EPathFollowerState.Completed),
                    $"Duration event changed boundary decision for {kind}; normalized={follower.NormalizedTime}, target duration={targetDuration}");
                Assert.That(follower.NormalizedTime, Is.EqualTo(tickProgress).Within(0.00001f));
            }
            finally { Object.DestroyImmediate(setting); }
        }

        private void Tick(QueuedPathManager manager, QueuedPathFollower queue, int frame, float delta)
        {
            ((IPathRuntimeTickable)manager).Tick(delta, delta, frame);
            ((IPathRuntimeTickable)queue).Tick(delta, delta, frame);
            queue.UnderlyingFollower.Tick(delta);
        }

        [Test]
        public void DirectReplacementReplacesOnlyOldOwnershipAndKeepsProgressLimit()
        {
            PathData path = Path();
            var manager = Actor().AddComponent<QueuedPathManager>(); manager.Init(); manager.ConfigureRoute(path);
            var queue = Actor().AddComponent<QueuedPathFollower>(); queue.Init();
            queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path), 0.3f);
            PathQueueRegistration old = manager.Register(queue);
            Tick(manager, queue, 1, 0.2f);
            queue.UnderlyingFollower.StartPlayback(PathPlaybackRequest.Single(path));
            Tick(manager, queue, 2, 0.2f);
            PathQueueRegistration current = manager.Register(queue);
            Assert.That(current.PlaybackId, Is.EqualTo(queue.PlaybackId));
            Assert.That(current.RegistrationId, Is.Not.EqualTo(old.RegistrationId));
            Assert.That(manager.AgentCount, Is.EqualTo(1));
            Assert.That(manager.Unregister(old), Is.False);
            Tick(manager, queue, 3, 5f);
            Assert.That(queue.GlobalNormalizedTime, Is.EqualTo(0.3f).Within(0.00001f));
            Assert.That(queue.State, Is.EqualTo(EPathFollowerState.Moving));
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        public void DirectStartInvalidatesMismatchedRouteOrReleasedManager(bool paused, bool released)
        {
            PathData path = Path();
            var manager = Actor().AddComponent<QueuedPathManager>(); manager.Init(); manager.ConfigureRoute(path);
            var queue = Actor().AddComponent<QueuedPathFollower>(); queue.Init();
            queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            if (paused) queue.PauseMove();
            if (released) manager.Release();
            int invalidations = 0;
            EPathQueueDetachReason expected = released ? EPathQueueDetachReason.ManagerReleased : EPathQueueDetachReason.ProviderInvalid;
            queue.QueueInvalidated += (_, reason) => { Assert.That(reason, Is.EqualTo(expected)); invalidations++; };
            queue.StateChanged += state => Assert.That(state, Is.EqualTo(queue.State), "Do not forward the stale Moving notification after rejecting participation");
            queue.UnderlyingFollower.StartPlayback(PathPlaybackRequest.Single(released ? path : Path()));
            Tick(manager, queue, 2, 0.2f);
            Assert.That(queue.State, Is.EqualTo(EPathFollowerState.Ready));
            Assert.That(queue.IsRegistered, Is.False);
            Assert.That(manager.AgentCount, Is.Zero);
            Assert.That(invalidations, Is.EqualTo(1));
        }

        [Test]
        public void DirectStartInvalidationCallbackCanChooseFreePlayback()
        {
            PathData path = Path(); PathData other = Path();
            var manager = Actor().AddComponent<QueuedPathManager>(); manager.Init(); manager.ConfigureRoute(path);
            var queue = Actor().AddComponent<QueuedPathFollower>(); queue.Init();
            queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            int invalidations = 0;
            queue.QueueInvalidated += (_, __) => { invalidations++; queue.StartFreePlayback(PathPlaybackRequest.Single(other)); };
            queue.UnderlyingFollower.StartPlayback(PathPlaybackRequest.Single(other));
            Tick(manager, queue, 2, 0.2f);
            Assert.That(invalidations, Is.EqualTo(1));
            Assert.That(queue.State, Is.EqualTo(EPathFollowerState.Moving));
            Assert.That(queue.IsRegistered, Is.False);
            Assert.That(queue.CurrentProvider, Is.SameAs(other));
            Assert.That(queue.GlobalNormalizedTime, Is.GreaterThan(0f));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void EveryAgentSnapshotIsCheckedWithoutGlobalWaitState(bool staleAfterFirstFrame)
        {
            PathData path = Path();
            var manager = Actor().AddComponent<QueuedPathManager>(); manager.Init(); manager.ConfigureRoute(path);
            var agent = new RevisionAgent(path) { SnapshotRevision = path.Revision };
            if (!staleAfterFirstFrame) agent.SnapshotRevision--;
            manager.Register(agent);
            ((IPathRuntimeTickable)manager).Tick(0.1f, 0.1f, 1);
            if (staleAfterFirstFrame)
            {
                Assert.That(agent.State.IsBlocked, Is.False);
                agent.SnapshotRevision--;
                ((IPathRuntimeTickable)manager).Tick(0.1f, 0.1f, 2);
            }
            Assert.That(agent.State.IsBlocked, Is.True);
            agent.SnapshotRevision = path.Revision;
            ((IPathRuntimeTickable)manager).Tick(0.1f, 0.1f, 3);
            Assert.That(agent.State.IsBlocked, Is.False);
        }

        [UnityTest]
        public IEnumerator ClosingPreviewSceneEvictsItsCacheAndPreservesLiveSceneCache()
        {
            PathData live = Path();
            Assert.That(PathDataScenePreviewCache.TryGet(live, out PathDataScenePreview preview), Is.True);
            Scene previous = SceneManager.GetActiveScene();
            Scene temporary = EditorSceneManager.NewPreviewScene();
            int closedId;
            try
            {
                PathData closed = Path(); closedId = closed.GetInstanceID();
                SceneManager.MoveGameObjectToScene(closed.gameObject, temporary);
                Assert.That(PathDataScenePreviewCache.TryGet(closed, out _), Is.True);
                EditorSceneManager.ClosePreviewScene(temporary);
                double deadline = EditorApplication.timeSinceStartup + 0.15d;
                while (EditorApplication.timeSinceStartup < deadline) yield return null;
                var cache = (IDictionary)typeof(PathDataScenePreviewCache).GetField("CACHES", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                Assert.That(cache.Contains(closedId), Is.False);
                Assert.That(PathDataScenePreviewCache.TryGet(live, out PathDataScenePreview again), Is.True);
                Assert.That(again, Is.SameAs(preview));
                Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(previous));
            }
            finally
            {
                if (temporary.IsValid() && temporary.isLoaded) EditorSceneManager.ClosePreviewScene(temporary);
            }
        }

        private sealed class RevisionAgent : IQueuedPathAgent
        {
            public RevisionAgent(IPathProvider provider) { QueueProvider = provider; }
            public ulong PlaybackId => 1;
            public IPathProvider QueueProvider { get; }
            public bool IsMoving => true;
            public float GlobalNormalizedTime => 0f;
            public int SnapshotRevision { get; set; }
            public PathQueueAgentSettings QueueSettings => new PathQueueAgentSettings(1.5f, true, true, true);
            public PathQueueState State;
            public void ApplyQueueState(PathQueueRegistration registration, PathQueueState state) { State = state; }
            public void OnQueueDetached(PathQueueRegistration registration, EPathQueueDetachReason reason) { }
        }

        private GameObject Actor()
        {
            var obj = new GameObject("Boundary ownership regression"); objects.Add(obj); return obj;
        }

        private PathData Path()
        {
            var path = Actor().AddComponent<PathData>();
            path.ConfigureBuildSettings(new PathBuildSettings(EPathCurveType.Linear, 10));
            path.ConfigureMovementSettings(PathMovementSettings.Time(5f));
            path.ConfigureControlPoints(new[] { Vector3.zero, Vector3.right * 10f }); path.Init(); return path;
        }
    }
}
