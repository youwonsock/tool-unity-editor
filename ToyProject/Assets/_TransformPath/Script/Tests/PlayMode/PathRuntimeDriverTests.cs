using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Common.TransformPath.Tests.PlayMode
{
    public sealed class PathRuntimeDriverTests
    {
        [UnityTest]
        public IEnumerator DriverSkipsItemsRemovedByAnEarlierCallback()
        {
            PathRuntimeDriver driver = new PathRuntimeDriver();
            FakeTickable first = new FakeTickable(1);
            FakeTickable second = new FakeTickable(2);
            first.OnTick = () => driver.Unregister(second);

            driver.Register(first);
            driver.Register(second);
            driver.Tick(1f, 1f, 42);

            Assert.AreEqual(1, first.TickCount);
            Assert.AreEqual(0, second.TickCount);
            Assert.AreEqual(42, first.LastFrameId);
            yield return null;
        }

        [Test]
        public void DriverSkipsAnItemThatWasRemovedAndReRegisteredInTheSameFrame()
        {
            PathRuntimeDriver driver = new PathRuntimeDriver();
            FakeTickable first = new FakeTickable(1);
            FakeTickable second = new FakeTickable(2);
            first.OnTick = () =>
            {
                driver.Unregister(second);
                driver.Register(second);
            };

            driver.Register(first);
            driver.Register(second);
            driver.Tick(0.1f, 0.1f, 9);

            Assert.AreEqual(1, first.TickCount);
            Assert.AreEqual(0, second.TickCount);
        }

        [Test]
        public void DriverSkipsAPlaybackThatChangedAfterFrameSnapshot()
        {
            PathRuntimeDriver driver = new PathRuntimeDriver();
            FakeTickable first = new FakeTickable(1);
            FakeTickable second = new FakeTickable(2);
            first.OnTick = () => second.PlaybackId = 3;

            driver.Register(first);
            driver.Register(second);
            driver.Tick(0.1f, 0.1f, 7);

            Assert.AreEqual(1, first.TickCount);
            Assert.AreEqual(0, second.TickCount);
            Assert.IsNull(driver.LastException);
        }

        [Test]
        public void DriverReportsTheOriginalFaultAndContinuesWithOtherItems()
        {
            PathRuntimeDriver driver = new PathRuntimeDriver();
            FaultTickable faulty = new FaultTickable(1);
            FakeTickable survivor = new FakeTickable(2);
            driver.Register(faulty);
            driver.Register(survivor);

            driver.Tick(0.1f, 0.1f, 8);

            Assert.AreEqual(1, faulty.FaultCount);
            Assert.AreEqual(1, survivor.TickCount);
            Assert.IsNotNull(driver.LastException);
            Assert.That(driver.LastException.Message, Is.EqualTo("tick failure"));
        }

        private sealed class FakeTickable : IPathRuntimeTickable
        {
            public FakeTickable(ulong playbackId)
            {
                PlaybackId = playbackId;
            }

            public ulong PlaybackId { get; set; }
            public int TickCount { get; private set; }
            public int LastFrameId { get; private set; }
            public Action OnTick { get; set; }

            public void Tick(float deltaTime, float unscaledDeltaTime, int frameId)
            {
                TickCount++;
                LastFrameId = frameId;
                OnTick?.Invoke();
            }
        }

        private sealed class FaultTickable : IPathRuntimeTickable, IPathRuntimeFaultHandler
        {
            public FaultTickable(ulong playbackId)
            {
                PlaybackId = playbackId;
            }

            public ulong PlaybackId { get; }
            public int FaultCount { get; private set; }

            public void Tick(float deltaTime, float unscaledDeltaTime, int frameId)
            {
                throw new InvalidOperationException("tick failure");
            }

            public void HandleRuntimeFault(ulong playbackId, Exception exception)
            {
                FaultCount++;
                throw new InvalidOperationException("cleanup failure");
            }
        }
    }
}
