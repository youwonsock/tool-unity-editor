using NUnit.Framework;

namespace Common.TransformPath.Tests.EditMode
{
    public sealed class PathPlaybackEngineTests
    {
        [Test]
        public void PlaybackIdentityAndRevisionRemainMonotonic()
        {
            PathPlaybackEngine engine = new PathPlaybackEngine();
            PathCommandReceipt before = engine.CreateReceipt(false);

            engine.BeginPlayback();
            PathCommandReceipt started = engine.CreateReceipt(true);
            engine.InvalidatePlayback();
            PathCommandReceipt released = engine.CreateReceipt(true);

            Assert.That(before.PlaybackId, Is.EqualTo(0UL));
            Assert.That(started.PlaybackId, Is.GreaterThan(0UL));
            Assert.That(released.PlaybackId, Is.EqualTo(0UL));
            Assert.That(started.StateRevision, Is.GreaterThan(before.StateRevision));
            Assert.That(released.StateRevision, Is.GreaterThan(started.StateRevision));
        }

        [Test]
        public void RuntimeEventDefinitionCopiesAuthoringCurve()
        {
            PathEventDefinition definition = new PathEventDefinition(
                "test",
                false,
                1f,
                1f,
                UnityEngine.AnimationCurve.Linear(0f, 0f, 1f, 1f),
                false,
                1f,
                1f,
                UnityEngine.AnimationCurve.Linear(0f, 0f, 1f, 1f),
                false,
                1f,
                1f,
                UnityEngine.AnimationCurve.Linear(0f, 0f, 1f, 1f));

            Assert.That(definition.DelayedEvents, Is.Empty);
            Assert.That(definition.EventName, Is.EqualTo("test"));
        }
    }
}
