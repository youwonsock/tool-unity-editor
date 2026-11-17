using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// One event's listeners and the notifications waiting for them. Publishing
    /// queues the listeners subscribed at that moment with the arguments; the
    /// boundary delivers them later in publication order across all signals.
    /// </summary>
    internal abstract class PathSignalBase
    {
        private Delegate _combined;

        /// <summary>The current listeners, replaced rather than changed, so a delivery can walk an older array.</summary>
        protected Delegate[] Invocations { get; private set; }

        protected void AddListener(Delegate listener)
        {
            _combined = Delegate.Combine(_combined, listener);
            Invocations = _combined?.GetInvocationList();
        }

        protected void RemoveListener(Delegate listener)
        {
            _combined = Delegate.Remove(_combined, listener);
            Invocations = _combined?.GetInvocationList();
        }

        /// <summary>Delivers the oldest waiting notification.</summary>
        internal abstract void DeliverNext();

        /// <summary>Drops the oldest waiting notification without delivering it.</summary>
        internal abstract void DropNext();

        // A listener that unsubscribed while the notification waited is skipped.
        protected bool StillHas(Delegate[] snapshot, Delegate listener)
        {
            return ReferenceEquals(snapshot, Invocations)
                || (Invocations != null && Array.IndexOf(Invocations, listener) >= 0);
        }

        protected static void Report(Exception exception) => Debug.LogException(exception);
    }

    internal sealed class PathSignal : PathSignalBase
    {
        private readonly Queue<Delegate[]> _pending = new Queue<Delegate[]>();

        public void Add(Action listener) => AddListener(listener);
        public void Remove(Action listener) => RemoveListener(listener);

        public void Publish()
        {
            if (Invocations == null)
                return;
            _pending.Enqueue(Invocations);
            PathExecutionBoundary.Enqueue(this);
        }

        internal override void DeliverNext()
        {
            Delegate[] snapshot = _pending.Dequeue();
            for (int i = 0; i < snapshot.Length; i++)
            {
                if (!StillHas(snapshot, snapshot[i]))
                    continue;
                try { ((Action)snapshot[i])(); }
                catch (Exception exception) { Report(exception); }
            }
        }

        internal override void DropNext() => _pending.Dequeue();
    }

    internal sealed class PathSignal<T> : PathSignalBase
    {
        private readonly Queue<(Delegate[] Snapshot, T Argument)> _pending = new Queue<(Delegate[], T)>();

        public void Add(Action<T> listener) => AddListener(listener);
        public void Remove(Action<T> listener) => RemoveListener(listener);

        public void Publish(T argument)
        {
            if (Invocations == null)
                return;
            _pending.Enqueue((Invocations, argument));
            PathExecutionBoundary.Enqueue(this);
        }

        internal override void DeliverNext()
        {
            (Delegate[] snapshot, T argument) = _pending.Dequeue();
            for (int i = 0; i < snapshot.Length; i++)
            {
                if (!StillHas(snapshot, snapshot[i]))
                    continue;
                try { ((Action<T>)snapshot[i])(argument); }
                catch (Exception exception) { Report(exception); }
            }
        }

        internal override void DropNext() => _pending.Dequeue();
    }

    internal sealed class PathSignal<T1, T2> : PathSignalBase
    {
        private readonly Queue<(Delegate[] Snapshot, T1 First, T2 Second)> _pending =
            new Queue<(Delegate[], T1, T2)>();

        public void Add(Action<T1, T2> listener) => AddListener(listener);
        public void Remove(Action<T1, T2> listener) => RemoveListener(listener);

        public void Publish(T1 first, T2 second)
        {
            if (Invocations == null)
                return;
            _pending.Enqueue((Invocations, first, second));
            PathExecutionBoundary.Enqueue(this);
        }

        internal override void DeliverNext()
        {
            (Delegate[] snapshot, T1 first, T2 second) = _pending.Dequeue();
            for (int i = 0; i < snapshot.Length; i++)
            {
                if (!StillHas(snapshot, snapshot[i]))
                    continue;
                try { ((Action<T1, T2>)snapshot[i])(first, second); }
                catch (Exception exception) { Report(exception); }
            }
        }

        internal override void DropNext() => _pending.Dequeue();
    }
}
