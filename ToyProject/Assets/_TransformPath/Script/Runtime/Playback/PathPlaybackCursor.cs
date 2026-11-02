using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// Where a playback is on its route and how it advances: the current segment,
    /// its progress, and the movement values of that segment. It knows nothing of
    /// events, transforms or the queue.
    /// </summary>
    internal sealed class PathPlaybackCursor
    {
        private const float EPSILON = 0.00001f;
        private const float MIN_DIRECTION_LOOK_AHEAD = 0.001f;
        private const float MIN_DIRECTION_LOOK_AHEAD_DISTANCE = 0.01f;

        private float _elapsed;
        private float _distance;

        public EPathMoveType MoveType { get; private set; }
        public float Speed { get; private set; }
        public float Duration { get; private set; }
        public bool Loop { get; private set; }
        public int Segment { get; private set; } = -1;
        public float Normalized { get; private set; }
        public float Global { get; private set; }
        public float PendingTime { get; set; }

        private AnimationCurve TimeCurve { get; set; }

        public void Begin(PathPlaybackSession session, bool loop)
        {
            Loop = loop;
            ConfigureSegment(session, 0, 0f, false);
            EnterSegment(session, 0);
        }

        /// <summary>Returns whether the segment moves in another way than the one before it.</summary>
        public bool ConfigureSegment(
            PathPlaybackSession session,
            int index,
            float previousNominalSpeed,
            bool preserveSpeed)
        {
            PathMovementSettings settings = session.GetMovementSettings(index);
            bool moveTypeChanged = settings.MoveType != MoveType;
            MoveType = settings.MoveType;
            TimeCurve = settings.TimeCurve;
            if (preserveSpeed
                && previousNominalSpeed >= PathMovementSettingsUtility.MIN_VALUE
                && PathValueUtility.IsFinite(previousNominalSpeed))
            {
                if (settings.MoveType == EPathMoveType.SpeedBased)
                    Speed = previousNominalSpeed;
                else
                {
                    Duration = Mathf.Clamp(
                        session.GetLength(index) / previousNominalSpeed,
                        PathMovementSettingsUtility.MIN_VALUE,
                        PathMovementSettingsUtility.MAX_VALUE);
                }
            }
            else if (settings.MoveType == EPathMoveType.SpeedBased)
                Speed = settings.Value;
            else
                Duration = settings.Value;
            return moveTypeChanged;
        }

        public void EnterSegment(PathPlaybackSession session, int index)
        {
            Segment = index;
            _elapsed = 0f;
            _distance = 0f;
            Normalized = 0f;
            Global = session.GetGlobalProgress(index, 0f);
        }

        /// <summary>
        /// Moves for up to the available time and returns how much of it was used.
        /// The global progress never passes the cap.
        /// </summary>
        public float Advance(
            PathPlaybackSession session,
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
                float duration = Mathf.Max(Duration, PathMovementSettingsUtility.MIN_VALUE);
                float current = PathProgressCalculator.EvaluateTimeProgress(_elapsed / duration, TimeCurve);
                if (capped && maxLocal <= current + EPSILON)
                    return 0f;
                step = PathProgressCalculator.AdvanceTime(_elapsed, duration, TimeCurve, availableTime, multiplier, maxLocal);
                _elapsed = step.Position;
            }
            else
            {
                float length = Mathf.Max(session.GetLength(Segment), PathMovementSettingsUtility.MIN_VALUE);
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
        public bool HasReachedSegmentEnd(PathPlaybackSession session)
        {
            return MoveType == EPathMoveType.TimeBased
                ? _elapsed >= Duration - EPSILON
                : _distance >= session.GetLength(Segment) - EPSILON;
        }

        public void MoveToSegmentEnd(PathPlaybackSession session)
        {
            Normalized = 1f;
            Global = session.GetGlobalProgress(Segment, 1f);
        }

        public void MoveTo(PathPlaybackSession session, int index, float local)
        {
            Segment = index;
            Normalized = Mathf.Clamp01(local);
            _elapsed = MoveType == EPathMoveType.TimeBased
                ? PathProgressCalculator.InverseTimeProgress(Normalized, TimeCurve) * Duration
                : 0f;
            _distance = MoveType == EPathMoveType.SpeedBased
                ? session.GetLength(index) * Normalized
                : 0f;
            Global = session.GetGlobalProgress(index, Normalized);
        }

        // The geometry changed: keep the local progress.
        public void SyncToProvider(PathPlaybackSession session)
        {
            MoveTo(session, Segment, Normalized);
        }

        public void SetSpeed(float speed)
        {
            Speed = speed;
        }

        public void SetDuration(float duration)
        {
            float normalized = Duration >= PathMovementSettingsUtility.MIN_VALUE
                ? PathProgressCalculator.EvaluateTimeProgress(_elapsed / Duration, TimeCurve)
                : 0f;
            Duration = duration;
            _elapsed = PathProgressCalculator.InverseTimeProgress(normalized, TimeCurve) * duration;
        }

        public float GetNominalSpeed(PathPlaybackSession session)
        {
            if (MoveType == EPathMoveType.SpeedBased)
                return Speed;
            return Duration >= PathMovementSettingsUtility.MIN_VALUE
                ? session.GetLength(Segment) / Duration
                : 0f;
        }

        public Vector3 SamplePosition(PathPlaybackSession session)
        {
            return session.Sample(Segment, Normalized);
        }

        /// <summary>The normalized direction of travel at the current position.</summary>
        public bool TryEvaluateDirection(PathPlaybackSession session, out Vector3 direction)
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
