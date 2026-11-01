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
                    $"{command}은(는) Modifier의 Observe, CaptureSnapshot, Modify 안에서 호출할 수 없습니다.");
        }

        /// <summary>Queues the command when called from Modifier code. Otherwise the caller runs it.</summary>
        internal bool TryDefer(Action command)
        {
            if (!IsInUserCode)
                return false;
            _deferredCommands.Enqueue(command);
            return true;
        }

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
            try
            {
                while (true)
                {
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
            internal FlowFieldSessionBase Source { get; }
            internal EventKind Kind { get; }
            internal int Generation { get; }
            internal FlowFieldRuntimeState State { get; }
            internal Exception Exception { get; }

            internal QueuedEvent(
                FlowFieldSessionBase source,
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
