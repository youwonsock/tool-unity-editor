using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// Keeps code TransformPath does not own out of its work. A listener runs
    /// only after the outermost operation has returned, so nothing inside
    /// playback or the queue has to check whether a callback changed its state.
    /// The order of notifications spans every follower and queue, which is why
    /// there is one queue behind a static access point. Each signal keeps its
    /// own arguments.
    /// </summary>
    internal static class PathExecutionBoundary
    {
        internal readonly struct OperationScope : IDisposable
        {
            public void Dispose() => Exit();
        }

        /// <summary>The order queue and how deep the current operation is.</summary>
        internal sealed class State
        {
            public readonly Queue<PathSignalBase> Pending = new Queue<PathSignalBase>(32);
            public int Depth;
            public bool Draining;
        }

        /// <summary>Puts the previous queue back when disposed.</summary>
        internal sealed class Isolation : IDisposable
        {
            private readonly State _previous;
            private bool _disposed;

            internal Isolation(State previous) => _previous = previous;

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                _state = _previous;
            }
        }

        // Far above what a frame raises. Listeners that keep raising each other
        // would otherwise hang the player here, with no frame and no log.
        internal const int MAX_NOTIFICATIONS_PER_DRAIN = 100000;

        private static State _state = new State();

        /// <summary>No operation is open and nothing waits for delivery.</summary>
        internal static bool IsIdle => _state.Depth == 0 && _state.Pending.Count == 0;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            State state = _state;
            while (state.Pending.Count > 0)
                state.Pending.Dequeue().DropNext();
            state.Depth = 0;
            state.Draining = false;
        }

        /// <summary>
        /// Swaps in an empty queue until the result is disposed, so tests share
        /// no notifications. Never used at runtime.
        /// </summary>
        internal static Isolation Isolate()
        {
            var isolation = new Isolation(_state);
            _state = new State();
            return isolation;
        }

        internal static OperationScope Enter()
        {
            _state.Depth++;
            return default(OperationScope);
        }

        /// <summary>A signal queued a notification. Outside any operation it is delivered at once.</summary>
        internal static void Enqueue(PathSignalBase signal)
        {
            State state = _state;
            state.Pending.Enqueue(signal);
            if (state.Depth == 0)
                Drain(state);
        }

        private static void Exit()
        {
            State state = _state;
            state.Depth--;
            if (state.Depth == 0)
                Drain(state);
        }

        // A listener's own commands are operations too. Their notifications join
        // the end of this queue, so listeners never nest.
        private static void Drain(State state)
        {
            if (state.Draining)
                return;
            state.Draining = true;
            Queue<PathSignalBase> pending = state.Pending;
            int delivered = 0;
            try
            {
                while (pending.Count > 0)
                {
                    if (++delivered > MAX_NOTIFICATIONS_PER_DRAIN)
                    {
                        Debug.LogError($"Path listeners raised more than {MAX_NOTIFICATIONS_PER_DRAIN} notifications in a row. Dropping {pending.Count} of them.");
                        while (pending.Count > 0)
                            pending.Dequeue().DropNext();
                        break;
                    }
                    pending.Dequeue().DeliverNext();
                }
            }
            finally
            {
                state.Draining = false;
            }
        }
    }
}
