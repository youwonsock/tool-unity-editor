using System;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>Why the last playback ended.</summary>
    internal enum EPathPlaybackEnd
    {
        None,
        /// <summary>Stop, Release or a fault.</summary>
        Stopped,
        Completed,
        /// <summary>The route's movement, events or segments changed.</summary>
        RouteChanged,
        /// <summary>The route's provider failed to build or was destroyed.</summary>
        RouteUnavailable,
    }

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

        // Who paused the playback. Only the Resume command undoes a Pause command;
        // an event's Resume undoes only what an event paused.
        private enum EPauseOwner
        {
            None,
            Command,
            Event,
        }

        private readonly PathPlaybackListeners _listeners;
        private readonly PathPlaybackCursor _cursor = new PathPlaybackCursor();
        private readonly PathEventEffects _effects;
        private PathRoute _route;
        private ulong _nextPlaybackId;
        private bool _positionDirty;
        private EPauseOwner _pauseOwner;

        public PathPlayback(PathPlaybackListeners listeners)
        {
            _listeners = listeners;
            _effects = new PathEventEffects(this);
        }

        public EPathFollowerState State { get; private set; } = EPathFollowerState.Uninitialized;
        public bool IsMoving { get; private set; }
        public ulong PlaybackId { get; private set; }
        /// <summary>Why the last playback ended; None while one runs.</summary>
        public EPathPlaybackEnd LastEnd { get; private set; }
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
        // A paused playback can still hold a segment whose path left the sequence and then failed.
        public bool CanSample => _route != null && _route.Provider.IsReady && _route.IsSegmentReady(_cursor.Segment);
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
            LastEnd = EPathPlaybackEnd.None;
            IsMoving = true;
            PlaybackId = ++_nextPlaybackId;
            MotionEpoch++;
            // Replacing a moving playback keeps the Moving state and raises nothing.
            SetState(EPathFollowerState.Moving);
            _positionDirty = true;
            return true;
        }

        public bool Stop() => Stop(EPathPlaybackEnd.Stopped);

        /// <summary>
        /// Cancels a value blend even when not moving, then pauses a moving
        /// playback. A playback an event paused stays paused but now waits for
        /// Resume: an event's Resume no longer wakes it.
        /// </summary>
        public bool Pause()
        {
            _effects.CancelBlend();
            if (State == EPathFollowerState.Paused)
            {
                _pauseOwner = EPauseOwner.Command;
                return false;
            }
            if (!PauseMovement())
                return false;
            _pauseOwner = EPauseOwner.Command;
            return true;
        }

        /// <summary>Resumes a paused playback, whoever paused it.</summary>
        public bool Resume() => ResumeMovement();

        #endregion

        #region Commands

        public bool Seek(float progress)
        {
            EnsureInitialized();
            if (_route == null)
                throw new InvalidOperationException("PathFollower has no active provider.");
            PathValueUtility.ThrowIfNotFinite(progress, nameof(progress));
            bool refreshed = RefreshRouteForSeek();
            float target = Mathf.Clamp01(progress);
            int segment = _route.FindSegment(target);
            return SeekTo(segment, _route.GetLocalProgress(segment, target)) || refreshed;
        }

        public bool SeekSegment(int segmentIndex, float localProgress)
        {
            EnsureInitialized();
            if (_route == null || !_route.IsSequence)
                throw new InvalidOperationException("SeekSegment requires an active sequence.");
            if (segmentIndex < 0 || segmentIndex >= _route.SegmentCount)
                throw new ArgumentOutOfRangeException(nameof(segmentIndex));
            PathValueUtility.ThrowIfNotFinite(localProgress, nameof(localProgress));
            bool refreshed = RefreshRouteForSeek();

            float local = Mathf.Clamp01(localProgress);
            if (local >= 1f && segmentIndex < _route.SegmentCount - 1)
            {
                segmentIndex++;
                local = 0f;
            }
            return SeekTo(segmentIndex, local) || refreshed;
        }

        /// <summary>Sets the Speed and cancels an event's Speed blend, which would overwrite it.</summary>
        public bool SetSpeed(float speed)
        {
            EnsureInitialized();
            PathValueUtility.ThrowIfNotMoveValue(speed, nameof(speed));
            if (_route == null)
                throw new InvalidOperationException("Speed can only be changed after Play.");
            if (_cursor.MoveType != EPathMoveType.SpeedBased)
                throw new InvalidOperationException("Speed can only be changed in SpeedBased mode.");
            _effects.CancelBlend(EPathEffectChannel.Speed);
            return _cursor.TrySetValue(EPathEffectChannel.Speed, speed);
        }

        public bool SetDuration(float duration)
        {
            EnsureInitialized();
            PathValueUtility.ThrowIfNotMoveValue(duration, nameof(duration));
            if (_route == null)
                throw new InvalidOperationException("Duration can only be changed after Play.");
            if (_cursor.MoveType != EPathMoveType.TimeBased)
                throw new InvalidOperationException("Duration can only be changed in TimeBased mode.");
            _effects.CancelBlend(EPathEffectChannel.Duration);
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

        /// <summary>
        /// Runs an event now, as if the follower reached it: an event that changes
        /// the playback drops the delayed events waiting, and its Resume only wakes
        /// a playback an event paused.
        /// </summary>
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
                Stop(_route.Provider.IsReady ? EPathPlaybackEnd.RouteChanged : EPathPlaybackEnd.RouteUnavailable);
                return;
            }
            if (deltaTime <= 0f)
                return;
            if (Limit.Blocked)
            {
                _cursor.PendingTime = 0f;
                return;
            }

            float remaining = deltaTime + _cursor.PendingTime;
            _cursor.PendingTime = 0f;
            if (remaining <= 0f)
                return;

            int segmentBefore = _cursor.Segment;
            float progressBefore = _cursor.Normalized;
            int transitions = 0;
            // Each step ends on a limit or uses the time up, so the loop always ends:
            // an event step takes an event, an end step changes segment or completes.
            while (remaining > 0f && transitions < PathConstants.MAX_SEGMENT_TRANSITIONS_PER_TICK)
            {
                EPathStep step = AdvanceOnce(ref remaining, out bool transitioned);
                // A Pause event or the end of the path stops the tick here.
                if (!IsMoving)
                {
                    _positionDirty = true;
                    return;
                }
                if (transitioned)
                    transitions++;
                else if (step != EPathStep.Event)
                    break;
            }

            // At most one frame's time waits, so a follower that crosses the limit
            // every frame does not build up time it would rush through later.
            if (remaining > 0f && transitions >= PathConstants.MAX_SEGMENT_TRANSITIONS_PER_TICK)
                _cursor.PendingTime = Mathf.Min(remaining, deltaTime);
            // A follower held in place writes nothing.
            if (_cursor.Segment != segmentBefore || _cursor.Normalized != progressBefore)
                _positionDirty = true;
        }

        /// <summary>
        /// Takes the provider's new revision when only the geometry changed,
        /// keeping the local progress on the new shape. Returns false when the
        /// route can no longer play: its provider is not ready or its definition changed.
        /// </summary>
        public bool TryRefreshRoute() => TryRefreshRoute(out _);

        private bool TryRefreshRoute(out bool refreshed)
        {
            refreshed = false;
            IPathProvider provider = _route.Provider;
            if (!provider.IsReady || provider.DefinitionRevision != _route.DefinitionRevision)
                return false;
            if (provider.Revision == _route.Revision)
                return true;
            PathRoute previous = _route;
            _route = _route.Refresh();
            _cursor.SyncToProvider(previous, _route);
            _positionDirty = true;
            refreshed = true;
            return true;
        }

        #endregion

        #region Movement

        // A paused playback has not seen what changed since it stopped moving.
        // Nothing changes when the route can no longer play. Returns whether
        // the new geometry moved the playback.
        private bool RefreshRouteForSeek()
        {
            if (TryRefreshRoute(out bool refreshed))
                return refreshed;
            throw new InvalidOperationException(_route.Provider.IsReady
                ? "Seek requires the route this playback started with; its movement, events or segments changed. Start a new playback."
                : "Seek requires a ready path provider.");
        }

        private bool SeekTo(int segment, float local)
        {
            if (_cursor.Segment == segment && Mathf.Abs(_cursor.Normalized - local) <= EPSILON)
                return false;

            _cursor.PendingTime = 0f;
            MotionEpoch++;
            // A Seek can come from a listener after this frame's Queue phase, so
            // the queue's last limit no longer fits; it waits for the next one.
            WaitForQueueAfterJump();
            // Another segment starts as if the playback had moved into it, so one
            // that preserves the previous speed takes the speed the playback has now.
            if (segment != _cursor.Segment)
                ConfigureSegment(segment, _cursor.GetNominalSpeed(_route), _route.PreservesPreviousSpeed(segment));
            _cursor.MoveTo(_route, segment, local);
            _cursor.SkipEventsToPosition(_route.GetEvents(segment));
            _positionDirty = true;
            return true;
        }

        private EPathStep AdvanceOnce(ref float remaining, out bool transitioned)
        {
            transitioned = false;
            PathRuntimeEvent[] events = _route.GetEvents(_cursor.Segment);
            EPathStep step = _cursor.Advance(_route, remaining, Limit.SpeedMultiplier, Limit.MaxProgress,
                events, out float consumed);
            remaining = consumed >= remaining ? 0f : remaining - consumed;
            if (step == EPathStep.Event)
                DispatchEvents(events, _cursor.Normalized);
            else if (step == EPathStep.End)
                ProcessSegmentEnd(out transitioned);
            return step;
        }

        // Every event of the segment was due before its end, so none is left here.
        private void ProcessSegmentEnd(out bool transitioned)
        {
            transitioned = false;
            _cursor.MoveToSegmentEnd(_route);
            bool hasNext = _cursor.Segment + 1 < _route.SegmentCount;
            if (!hasNext && !_cursor.Loop)
            {
                Complete();
                return;
            }

            EnterSegment(hasNext ? _cursor.Segment + 1 : 0);
            transitioned = true;
            if (hasNext)
                return;
            MotionEpoch++;
            WaitForQueueAfterJump();
        }

        // A queue limit holds for the position it was computed at; after a jump
        // the member stands still until the next Queue phase sees it.
        private void WaitForQueueAfterJump()
        {
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
            _cursor.ConfigureSegment(_route, index, previousNominalSpeed, preserveSpeed);
            // A blend belongs to the segment that started it. The next segment starts
            // from its own value, or the speed it inherits, as after an instant change.
            _effects.CancelBlend();
        }

        private void Complete()
        {
            HaltMovement();
            EndPlayback();
            LastEnd = EPathPlaybackEnd.Completed;
            SetState(EPathFollowerState.Completed);
            _listeners.Completed.Publish();
        }

        private bool Stop(EPathPlaybackEnd reason)
        {
            if (State == EPathFollowerState.Uninitialized || State == EPathFollowerState.Ready)
                return false;
            // A completed playback keeps its end; Stop only leaves the Completed state.
            bool running = State != EPathFollowerState.Completed;
            HaltMovement();
            EndPlayback();
            if (running)
                LastEnd = reason;
            SetState(EPathFollowerState.Ready);
            return true;
        }

        private void HaltMovement()
        {
            IsMoving = false;
            _cursor.PendingTime = 0f;
            _pauseOwner = EPauseOwner.None;
        }

        private bool PauseMovement()
        {
            if (!IsMoving)
                return false;
            IsMoving = false;
            _cursor.PendingTime = 0f;
            SetState(EPathFollowerState.Paused);
            return true;
        }

        private bool ResumeMovement()
        {
            if (_route == null || State != EPathFollowerState.Paused)
                return false;
            _pauseOwner = EPauseOwner.None;
            IsMoving = true;
            SetState(EPathFollowerState.Moving);
            return true;
        }

        private void EndPlayback()
        {
            PlaybackId = 0;
            _effects.Clear();
        }

        #endregion

        #region Events

        // Runs the due events in authoring order. Each is taken before it runs, so
        // a Pause event counts as passed; the events after it wait for the resume.
        private void DispatchEvents(PathRuntimeEvent[] events, float progress)
        {
            while (State == EPathFollowerState.Moving && _cursor.TryTakeEventAt(events, progress, out PathEventDefinition definition))
                _effects.Run(definition);
        }

        #endregion

        #region Effect target

        bool IPathEffectTarget.HasRoute => _route != null;

        // Pause cancels the blend itself, moving or not.
        bool IPathEffectTarget.Pause()
        {
            _effects.CancelBlend();
            if (!PauseMovement())
                return false;
            _pauseOwner = EPauseOwner.Event;
            return true;
        }

        bool IPathEffectTarget.Resume() => _pauseOwner == EPauseOwner.Event && ResumeMovement();

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
