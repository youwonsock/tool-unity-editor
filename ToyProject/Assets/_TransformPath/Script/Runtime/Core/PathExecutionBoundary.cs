using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// Keeps code TransformPath does not own out of its work. A public listener
    /// or event receiver runs only after the outermost operation has returned,
    /// so nothing inside playback or the queue has to check whether a callback
    /// changed its state. A listener that throws is logged and changes nothing.
    /// </summary>
    internal static class PathExecutionBoundary
    {
        internal readonly struct OperationScope : IDisposable
        {
            public void Dispose() => Exit();
        }

        private enum ENotification
        {
            State,
            Segment,
            Plain,
            Queue,
            QueueSpeed,
            QueueInvalidated,
            EventName,
            EventReceiver,
        }

        private struct Notification
        {
            public ENotification Kind;
            public Delegate[] Listeners;
            public object Source;
            public IPathEventReceiver Receiver;
            public string Text;
            public int Number;
            public float Amount;
        }

        private static readonly Queue<Notification> Pending = new Queue<Notification>(32);
        // A receiver is called directly. This stands in for its one listener.
        private static readonly Delegate[] ReceiverSlot = new Delegate[1];
        private static int _depth;
        private static bool _draining;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Pending.Clear();
            _depth = 0;
            _draining = false;
        }

        internal static OperationScope Enter()
        {
            _depth++;
            return default(OperationScope);
        }

        internal static void PublishState(Delegate[] listeners, EPathFollowerState state)
            => Publish(new Notification { Kind = ENotification.State, Listeners = listeners, Number = (int)state });

        internal static void PublishSegment(Delegate[] listeners, int segment)
            => Publish(new Notification { Kind = ENotification.Segment, Listeners = listeners, Number = segment });

        internal static void PublishPlain(Delegate[] listeners)
            => Publish(new Notification { Kind = ENotification.Plain, Listeners = listeners });

        internal static void PublishQueue(Delegate[] listeners, QueuedPathFollower follower)
            => Publish(new Notification { Kind = ENotification.Queue, Listeners = listeners, Source = follower });

        internal static void PublishQueueSpeed(Delegate[] listeners, QueuedPathFollower follower, float speed)
            => Publish(new Notification { Kind = ENotification.QueueSpeed, Listeners = listeners, Source = follower, Amount = speed });

        internal static void PublishQueueInvalidated(Delegate[] listeners, QueuedPathFollower follower, EPathQueueDetachReason reason)
            => Publish(new Notification { Kind = ENotification.QueueInvalidated, Listeners = listeners, Source = follower, Number = (int)reason });

        internal static void PublishEventName(Delegate[] listeners, string name)
            => Publish(new Notification { Kind = ENotification.EventName, Listeners = listeners, Text = name });

        internal static void PublishEventReceiver(IPathEventReceiver receiver, string name, IPathPlaybackState playback)
        {
            if (receiver != null)
                Enqueue(new Notification { Kind = ENotification.EventReceiver, Listeners = ReceiverSlot, Receiver = receiver, Source = playback, Text = name });
        }

        private static void Publish(in Notification notification)
        {
            if (notification.Listeners != null)
                Enqueue(notification);
        }

        private static void Enqueue(in Notification notification)
        {
            Pending.Enqueue(notification);
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
            try
            {
                while (Pending.Count > 0)
                {
                    Notification notification = Pending.Dequeue();
                    for (int i = 0; i < notification.Listeners.Length; i++)
                    {
                        try
                        {
                            Invoke(notification, notification.Listeners[i]);
                        }
                        catch (Exception exception)
                        {
                            Debug.LogException(exception);
                        }
                    }
                }
            }
            finally
            {
                _draining = false;
            }
        }

        private static void Invoke(in Notification notification, Delegate listener)
        {
            switch (notification.Kind)
            {
                case ENotification.State:
                    ((Action<EPathFollowerState>)listener)((EPathFollowerState)notification.Number);
                    break;
                case ENotification.Segment:
                    ((Action<int>)listener)(notification.Number);
                    break;
                case ENotification.Plain:
                    ((Action)listener)();
                    break;
                case ENotification.Queue:
                    ((Action<QueuedPathFollower>)listener)((QueuedPathFollower)notification.Source);
                    break;
                case ENotification.QueueSpeed:
                    ((Action<QueuedPathFollower, float>)listener)((QueuedPathFollower)notification.Source, notification.Amount);
                    break;
                case ENotification.QueueInvalidated:
                    ((Action<QueuedPathFollower, EPathQueueDetachReason>)listener)((QueuedPathFollower)notification.Source, (EPathQueueDetachReason)notification.Number);
                    break;
                case ENotification.EventName:
                    ((Action<string>)listener)(notification.Text);
                    break;
                case ENotification.EventReceiver:
                    notification.Receiver.ReceivePathEvent(notification.Text, (IPathPlaybackState)notification.Source);
                    break;
            }
        }
    }
}
