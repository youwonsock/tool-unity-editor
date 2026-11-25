using System;
using System.Collections.Generic;
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
        public void DefaultTimeCurve_MapsTimeToProgressOneToOne()
        {
            PathMovementSettings linear = PathMovementSettings.Time(10f);
            PathMovementSettings eased = PathMovementSettings.Time(10f, AnimationCurve.EaseInOut(0f, 0f, 1f, 1f));

            Assert.AreEqual(0.37f, linear.EvaluateProgress(0.37f));
            Assert.AreEqual(0.37f, linear.InverseProgress(0.37f));
            Assert.AreNotEqual(0.37f, eased.EvaluateProgress(0.37f));
            Assert.AreEqual(0.37f, eased.EvaluateProgress(eased.InverseProgress(0.37f)), 1e-5f);
        }

        [Test]
        public void CurvedTimeCurve_StopsAtEachEvent_WhenTheCurveReachesIt()
        {
            var movement = PathMovementSettings.Time(10f, AnimationCurve.EaseInOut(0f, 0f, 1f, 1f));
            PathData path = Line(movement);
            PathEventSettingSO first = _scene.CreateEvent("First");
            first.Lifecycle = EPathEventLifecycle.Pause;
            PathEventSettingSO second = _scene.CreateEvent("Second");
            second.Lifecycle = EPathEventLifecycle.Pause;
            _scene.SetEvents(path, (0.3f, first), (0.6f, second));
            path.Rebuild();
            var recorder = new RecordingPlayback();
            recorder.Playback.Play(PathPlaybackRequest.Single(path));

            // Moving frames from the start, at 100 a second; the last one of each stop is partial.
            int moving = 0;
            foreach (float position in new[] { 0.3f, 0.6f })
            {
                while (recorder.Playback.State == EPathFollowerState.Moving && moving < 2000)
                {
                    recorder.Frame(0.01f);
                    moving++;
                }
                Assert.AreEqual(EPathFollowerState.Paused, recorder.Playback.State);
                Assert.AreEqual(position, recorder.Playback.Progress, 1e-6f);
                Assert.AreEqual(movement.InverseProgress(position) * 10f, moving * 0.01f, 0.021f);
                recorder.Playback.Resume();
            }
            Assert.AreEqual(EPathFollowerState.Moving, recorder.Playback.State);
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
            PathData path = Line(PathMovementSettings.Speed(9999f));
            _scene.SetEvents(path, (0.5f, _scene.CreateEvent("lap")));
            var recorder = new RecordingPlayback();
            recorder.Playback.Play(PathPlaybackRequest.Single(path, true));

            // One second covers about a thousand laps; a frame stops at the limit.
            recorder.Playback.TickMovement(1f);

            Assert.AreEqual(EPathFollowerState.Moving, recorder.Playback.State);
            Assert.AreEqual(0f, recorder.Playback.Progress, 1e-5f);
            Assert.AreEqual(PathConstants.MAX_SEGMENT_TRANSITIONS_PER_TICK,
                recorder.Log.FindAll(entry => entry == "event lap").Count);
            // The rest of the second waits for the next frame.
            recorder.Playback.TickMovement(0.0001f);
            Assert.AreEqual(2 * PathConstants.MAX_SEGMENT_TRANSITIONS_PER_TICK,
                recorder.Log.FindAll(entry => entry == "event lap").Count);
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
        public void SetControlPoints_ValidatesFirst_ThenReplacesTheTransforms()
        {
            PathData path = Line(PathMovementSettings.Speed(2f));
            int revision = path.Revision;

            Assert.Throws<ArgumentException>(() => path.SetControlPoints(new[] { Vector3.zero }));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => path.SetControlPoints(new[] { Vector3.zero, new Vector3(float.NaN, 0f, 0f) }));
            Assert.AreEqual(revision, path.Revision);
            Assert.AreEqual(10f, path.PathLength, 1e-4f);

            Vector3[] points = { Vector3.zero, new Vector3(0f, 0f, 4f) };
            path.SetControlPoints(points);
            Assert.AreEqual(revision + 1, path.Revision);
            Assert.AreEqual(4f, path.PathLength, 1e-4f);
            var copied = new List<Vector3>();
            Assert.IsTrue(path.CopyControlPoints(copied));
            CollectionAssert.AreEqual(points, copied);
            // The Transforms no longer drive the path.
            path.transform.GetChild(1).position = new Vector3(20f, 0f, 0f);
            path.Rebuild();
            Assert.AreEqual(4f, path.PathLength, 1e-4f);

            path.SetPathPoints(new[] { path.transform.GetChild(0), path.transform.GetChild(1) });
            Assert.AreEqual(20f, path.PathLength, 1e-4f);
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

        private MultiPathData Sequence(PathMovementSettings first, PathMovementSettings second, float a, float b)
        {
            PathData pa = _scene.CreatePath(first, EPathCurveType.Linear, Vector3.zero, new Vector3(a, 0f, 0f));
            PathData pb = _scene.CreatePath(second, EPathCurveType.Linear, new Vector3(a, 0f, 0f), new Vector3(a, 0f, b));
            MultiPathData sequence = _scene.CreateSequence();
            sequence.SetSegments(new[] { new PathSegmentAuthoring(pa), new PathSegmentAuthoring(pb) });
            return sequence;
        }

        private static AnimationCurve Curve(params (float time, float value)[] keys)
        {
            var curve = new AnimationCurve();
            foreach (var (time, value) in keys)
                curve.AddKey(new Keyframe(time, value, 0f, 0f));
            for (int i = 0; i < curve.length; i++)
                UnityEditor.AnimationUtility.SetKeyLeftTangentMode(curve, i, UnityEditor.AnimationUtility.TangentMode.Linear);
            for (int i = 0; i < curve.length; i++)
                UnityEditor.AnimationUtility.SetKeyRightTangentMode(curve, i, UnityEditor.AnimationUtility.TangentMode.Linear);
            return curve;
        }

        // 599.6 + 31.1 used to stop at a local 0.9999992 of the last segment for good.
        [TestCase(EPathMoveType.SpeedBased)]
        [TestCase(EPathMoveType.TimeBased)]
        public void LongSequence_Completes(EPathMoveType moveType)
        {
            MultiPathData sequence = moveType == EPathMoveType.SpeedBased
                ? Sequence(PathMovementSettings.Speed(10f), PathMovementSettings.Speed(10f), 599.6f, 31.1f)
                : Sequence(PathMovementSettings.Time(60f), PathMovementSettings.Time(15f), 383.275f, 12.227f);
            var recorder = new RecordingPlayback();
            recorder.Playback.Play(PathPlaybackRequest.Sequence(sequence));

            recorder.Frames(200, 0.5f);

            Assert.AreEqual(EPathFollowerState.Completed, recorder.Playback.State);
            Assert.AreEqual(1f, recorder.Playback.Progress);
        }

        [Test]
        public void LongSequence_SeekToTheEnd_CompletesOnTheNextMove()
        {
            MultiPathData sequence = Sequence(PathMovementSettings.Speed(10f), PathMovementSettings.Speed(10f), 599.6f, 31.1f);
            var recorder = new RecordingPlayback();
            recorder.Playback.Play(PathPlaybackRequest.Sequence(sequence));

            recorder.Playback.Seek(1f);
            Assert.AreEqual(EPathFollowerState.Moving, recorder.Playback.State);
            recorder.Frame();

            Assert.AreEqual(EPathFollowerState.Completed, recorder.Playback.State);
        }

        [Test]
        public void LongSequence_Loop_WrapsAround()
        {
            MultiPathData sequence = Sequence(PathMovementSettings.Speed(10f), PathMovementSettings.Speed(10f), 599.6f, 31.1f);
            var recorder = new RecordingPlayback();
            recorder.Playback.Play(PathPlaybackRequest.Sequence(sequence, loop: true));

            recorder.Frames(140, 0.5f);

            Assert.AreEqual(0, recorder.Playback.SegmentIndex);
            Assert.Greater(recorder.Playback.Progress, 0f);
        }

        [Test]
        public void LongPath_AtTheLowestSpeed_StillMoves()
        {
            PathData path = _scene.CreatePath(PathMovementSettings.Speed(PathConstants.MIN_MOVE_VALUE), EPathCurveType.Linear,
                Vector3.zero, new Vector3(10000f, 0f, 0f));
            var recorder = new RecordingPlayback();
            recorder.Playback.Play(PathPlaybackRequest.Single(path));
            recorder.Frames(3);
            float before = recorder.Playback.Progress;

            recorder.Frames(600);

            Assert.Greater(recorder.Playback.Progress, before);
        }

        [TestCase(EPathMoveType.SpeedBased)]
        [TestCase(EPathMoveType.TimeBased)]
        public void PauseEvent_StopsExactlyAtItsPosition(EPathMoveType moveType)
        {
            PathData path = Line(moveType == EPathMoveType.SpeedBased
                ? PathMovementSettings.Speed(10f)
                : PathMovementSettings.Time(1f, AnimationCurve.EaseInOut(0f, 0f, 1f, 1f)));
            PathEventSettingSO pause = _scene.CreateEvent("pause");
            pause.Lifecycle = EPathEventLifecycle.Pause;
            _scene.SetEvents(path, (0.47f, pause));
            var recorder = new RecordingPlayback();
            recorder.Playback.Play(PathPlaybackRequest.Single(path));

            recorder.Frames(20, 0.05f);

            Assert.AreEqual(EPathFollowerState.Paused, recorder.Playback.State);
            Assert.AreEqual(0.47f, recorder.Playback.Progress);
        }

        [Test]
        public void PauseEvent_OnAHold_StopsAtTheHoldStart_AndTheHoldContinuesAfterResume()
        {
            // Progress holds at 0.5 from 30% to 70% of the duration.
            PathData path = Line(PathMovementSettings.Time(10f, Curve((0f, 0f), (0.3f, 0.5f), (0.7f, 0.5f), (1f, 1f))));
            PathEventSettingSO pause = _scene.CreateEvent("pause");
            pause.Lifecycle = EPathEventLifecycle.Pause;
            _scene.SetEvents(path, (0.5f, pause));
            var recorder = new RecordingPlayback();
            recorder.Playback.Play(PathPlaybackRequest.Single(path));

            recorder.Frames(40, 0.1f);
            Assert.AreEqual(EPathFollowerState.Paused, recorder.Playback.State);
            Assert.AreEqual(0.5f, recorder.Playback.Progress, 1e-4f);

            recorder.Playback.Resume();
            recorder.Frames(39, 0.1f);
            Assert.AreEqual(0.5f, recorder.Playback.Progress, 1e-4f);
            recorder.Frames(3, 0.1f);
            Assert.Greater(recorder.Playback.Progress, 0.5f);
        }

        [Test]
        public void ValueChange_AppliesFromItsPosition_ForTheRestOfTheFrame()
        {
            PathData path = Line(PathMovementSettings.Speed(1f));
            PathEventSettingSO faster = _scene.CreateEvent("faster");
            faster.UseSpeedChange = true;
            faster.SpeedTarget = 3f;
            _scene.SetEvents(path, (0.5f, faster));
            var recorder = new RecordingPlayback();
            recorder.Playback.Play(PathPlaybackRequest.Single(path));

            // 5 m at 1 m/s reach the event, the last second runs at 3 m/s.
            recorder.Frame(6f);

            Assert.AreEqual(0.8f, recorder.Playback.Progress, 1e-4f);
        }

        [Test]
        public void EventAtZero_RunsOnTheFirstMove()
        {
            PathData path = Line(PathMovementSettings.Speed(1f));
            _scene.SetEvents(path, (0f, _scene.CreateEvent("start")));
            var recorder = new RecordingPlayback();
            recorder.Playback.Play(PathPlaybackRequest.Single(path));
            CollectionAssert.DoesNotContain(recorder.Log, "event start");

            recorder.Frame();

            CollectionAssert.Contains(recorder.Log, "event start");
        }

        [Test]
        public void EventsAtOnePosition_RunInAuthoringOrder_APauseHoldsTheRest()
        {
            PathData path = Line(PathMovementSettings.Speed(10f));
            PathEventSettingSO pause = _scene.CreateEvent("pause");
            pause.Lifecycle = EPathEventLifecycle.Pause;
            _scene.SetEvents(path, (0.3f, _scene.CreateEvent("a")), (0.3f, pause), (0.3f, _scene.CreateEvent("b")));
            var recorder = new RecordingPlayback();
            recorder.Playback.Play(PathPlaybackRequest.Single(path));

            recorder.Frames(30);
            CollectionAssert.Contains(recorder.Log, "event pause");
            CollectionAssert.DoesNotContain(recorder.Log, "event b");
            Assert.AreEqual(0.3f, recorder.Playback.Progress);

            recorder.Playback.Resume();
            recorder.Frame();

            int a = recorder.Log.IndexOf("event a"), p = recorder.Log.IndexOf("event pause"), b = recorder.Log.IndexOf("event b");
            Assert.That(a < p && p < b);
        }

        [Test]
        public void CapBeforeAnEvent_HoldsThere_TheEventRunsOnceTheCapPasses()
        {
            PathData path = Line(PathMovementSettings.Speed(10f));
            _scene.SetEvents(path, (0.3f, _scene.CreateEvent("e")));
            var recorder = new RecordingPlayback();
            recorder.Playback.Play(PathPlaybackRequest.Single(path));
            recorder.Playback.Limit = new PathMotionLimit(false, 1f, 0.2f, false);

            recorder.Frames(30);
            Assert.AreEqual(0.2f, recorder.Playback.Progress, 1e-6f);
            CollectionAssert.DoesNotContain(recorder.Log, "event e");

            recorder.Playback.Limit = PathMotionLimit.None;
            recorder.Frames(10);
            CollectionAssert.Contains(recorder.Log, "event e");
        }

        [Test]
        public void Loop_RunsAnEventOnEveryLap()
        {
            PathData path = Line(PathMovementSettings.Speed(10f));
            _scene.SetEvents(path, (0.5f, _scene.CreateEvent("half")));
            var recorder = new RecordingPlayback();
            recorder.Playback.Play(PathPlaybackRequest.Single(path, true));

            // Passes 0.5 at 0.5 s, 1.5 s and 2.5 s.
            recorder.Frame(3.2f);

            Assert.AreEqual(3, recorder.Log.FindAll(entry => entry == "event half").Count);
        }

        [Test]
        public void LastEvent_RunsBeforeCompletion()
        {
            PathData path = Line(PathMovementSettings.Speed(10f));
            _scene.SetEvents(path, (PathConstants.MAX_EVENT_POSITION, _scene.CreateEvent("end")));
            var recorder = new RecordingPlayback();
            recorder.Playback.Play(PathPlaybackRequest.Single(path));

            recorder.Frame(5f);

            Assert.Less(recorder.Log.IndexOf("event end"), recorder.Log.IndexOf("state Completed"));
        }

        [Test]
        public void TimeBased_GeometryChangingEveryFrame_StillLeavesAHold()
        {
            // Holds at 0 for the first half of the duration.
            PathData path = Line(PathMovementSettings.Time(2f, Curve((0f, 0f), (0.5f, 0f), (1f, 1f))));
            var recorder = new RecordingPlayback();
            recorder.Playback.Play(PathPlaybackRequest.Single(path));

            for (int i = 0; i < 90; i++)
            {
                path.transform.GetChild(1).position = new Vector3(10f + (i % 2) * 0.01f, 0f, 0f);
                path.Rebuild();
                recorder.Frame();
            }

            Assert.Greater(recorder.Playback.Progress, 0f);
        }

        [Test]
        public void TimeBased_SetDurationDuringAHold_KeepsTheTimeFraction()
        {
            PathData path = Line(PathMovementSettings.Time(10f, Curve((0f, 0f), (0.3f, 0.5f), (0.7f, 0.5f), (1f, 1f))));
            var recorder = new RecordingPlayback();
            recorder.Playback.Play(PathPlaybackRequest.Single(path));
            recorder.Frames(50, 0.1f);

            Assert.IsTrue(recorder.Playback.SetDuration(20f));
            Assert.AreEqual(0.5f, recorder.Playback.Progress, 1e-4f);

            // Half the time passed; the hold ends at 70%, 4 s later at the new duration.
            recorder.Frames(45, 0.1f);
            Assert.Greater(recorder.Playback.Progress, 0.5f);
        }

        [Test]
        public void PreservedSpeed_StaysWithinTheMaximum()
        {
            MultiPathData sequence = _scene.CreateSequence();
            PathData quick = _scene.CreatePath(PathMovementSettings.Time(PathConstants.MIN_MOVE_VALUE), EPathCurveType.Linear,
                Vector3.zero, new Vector3(20f, 0f, 0f));
            PathData next = _scene.CreatePath(PathMovementSettings.Speed(5f), EPathCurveType.Linear,
                new Vector3(20f, 0f, 0f), new Vector3(40f, 0f, 0f));
            sequence.SetSegments(new[] { new PathSegmentAuthoring(quick), new PathSegmentAuthoring(next, true) });
            var recorder = new RecordingPlayback();
            recorder.Playback.Play(PathPlaybackRequest.Sequence(sequence));

            recorder.Playback.TickMovement(0.0011f);

            Assert.AreEqual(1, recorder.Playback.SegmentIndex);
            Assert.AreEqual(PathConstants.MAX_MOVE_VALUE, recorder.Playback.Speed);
            Assert.DoesNotThrow(() => recorder.Playback.SetSpeed(recorder.Playback.Speed));
        }

        private PathEventSettingSO PauseWithDelayedResume(float delay)
        {
            PathEventSettingSO resume = _scene.CreateEvent("resume");
            resume.Lifecycle = EPathEventLifecycle.Resume;
            PathEventSettingSO pause = _scene.CreateEvent("pause");
            pause.Lifecycle = EPathEventLifecycle.Pause;
            pause.DelayedEvents.Add(new PathEventSettingSO.DelayedEventEntry { Delay = delay, EventSetting = resume });
            return pause;
        }

        [Test]
        public void CommandPause_IsNotUndoneByAnEventsDelayedResume()
        {
            PathData path = Line(PathMovementSettings.Speed(10f));
            PathEventSettingSO pause = PauseWithDelayedResume(0.5f);
            _scene.SetEvents(path, (0.2f, pause));
            var recorder = new RecordingPlayback();
            recorder.Playback.Play(PathPlaybackRequest.Single(path));
            recorder.Frames(15);
            Assert.AreEqual(EPathFollowerState.Paused, recorder.Playback.State);

            // The user pauses the event-paused playback: it now waits for Resume.
            Assert.IsFalse(recorder.Playback.Pause());
            recorder.Frames(60);
            Assert.AreEqual(EPathFollowerState.Paused, recorder.Playback.State);

            Assert.IsTrue(recorder.Playback.Resume());
            Assert.AreEqual(EPathFollowerState.Moving, recorder.Playback.State);
        }

        [Test]
        public void CommandPause_IsNotUndoneByATriggeredResume()
        {
            var recorder = new RecordingPlayback();
            recorder.Playback.Play(PathPlaybackRequest.Single(Line(PathMovementSettings.Speed(1f))));
            recorder.Frame();
            PathEventSettingSO resume = _scene.CreateEvent("resume");
            resume.Lifecycle = EPathEventLifecycle.Resume;

            Assert.IsTrue(recorder.Playback.Pause());
            recorder.Playback.TriggerEvent(PathEventDefinition.FromAuthoring(resume));

            Assert.AreEqual(EPathFollowerState.Paused, recorder.Playback.State);
        }

        [Test]
        public void EventPause_IsUndoneByAnEventResumeOrTheCommand()
        {
            PathData path = Line(PathMovementSettings.Speed(10f));
            _scene.SetEvents(path, (0.2f, PauseWithDelayedResume(0.5f)));
            var recorder = new RecordingPlayback();
            recorder.Playback.Play(PathPlaybackRequest.Single(path));
            recorder.Frames(15);
            Assert.AreEqual(EPathFollowerState.Paused, recorder.Playback.State);
            recorder.Frames(40);
            Assert.AreEqual(EPathFollowerState.Moving, recorder.Playback.State);

            recorder.Playback.Seek(0f);
            recorder.Frames(15);
            Assert.AreEqual(EPathFollowerState.Paused, recorder.Playback.State);
            Assert.IsTrue(recorder.Playback.Resume());
        }

        [TestCase(EPathMoveType.SpeedBased)]
        [TestCase(EPathMoveType.TimeBased)]
        public void SetCommand_CancelsAnEventBlendOfItsChannel(EPathMoveType moveType)
        {
            bool speed = moveType == EPathMoveType.SpeedBased;
            PathData path = Line(speed ? PathMovementSettings.Speed(1f) : PathMovementSettings.Time(10f));
            PathEventSettingSO change = _scene.CreateEvent("change");
            change.UseSpeedChange = speed;
            change.SpeedTarget = 4f;
            change.SpeedBlendTime = 2f;
            change.UseDurationChange = !speed;
            change.DurationTarget = 4f;
            change.DurationBlendTime = 2f;
            _scene.SetEvents(path, (0f, change));
            var recorder = new RecordingPlayback();
            recorder.Playback.Play(PathPlaybackRequest.Single(path));
            recorder.Frames(10);
            Assert.IsTrue(recorder.Playback.HasPendingEffects);

            if (speed)
                recorder.Playback.SetSpeed(2f);
            else
                recorder.Playback.SetDuration(7f);
            recorder.Frames(60);

            Assert.IsFalse(recorder.Playback.HasPendingEffects);
            Assert.AreEqual(speed ? 2f : 7f, speed ? recorder.Playback.Speed : recorder.Playback.Duration, 1e-5f);
        }

        [Test]
        public void SeekWhilePaused_UsesTheCurrentGeometry()
        {
            PathData path = Line(PathMovementSettings.Speed(2f));
            var recorder = new RecordingPlayback();
            recorder.Playback.Play(PathPlaybackRequest.Single(path));
            recorder.Frame();
            recorder.Playback.Pause();

            path.transform.GetChild(1).position = new Vector3(20f, 0f, 0f);
            path.Rebuild();
            recorder.Playback.Seek(0.5f);

            Assert.AreEqual(new Vector3(10f, 0f, 0f), recorder.Playback.SamplePosition());
        }

        [Test]
        public void SeekAfterADefinitionChange_ThrowsAndChangesNothing()
        {
            MultiPathData sequence = Sequence(PathMovementSettings.Speed(2f), PathMovementSettings.Speed(2f), 4f, 4f);
            var recorder = new RecordingPlayback();
            recorder.Playback.Play(PathPlaybackRequest.Sequence(sequence));
            recorder.Frames(30);
            recorder.Playback.Pause();
            float progress = recorder.Playback.Progress;
            int epoch = recorder.Playback.MotionEpoch;

            sequence.GetSegmentPath(0).MovementSettings = PathMovementSettings.Speed(3f);

            Assert.Throws<InvalidOperationException>(() => recorder.Playback.Seek(0.8f));
            Assert.Throws<InvalidOperationException>(() => recorder.Playback.SeekSegment(1, 0.5f));
            Assert.AreEqual(progress, recorder.Playback.Progress);
            Assert.AreEqual(epoch, recorder.Playback.MotionEpoch);
            Assert.AreEqual(EPathFollowerState.Paused, recorder.Playback.State);
        }

        [Test]
        public void LastEnd_TellsWhyThePlaybackEnded()
        {
            PathData path = Line(PathMovementSettings.Speed(10f));
            var recorder = new RecordingPlayback();
            recorder.Playback.Play(PathPlaybackRequest.Single(path));
            Assert.AreEqual(EPathPlaybackEnd.None, recorder.Playback.LastEnd);
            recorder.Frames(70);
            Assert.AreEqual(EPathPlaybackEnd.Completed, recorder.Playback.LastEnd);

            recorder.Playback.Play(PathPlaybackRequest.Single(path));
            recorder.Playback.Stop();
            Assert.AreEqual(EPathPlaybackEnd.Stopped, recorder.Playback.LastEnd);

            recorder.Playback.Play(PathPlaybackRequest.Single(path));
            path.MovementSettings = PathMovementSettings.Speed(3f);
            recorder.Frame();
            Assert.AreEqual(EPathPlaybackEnd.RouteChanged, recorder.Playback.LastEnd);

            recorder.Playback.Play(PathPlaybackRequest.Single(path));
            path.SetPathPoints(new Transform[0]);
            recorder.Frame();
            Assert.AreEqual(EPathPlaybackEnd.RouteUnavailable, recorder.Playback.LastEnd);
            UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("could not build"));
        }

        [Test]
        public void SeekPastTheCap_HoldsThere_AndKeepsItsProgress()
        {
            var recorder = new RecordingPlayback();
            recorder.Playback.Play(PathPlaybackRequest.Single(Line(PathMovementSettings.Speed(2f))));
            recorder.Playback.Limit = new PathMotionLimit(false, 1f, 0.5f, false);

            recorder.Playback.Seek(0.8f);
            recorder.Frames(10);

            Assert.AreEqual(0.8f, recorder.Playback.Progress, 1e-6f);
            Assert.AreEqual(0.8f, recorder.Playback.SegmentProgress, 1e-6f);
        }

        [Test]
        public void StopAfterCompletion_KeepsTheCompletedEnd()
        {
            var recorder = new RecordingPlayback();
            recorder.Playback.Play(PathPlaybackRequest.Single(Line(PathMovementSettings.Speed(10f))));
            recorder.Frames(70);

            Assert.IsTrue(recorder.Playback.Stop());

            Assert.AreEqual(EPathFollowerState.Ready, recorder.Playback.State);
            Assert.AreEqual(EPathPlaybackEnd.Completed, recorder.Playback.LastEnd);
        }

        [Test]
        public void SeekWhilePaused_ReportsTheNewGeometryAsAChange()
        {
            PathData path = Line(PathMovementSettings.Speed(2f));
            var recorder = new RecordingPlayback();
            recorder.Playback.Play(PathPlaybackRequest.Single(path));
            recorder.Frames(60);
            recorder.Playback.Pause();
            path.transform.GetChild(1).position += new Vector3(0f, 0f, 4f);
            path.Rebuild();

            Assert.IsTrue(recorder.Playback.Seek(recorder.Playback.Progress));
            Assert.IsFalse(recorder.Playback.Seek(recorder.Playback.Progress));
        }

        [Test]
        public void GeometryChange_KeepsTheLocalProgress_AndTheSpeedInMetres()
        {
            PathData path = Line(PathMovementSettings.Speed(2f));
            var recorder = new RecordingPlayback();
            recorder.Playback.Play(PathPlaybackRequest.Single(path));
            recorder.Frames(30);
            Assert.AreEqual(0.1f, recorder.Playback.Progress, 1e-5f);

            path.transform.GetChild(1).position = new Vector3(20f, 0f, 0f);
            path.Rebuild();
            recorder.Frame();

            // Still a tenth of the way, now of 20 m, then one frame at 2 m/s.
            Assert.AreEqual(0.1f + 2f / 60f / 20f, recorder.Playback.Progress, 1e-5f);
        }

        [Test]
        public void SubMillimetreSegment_KeepsItsProgressThroughSeekAndGrowth()
        {
            PathData path = _scene.CreatePath(PathMovementSettings.Speed(0.001f), EPathCurveType.Linear,
                Vector3.zero, new Vector3(0.0005f, 0f, 0f));
            var recorder = new RecordingPlayback();
            recorder.Playback.Play(PathPlaybackRequest.Single(path));
            recorder.Playback.Pause();
            recorder.Playback.Seek(0.5f);
            recorder.Playback.Resume();

            path.transform.GetChild(1).position = new Vector3(1f, 0f, 0f);
            path.Rebuild();
            recorder.Frame();

            Assert.AreEqual(0.5f, recorder.Playback.Progress, 1e-4f);
        }

        [Test]
        public void GeometryChangingEveryFrame_KeepsASlowFollowerMoving()
        {
            PathData path = _scene.CreatePath(PathMovementSettings.Speed(0.003f), EPathCurveType.Linear,
                Vector3.zero, new Vector3(2000f, 0f, 0f));
            Transform end = path.transform.GetChild(1);
            var recorder = new RecordingPlayback();
            recorder.Playback.Play(PathPlaybackRequest.Single(path));
            recorder.Playback.Seek(0.6f);
            float before = recorder.Playback.Progress;

            for (int i = 0; i < 600; i++)
            {
                end.position += new Vector3(i % 2 == 0 ? 0.001f : -0.001f, 0f, 0f);
                path.Rebuild();
                recorder.Frame();
            }

            // Ten seconds at 0.003 m/s.
            Assert.AreEqual(0.03f, (recorder.Playback.Progress - before) * 2000f, 0.002f);
        }

        [Test]
        public void FramesAtTheTransitionLimit_CarryAtMostOneFrame()
        {
            PathData path = _scene.CreatePath(PathMovementSettings.Speed(9999f), EPathCurveType.Linear,
                Vector3.zero, new Vector3(0.1f, 0f, 0f));
            var recorder = new RecordingPlayback();
            recorder.Playback.Play(PathPlaybackRequest.Single(path, true));
            // Each frame covers about 1600 laps, far past the limit.
            recorder.Frames(120);
            recorder.Playback.SetSpeed(0.01f);
            float before = recorder.Playback.Progress;

            recorder.Frame();

            float moved = Mathf.Repeat(recorder.Playback.Progress - before, 1f) * 0.1f;
            // This frame and at most one carried frame.
            Assert.LessOrEqual(moved, 0.01f * 2f / 60f + 1e-5f);
        }

        [Test]
        public void PathMovedInStepsBelowTheTolerance_IsPublishedOnceTheStepsAddUp()
        {
            PathData path = Line(PathMovementSettings.Speed(2f));
            Transform end = path.transform.GetChild(1);
            int revision = path.Revision;

            // Each step alone counts as the same path; together they move the end by 4.5 cm.
            for (int i = 0; i < 5000; i++)
            {
                end.position += new Vector3(0.9e-5f, 0f, 0f);
                path.Rebuild();
            }

            Assert.Greater(path.Revision, revision);
            Assert.AreEqual(end.position.x, path.Sample(1f).x, 2e-5f);
            Assert.AreEqual(end.position.x, path.PathLength, 2e-5f);
        }
    }
}
