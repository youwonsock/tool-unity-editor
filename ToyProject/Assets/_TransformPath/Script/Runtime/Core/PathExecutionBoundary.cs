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
            public PathListeners Owner;
            public object Source;
            public IPathEventReceiver Receiver;
            public string Text;
            public int Number;
            public float Amount;
        }

        // Far above what a frame raises. Listeners that keep raising each other
        // would otherwise hang the player here, with no frame and no log.
        private const int MAX_NOTIFICATIONS_PER_DRAIN = 100000;

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

        internal static void PublishState(PathListeners listeners, EPathFollowerState state)
            => Publish(listeners, new Notification { Kind = ENotification.State, Number = (int)state });

        internal static void PublishSegment(PathListeners listeners, int segment)
            => Publish(listeners, new Notification { Kind = ENotification.Segment, Number = segment });

        internal static void PublishPlain(PathListeners listeners)
            => Publish(listeners, new Notification { Kind = ENotification.Plain });

        internal static void PublishQueue(PathListeners listeners, QueuedPathFollower follower)
            => Publish(listeners, new Notification { Kind = ENotification.Queue, Source = follower });

        internal static void PublishQueueSpeed(PathListeners listeners, QueuedPathFollower follower, float speed)
            => Publish(listeners, new Notification { Kind = ENotification.QueueSpeed, Source = follower, Amount = speed });

        internal static void PublishQueueInvalidated(PathListeners listeners, QueuedPathFollower follower, EPathQueueDetachReason reason)
            => Publish(listeners, new Notification { Kind = ENotification.QueueInvalidated, Source = follower, Number = (int)reason });

        internal static void PublishEventName(PathListeners listeners, string name)
            => Publish(listeners, new Notification { Kind = ENotification.EventName, Text = name });

        internal static void PublishEventReceiver(IPathEventReceiver receiver, string name, IPathPlaybackState playback)
        {
            if (receiver != null)
                Enqueue(new Notification { Kind = ENotification.EventReceiver, Listeners = ReceiverSlot, Receiver = receiver, Source = playback, Text = name });
        }

        private static void Publish(PathListeners listeners, Notification notification)
        {
            notification.Listeners = listeners.Invocations;
            notification.Owner = listeners;
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
            int delivered = 0;
            try
            {
                while (Pending.Count > 0)
                {
                    if (++delivered > MAX_NOTIFICATIONS_PER_DRAIN)
                    {
                        Debug.LogError($"Path listeners raised more than {MAX_NOTIFICATIONS_PER_DRAIN} notifications in a row. Dropping {Pending.Count} of them.");
                        Pending.Clear();
                        break;
                    }

                    Notification notification = Pending.Dequeue();
                    for (int i = 0; i < notification.Listeners.Length; i++)
                    {
                        Delegate listener = notification.Listeners[i];
                        // It may have unsubscribed while the notification waited.
                        if (notification.Owner != null && !notification.Owner.StillHas(notification.Listeners, listener))
                            continue;
                        try
                        {
                            Invoke(notification, listener);
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
