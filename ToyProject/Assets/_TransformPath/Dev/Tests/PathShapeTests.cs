using System;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Common.TransformPath.Tests
{
    public sealed class PathShapeTests : PathBoundaryIsolatedTest
    {
        private GameObject _host;

        [SetUp]
        public void SetUp() => _host = new GameObject("Shape");

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_host);

        private TestCircleShape CreateShape(float radius, PathMovementSettings movement)
        {
            TestCircleShape shape = _host.AddComponent<TestCircleShape>();
            shape.Radius = radius;
            shape.MovementSettings = movement;
            shape.Rebuild();
            return shape;
        }

        [Test]
        public void Shape_MovesSpeedTimesTimeAlongItsArc()
        {
            TestCircleShape shape = CreateShape(2f, PathMovementSettings.Speed(2f));
            var recorder = new RecordingPlayback();
            recorder.Playback.Play(PathPlaybackRequest.Shape(shape));

            recorder.Frames(60);

            Assert.AreEqual(4f * Mathf.PI, shape.PathLength, 1e-4f);
            Assert.AreEqual(2f, recorder.Playback.Progress * shape.PathLength, 1e-3f);
            // Two units along a circle of radius two is one radian.
            Vector3 expected = new Vector3(Mathf.Cos(1f), 0f, Mathf.Sin(1f)) * 2f;
            Assert.Less(Vector3.Distance(expected, recorder.Playback.SamplePosition()), 1e-3f);
        }

        [Test]
        public void Shape_PublishesGeometryAndDefinitionRevisionsApart()
        {
            TestCircleShape shape = CreateShape(2f, PathMovementSettings.Speed(2f));
            int revision = shape.Revision;
            int definition = shape.DefinitionRevision;

            shape.Rebuild();
            Assert.AreEqual(revision, shape.Revision);

            shape.Radius = 3f;
            shape.Rebuild();
            Assert.AreEqual(revision + 1, shape.Revision);
            Assert.AreEqual(definition, shape.DefinitionRevision);
            Assert.AreEqual(6f * Mathf.PI, shape.PathLength, 1e-4f);

            shape.MovementSettings = PathMovementSettings.Speed(4f);
            Assert.AreEqual(revision + 2, shape.Revision);
            Assert.AreEqual(definition + 1, shape.DefinitionRevision);
        }

        [Test]
        public void Shape_ThrowingEvaluate_FailsTheBuild_UntilItIsFixed()
        {
            TestCircleShape shape = CreateShape(2f, PathMovementSettings.Speed(2f));
            int revision = shape.Revision;
            LogAssert.Expect(LogType.Error, new Regex("could not build: Evaluate\\(0\\) threw InvalidOperationException"));

            shape.BreakEvaluate = true;
            shape.Rebuild();

            Assert.IsFalse(shape.IsReady);
            var recorder = new RecordingPlayback();
            Assert.Throws<InvalidOperationException>(() => recorder.Playback.Play(PathPlaybackRequest.Shape(shape)));
            shape.BreakEvaluate = false;
            shape.Rebuild();
            Assert.IsTrue(shape.IsReady);
            // What TryPrepare kept may have changed during the failed build.
            Assert.AreEqual(revision + 1, shape.Revision);
        }

        [Test]
        public void Shape_UnmeasurableLength_FailsTheBuild()
        {
            TestCircleShape shape = CreateShape(2f, PathMovementSettings.Speed(2f));
            LogAssert.Expect(LogType.Error, new Regex("could not build: TryPrepare returned a length of NaN"));

            shape.BreakLength = true;
            shape.Rebuild();

            Assert.IsFalse(shape.IsReady);
        }
    }
}
