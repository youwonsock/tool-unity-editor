using System.Collections.Generic;

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
            public PathEventSettingSO EventSetting;
            public PathFollower PathFollower;
            public float RemainingTime;
            public bool ResumeOnly;
            public int StartFrame;
        }

        #endregion


        #region Member Variables

        private readonly List<ScheduledEvent> _scheduledEvents =
            new List<ScheduledEvent>();
        private int _revision;

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
            PathEventSettingSO eventSetting,
            PathFollower pathFollower,
            float delay,
            bool resumeOnly,
            int startFrame)
        {
            _scheduledEvents.Add(new ScheduledEvent
            {
                EventSetting = eventSetting,
                PathFollower = pathFollower,
                RemainingTime = delay,
                ResumeOnly = resumeOnly,
                StartFrame = startFrame,
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
            for (int i = 0; i < _scheduledEvents.Count; i++)
            {
                ScheduledEvent candidate = _scheduledEvents[i];
                if (candidate.StartFrame >= frame || candidate.RemainingTime > 0f)
                    continue;

                scheduledEvent = candidate;
                int lastIndex = _scheduledEvents.Count - 1;
                for (int moveIndex = i; moveIndex < lastIndex; moveIndex++)
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