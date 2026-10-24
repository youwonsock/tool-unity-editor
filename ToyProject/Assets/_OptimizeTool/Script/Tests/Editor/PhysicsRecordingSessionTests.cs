#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Common.OptimizeTool.Tests
{
    public sealed class PhysicsRecordingSessionTests
    {
        private static PhysicsPoseSample[] Pose(float time, float x, Quaternion? rotation = null) =>
            new[] { new PhysicsPoseSample(time, Vector3.right * x, rotation ?? Quaternion.identity) };

        private static PhysicsRecordingSession Session(float duration, float interval = 0.1f, bool onlyMoved = true) =>
            new(new[] { "Body" }, Pose(0f, 0f), duration, interval, onlyMoved, 0.001f, 0.001f);

        [TestCase("paths")]
        [TestCase("times")]
        [TestCase("nonfinite-time")]
        [TestCase("poses")]
        [TestCase("rotation")]
        public void MalformedReferenceIsRejectedBeforeComparison(string damage)
        {
            var session = new PhysicsRecordingSession(new[] { "", "Body" },
                new[] { Pose(0f, 0f)[0], Pose(0f, 1f)[0] }, 0.2f, 0.1f, true, 0.001f, 0.001f);
            session.Observe(0.1f, new[] { Pose(0.1f, 0f)[0], Pose(0.1f, 2f)[0] });
            session.Observe(0.2f, new[] { Pose(0.2f, 0f)[0], Pose(0.2f, 3f)[0] });
            var clip = new AnimationClip { legacy = true };
            clip.SetCurve("", typeof(Transform), "m_LocalPosition.x", AnimationCurve.Linear(0f, 0f, 0.2f, 1f));
            var reference = ScriptableObject.CreateInstance<PhysicsRecordingReference>();
            try
            {
                reference.Initialize(clip, session.Take);
                Assert.DoesNotThrow(() => reference.Validate(clip));
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var type = typeof(PhysicsRecordingReference);
                if (damage == "paths") type.GetField("_paths", flags).SetValue(reference, new[] { "", "" });
                if (damage == "times") type.GetField("_times", flags).SetValue(reference, new[] { 0f, 0.2f, 0.1f });
                if (damage == "nonfinite-time") type.GetField("_times", flags).SetValue(reference, new[] { 0f, float.NaN, 0.2f });
                var field = type.GetField("_poses", flags);
                var poses = (Array)field.GetValue(reference);
                if (damage == "poses") field.SetValue(reference, Array.CreateInstance(poses.GetType().GetElementType(), 1));
                if (damage == "rotation")
                {
                    object pose = poses.GetValue(0);
                    pose.GetType().GetField("Rotation").SetValue(pose, new Quaternion());
                    poses.SetValue(pose, 0);
                }
                Assert.Throws<InvalidOperationException>(() => reference.Validate(clip));
            }
            finally { UnityEngine.Object.DestroyImmediate(reference); UnityEngine.Object.DestroyImmediate(clip); }
        }

        [Test]
        public void FrameSamplingAndDirectObservationRetainTheOriginalReferencePoses()
        {
            var session = new PhysicsRecordingSession(new[] { "" }, Pose(0f, 0f), 0.2f, 0.2f, true, 0.001f, 0.001f);
            session.Observe(0.1f, Pose(0.1f, 2f));
            session.Observe(0.2f, Pose(0.2f, 5f));
            var reference = ScriptableObject.CreateInstance<PhysicsRecordingReference>();
            try
            {
                reference.Initialize(null, session.Take);
                var frame = new PhysicsPoseSample[1];
                foreach (float time in new[] { 0f, 0.05f, 0.1f, 0.17f, 0.2f, 0.3f })
                {
                    reference.SampleFrame(time, frame);
                    Assert.AreEqual(reference.Sample(0, time).Position, frame[0].Position);
                    Assert.AreEqual(reference.Sample(0, time).Rotation, frame[0].Rotation);
                }
                Assert.AreEqual(2f, reference.Observation(0, 1).Position.x);
                Assert.AreEqual(0.1f, reference.Observation(0, 1).Time);
            }
            finally { UnityEngine.Object.DestroyImmediate(reference); }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void IdleBoundariesAndInitialFinalPosesAreRetained(bool onlyMoved)
        {
            var session = Session(0.8f, onlyMoved: onlyMoved);
            session.Observe(0.1f, Pose(0.1f, 0f));
            session.Observe(0.2f, Pose(0.2f, 0f));
            session.Observe(0.3f, Pose(0.3f, 1f));
            session.Observe(0.4f, Pose(0.4f, 1f));
            session.Observe(0.5f, Pose(0.5f, 1f));
            session.Observe(0.6f, Pose(0.6f, 2f));
            Assert.IsTrue(session.Observe(0.8f, Pose(0.8f, 2f)));
            var keys = session.Take.Tracks[0].Keys;
            Assert.AreEqual(0f, keys[0].Time);
            Assert.AreEqual(0f, keys[0].Position.x);
            Assert.AreEqual(0.8f, keys[keys.Count - 1].Time);
            Assert.AreEqual(2f, keys[keys.Count - 1].Position.x);
            Assert.IsTrue(keys.Any(k => k.Time == 0.2f && k.Position.x == 0f));
            Assert.IsTrue(keys.Any(k => k.Time == 0.5f && k.Position.x == 1f));
            Assert.AreEqual(onlyMoved ? 6 : 8, keys.Count);
        }

        [Test]
        public void EndpointUsesThePreviousObservationEvenWhenItWasNotStoredAsAKey()
        {
            var session = Session(0.05f, 0.03f);
            session.Observe(0.02f, Pose(0.02f, 2f));
            session.Observe(0.04f, Pose(0.04f, 4f));
            session.Observe(0.06f, Pose(0.06f, 6f));
            var last = session.Take.Tracks[0].Keys.Last();
            Assert.That(last.Time, Is.EqualTo(0.05f));
            Assert.That(last.Position.x, Is.EqualTo(5f).Within(0.00001f));
        }

        [Test]
        public void SamplingIntervalRemainsAnchoredToTheStartTime()
        {
            var session = Session(0.12f, 0.03f, false);
            for (int i = 1; i <= 6; i++) session.Observe(i * 0.02f, Pose(i * 0.02f, i));
            float[] times = session.Take.Tracks[0].Keys.Select(k => k.Time).ToArray();
            Assert.That(times, Is.EqualTo(new[] { 0f, 0.04f, 0.06f, 0.1f, 0.12f }).Within(0.00001f));
        }

        [Test]
        public void QuaternionSignsStayContinuousAcrossAnIdleSpan()
        {
            var session = Session(0.3f);
            session.Observe(0.1f, Pose(0.1f, 0f, new Quaternion(0f, 0f, 0f, -1f)));
            session.Observe(0.2f, Pose(0.2f, 1f, Quaternion.Euler(0f, 20f, 0f)));
            session.Observe(0.3f, Pose(0.3f, 1f, Quaternion.Euler(0f, 20f, 0f)));
            var keys = session.Take.Tracks[0].Keys;
            for (int i = 1; i < keys.Count; i++) Assert.GreaterOrEqual(Quaternion.Dot(keys[i - 1].Rotation, keys[i].Rotation), 0f);
        }

        [Test]
        public void CompletedTakeCannotBeMutatedThroughTheSessionOrPublishedLists()
        {
            var session = Session(0.1f);
            session.Observe(0.1f, Pose(0.1f, 2f));
            var take = session.Take;
            Assert.Throws<InvalidOperationException>(() => session.Observe(0.2f, Pose(0.2f, 3f)));
            Assert.Throws<InvalidOperationException>(() => session.Stop(0.2f, Pose(0.2f, 3f)));
            Assert.Throws<NotSupportedException>(() => ((IList<PhysicsPoseSample>)take.Tracks[0].Keys)[0] = Pose(0f, 9f)[0]);
            Assert.AreEqual(2f, take.Tracks[0].Keys.Last().Position.x);
        }

        [Test]
        public void SmallRotationsAboveTheConfiguredThresholdAreNotDiscarded()
        {
            var session = Session(0.3f);
            session.Observe(0.1f, Pose(0.1f, 0f, Quaternion.Euler(0f, 0.02f, 0f)));
            session.Observe(0.2f, Pose(0.2f, 0f, Quaternion.Euler(0f, 0.04f, 0f)));
            session.Observe(0.3f, Pose(0.3f, 0f, Quaternion.Euler(0f, 0.06f, 0f)));
            Assert.AreEqual(4, session.Take.Tracks[0].Keys.Count);
            Assert.That(PhysicsPoseSample.RotationDifference(Quaternion.identity, Quaternion.Euler(0f, 0.02f, 0f)),
                Is.EqualTo(0.02f).Within(0.00001f));
            Quaternion q = Quaternion.Euler(27f, 41f, 13f);
            Assert.Less(PhysicsPoseSample.RotationDifference(q, new Quaternion(-q.x, -q.y, -q.z, -q.w)), 0.00001f);
        }

        [Test]
        public void ReferenceKeepsEveryObservationAndTheInterpolatedEndpointIndependentlyOfKeyReduction()
        {
            var session = Session(0.05f, 0.04f);
            session.Observe(0.02f, Pose(0.02f, 2f));
            session.Observe(0.04f, Pose(0.04f, 4f));
            session.Observe(0.06f, Pose(0.06f, 6f));
            var track = session.Take.Tracks[0];
            Assert.AreEqual(3, track.Keys.Count);
            Assert.AreEqual(4, track.Observations.Count);
            Assert.That(track.Observations.Select(p => p.Time), Is.EqualTo(new[] { 0f, 0.02f, 0.04f, 0.05f }).Within(0.00001f));
            Assert.That(track.Observations.Last().Position.x, Is.EqualTo(5f).Within(0.00001f));
            Assert.Throws<NotSupportedException>(() => ((IList<PhysicsPoseSample>)track.Observations)[0] = Pose(0f, 9f)[0]);
        }

        [Test]
        public void ManualStopAtTheSameTimeReplacesTheBoundaryKey()
        {
            var session = Session(1f);
            session.Observe(0.1f, Pose(0.1f, 1f));
            session.Stop(0.1f, Pose(0.1f, 2f));
            Assert.AreEqual(0.1f, session.Take.Duration);
            Assert.AreEqual(2, session.Take.Tracks[0].Keys.Count);
            Assert.AreEqual(2f, session.Take.Tracks[0].Keys.Last().Position.x);
        }
    }
}
#endif
