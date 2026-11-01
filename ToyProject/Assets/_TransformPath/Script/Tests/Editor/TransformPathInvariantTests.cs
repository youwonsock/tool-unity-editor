using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using Object = UnityEngine.Object;

namespace Common.TransformPath.Tests
{
    /// <summary>Rules the other tests did not pin down.</summary>
    public sealed class TransformPathInvariantTests
    {
        private readonly List<Object> _objects = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (Object item in _objects)
                if (item != null) Object.DestroyImmediate(item);
            _objects.Clear();
        }

        // One tick crosses at most 65 segments and keeps the rest for the next.
        [Test]
        public void ALongFrameCarriesItsRemainingTimeAcrossTicks()
        {
            var segments = new List<PathSegmentDescriptor>();
            for (int i = 0; i < 200; i++)
                segments.Add(new PathSegmentDescriptor(CreatePath(Vector3.right * i, Vector3.right * (i + 1)), PathMovementSettings.Speed(1f), false));
            var route = Create<MultiPathData>("Route");
            route.ConfigureSegments(segments);
            var follower = Create<PathFollower>("Follower");
            follower.Init();
            int transitions = 0;
            follower.SegmentChanged += _ => transitions++;
            follower.StartPlayback(PathPlaybackRequest.Sequence(route));

            follower.Tick(100.5f);
            Assert.That(transitions, NUnit.Framework.Is.EqualTo(65));
            Assert.That(follower.CurrentSegmentIndex, NUnit.Framework.Is.EqualTo(65));

            // A tick with no time of its own still spends what the last one kept.
            follower.Tick(0.00001f);
            Assert.That(follower.CurrentSegmentIndex, NUnit.Framework.Is.EqualTo(100));
            Assert.That(follower.NormalizedTime, NUnit.Framework.Is.EqualTo(0.5f).Within(0.001f));
        }

        [Test]
        public void AFollowerTickDoesNotAllocate()
        {
            var follower = Create<PathFollower>("Follower");
            follower.Init();
            follower.StartPlayback(PathPlaybackRequest.Single(CreatePath(Vector3.zero, Vector3.right * 1000f)));
            follower.Tick(0.016f);
            Assert.That(() => follower.Tick(0.016f), Is.Not.AllocatingGCMemory());
        }

        private T Create<T>(string name) where T : Component
        {
            var item = new GameObject(name);
            _objects.Add(item);
            return item.AddComponent<T>();
        }

        private PathData CreatePath(Vector3 start, Vector3 end)
        {
            var path = Create<PathData>("Path");
            path.ConfigureMovementSettings(PathMovementSettings.Speed(1f));
            path.ConfigureBuildSettings(new PathBuildSettings(EPathCurveType.Linear, 4));
            path.ConfigureControlPoints(new[] { start, end });
            path.Init();
            return path;
        }
    }
}
