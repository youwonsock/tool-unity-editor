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
    /// this one queue is static. Each signal keeps its own arguments.
    /// </summary>
    internal static class PathExecutionBoundary
    {
        internal readonly struct OperationScope : IDisposable
        {
            public void Dispose() => Exit();
        }

        // Far above what a frame raises. Listeners that keep raising each other
        // would otherwise hang the player here, with no frame and no log.
        internal const int MAX_NOTIFICATIONS_PER_DRAIN = 100000;

        private static readonly Queue<PathSignalBase> Pending = new Queue<PathSignalBase>(32);
        private static int _depth;
        private static bool _draining;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            while (Pending.Count > 0)
                Pending.Dequeue().DropNext();
            _depth = 0;
            _draining = false;
        }

        internal static OperationScope Enter()
        {
            _depth++;
            return default(OperationScope);
        }

        /// <summary>A signal queued a notification. Outside any operation it is delivered at once.</summary>
        internal static void Enqueue(PathSignalBase signal)
        {
            Pending.Enqueue(signal);
            if (_depth == 0)
                Drain();
        }

        private static void Exit()
        {
            _depth--;
            if (_depth == 0)
                Drain();
        }

        // A listener's own commands are operations too. Their notifications join
        // the end of this queue, so listeners never nest.
        private static void Drain()
        {
            if (_draining)
                return;
            _draining = true;
            int delivered = 0;
            try
            {
                while (Pending.Count > 0)
                {
                    if (++delivered > MAX_NOTIFICATIONS_PER_DRAIN)
                    {
                        Debug.LogError($"Path listeners raised more than {MAX_NOTIFICATIONS_PER_DRAIN} notifications in a row. Dropping {Pending.Count} of them.");
                        while (Pending.Count > 0)
                            Pending.Dequeue().DropNext();
                        break;
                    }
                    Pending.Dequeue().DeliverNext();
                }
            }
            finally
            {
                _draining = false;
            }
        }
    }
}
