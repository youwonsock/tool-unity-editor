using System;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>Why one step of movement stopped.</summary>
    internal enum EPathStep
    {
        /// <summary>Nothing moved: the queue's multiplier is zero.</summary>
        NoMove,
        /// <summary>The whole slice of time was used before any limit.</summary>
        TimeUsed,
        /// <summary>The next event's position was reached; it is due now.</summary>
        Event,
        /// <summary>The queue's progress cap was reached.</summary>
        Cap,
        /// <summary>The end of the segment was reached; every event of it was due before.</summary>
        End,
    }

    /// <summary>
    /// Where a playback is on its route and how it advances: the current segment,
    /// its progress, the movement values of that segment and the next event to
    /// pass. It knows nothing of what events do, transforms or the queue.
    /// </summary>
    internal sealed class PathPlaybackCursor
    {
        private const float EPSILON = PathConstants.EPSILON;
        private const float MIN_DIRECTION_LOOK_AHEAD = 0.001f;
        private const float MIN_DIRECTION_LOOK_AHEAD_DISTANCE = 0.01f;

        // Double, so a long path at a low speed still moves every frame.
        private double _elapsed;
        private double _distance;
        private int _nextEvent;

        public EPathMoveType MoveType { get; private set; }
        public float Speed { get; private set; }
        public float Duration { get; private set; }
        public bool Loop { get; private set; }
        public int Segment { get; private set; } = -1;
        public float Normalized { get; private set; }
        public float Global { get; private set; }
        public float PendingTime { get; set; }

        private PathMovementSettings _movement;

        public void Begin(PathRoute session, bool loop)
        {
            Loop = loop;
            ConfigureSegment(session, 0, 0f, false);
            EnterSegment(session, 0);
        }

        /// <summary>Returns whether the segment moves in another way than the one before it.</summary>
        public bool ConfigureSegment(
            PathRoute session,
            int index,
            float previousNominalSpeed,
            bool preserveSpeed)
        {
            PathMovementSettings settings = session.GetMovementSettings(index);
            bool moveTypeChanged = settings.MoveType != MoveType;
            MoveType = settings.MoveType;
            _movement = settings;
            if (preserveSpeed
                && previousNominalSpeed >= PathConstants.MIN_MOVE_VALUE
                && PathValueUtility.IsFinite(previousNominalSpeed))
            {
                if (settings.MoveType == EPathMoveType.SpeedBased)
                    Speed = Mathf.Min(previousNominalSpeed, PathConstants.MAX_MOVE_VALUE);
                else
                {
                    Duration = Mathf.Clamp(
                        session.GetLength(index) / previousNominalSpeed,
                        PathConstants.MIN_MOVE_VALUE,
                        PathConstants.MAX_MOVE_VALUE);
                }
            }
            else if (settings.MoveType == EPathMoveType.SpeedBased)
                Speed = settings.Value;
            else
                Duration = settings.Value;
            return moveTypeChanged;
        }

        public void EnterSegment(PathRoute session, int index)
        {
            Segment = index;
            _elapsed = 0d;
            _distance = 0d;
            Normalized = 0f;
            Global = session.GetGlobalProgress(index, 0f);
            _nextEvent = 0;
        }

        /// <summary>Takes the next event of the segment when it is at or before the progress.</summary>
        public bool TryTakeEventAt(PathRuntimeEvent[] events, float progress, out PathEventDefinition definition)
        {
            if (TryPeekEvent(events, out float position) && position <= progress)
            {
                definition = events[_nextEvent++].Definition;
                return true;
            }
            definition = null;
            return false;
        }

        /// <summary>After a Seek: the current position and everything before it count as passed.</summary>
        public void SkipEventsToPosition(PathRuntimeEvent[] events)
        {
            _nextEvent = 0;
            while (_nextEvent < events.Length && events[_nextEvent].NormalizedTime <= Normalized + EPSILON)
                _nextEvent++;
        }

        /// <summary>
        /// Moves for up to the available time toward the nearest limit: the next
        /// event, the queue's cap or the segment end. The global progress never
        /// passes the cap. A step that gets to its limit sits on it exactly, and an
        /// event already behind the position is due at once.
        /// </summary>
        public EPathStep Advance(
            PathRoute session,
            float availableTime,
            float multiplier,
            float cap,
            PathRuntimeEvent[] events,
            out float consumedTime)
        {
            if (multiplier <= EPSILON)
            {
                consumedTime = availableTime;
                return EPathStep.NoMove;
            }

            bool capped = session.GetGlobalProgress(Segment, 1f) > cap;
            EPathStep reason = capped ? EPathStep.Cap : EPathStep.End;
            float limit = capped ? session.GetLocalProgress(Segment, cap) : 1f;
            // The limit is chosen in progress space and only then turned into time
            // or distance, so an event the conversion rounds short is still reached.
            if (TryPeekEvent(events, out float eventPosition) && eventPosition <= limit)
            {
                reason = EPathStep.Event;
                limit = eventPosition;
            }

            PathProgressStep step;
            float progress;
            if (MoveType == EPathMoveType.TimeBased)
            {
                double duration = Math.Max(Duration, PathConstants.MIN_MOVE_VALUE);
                double target = reason == EPathStep.End
                    ? duration
                    : _movement.InverseProgress(limit) * duration;
                step = PathProgressCalculator.AdvanceToward(_elapsed, target, multiplier, availableTime);
                _elapsed = step.Position;
                progress = _movement.EvaluateProgress((float)(_elapsed / duration));
            }
            else
            {
                double length = Math.Max(session.GetLength(Segment), PathConstants.MIN_MOVE_VALUE);
                double target = reason == EPathStep.End ? length : limit * length;
                step = PathProgressCalculator.AdvanceToward(_distance, target,
                    Math.Max(Speed, PathConstants.MIN_MOVE_VALUE) * (double)multiplier, availableTime);
                _distance = step.Position;
                progress = (float)Math.Min(1d, _distance / length);
            }

            consumedTime = step.ConsumedTime;
            float before = Normalized;
            if (step.Reached)
                Normalized = reason == EPathStep.End ? 1f : Mathf.Max(Normalized, limit);
            else
                Normalized = Mathf.Min(progress, limit);
            // A limit already behind the position (a cap passed by a Seek, an event
            // left from a Pause) moves nothing, so the progress stays where it is.
            if (Normalized != before)
                Global = Mathf.Min(cap, session.GetGlobalProgress(Segment, Normalized));
            return step.Reached ? reason : EPathStep.TimeUsed;
        }

        public void MoveToSegmentEnd(PathRoute session)
        {
            Normalized = 1f;
            Global = session.GetGlobalProgress(Segment, 1f);
        }

        public void MoveTo(PathRoute session, int index, float local)
        {
            Segment = index;
            Normalized = Mathf.Clamp01(local);
            _elapsed = MoveType == EPathMoveType.TimeBased
                ? _movement.InverseProgress(Normalized) * (double)Duration
                : 0d;
            // Measured on the same floored length as Advance.
            _distance = MoveType == EPathMoveType.SpeedBased
                ? Math.Max(session.GetLength(index), PathConstants.MIN_MOVE_VALUE) * (double)Normalized
                : 0d;
            Global = session.GetGlobalProgress(index, Normalized);
        }

        /// <summary>
        /// The geometry changed. Time-based progress does not depend on it, so the
        /// elapsed time stays; a speed-based one keeps its local progress on the new length.
        /// </summary>
        public void SyncToProvider(PathRoute previous, PathRoute session)
        {
            if (MoveType == EPathMoveType.SpeedBased)
            {
                // Scaled in double: rebuilt from the float progress, a slow
                // follower on a long path would lose its movement every frame.
                // The same floor as Advance, so a sub-millimetre segment keeps its progress too.
                double previousLength = Math.Max(previous.GetLength(Segment), PathConstants.MIN_MOVE_VALUE);
                double length = Math.Max(session.GetLength(Segment), PathConstants.MIN_MOVE_VALUE);
                _distance *= length / previousLength;
            }
            Global = session.GetGlobalProgress(Segment, Normalized);
        }

        /// <summary>Sets Speed or Duration. Returns false when the value is already that.</summary>
        public bool TrySetValue(EPathEffectChannel channel, float value)
        {
            if (channel == EPathEffectChannel.Speed)
            {
                if (Mathf.Approximately(Speed, value))
                    return false;
                Speed = value;
                return true;
            }
            if (Mathf.Approximately(Duration, value))
                return false;
            SetDuration(value);
            return true;
        }

        // Keeps the fraction of the duration that has passed, so the progress stays exactly.
        private void SetDuration(float duration)
        {
            double fraction = Duration >= PathConstants.MIN_MOVE_VALUE ? _elapsed / Duration : 0d;
            Duration = duration;
            _elapsed = fraction * duration;
        }

        public float GetNominalSpeed(PathRoute session)
        {
            if (MoveType == EPathMoveType.SpeedBased)
                return Speed;
            return Duration >= PathConstants.MIN_MOVE_VALUE
                ? session.GetLength(Segment) / Duration
                : 0f;
        }

        public Vector3 SamplePosition(PathRoute session)
        {
            return session.Sample(Segment, Normalized);
        }

        /// <summary>The normalized direction of travel at the current position.</summary>
        public bool TryEvaluateDirection(PathRoute session, out Vector3 direction)
        {
            direction = Vector3.zero;
            float length = session.GetLength(Segment);
            // Look ahead by distance so a short path has as usable a direction
            // as a long one. The sample is still clamped at the path end.
            float lookAhead = Mathf.Min(1f, Mathf.Max(
                MIN_DIRECTION_LOOK_AHEAD,
                MIN_DIRECTION_LOOK_AHEAD_DISTANCE / length));
            Vector3 position = session.Sample(Segment, Normalized);
            Vector3 ahead = session.Sample(Segment, Mathf.Min(1f, Normalized + lookAhead));
            Vector3 delta = ahead - position;

            // Near the end the clamped sample shrinks towards zero. Hold the
            // rotation there, before rounding starts to decide the direction.
            float minimumDistance = lookAhead * length * 0.5f;
            float sqrDistance = delta.sqrMagnitude;
            if (sqrDistance <= Mathf.Min(EPSILON, minimumDistance * minimumDistance))
                return false;

            // Vector3.normalized returns zero below a fixed magnitude.
            direction = delta / Mathf.Sqrt(sqrDistance);
            return true;
        }

        // The next event that has a definition, without taking it.
        private bool TryPeekEvent(PathRuntimeEvent[] events, out float position)
        {
            while (_nextEvent < events.Length && events[_nextEvent].Definition == null)
                _nextEvent++;
            if (_nextEvent < events.Length)
            {
                position = events[_nextEvent].NormalizedTime;
                return true;
            }
            position = 1f;
            return false;
        }
    }
}
