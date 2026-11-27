using NUnit.Framework;
using UnityEngine;

namespace Common.TransformPath.Tests
{
    public sealed class PathQueueTests : PathBoundaryIsolatedTest
    {
        private static PathQueueStatus Status(bool blocked, float multiplier, float? distance) =>
            new PathQueueStatus(null, distance, 1.5f, blocked, multiplier, 1f);

        [Test]
        public void SpeedMultiplier_ShortOfTheSpacing_KeepsAnApproachSpeed()
        {
            AnimationCurve ease = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

            Assert.AreEqual(0f, PathQueueCoordinator.CalculateSpeedMultiplier(true, 1.5f, 1.5f, 1.5f, 0f, ease));
            Assert.AreEqual(PathConstants.MIN_APPROACH_SPEED_MULTIPLIER,
                PathQueueCoordinator.CalculateSpeedMultiplier(true, 1.501f, 1.5f, 1.5f, 0f, ease), 1e-6f);
            Assert.AreEqual(0.5f, PathQueueCoordinator.CalculateSpeedMultiplier(true, 2.25f, 1.5f, 1.5f, 0f, ease), 1e-5f);
        }

        [Test]
        public void SpeedMultiplier_SlowsBetweenSpacingAndSlowdownDistance()
        {
            AnimationCurve linear = AnimationCurve.Linear(0f, 0f, 1f, 1f);

            Assert.AreEqual(1f, PathQueueCoordinator.CalculateSpeedMultiplier(true, null, 1.5f, 1.5f, 0.1f, linear));
            Assert.AreEqual(0f, PathQueueCoordinator.CalculateSpeedMultiplier(true, 1.5f, 1.5f, 1.5f, 0.1f, linear));
            Assert.AreEqual(0.55f, PathQueueCoordinator.CalculateSpeedMultiplier(true, 2.25f, 1.5f, 1.5f, 0.1f, linear), 1e-5f);
            Assert.AreEqual(1f, PathQueueCoordinator.CalculateSpeedMultiplier(true, 3f, 1.5f, 1.5f, 0.1f, linear));
            Assert.AreEqual(1f, PathQueueCoordinator.CalculateSpeedMultiplier(false, 1f, 1.5f, 1.5f, 0.1f, linear));
        }

        [Test]
        public void MaxProgress_StaysASpacingBehindTheMemberAhead()
        {
            Assert.AreEqual(1f, PathQueueCoordinator.CalculateMaxProgress(true, null, 1.5f, 30f));
            Assert.AreEqual(0.45f, PathQueueCoordinator.CalculateMaxProgress(true, 0.5f, 1.5f, 30f), 1e-5f);
            Assert.AreEqual(1f, PathQueueCoordinator.CalculateMaxProgress(false, 0.5f, 1.5f, 30f));
        }

        [Test]
        public void Wait_RestartsOnlyAfterTheDelayAndExtraDistance()
        {
            PathQueueWait wait = PathQueueWait.Free;
            wait.Step(0.1f, Status(true, 0f, 1f), 1.5f, 0.2f, 0.3f, 0f);
            Assert.IsTrue(wait.IsBlocked);

            // Free again, but too close for the hysteresis.
            wait.Step(0.25f, Status(false, 1f, 1.6f), 1.5f, 0.2f, 0.3f, 0f);
            Assert.IsTrue(wait.IsBlocked);
            Assert.AreEqual(0f, wait.SpeedMultiplier);

            wait.Step(0.1f, Status(false, 1f, 2f), 1.5f, 0.2f, 0.3f, 0f);
            Assert.IsFalse(wait.IsBlocked);
            Assert.AreEqual(1f, wait.SpeedMultiplier);
        }

        [Test]
        public void Wait_HoldsWhileManuallyBlocked()
        {
            PathQueueWait wait = PathQueueWait.Free;
            wait.ManualBlock = true;
            wait.Step(1f, Status(false, 1f, null), 1.5f, 0f, 0f, 0f);
            Assert.IsTrue(wait.IsBlocked);

            wait.ManualBlock = false;
            wait.Step(1f, Status(false, 1f, null), 1.5f, 0f, 0f, 0f);
            Assert.IsFalse(wait.IsBlocked);
        }

        [Test]
        public void Wait_SmoothsTheSpeedMultiplier()
        {
            PathQueueWait wait = PathQueueWait.Free;
            wait.SpeedMultiplier = 0f;

            wait.Step(0.1f, Status(false, 1f, null), 1.5f, 0f, 0f, 5f);

            Assert.AreEqual(0.5f, wait.SpeedMultiplier, 1e-5f);
        }

        [Test]
        public void StableSort_KeepsTheOrderOfEqualItems()
        {
            var items = new System.Collections.Generic.List<(int key, string name)>
            {
                (2, "a"), (1, "b"), (2, "c"), (1, "d"),
            };

            bool moved = PathSortUtility.StableInsertionSort(items, (l, r) => l.key.CompareTo(r.key));

            Assert.IsTrue(moved);
            CollectionAssert.AreEqual(new[] { "b", "d", "a", "c" }, items.ConvertAll(i => i.name));
        }

        [Test]
        public void SpeedMultiplier_TreatsAnEmptyCurveAsLinear()
        {
            Assert.AreEqual(0.55f, PathQueueCoordinator.CalculateSpeedMultiplier(true, 2.25f, 1.5f, 1.5f, 0.1f, new AnimationCurve()), 1e-5f);
            Assert.AreEqual(0.55f, PathQueueCoordinator.CalculateSpeedMultiplier(true, 2.25f, 1.5f, 1.5f, 0.1f, null), 1e-5f);
        }

        [Test]
        public void SpeedMultiplier_SlowdownMovesWithAWiderSpacing()
        {
            AnimationCurve linear = AnimationCurve.Linear(0f, 0f, 1f, 1f);

            // A policy doubled the spacing to 3: the slowdown runs from 3 to 4.5.
            Assert.AreEqual(0f, PathQueueCoordinator.CalculateSpeedMultiplier(true, 3f, 3f, 1.5f, 0.1f, linear));
            Assert.AreEqual(0.55f, PathQueueCoordinator.CalculateSpeedMultiplier(true, 3.75f, 3f, 1.5f, 0.1f, linear), 1e-5f);
            Assert.AreEqual(1f, PathQueueCoordinator.CalculateSpeedMultiplier(true, 4.5f, 3f, 1.5f, 0.1f, linear));
            // No range: full speed until the spacing.
            Assert.AreEqual(1f, PathQueueCoordinator.CalculateSpeedMultiplier(true, 3.1f, 3f, 0f, 0.1f, linear));
        }

        [Test]
        public void SerializedManager_KeepsAUsableSlowdownCurve()
        {
            var source = new GameObject("Queue Source");
            source.SetActive(false);
            try
            {
                source.AddComponent<QueuedPathManager>();
                GameObject copy = Object.Instantiate(source);
                try
                {
                    var curve = (AnimationCurve)typeof(QueuedPathManager)
                        .GetField("_slowdownCurve", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                        .GetValue(copy.GetComponent<QueuedPathManager>());
                    Assert.IsTrue(PathValueUtility.IsUsableCurve(curve));
                }
                finally
                {
                    Object.DestroyImmediate(copy);
                }
            }
            finally
            {
                Object.DestroyImmediate(source);
            }
        }
    }
}
