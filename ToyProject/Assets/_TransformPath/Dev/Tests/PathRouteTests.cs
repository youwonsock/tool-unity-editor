using NUnit.Framework;
using UnityEngine;

namespace Common.TransformPath.Tests
{
    public sealed class PathRouteTests : PathBoundaryIsolatedTest
    {
        private static PathRoute ThreeSegments()
        {
            var provider = new LineProvider(10f);
            var movement = PathMovementSettings.Speed(1f);
            return PathRoute.FromProvider(provider, new[]
            {
                new PathRoute.Segment(new LineProvider(2f), 0f, 2f, movement, false, null),
                new PathRoute.Segment(new LineProvider(3f), 2f, 3f, movement, false, null),
                new PathRoute.Segment(new LineProvider(5f), 5f, 5f, movement, true, null),
            }, true);
        }

        [Test]
        public void GlobalProgress_IsWeightedByLength()
        {
            PathRoute route = ThreeSegments();

            Assert.AreEqual(10f, route.TotalLength, 1e-5f);
            Assert.AreEqual(0.35f, route.GetGlobalProgress(1, 0.5f), 1e-5f);
            Assert.AreEqual(0.5f, route.GetLocalProgress(1, 0.35f), 1e-5f);
        }

        [Test]
        public void SegmentEnds_AreHalfOpen_AndTheLastIncludesOne()
        {
            PathRoute route = ThreeSegments();

            Assert.AreEqual(0, route.FindSegment(0.199f));
            Assert.AreEqual(1, route.FindSegment(0.2f));
            Assert.AreEqual(2, route.FindSegment(0.5f));
            Assert.AreEqual(2, route.FindSegment(1f));
        }

        [Test]
        public void SingleSegment_KeepsProgressExact()
        {
            var provider = new LineProvider(7f);
            PathRoute route = PathRoute.Aggregate(provider, PathMovementSettings.Speed(1f));

            Assert.IsFalse(route.IsSequence);
            Assert.AreEqual(0.123f, route.GetGlobalProgress(0, 0.123f));
            Assert.AreEqual(0.123f, route.GetLocalProgress(0, 0.123f));
        }

        [Test]
        public void Refresh_RebuildsFromTheProvidersCurrentRoute()
        {
            var provider = new LineProvider(4f);
            PathRoute route = provider.GetRoute();
            provider.PathLength = 6f;
            provider.Revision = 2;

            PathRoute refreshed = route.Refresh();

            Assert.AreEqual(2, refreshed.Revision);
            Assert.AreEqual(6f, refreshed.TotalLength, 1e-5f);
        }

        [Test]
        public void TimeCurve_InverseMatchesEvaluation()
        {
            var movement = PathMovementSettings.Time(2f, AnimationCurve.EaseInOut(0f, 0f, 1f, 1f));

            foreach (float progress in new[] { 0.1f, 0.5f, 0.9f })
                Assert.AreEqual(progress, movement.EvaluateProgress(movement.InverseProgress(progress)), 1e-4f);
        }

        [Test]
        public void MovementSettings_KeepTheirOwnCurve()
        {
            var curve = AnimationCurve.Linear(0f, 0f, 1f, 1f);
            var movement = PathMovementSettings.Time(2f, curve);
            curve.MoveKey(1, new Keyframe(1f, 0.5f));

            Assert.AreEqual(1f, movement.EvaluateProgress(1f), 1e-5f);
            Assert.IsTrue(movement.TryValidate(out _));
        }
    }
}
