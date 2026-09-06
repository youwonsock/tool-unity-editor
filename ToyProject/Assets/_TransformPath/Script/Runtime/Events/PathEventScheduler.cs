using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// Reusable game-time scheduler for delayed path events.
    /// </summary>
    internal sealed class PathEventScheduler
    {
        #region Inner Classes / Structs

        internal struct ScheduledEvent
        {
            public PathEventDefinition Definition;
            public IPathFollower Follower;
            public ulong PlaybackId;
            public ulong EventBatchId;
            public float RemainingTime;
            public bool ResumeOnly;
            public int StartFrame;
            public ulong RegistrationOrder;
        }

        #endregion


        #region Member Variables

        private readonly List<ScheduledEvent> _scheduledEvents =
            new List<ScheduledEvent>();
        private int _revision;
        private ulong _registrationOrder;

        #endregion


        #region Properties

        public int Count => _scheduledEvents.Count;
        public int Revision => _revision;

        #endregion


        #region Public Methods

        public void EnsureCapacity(int capacity)
        {
            if (capacity > _scheduledEvents.Capacity)
                _scheduledEvents.Capacity = capacity;
        }

        public void Schedule(
            PathEventDefinition definition,
            IPathFollower follower,
            float delay,
            bool resumeOnly,
            int startFrame,
            ulong playbackId,
            ulong eventBatchId)
        {
            _scheduledEvents.Add(new ScheduledEvent
            {
                Definition = definition,
                Follower = follower,
                PlaybackId = playbackId,
                EventBatchId = eventBatchId,
                RemainingTime = delay,
                ResumeOnly = resumeOnly,
                StartFrame = startFrame,
                RegistrationOrder = ++_registrationOrder,
            });
        }

        public void Advance(float deltaTime, int frame)
        {
            if (deltaTime <= 0f)
                return;

            for (int i = 0; i < _scheduledEvents.Count; i++)
            {
                ScheduledEvent scheduledEvent = _scheduledEvents[i];
                if (scheduledEvent.StartFrame >= frame)
                    continue;

                scheduledEvent.RemainingTime -= deltaTime;
                _scheduledEvents[i] = scheduledEvent;
            }
        }

        public bool TryDequeueDue(int frame, out ScheduledEvent scheduledEvent)
        {
            int selectedIndex = -1;
            for (int i = 0; i < _scheduledEvents.Count; i++)
            {
                ScheduledEvent candidate = _scheduledEvents[i];
                if (candidate.StartFrame >= frame || candidate.RemainingTime > 0f)
                    continue;
                if (selectedIndex < 0
                    || candidate.RemainingTime
                        < _scheduledEvents[selectedIndex].RemainingTime
                    || (Mathf.Approximately(
                            candidate.RemainingTime,
                            _scheduledEvents[selectedIndex].RemainingTime)
                        && candidate.RegistrationOrder
                            < _scheduledEvents[selectedIndex].RegistrationOrder))
                    selectedIndex = i;
            }

            if (selectedIndex >= 0)
            {
                scheduledEvent = _scheduledEvents[selectedIndex];
                int lastIndex = _scheduledEvents.Count - 1;
                for (int moveIndex = selectedIndex; moveIndex < lastIndex; moveIndex++)
                    _scheduledEvents[moveIndex] = _scheduledEvents[moveIndex + 1];
                _scheduledEvents.RemoveAt(lastIndex);
                return true;
            }

            scheduledEvent = default(ScheduledEvent);
            return false;
        }

        public void Clear()
        {
            _scheduledEvents.Clear();
            _revision++;
        }

        #endregion
    }
}
