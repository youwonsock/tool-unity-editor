using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Common.TransformPath.Tests
{
    public sealed class PathGeometryTests : PathBoundaryIsolatedTest
    {
        private static PathPolyline Build(EPathCurveType curve, int segments, params Vector3[] points)
        {
            Assert.IsTrue(PathGeometryUtility.TryBuild(points, curve, segments, new PathGeometryBuildBuffer(),
                out PathPolyline polyline, out string error), error);
            return polyline;
        }

        [Test]
        public void Polyline_SamplesEndpointsAndInterpolatesByDistance()
        {
            PathPolyline line = Build(EPathCurveType.Linear, 10, Vector3.zero, new Vector3(10f, 0f, 0f));

            Assert.AreEqual(10f, line.Length, 1e-4f);
            Assert.AreEqual(Vector3.zero, line.Sample(-1f));
            Assert.AreEqual(new Vector3(10f, 0f, 0f), line.Sample(2f));
            Assert.AreEqual(2.5f, line.Sample(0.25f).x, 1e-4f);
            Assert.AreEqual(7.25f, line.SampleDistance(7.25f).x, 1e-4f);
        }

        [Test]
        public void Polyline_SkipsZeroLengthSamples()
        {
            var polyline = new PathPolyline(
                new[] { Vector3.zero, Vector3.zero, new Vector3(4f, 0f, 0f) },
                new[] { 0f, 0f, 4f });

            Assert.AreEqual(1f, polyline.SampleDistance(1f).x, 1e-5f);
            Assert.AreEqual(Vector3.zero, polyline.Sample(0f));
        }

        [TestCase(EPathCurveType.Linear)]
        [TestCase(EPathCurveType.SplineApproximating)]
        [TestCase(EPathCurveType.SplineInterpolating)]
        public void EveryCurve_StartsAndEndsOnItsControlPoints(EPathCurveType curve)
        {
            Vector3[] points = { Vector3.zero, new Vector3(5f, 0f, 0f), new Vector3(5f, 0f, 5f), new Vector3(0f, 2f, 5f) };
            PathPolyline polyline = Build(curve, 100, points);

            Assert.AreEqual(points[0], polyline.Sample(0f));
            Assert.AreEqual(points[3], polyline.Sample(1f));
        }

        [Test]
        public void SegmentSearch_FindsLastStartAtOrBeforeDistance()
        {
            float[] starts = { 0f, 2f, 2f, 5f };

            Assert.AreEqual(0, PathGeometryUtility.FindSegmentStartIndex(starts, 1.9f));
            Assert.AreEqual(2, PathGeometryUtility.FindSegmentStartIndex(starts, 2f));
            Assert.AreEqual(3, PathGeometryUtility.FindSegmentStartIndex(starts, 9f));
        }

        // 7 samples spread by distance alone would put none on the corner and cut it.
        [Test]
        public void Linear_KeepsEveryControlPointAsACorner()
        {
            PathPolyline polyline = Build(EPathCurveType.Linear, 7,
                Vector3.zero, new Vector3(10f, 0f, 0f), new Vector3(10f, 0f, 10f));

            Assert.AreEqual(20f, polyline.Length, 1e-4f);
            Assert.AreEqual(new Vector3(10f, 0f, 0f), polyline.Sample(0.5f));
            Assert.AreEqual(new Vector3(5f, 0f, 0f), polyline.Sample(0.25f));
        }

        // Splines are sampled by parameter, then read by distance. More samples
        // make a constant progress rate closer to a constant speed.
        [TestCase(EPathCurveType.SplineApproximating)]
        [TestCase(EPathCurveType.SplineInterpolating)]
        public void SplineSpeed_GetsMoreEvenWithMoreSamples(EPathCurveType curve)
        {
            Vector3[] points = { Vector3.zero, new Vector3(2f, 0f, 0f), new Vector3(2f, 0f, 12f), new Vector3(14f, 0f, 12f) };
            var deviations = new List<float>();
            foreach (int segments in new[] { 20, 100, 500 })
            {
                PathPolyline polyline = Build(curve, segments, points);
                deviations.Add(MaxSpeedDeviation(polyline));
            }
            Assert.LessOrEqual(deviations[1], deviations[0] + 1e-4f);
            Assert.LessOrEqual(deviations[2], deviations[1] + 1e-4f);
            Assert.Less(deviations[2], 0.01f);
        }

        // Distance covered per equal progress step, against the average step.
        private static float MaxSpeedDeviation(PathPolyline polyline)
        {
            const int steps = 1000;
            float expected = polyline.Length / steps;
            float worst = 0f;
            Vector3 previous = polyline.Sample(0f);
            for (int i = 1; i <= steps; i++)
            {
                Vector3 current = polyline.Sample(i / (float)steps);
                worst = Mathf.Max(worst, Mathf.Abs(Vector3.Distance(previous, current) - expected) / expected);
                previous = current;
            }
            return worst;
        }
    }
}
