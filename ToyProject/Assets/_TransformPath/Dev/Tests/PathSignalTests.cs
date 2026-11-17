using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.TransformPath.Tests
{
    public sealed class PathSignalTests
    {
        [Test]
        public void OutsideAnOperation_DeliversAtOnce()
        {
            var signal = new PathSignal<int>();
            var heard = new List<int>();
            signal.Add(heard.Add);

            signal.Publish(3);

            CollectionAssert.AreEqual(new[] { 3 }, heard);
        }

        [Test]
        public void InsideAnOperation_DeliversAfterItInPublicationOrderAcrossSignals()
        {
            var first = new PathSignal<int>();
            var second = new PathSignal<string, int>();
            var third = new PathSignal();
            var heard = new List<string>();
            first.Add(value => heard.Add($"first {value}"));
            second.Add((name, value) => heard.Add($"second {name} {value}"));
            third.Add(() => heard.Add("third"));

            using (PathExecutionBoundary.Enter())
            {
                first.Publish(1);
                second.Publish("a", 2);
                third.Publish();
                first.Publish(4);
                Assert.IsEmpty(heard);
            }

            CollectionAssert.AreEqual(new[] { "first 1", "second a 2", "third", "first 4" }, heard);
        }

        [Test]
        public void ListenerRemovedWhileWaiting_IsSkipped()
        {
            var signal = new PathSignal();
            int heard = 0;
            void Listener() => heard++;
            signal.Add(Listener);

            using (PathExecutionBoundary.Enter())
            {
                signal.Publish();
                signal.Remove(Listener);
            }

            Assert.AreEqual(0, heard);
        }

        [Test]
        public void ListenerAddedAfterPublication_DoesNotHearIt()
        {
            var signal = new PathSignal();
            var heard = new List<string>();
            signal.Add(() => heard.Add("early"));

            using (PathExecutionBoundary.Enter())
            {
                signal.Publish();
                signal.Add(() => heard.Add("late"));
            }

            CollectionAssert.AreEqual(new[] { "early" }, heard);
        }

        [Test]
        public void ListenerPublishing_JoinsTheEndInsteadOfNesting()
        {
            var outer = new PathSignal<int>();
            var inner = new PathSignal<int>();
            var heard = new List<string>();
            int depth = 0;
            outer.Add(value =>
            {
                depth++;
                heard.Add($"outer {value} depth {depth}");
                if (value == 1)
                    inner.Publish(10);
                depth--;
            });
            inner.Add(value =>
            {
                depth++;
                heard.Add($"inner {value} depth {depth}");
                depth--;
            });

            using (PathExecutionBoundary.Enter())
            {
                outer.Publish(1);
                outer.Publish(2);
            }

            CollectionAssert.AreEqual(new[] { "outer 1 depth 1", "outer 2 depth 1", "inner 10 depth 1" }, heard);
        }

        [Test]
        public void ThrowingListener_IsLoggedAndOthersStillHear()
        {
            var signal = new PathSignal();
            int heard = 0;
            signal.Add(() => throw new System.InvalidOperationException("listener failed"));
            signal.Add(() => heard++);
            LogAssert.Expect(LogType.Exception, new Regex("listener failed"));

            signal.Publish();

            Assert.AreEqual(1, heard);
        }

        [Test]
        public void ListenersRaisingEachOtherForever_AreDroppedPastTheLimit()
        {
            var signal = new PathSignal();
            int heard = 0;
            signal.Add(() =>
            {
                heard++;
                signal.Publish();
            });
            LogAssert.Expect(LogType.Error, new Regex("more than"));

            signal.Publish();

            Assert.AreEqual(PathExecutionBoundary.MAX_NOTIFICATIONS_PER_DRAIN, heard);

            // The boundary is empty again: a new publication is heard once more.
            heard = 0;
            var other = new PathSignal();
            other.Add(() => heard++);
            other.Publish();
            Assert.AreEqual(1, heard);
        }
    }
}
