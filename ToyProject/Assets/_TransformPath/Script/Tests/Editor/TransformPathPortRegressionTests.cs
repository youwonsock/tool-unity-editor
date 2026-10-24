using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Serialization;
using Object = UnityEngine.Object;

namespace Common.TransformPath.Tests
{
    public sealed class TransformPathPortRegressionTests
    {
        private readonly List<Object> _objects = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            for (int i = _objects.Count - 1; i >= 0; i--)
                if (_objects[i] != null)
                    Object.DestroyImmediate(_objects[i]);
            _objects.Clear();
        }

        [Test]
        public void ReversedRegistrationStillAppliesFirstQueueResultBeforeMovement()
        {
            PathData path = CreatePath(PathMovementSettings.Time(5f));
            QueuedPathManager manager = CreateManager(path);
            QueuedPathFollower queued = CreateQueuedFollower();
            SetField(queued, "_resumeMovementDelay", 0f);
            SetField(queued, "_speedSmoothRate", 0f);
            queued.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));

            var driver = new PathRuntimeDriver();
            driver.Register(queued.UnderlyingFollower);
            driver.Register(queued);
            driver.Register(manager);
            driver.Tick(0.2f, 0.2f, 0);

            Assert.That(driver.LastException, Is.Null);
            Assert.That(queued.GlobalNormalizedTime, Is.GreaterThan(0f));
            Assert.That(queued.IsRegistered, Is.True);
        }

        [TestCase(EPathMoveType.TimeBased, false, false)]
        [TestCase(EPathMoveType.TimeBased, true, false)]
        [TestCase(EPathMoveType.SpeedBased, false, false)]
        [TestCase(EPathMoveType.SpeedBased, true, false)]
        [TestCase(EPathMoveType.TimeBased, false, true)]
        [TestCase(EPathMoveType.TimeBased, true, true)]
        public void ProgressCapPreventsPrematureCompletionAndLoop(
            EPathMoveType mode, bool sequence, bool loop)
        {
            PathMovementSettings movement = mode == EPathMoveType.TimeBased
                ? PathMovementSettings.Time(0.001f)
                : PathMovementSettings.Speed(100f);
            PathData path = CreatePath(movement, Vector3.right * 0.001f);
            PathFollower follower = CreateFollower();
            PathPlaybackRequest request = sequence
                ? PathPlaybackRequest.Sequence(CreateSequence(path), loop)
                : PathPlaybackRequest.Single(path, loop);
            int completed = 0;
            follower.Completed += () => completed++;
            follower.StartPlayback(request);
            var token = new PathQueueRegistration(new object(), 1, follower.PlaybackId, follower.SnapshotRevision);
            Assert.That(follower.AttachQueueConstraint(token), Is.True);
            follower.ApplyQueueConstraint(token,
                new PathQueueConstraint(token, false, 1f, 0.999f, 0, token.RouteRevision));

            follower.Tick(0.02f);
            Assert.That(follower.State, Is.EqualTo(EPathFollowerState.Moving));
            Assert.That(follower.GlobalNormalizedTime, Is.EqualTo(0.999f).Within(0.000001f));
            Assert.That(completed, Is.Zero);

            if (loop)
                return;
            follower.ApplyQueueConstraint(token,
                new PathQueueConstraint(token, false, 1f, 1f, 1, token.RouteRevision));
            follower.Tick(0.02f);
            Assert.That(follower.State, Is.EqualTo(EPathFollowerState.Completed));
            Assert.That(completed, Is.EqualTo(1));
        }

        [Test]
        public void SequenceCannotCrossCappedBoundaryAndFinishesAfterCapIsReleased()
        {
            PathData first = CreatePath(PathMovementSettings.Time(0.001f));
            PathData second = CreatePath(PathMovementSettings.Time(0.001f));
            PathFollower follower = CreateFollower();
            follower.StartPlayback(PathPlaybackRequest.Sequence(CreateSequence(first, second)));
            var token = new PathQueueRegistration(new object(), 1, follower.PlaybackId, follower.SnapshotRevision);
            follower.AttachQueueConstraint(token);
            follower.ApplyQueueConstraint(token,
                new PathQueueConstraint(token, false, 1f, 0.4999f, 0, token.RouteRevision));
            follower.Tick(0.02f);

            Assert.That(follower.CurrentSegmentIndex, Is.Zero);
            Assert.That(follower.GlobalNormalizedTime, Is.EqualTo(0.4999f).Within(0.000001f));
            follower.ApplyQueueConstraint(token,
                new PathQueueConstraint(token, false, 1f, 1f, 1, token.RouteRevision));
            follower.Tick(0.02f);
            Assert.That(follower.State, Is.EqualTo(EPathFollowerState.Completed));
        }

        [TestCase("stop")]
        [TestCase("release")]
        [TestCase("complete")]
        public void CharacterControllerIsSuppressedUntilPlaybackEnds(string ending)
        {
            GameObject actor = CreateObject("Actor");
            CharacterController controller = actor.AddComponent<CharacterController>();
            PathFollower follower = actor.AddComponent<PathFollower>();
            follower.Init();
            PathData path = CreatePath(PathMovementSettings.Time(0.1f));
            follower.StartPlayback(PathPlaybackRequest.Single(path));
            Assert.That(controller.enabled, Is.False);
            follower.PauseMove();
            Assert.That(controller.enabled, Is.False);
            follower.ResumeMove();

            if (ending == "stop") follower.StopMove();
            else if (ending == "release") follower.Release();
            else follower.Tick(1f);
            Assert.That(controller.enabled, Is.True);
        }

        [Test]
        public void AlreadyDisabledCharacterControllerIsNotEnabledOnStop()
        {
            GameObject actor = CreateObject("Actor");
            CharacterController controller = actor.AddComponent<CharacterController>();
            controller.enabled = false;
            PathFollower follower = actor.AddComponent<PathFollower>();
            follower.Init();
            follower.StartPlayback(PathPlaybackRequest.Single(CreatePath(PathMovementSettings.Time(5f))));
            follower.StopMove();
            Assert.That(controller.enabled, Is.False);
        }

        [Test]
        public void PausingFromStartCallbackKeepsControllerSuppressedUntilStop()
        {
            GameObject actor = CreateObject("Actor");
            CharacterController controller = actor.AddComponent<CharacterController>();
            PathFollower follower = actor.AddComponent<PathFollower>();
            follower.Init();
            follower.StateChanged += state =>
            {
                if (state == EPathFollowerState.Moving)
                    follower.PauseMove();
            };
            follower.StartPlayback(PathPlaybackRequest.Single(CreatePath(PathMovementSettings.Time(5f))));
            Assert.That(follower.State, Is.EqualTo(EPathFollowerState.Paused));
            Assert.That(controller.enabled, Is.False);
            follower.StopMove();
            Assert.That(controller.enabled, Is.True);
        }

        [Test]
        public void StopCallbackStartingNewPlaybackKeepsControllerSuppressed()
        {
            GameObject actor = CreateObject("Actor");
            CharacterController controller = actor.AddComponent<CharacterController>();
            PathFollower follower = actor.AddComponent<PathFollower>();
            follower.Init();
            PathData path = CreatePath(PathMovementSettings.Time(5f));
            follower.StartPlayback(PathPlaybackRequest.Single(path));
            Action<EPathFollowerState> restart = state =>
            {
                if (state == EPathFollowerState.Ready)
                    follower.StartPlayback(PathPlaybackRequest.Single(path));
            };
            follower.StateChanged += restart;
            try
            {
                follower.StopMove();
                Assert.That(follower.State, Is.EqualTo(EPathFollowerState.Moving));
                Assert.That(controller.enabled, Is.False);
            }
            finally
            {
                follower.StateChanged -= restart;
            }
        }

        [Test]
        public void YOnlyAlignmentPreservesOtherAxesAndRotationCanRemainFixed()
        {
            PathData path = CreatePath(PathMovementSettings.Time(5f), new Vector3(10f, 5f, 10f));
            PathFollower follower = CreateFollower();
            Quaternion initial = Quaternion.Euler(12f, 5f, 8f);
            follower.transform.rotation = initial;
            follower.SetRotateToPathDirection(false);
            follower.StartPlayback(PathPlaybackRequest.Single(path));
            Assert.That(follower.AlignRotationToPathDirection(EPathAlignRotationAxes.Y), Is.True);
            Vector3 angles = follower.transform.eulerAngles;
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(angles.x, initial.eulerAngles.x)), Is.LessThan(0.01f));
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(angles.z, initial.eulerAngles.z)), Is.LessThan(0.01f));
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(angles.y, 45f)), Is.LessThan(0.1f));
            Quaternion aligned = follower.transform.rotation;
            follower.Tick(0.2f);
            Assert.That(Quaternion.Angle(aligned, follower.transform.rotation), Is.LessThan(0.01f));
            follower.SetRotateToPathDirection(true);
            follower.Tick(0.2f);
            Assert.That(Quaternion.Angle(aligned, follower.transform.rotation), Is.GreaterThan(1f));
        }

        [TestCase(EPathMoveType.SpeedBased)]
        [TestCase(EPathMoveType.TimeBased)]
        public void ResumeEventAppliesItsTargetMovementValue(EPathMoveType mode)
        {
            GameObject actor = CreateObject("Actor");
            PathEventHandler handler = actor.AddComponent<PathEventHandler>();
            PathFollower follower = actor.AddComponent<PathFollower>();
            follower.Init();
            PathData path = CreatePath(new PathMovementSettings(mode, 5f));
            follower.StartPlayback(PathPlaybackRequest.Single(path));
            follower.PauseMove();
            PathEventSettingSO setting = ScriptableObject.CreateInstance<PathEventSettingSO>();
            _objects.Add(setting);
            setting.UseModifyPathMoveSpeed = mode == EPathMoveType.SpeedBased;
            setting.UseModifyPathMoveDuration = mode == EPathMoveType.TimeBased;
            setting.MoveSpeedTargetValue = 2f;
            setting.MoveDurationTargetValue = 2f;
            handler.HandleEvent(setting);
            Assert.That(follower.State, Is.EqualTo(EPathFollowerState.Moving));
            Assert.That(mode == EPathMoveType.SpeedBased ? follower.Speed : follower.Duration,
                Is.EqualTo(2f).Within(0.00001f));
        }

        [Test]
        public void QueueAndFreeTransitionsPreserveCorrectRegistrationAndCompletion()
        {
            PathData path = CreatePath(PathMovementSettings.Time(0.1f));
            QueuedPathManager manager = CreateManager(path);
            QueuedPathFollower queued = CreateQueuedFollower();
            queued.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path), 0.999f);
            ulong id = queued.PlaybackId;
            queued.ForceBlock();
            Assert.That(queued.IsQueueHeld, Is.True);
            Assert.That(queued.PlaybackId, Is.EqualTo(id));
            queued.DetachQueue();
            Assert.That(queued.IsRegistered, Is.False);
            Assert.That(queued.PlaybackId, Is.EqualTo(id));
            Assert.That(queued.IsMoving, Is.True);

            queued.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            queued.StartFreePlayback(PathPlaybackRequest.Single(path));
            Assert.That(manager.AgentCount, Is.Zero);
            Assert.That(queued.ActiveManager, Is.Null);
            int completed = 0;
            ((IPathFollower)queued).Completed += () => completed++;
            queued.PauseMove();
            queued.ResumeMove();
            Assert.That(queued.IsRegistered, Is.False);
            queued.UnderlyingFollower.Tick(1f);
            Assert.That(completed, Is.EqualTo(1));
        }

        [Test]
        public void QueueReleasesOnlyAfterDelayAndExtraSpacingThenSmoothsSpeed()
        {
            PathData path = CreatePath(PathMovementSettings.Time(5f));
            QueuedPathManager manager = CreateManager(path);
            QueuedPathFollower queued = CreateQueuedFollower();
            queued.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            PathQueueRegistration token = manager.Register(queued);
            queued.ForceBlock();
            queued.ForceUnblock();
            ApplyState(queued, token, manager.DefaultSpacing + 0.1f, 0);
            ((IPathRuntimeTickable)queued).Tick(0.2f, 0.2f, 0);
            Assert.That(queued.IsBlocked, Is.True);
            ApplyState(queued, token, manager.DefaultSpacing + 0.5f, 1);
            ((IPathRuntimeTickable)queued).Tick(0.02f, 0.02f, 1);
            Assert.That(queued.IsBlocked, Is.False);
            Assert.That(queued.CurrentSpeedMultiplier, Is.InRange(0.01f, 0.99f));
        }

        [Test]
        public void ProgressBeyondQueueCapReportsInvalidationAndStops()
        {
            PathData path = CreatePath(PathMovementSettings.Time(5f));
            QueuedPathManager manager = CreateManager(path);
            QueuedPathFollower queued = CreateQueuedFollower();
            EPathQueueDetachReason? reason = null;
            queued.QueueInvalidated += (_, value) => reason = value;
            queued.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path), 0.5f);
            queued.Seek(0.75f);
            ApplyState(queued, manager.Register(queued), 10f, 0);
            ((IPathRuntimeTickable)queued).Tick(0.02f, 0.02f, 0);
            Assert.That(reason, Is.EqualTo(EPathQueueDetachReason.ConstraintViolation));
            Assert.That(queued.IsRegistered, Is.False);
            Assert.That(queued.IsMoving, Is.False);
        }

        [Test]
        public void RemovingHeadPreservesOrderBeforeNextManagerTick()
        {
            PathData path = CreatePath(PathMovementSettings.Time(5f));
            QueuedPathManager manager = CreateManager(path);
            var head = new QueueAgent(path);
            var middle = new QueueAgent(path);
            var tail = new QueueAgent(path);
            PathQueueRegistration token = manager.Register(head);
            manager.Register(middle);
            manager.Register(tail);
            manager.Unregister(token);
            Assert.That(manager.GetAgent(0), Is.SameAs(middle));
            Assert.That(manager.GetAgent(1), Is.SameAs(tail));
        }

        [Test]
        public void JointSynchronizationFollowsThePointWithLargestMovement()
        {
            PathData first = CreatePath(PathMovementSettings.Time(5f));
            PathData second = CreatePath(PathMovementSettings.Time(5f));
            Transform end = CreateObject("End").transform;
            Transform start = CreateObject("Start").transform;
            end.position = Vector3.right * 4f;
            start.position = Vector3.right;
            Assert.That(PathLinkEditorUtility.TrySyncJointByMovement(first, second, end, start,
                Vector3.zero, Vector3.zero, 0.000001f), Is.True);
            Assert.That(start.position, Is.EqualTo(end.position));
        }

        [Test]
        public void WaypointGroupFollowsMovedMemberWithoutChangingRotation()
        {
            Transform first = CreateObject("First").transform;
            Transform second = CreateObject("Second").transform;
            var group = new PathWaypointSyncGroup();
            SetField(group, "_members", new[] { first, second });
            second.rotation = Quaternion.Euler(0f, 30f, 0f);
            first.position = new Vector3(3f, 2f, 1f);
            Assert.That(PathWaypointSyncEditorUtility.TrySyncGroupByMovement(group,
                new[] { Vector3.zero, Vector3.zero }, 0.000001f, null, null), Is.True);
            Assert.That(second.position, Is.EqualTo(first.position));
            Assert.That(Quaternion.Angle(second.rotation, Quaternion.Euler(0f, 30f, 0f)),
                Is.LessThan(0.01f));
        }

        [Test]
        public void CurrentSegmentFieldAndShowcaseScriptReferencesAreValid()
        {
            FieldInfo field = typeof(PathSegmentAuthoring).GetField("_providerObject",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var alias = field.GetCustomAttribute<FormerlySerializedAsAttribute>();
            Assert.That(alias, Is.Null);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/_TransformPath/Prefab/TransformPathShowcase.prefab");
            Assert.That(prefab, Is.Not.Null);
            foreach (Transform item in prefab.GetComponentsInChildren<Transform>(true))
                Assert.That(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(item.gameObject),
                    Is.Zero, item.name);
            Assert.That(prefab.GetComponentsInChildren<PathData>(true).Length, Is.GreaterThan(0));
            Assert.That(prefab.GetComponentsInChildren<QueuedPathFollower>(true).Length, Is.GreaterThan(0));
        }

        private GameObject CreateObject(string name)
        {
            var gameObject = new GameObject(name);
            _objects.Add(gameObject);
            return gameObject;
        }

        private PathData CreatePath(PathMovementSettings movement, Vector3? end = null)
        {
            PathData path = CreateObject("Path").AddComponent<PathData>();
            path.ConfigureMovementSettings(movement);
            path.ConfigureControlPoints(new[] { Vector3.zero, end ?? Vector3.right * 10f });
            path.Init();
            Assert.That(path.IsReady, Is.True);
            return path;
        }

        private MultiPathData CreateSequence(params PathData[] paths)
        {
            MultiPathData sequence = CreateObject("Sequence").AddComponent<MultiPathData>();
            var segments = new PathSegmentDescriptor[paths.Length];
            for (int i = 0; i < paths.Length; i++)
                segments[i] = new PathSegmentDescriptor(paths[i], paths[i].MovementSettings, false);
            sequence.ConfigureSegments(segments);
            return sequence;
        }

        private PathFollower CreateFollower()
        {
            PathFollower follower = CreateObject("Actor").AddComponent<PathFollower>();
            follower.Init();
            return follower;
        }

        private QueuedPathFollower CreateQueuedFollower()
        {
            GameObject actor = CreateObject("Queue Actor");
            actor.AddComponent<PathFollower>();
            return actor.AddComponent<QueuedPathFollower>();
        }

        private QueuedPathManager CreateManager(IPathProvider route)
        {
            QueuedPathManager manager = CreateObject("Manager").AddComponent<QueuedPathManager>();
            manager.Init();
            manager.ConfigureRoute(route);
            return manager;
        }

        private static void ApplyState(QueuedPathFollower queued, PathQueueRegistration token,
            float distance, int frame)
        {
            queued.ApplyQueueState(token, new PathQueueState(null, distance, false, 1f, 1f,
                token.RouteRevision, queued.PlaybackId, frame));
        }

        private static void SetField(object target, string name, object value)
        {
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(target, value);
        }

        private sealed class QueueAgent : IQueuedPathAgent
        {
            public QueueAgent(IPathProvider provider) => QueueProvider = provider;
            public ulong PlaybackId => 1;
            public IPathProvider QueueProvider { get; }
            public bool IsMoving => true;
            public float GlobalNormalizedTime => 0f;
            public int SnapshotRevision => QueueProvider.Revision;
            public PathQueueAgentSettings QueueSettings => new PathQueueAgentSettings(1.5f, true, true, true);
            public void ApplyQueueState(PathQueueRegistration registration, PathQueueState state) { }
            public void OnQueueDetached(PathQueueRegistration registration, EPathQueueDetachReason reason) { }
        }
    }
}
