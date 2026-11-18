using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Keeps code FlowField does not own out of its own work. Event handlers run
    /// only after the outermost operation has returned, and a Modifier running
    /// inside an operation can queue registration changes but not start another
    /// operation. Build code therefore never has to re-check its own lifetime
    /// after calling out. A Manager shares one boundary between its sessions.
    /// </summary>
    internal sealed class FlowFieldExecutionBoundary
    {
        // Far above what a frame raises. Handlers that keep triggering each other
        // would otherwise hang the player here, with no frame and no log.
        private const int MAX_WORK_PER_DRAIN = 100000;

        private readonly Queue<Action> _deferredCommands = new Queue<Action>(4);
        private readonly Queue<QueuedEvent> _events = new Queue<QueuedEvent>(8);
        private int _operationDepth;
        private int _userCodeDepth;
        private bool _draining;

        internal bool IsInUserCode => _userCodeDepth > 0;

        internal OperationScope EnterOperation()
        {
            _operationDepth++;
            return new OperationScope(this, userCode: false);
        }

        /// <summary>Marks a stretch that calls Modifier code, which is part of an operation.</summary>
        internal OperationScope EnterUserCode()
        {
            _operationDepth++;
            _userCodeDepth++;
            return new OperationScope(this, userCode: true);
        }

        internal void ThrowIfInUserCode(string command)
        {
            if (IsInUserCode)
                throw new InvalidOperationException(
                    $"{command} cannot be called from a Modifier's Observe, CaptureSnapshot or Modify.");
        }

        /// <summary>Queues the command when called from Modifier code. Otherwise the caller runs it.</summary>
        internal bool TryDefer(Action command)
        {
            if (!IsInUserCode)
                return false;
            Defer(command);
            return true;
        }

        /// <summary>Queues a command for a caller that has already checked <see cref="IsInUserCode"/>.</summary>
        internal void Defer(Action command) => _deferredCommands.Enqueue(command);

        internal void Publish(in QueuedEvent queuedEvent)
        {
            _events.Enqueue(queuedEvent);
            Drain();
        }

        private void Exit(bool userCode)
        {
            if (userCode)
                _userCodeDepth--;
            _operationDepth--;
            Drain();
        }

        // A handler may issue commands. Their events join this queue, so handlers
        // see them in order and never nested inside one another.
        private void Drain()
        {
            if (_operationDepth > 0 || _draining)
                return;
            _draining = true;
            int done = 0;
            try
            {
                while (true)
                {
                    if (_deferredCommands.Count + _events.Count > 0 && ++done > MAX_WORK_PER_DRAIN)
                    {
                        int dropped = DropRunawayWork();
                        Debug.LogError($"[FlowField] 핸들러가 명령과 이벤트를 연달아 {MAX_WORK_PER_DRAIN}개 넘게 만들었습니다. "
                            + $"{dropped}개를 버렸고, Released와 해제 이벤트는 전달했습니다.");
                        return;
                    }
                    if (_deferredCommands.Count > 0)
                        RunDeferredCommand(_deferredCommands.Dequeue());
                    else if (_events.Count > 0)
                    {
                        QueuedEvent next = _events.Dequeue();
                        next.Source.Deliver(in next);
                    }
                    else
                        return;
                }
            }
            finally
            {
                _draining = false;
            }
        }

        /// <summary>
        /// Drops the queued work except the events that end a session lifetime:
        /// a subscriber that never sees Released keeps waiting on a session that
        /// is gone. Those are delivered once, in order. Whatever their handlers
        /// raise is dropped too, so a handler cannot restart the runaway.
        /// Returns how many items were dropped.
        /// </summary>
        private int DropRunawayWork()
        {
            int dropped = _deferredCommands.Count;
            _deferredCommands.Clear();
            List<QueuedEvent> lifetimeEnds = null;
            while (_events.Count > 0)
            {
                QueuedEvent queuedEvent = _events.Dequeue();
                if (EndsLifetime(in queuedEvent))
                    (lifetimeEnds ??= new List<QueuedEvent>()).Add(queuedEvent);
                else
                    dropped++;
            }

            if (lifetimeEnds != null)
            {
                for (int i = 0; i < lifetimeEnds.Count; i++)
                {
                    QueuedEvent queuedEvent = lifetimeEnds[i];
                    queuedEvent.Source.Deliver(in queuedEvent);
                }
            }

            dropped += _deferredCommands.Count + _events.Count;
            _deferredCommands.Clear();
            _events.Clear();
            return dropped;
        }

        private static bool EndsLifetime(in QueuedEvent queuedEvent)
            => queuedEvent.Kind == EventKind.Disposed
                || queuedEvent.Kind == EventKind.StateChanged
                    && queuedEvent.State == FlowFieldRuntimeState.Released;

        private static void RunDeferredCommand(Action command)
        {
            try
            {
                command();
            }
            catch (Exception exception)
            {
                // The Modifier that asked for this has already returned.
                Debug.LogException(exception);
            }
        }

        internal readonly struct OperationScope : IDisposable
        {
            private readonly FlowFieldExecutionBoundary _owner;
            private readonly bool _userCode;

            internal OperationScope(FlowFieldExecutionBoundary owner, bool userCode)
            {
                _owner = owner;
                _userCode = userCode;
            }

            public void Dispose() => _owner.Exit(_userCode);
        }

        internal enum EventKind
        {
            StateChanged,
            FieldCommitted,
            Failed,
            Disposed,
        }

        internal readonly struct QueuedEvent
        {
            internal FlowFieldSession Source { get; }
            internal EventKind Kind { get; }
            internal int Generation { get; }
            internal FlowFieldRuntimeState State { get; }
            internal Exception Exception { get; }

            internal QueuedEvent(
                FlowFieldSession source,
                EventKind kind,
                int generation,
                FlowFieldRuntimeState state = default,
                Exception exception = null)
            {
                Source = source;
                Kind = kind;
                Generation = generation;
                State = state;
                Exception = exception;
            }
        }
    }
}
