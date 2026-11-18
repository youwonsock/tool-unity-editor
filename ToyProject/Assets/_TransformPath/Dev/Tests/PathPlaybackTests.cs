using System;
using NUnit.Framework;
using UnityEngine;

namespace Common.TransformPath.Tests
{
    public sealed class PathPlaybackTests : PathBoundaryIsolatedTest
    {
        private PathTestScene _scene;

        [SetUp]
        public void SetUp() => _scene = new PathTestScene();

        [TearDown]
        public void TearDown() => _scene.Dispose();

        private PathData Line(PathMovementSettings movement) =>
            _scene.CreatePath(movement, EPathCurveType.Linear, Vector3.zero, new Vector3(10f, 0f, 0f));

        [Test]
        public void SpeedBased_CompletesAfterLengthOverSpeed()
        {
            var recorder = new RecordingPlayback();
            recorder.Playback.Play(PathPlaybackRequest.Single(Line(PathMovementSettings.Speed(5f))));

            recorder.Frames(119);
            Assert.AreEqual(EPathFollowerState.Moving, recorder.Playback.State);
            recorder.Frames(2);

            Assert.AreEqual(EPathFollowerState.Completed, recorder.Playback.State);
            Assert.AreEqual(1f, recorder.Playback.Progress);
            Assert.AreEqual(0UL, recorder.Playback.PlaybackId);
            CollectionAssert.AreEqual(new[] { "state Ready", "state Moving", "state Completed", "completed" }, recorder.Log);
        }

        [Test]
        public void TimeBased_SetDuration_KeepsProgress()
        {
            var recorder = new RecordingPlayback();
            recorder.Playback.Play(PathPlaybackRequest.Single(Line(PathMovementSettings.Time(4f))));
            recorder.Frames(60);
            float before = recorder.Playback.Progress;

            Assert.IsTrue(recorder.Playback.SetDuration(8f));

            Assert.AreEqual(before, recorder.Playback.Progress, 1e-5f);
            recorder.Frames(60);
            Assert.AreEqual(before + 0.125f, recorder.Playback.Progress, 1e-3f);
        }

        [Test]
        public void Commands_ValidateBeforeChanging()
        {
            var recorder = new RecordingPlayback();
            Assert.Throws<InvalidOperationException>(() => recorder.Playback.Seek(0.5f));
            recorder.Playback.Play(PathPlaybackRequest.Single(Line(PathMovementSettings.Speed(2f))));

            Assert.Throws<ArgumentOutOfRangeException>(() => recorder.Playback.SetSpeed(0f));
            Assert.Throws<InvalidOperationException>(() => recorder.Playback.SetDuration(2f));
            Assert.Throws<InvalidOperationException>(() => recorder.Playback.SeekSegment(0, 0.5f));
            Assert.AreEqual(2f, recorder.Playback.Speed);
        }

        [Test]
        public void Loop_CarriesTimeOverTheSeam()
        {
            var recorder = new RecordingPlayback();
            recorder.Playback.Play(PathPlaybackRequest.Single(Line(PathMovementSettings.Speed(10f)), true));

            recorder.Frame(1.25f);

            Assert.AreEqual(EPathFollowerState.Moving, recorder.Playback.State);
            Assert.AreEqual(0.25f, recorder.Playback.Progress, 1e-4f);
        }

        [Test]
        public void LongFrame_StopsAtTheTransitionLimit_AndKeepsMoving()
        {
            var recorder = new RecordingPlayback();
            recorder.Playback.Play(PathPlaybackRequest.Single(Line(PathMovementSettings.Speed(9999f)), true));

            // One second covers about a thousand laps; a frame stops at the limit.
            recorder.Playback.TickMovement(1f);

            Assert.AreEqual(EPathFollowerState.Moving, recorder.Playback.State);
            Assert.AreEqual(0f, recorder.Playback.Progress, 1e-5f);
        }

        [Test]
        public void Seek_SkipsEventsAtAndBeforeTheTarget()
        {
            PathData path = Line(PathMovementSettings.Speed(5f));
            _scene.SetEvents(path, (0.2f, _scene.CreateEvent("early")), (0.6f, _scene.CreateEvent("late")));
            var recorder = new RecordingPlayback();
            recorder.Playback.Play(PathPlaybackRequest.Single(path));

            recorder.Playback.Seek(0.4f);
            recorder.Frames(120);

            CollectionAssert.DoesNotContain(recorder.Log, "event early");
            CollectionAssert.Contains(recorder.Log, "event late");
        }

        [Test]
        public void PauseEvent_ResumesThroughItsDelayedEvent_WithTheResumeValue()
        {
            PathData path = Line(PathMovementSettings.Time(6f));
            PathEventSettingSO resume = _scene.CreateEvent("resume");
            resume.Lifecycle = EPathEventLifecycle.Resume;
            resume.UseSpeedChange = true;
            resume.SpeedTarget = 2f;
            resume.UseDurationChange = true;
            resume.DurationTarget = 3f;
            PathEventSettingSO pause = _scene.CreateEvent("pause");
            pause.Lifecycle = EPathEventLifecycle.Pause;
            pause.DelayedEvents.Add(new PathEventSettingSO.DelayedEventEntry { Delay = 0.5f, EventSetting = resume });
            _scene.SetEvents(path, (0.1f, pause));
            var recorder = new RecordingPlayback();
            recorder.Playback.Play(PathPlaybackRequest.Single(path));

            recorder.Frames(40);
            Assert.AreEqual(EPathFollowerState.Paused, recorder.Playback.State);
            recorder.Frames(40);

            Assert.AreEqual(EPathFollowerState.Moving, recorder.Playback.State);
            // A TimeBased segment takes the Duration channel only.
            Assert.AreEqual(3f, recorder.Playback.Duration, 1e-5f);
        }

        [Test]
        public void NextPathEvent_CancelsPendingDelayedEvents()
        {
            PathData path = Line(PathMovementSettings.Speed(5f));
            PathEventSettingSO delayed = _scene.CreateEvent("delayed");
            PathEventSettingSO first = _scene.CreateEvent("first");
            first.DelayedEvents.Add(new PathEventSettingSO.DelayedEventEntry { Delay = 1f, EventSetting = delayed });
            _scene.SetEvents(path, (0.1f, first), (0.2f, _scene.CreateEvent("second")));
            var recorder = new RecordingPlayback();
            recorder.Playback.Play(PathPlaybackRequest.Single(path));

            recorder.Frames(120);

            CollectionAssert.DoesNotContain(recorder.Log, "event delayed");
        }

        [Test]
        public void SpeedBlend_ReachesItsTargetOverTheBlendTime()
        {
            var recorder = new RecordingPlayback();
            PathEventSettingSO faster = _scene.CreateEvent("faster");
            faster.UseSpeedChange = true;
            faster.SpeedTarget = 4f;
            faster.SpeedBlendTime = 1f;
            faster.SpeedBlendCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f);
            recorder.Playback.Play(PathPlaybackRequest.Single(Line(PathMovementSettings.Speed(2f))));
            recorder.Frame();

            // A blend starts counting on the frame after it started.
            recorder.Playback.TriggerEvent(PathEventDefinition.FromAuthoring(faster));
            recorder.Frames(30);
            Assert.AreEqual(3f, recorder.Playback.Speed, 0.1f);
            recorder.Frames(40);

            Assert.AreEqual(4f, recorder.Playback.Speed, 1e-5f);
        }

        [Test]
        public void CyclicDelayedEvents_AreRejected()
        {
            PathEventSettingSO a = _scene.CreateEvent("a");
            PathEventSettingSO b = _scene.CreateEvent("b");
            a.DelayedEvents.Add(new PathEventSettingSO.DelayedEventEntry { Delay = 1f, EventSetting = b });
            b.DelayedEvents.Add(new PathEventSettingSO.DelayedEventEntry { Delay = 1f, EventSetting = a });

            Assert.Throws<ArgumentException>(() => PathEventDefinition.FromAuthoring(a));
        }

        [Test]
        public void GeometryChange_KeepsPlaying_DefinitionChange_Stops()
        {
            PathData path = Line(PathMovementSettings.Speed(2f));
            var recorder = new RecordingPlayback();
            recorder.Playback.Play(PathPlaybackRequest.Single(path));
            recorder.Frames(30);

            path.transform.GetChild(1).position = new Vector3(20f, 0f, 0f);
            path.Rebuild();
            recorder.Frame();
            Assert.AreEqual(EPathFollowerState.Moving, recorder.Playback.State);

            path.MovementSettings = PathMovementSettings.Speed(3f);
            recorder.Frame();
            Assert.AreEqual(EPathFollowerState.Ready, recorder.Playback.State);
        }

        [Test]
        public void Sequence_PreservesSpeedIntoTheNextSegment()
        {
            PathData first = _scene.CreatePath(PathMovementSettings.Speed(4f), EPathCurveType.Linear,
                Vector3.zero, new Vector3(2f, 0f, 0f));
            PathData second = _scene.CreatePath(PathMovementSettings.Time(10f), EPathCurveType.Linear,
                new Vector3(2f, 0f, 0f), new Vector3(10f, 0f, 0f));
            var host = new GameObject("Sequence");
            try
            {
                MultiPathData sequence = host.AddComponent<MultiPathData>();
                sequence.SetSegments(new[] { new PathSegmentAuthoring(first), new PathSegmentAuthoring(second, true) });
                var recorder = new RecordingPlayback();
                recorder.Playback.Play(PathPlaybackRequest.Sequence(sequence));

                recorder.Frames(40);

                Assert.AreEqual(1, recorder.Playback.SegmentIndex);
                Assert.AreEqual(EPathMoveType.TimeBased, recorder.Playback.MoveType);
                // 8 units at the 4 units per second carried over.
                Assert.AreEqual(2f, recorder.Playback.Duration, 1e-4f);
                CollectionAssert.Contains(recorder.Log, "segment 1");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }
    }
}
