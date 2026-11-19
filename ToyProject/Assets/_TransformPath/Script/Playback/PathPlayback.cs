using System;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// One follower's playback without Unity: the route, the position on it,
    /// which path events it passes, and the queue's limit. What an event does
    /// is <see cref="PathEventEffects"/>' work. The playback owns its state, so
    /// nothing has to check whether another object changed it. The owner applies
    /// the position after a call reports it changed.
    /// </summary>
    internal sealed class PathPlayback : IPathEffectTarget
    {
        private const float EPSILON = PathConstants.EPSILON;

        private readonly PathPlaybackListeners _listeners;
        private readonly PathPlaybackCursor _cursor = new PathPlaybackCursor();
        private readonly PathEventEffects _effects;
        private PathRoute _route;
        private ulong _nextPlaybackId;
        private bool _positionDirty;

        public PathPlayback(PathPlaybackListeners listeners)
        {
            _listeners = listeners;
            _effects = new PathEventEffects(this);
        }

        public EPathFollowerState State { get; private set; } = EPathFollowerState.Uninitialized;
        public bool IsMoving { get; private set; }
        public ulong PlaybackId { get; private set; }
        public PathMotionLimit Limit { get; set; } = PathMotionLimit.None;
        /// <summary>
        /// Rises whenever the position jumps instead of moving on: Play, a Seek
        /// that moved, a loop seam. A queue treats each rise as a new start.
        /// </summary>
        public int MotionEpoch { get; private set; }

        public PathRoute Route => _route;
        public float Progress => _cursor.Global;
        public float SegmentProgress => _cursor.Normalized;
        public int SegmentIndex => _cursor.Segment;
        public EPathMoveType MoveType => _cursor.MoveType;
        public float Speed => _cursor.Speed;
        public float Duration => _cursor.Duration;
        public bool CanSample => _route != null && _route.Provider.IsReady;
        /// <summary>Whether a blend or a delayed event waits for the effects tick.</summary>
        public bool HasPendingEffects => _effects.HasPending;

        /// <summary>Whether the position changed since the last call. Clears the flag.</summary>
        public bool ConsumePositionDirty()
        {
            bool dirty = _positionDirty;
            _positionDirty = false;
            return dirty;
        }

        public Vector3 SamplePosition() => _cursor.SamplePosition(_route);

        public bool TryEvaluateDirection(out Vector3 direction)
        {
            direction = Vector3.zero;
            return CanSample && _cursor.TryEvaluateDirection(_route, out direction);
        }

        #region Lifecycle

        public void Initialize()
        {
            if (State != EPathFollowerState.Uninitialized)
                return;
            SetState(EPathFollowerState.Ready);
        }

        /// <summary>Ends any playback and forgets the route.</summary>
        public bool Release()
        {
            if (State == EPathFollowerState.Uninitialized && _route == null)
                return false;
            Stop();
            _route = null;
            SetState(EPathFollowerState.Uninitialized);
            return true;
        }

        public bool Play(PathPlaybackRequest request)
        {
            EnsureInitialized();
            PathRoute route = request.CreateRoute();
            HaltMovement();
            if (PlaybackId != 0)
                EndPlayback();
            _route = route;
            _cursor.Begin(route, request.Loop);
            IsMoving = true;
            PlaybackId = ++_nextPlaybackId;
            MotionEpoch++;
            // Replacing a moving playback keeps the Moving state and raises nothing.
            SetState(EPathFollowerState.Moving);
            _positionDirty = true;
            return true;
        }

        public bool Stop()
        {
            if (State == EPathFollowerState.Uninitialized || State == EPathFollowerState.Ready)
                return false;
            HaltMovement();
            EndPlayback();
            SetState(EPathFollowerState.Ready);
            return true;
        }

        /// <summary>Cancels a value blend even when not moving, then pauses a moving playback.</summary>
        public bool Pause()
        {
            _effects.CancelBlend();
            if (!IsMoving)
                return false;
            IsMoving = false;
            _cursor.PendingTime = 0f;
            SetState(EPathFollowerState.Paused);
            return true;
        }

        public bool Resume()
        {
            if (_route == null || State != EPathFollowerState.Paused)
                return false;
            IsMoving = true;
            SetState(EPathFollowerState.Moving);
            return true;
        }

        #endregion

        #region Commands

        public bool Seek(float progress)
        {
            EnsureInitialized();
            if (_route == null)
                throw new InvalidOperationException("PathFollower has no active provider.");
            PathValueUtility.ThrowIfNotFinite(progress, nameof(progress));
            float target = Mathf.Clamp01(progress);
            int segment = _route.FindSegment(target);
            return SeekTo(segment, _route.GetLocalProgress(segment, target));
        }

        public bool SeekSegment(int segmentIndex, float localProgress)
        {
            EnsureInitialized();
            if (_route == null || !_route.IsSequence)
                throw new InvalidOperationException("SeekSegment requires an active sequence.");
            if (segmentIndex < 0 || segmentIndex >= _route.SegmentCount)
                throw new ArgumentOutOfRangeException(nameof(segmentIndex));
            PathValueUtility.ThrowIfNotFinite(localProgress, nameof(localProgress));

            float local = Mathf.Clamp01(localProgress);
            if (local >= 1f && segmentIndex < _route.SegmentCount - 1)
            {
                segmentIndex++;
                local = 0f;
            }
            return SeekTo(segmentIndex, local);
        }

        public bool SetSpeed(float speed)
        {
            EnsureInitialized();
            PathValueUtility.ThrowIfNotMoveValue(speed, nameof(speed));
            if (_route == null)
                throw new InvalidOperationException("Speed can only be changed during playback.");
            if (_cursor.MoveType != EPathMoveType.SpeedBased)
                throw new InvalidOperationException("Speed can only be changed in SpeedBased mode.");
            return _cursor.TrySetValue(EPathEffectChannel.Speed, speed);
        }

        public bool SetDuration(float duration)
        {
            EnsureInitialized();
            PathValueUtility.ThrowIfNotMoveValue(duration, nameof(duration));
            if (_route == null)
                throw new InvalidOperationException("Duration can only be changed during playback.");
            if (_cursor.MoveType != EPathMoveType.TimeBased)
                throw new InvalidOperationException("Duration can only be changed in TimeBased mode.");
            return _cursor.TrySetValue(EPathEffectChannel.Duration, duration);
        }

        /// <summary>Drops the running blend and the delayed events. Returns whether any was pending.</summary>
        public bool CancelEffects()
        {
            if (!_effects.HasPending)
                return false;
            _effects.Clear();
            return true;
        }

        /// <summary>Runs an event now, as if the follower reached it. Pending delayed events are dropped.</summary>
        public void TriggerEvent(PathEventDefinition definition)
        {
            if (definition == null)
                throw new ArgumentNullException(nameof(definition));
            EnsureInitialized();
            _effects.Run(definition);
        }

        #endregion

        #region Tick

        /// <summary>Advances value blends and delayed events. Runs every frame, before any queue or movement.</summary>
        public void TickEffects(float deltaTime, int frame) => _effects.Tick(deltaTime, frame);

        /// <summary>Moves for one frame within the queue's limit.</summary>
        public void TickMovement(float deltaTime)
        {
            if (!IsMoving || _route == null)
                return;
            if (!TryRefreshRoute())
            {
                Stop();
                return;
            }
            if (deltaTime <= 0f)
                return;
            if (Limit.Blocked)
            {
                _cursor.PendingTime = 0f;
                _positionDirty = true;
                return;
            }

            float remaining = deltaTime + _cursor.PendingTime;
            _cursor.PendingTime = 0f;
            if (remaining <= 0f)
                return;

            int transitions = 0;
            while (remaining > EPSILON && transitions < PathConstants.MAX_SEGMENT_TRANSITIONS_PER_TICK)
            {
                float consumed = AdvanceOnce(remaining, out bool transitioned);
                // A Pause event or the end of the path stops the tick here.
                if (!IsMoving)
                {
                    _positionDirty = true;
                    return;
                }
                if (transitioned)
                    transitions++;
                if (consumed <= EPSILON && !transitioned)
                    break;
                remaining -= consumed;
            }

            if (remaining > EPSILON && transitions >= PathConstants.MAX_SEGMENT_TRANSITIONS_PER_TICK)
                _cursor.PendingTime = remaining;
            _positionDirty = true;
        }

        /// <summary>
        /// Takes the provider's new revision when only the geometry changed,
        /// keeping the local progress on the new shape. Returns false when the
        /// route can no longer play: its provider is not ready or its definition changed.
        /// </summary>
        public bool TryRefreshRoute()
        {
            IPathProvider provider = _route.Provider;
            if (!provider.IsReady || provider.DefinitionRevision != _route.DefinitionRevision)
                return false;
            if (provider.Revision == _route.Revision)
                return true;
            _route = _route.Refresh();
            _cursor.SyncToProvider(_route);
            _positionDirty = true;
            return true;
        }

        #endregion

        #region Movement

        private bool SeekTo(int segment, float local)
        {
            if (!_route.Provider.IsReady)
                throw new InvalidOperationException("Seek requires a ready path provider.");
            if (_cursor.Segment == segment && Mathf.Abs(_cursor.Normalized - local) <= EPSILON)
                return false;

            _cursor.PendingTime = 0f;
            MotionEpoch++;
            if (segment != _cursor.Segment)
                ConfigureSegment(segment, 0f, false);
            _cursor.MoveTo(_route, segment, local);
            _cursor.SkipEventsToPosition(_route.GetEvents(segment));
            _positionDirty = true;
            return true;
        }

        private float AdvanceOnce(float availableTime, out bool transitioned)
        {
            transitioned = false;
            float cap = Limit.MaxProgress;
            float consumed = _cursor.Advance(_route, availableTime, Limit.SpeedMultiplier, cap, out bool moved);
            if (!moved)
                return consumed;

            DispatchEvents(_route.GetEvents(_cursor.Segment), _cursor.Normalized);
            if (State == EPathFollowerState.Moving
                && _route.GetGlobalProgress(_cursor.Segment, 1f) <= cap
                && _cursor.HasReachedSegmentEnd(_route))
                ProcessSegmentEnd(out transitioned);
            return Mathf.Max(consumed, EPSILON);
        }

        private void ProcessSegmentEnd(out bool transitioned)
        {
            transitioned = false;
            PathRuntimeEvent[] events = _route.GetEvents(_cursor.Segment);
            _cursor.MoveToSegmentEnd(_route);
            bool hasNext = _cursor.Segment + 1 < _route.SegmentCount;
            if (!hasNext && !_cursor.Loop)
            {
                Complete(events);
                return;
            }

            DispatchEvents(events, 1f);
            if (State != EPathFollowerState.Moving)
                return;
            EnterSegment(hasNext ? _cursor.Segment + 1 : 0);
            transitioned = true;
            if (hasNext)
                return;
            MotionEpoch++;
            if (Limit.WaitOnDiscontinuity)
                Limit = new PathMotionLimit(true, 0f, 0f, true);
        }

        private void EnterSegment(int index)
        {
            // A Single or Aggregate lap keeps the values set during the last one.
            if (_route.IsSequence)
                ConfigureSegment(index, _cursor.GetNominalSpeed(_route), _route.PreservesPreviousSpeed(index));
            _cursor.EnterSegment(_route, index);
            if (_route.IsSequence)
                _listeners.SegmentChanged.Publish(index);
        }

        private void ConfigureSegment(int index, float previousNominalSpeed, bool preserveSpeed)
        {
            // A blend drives the channel of the segment that started it.
            if (_cursor.ConfigureSegment(_route, index, previousNominalSpeed, preserveSpeed))
                _effects.CancelBlend();
        }

        private void Complete(PathRuntimeEvent[] events)
        {
            IsMoving = false;
            _cursor.PendingTime = 0f;
            DispatchEvents(events, 1f);
            if (State != EPathFollowerState.Moving)
                return;
            EndPlayback();
            SetState(EPathFollowerState.Completed);
            _listeners.Completed.Publish();
        }

        private void HaltMovement()
        {
            IsMoving = false;
            _cursor.PendingTime = 0f;
        }

        private void EndPlayback()
        {
            PlaybackId = 0;
            _effects.Clear();
        }

        #endregion

        #region Events

        // An event that pauses or ends the playback stops the rest.
        private void DispatchEvents(PathRuntimeEvent[] events, float progress)
        {
            while (State == EPathFollowerState.Moving && _cursor.TryTakeEvent(events, progress, out PathEventDefinition definition))
                _effects.Run(definition);
        }

        #endregion

        #region Effect target

        bool IPathEffectTarget.HasRoute => _route != null;

        bool IPathEffectTarget.ApplyValue(EPathEffectChannel channel, float value) => _cursor.TrySetValue(channel, value);

        void IPathEffectTarget.PublishEvent(string eventName)
        {
            _listeners.Receiver.Publish(eventName, _listeners.Owner);
            _listeners.EventReached.Publish(eventName);
        }

        #endregion

        private void SetState(EPathFollowerState state)
        {
            if (State == state)
                return;
            State = state;
            _listeners.StateChanged.Publish(state);
        }

        private void EnsureInitialized()
        {
            if (State == EPathFollowerState.Uninitialized)
                throw new InvalidOperationException("PathFollower is not initialized.");
        }
    }
}
