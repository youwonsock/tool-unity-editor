using UnityEngine;

namespace Common.TransformPath
{
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

        private float _elapsed;
        private float _distance;
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
                    Speed = previousNominalSpeed;
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
            _elapsed = 0f;
            _distance = 0f;
            Normalized = 0f;
            Global = session.GetGlobalProgress(index, 0f);
            _nextEvent = 0;
        }

        /// <summary>The next event of the segment at or before the progress, in position order.</summary>
        public bool TryTakeEvent(PathRuntimeEvent[] events, float progress, out PathEventDefinition definition)
        {
            while (_nextEvent < events.Length && events[_nextEvent].NormalizedTime <= progress + EPSILON)
            {
                definition = events[_nextEvent++].Definition;
                if (definition != null)
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
        /// Moves for up to the available time and returns how much of it was used.
        /// The global progress never passes the cap.
        /// </summary>
        public float Advance(
            PathRoute session,
            float availableTime,
            float multiplier,
            float cap,
            out bool moved)
        {
            moved = false;
            if (multiplier <= EPSILON)
                return availableTime;

            float maxLocal = session.GetLocalProgress(Segment, cap);
            bool capped = session.GetGlobalProgress(Segment, 1f) > cap;
            PathProgressStep step;
            if (MoveType == EPathMoveType.TimeBased)
            {
                float duration = Mathf.Max(Duration, PathConstants.MIN_MOVE_VALUE);
                float current = _movement.EvaluateProgress(_elapsed / duration);
                if (capped && maxLocal <= current + EPSILON)
                    return 0f;
                step = PathProgressCalculator.AdvanceTime(_elapsed, duration, _movement, availableTime, multiplier, maxLocal);
                _elapsed = step.Position;
            }
            else
            {
                float length = Mathf.Max(session.GetLength(Segment), PathConstants.MIN_MOVE_VALUE);
                if (capped && maxLocal * length <= _distance + EPSILON)
                    return 0f;
                step = PathProgressCalculator.AdvanceDistance(_distance, length, Speed, availableTime, multiplier, maxLocal);
                _distance = step.Position;
            }

            Normalized = Mathf.Min(step.Progress, maxLocal);
            Global = Mathf.Min(cap, session.GetGlobalProgress(Segment, Normalized));
            moved = true;
            return step.ConsumedTime;
        }

        // SetDuration preserves progress by rescaling elapsed time, so this
        // compares with the current duration, after event callbacks.
        public bool HasReachedSegmentEnd(PathRoute session)
        {
            return MoveType == EPathMoveType.TimeBased
                ? _elapsed >= Duration - EPSILON
                : _distance >= session.GetLength(Segment) - EPSILON;
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
                ? _movement.InverseProgress(Normalized) * Duration
                : 0f;
            _distance = MoveType == EPathMoveType.SpeedBased
                ? session.GetLength(index) * Normalized
                : 0f;
            Global = session.GetGlobalProgress(index, Normalized);
        }

        // The geometry changed: keep the local progress.
        public void SyncToProvider(PathRoute session)
        {
            MoveTo(session, Segment, Normalized);
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

        // Keeps the progress by rescaling the elapsed time.
        private void SetDuration(float duration)
        {
            float normalized = Duration >= PathConstants.MIN_MOVE_VALUE
                ? _movement.EvaluateProgress(_elapsed / Duration)
                : 0f;
            Duration = duration;
            _elapsed = _movement.InverseProgress(normalized) * duration;
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
    }
}
