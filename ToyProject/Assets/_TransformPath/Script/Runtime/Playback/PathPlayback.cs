using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>What a queue currently allows one follower.</summary>
    internal readonly struct PathMotionLimit
    {
        public static readonly PathMotionLimit None = new PathMotionLimit(false, 1f, 1f, false);

        public readonly bool Blocked;
        public readonly float SpeedMultiplier;
        public readonly float MaxProgress;
        /// <summary>A queue member stops at a loop seam and waits for the queue before its next lap.</summary>
        public readonly bool HoldAtLoopSeam;

        public PathMotionLimit(bool blocked, float speedMultiplier, float maxProgress, bool holdAtLoopSeam)
        {
            Blocked = blocked;
            SpeedMultiplier = Mathf.Clamp01(speedMultiplier);
            MaxProgress = Mathf.Clamp01(maxProgress);
            HoldAtLoopSeam = holdAtLoopSeam;
        }
    }

    /// <summary>The listeners a playback notifies. They run after the operation, through the boundary.</summary>
    internal sealed class PathPlaybackListeners
    {
        public readonly PathListeners StateChanged = new PathListeners();
        public readonly PathListeners SegmentChanged = new PathListeners();
        public readonly PathListeners Completed = new PathListeners();
        public readonly PathListeners EventReached = new PathListeners();
        public IPathEventReceiver Receiver;
        public PathFollower Owner;
    }

    /// <summary>
    /// One follower's playback without Unity: the route, the position on it,
    /// path events and their effects, and the queue's limit. It owns all of that
    /// state, so nothing has to check whether another object changed it.
    /// The owner applies the position after a call reports it changed.
    /// </summary>
    internal sealed class PathPlayback
    {
        private const float EPSILON = PathConstants.EPSILON;

        private enum EChannel
        {
            None,
            Speed,
            Duration,
        }

        private struct Blend
        {
            public EChannel Channel;
            public float StartValue;
            public PathValueChange Change;
            public float Elapsed;
            public int StartFrame;
            public ulong PlaybackId;
        }

        private struct DelayedEvent
        {
            public PathEventDefinition Definition;
            public ulong PlaybackId;
            public float RemainingTime;
            public int StartFrame;
            public ulong Order;
        }

        private readonly PathPlaybackListeners _listeners;
        private readonly PathPlaybackCursor _cursor = new PathPlaybackCursor();
        private readonly List<DelayedEvent> _delayed = new List<DelayedEvent>();
        private PathRoute _route;
        private int _nextEventIndex;
        private Blend _blend;
        private ulong _nextPlaybackId;
        private ulong _delayedOrder;
        private int _effectsFrame;
        private bool _positionDirty;
        // Rises whenever an event or command changes the state or a value. A
        // delayed event that changed something ends that frame's delayed events.
        private int _changes;

        public PathPlayback(PathPlaybackListeners listeners)
        {
            _listeners = listeners ?? new PathPlaybackListeners();
        }

        public EPathFollowerState State { get; private set; } = EPathFollowerState.Uninitialized;
        public bool IsMoving { get; private set; }
        public ulong PlaybackId { get; private set; }
        public PathMotionLimit Limit { get; set; } = PathMotionLimit.None;
        /// <summary>Rises on every Seek that moved the position, so a queue can drop limits from before it.</summary>
        public int SeekCount { get; private set; }
        /// <summary>Rises at every loop seam, so a queue can order the member again.</summary>
        public int LoopCount { get; private set; }

        public PathRoute Route => _route;
        public float Progress => _cursor.Global;
        public float SegmentProgress => _cursor.Normalized;
        public int SegmentIndex => _cursor.Segment;
        public EPathMoveType MoveType => _cursor.MoveType;
        public float Speed => _cursor.Speed;
        public float Duration => _cursor.Duration;
        public bool CanSample => _route != null && _route.Provider.IsReady;

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
            _nextEventIndex = 0;
            IsMoving = true;
            PlaybackId = ++_nextPlaybackId;
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

        public bool Pause()
        {
            if (!IsMoving)
                return false;
            IsMoving = false;
            _cursor.PendingTime = 0f;
            CancelBlend();
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
            return ApplyValue(EChannel.Speed, speed);
        }

        public bool SetDuration(float duration)
        {
            EnsureInitialized();
            PathValueUtility.ThrowIfNotMoveValue(duration, nameof(duration));
            if (_route == null)
                throw new InvalidOperationException("Duration can only be changed during playback.");
            if (_cursor.MoveType != EPathMoveType.TimeBased)
                throw new InvalidOperationException("Duration can only be changed in TimeBased mode.");
            return ApplyValue(EChannel.Duration, duration);
        }

        /// <summary>Runs an event now, as if the follower reached it. Pending delayed events are dropped.</summary>
        public void TriggerEvent(PathEventDefinition definition)
        {
            if (definition == null)
                throw new ArgumentNullException(nameof(definition));
            EnsureInitialized();
            _delayed.Clear();
            ProcessEvent(definition);
        }

        #endregion

        #region Tick

        /// <summary>Advances value blends and delayed events. Runs every frame, before any queue or movement.</summary>
        public void TickEffects(float deltaTime, int frame)
        {
            _effectsFrame = frame;
            if (PlaybackId == 0)
                return;
            TickBlend(deltaTime, frame);
            if (deltaTime > 0f)
            {
                for (int i = 0; i < _delayed.Count; i++)
                {
                    DelayedEvent item = _delayed[i];
                    if (item.StartFrame >= frame)
                        continue;
                    item.RemainingTime -= deltaTime;
                    _delayed[i] = item;
                }
            }

            while (TryTakeDueEvent(frame, out DelayedEvent due))
            {
                if (due.PlaybackId != PlaybackId)
                    continue;
                int before = _changes;
                ProcessEvent(due.Definition);
                if (_changes != before)
                    break;
            }
        }

        /// <summary>Moves for one frame within the queue's limit.</summary>
        public void TickMovement(float deltaTime)
        {
            if (!IsMoving || _route == null)
                return;
            if (!RefreshRouteIfNeeded())
                return;
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

        #endregion

        #region Movement

        private bool SeekTo(int segment, float local)
        {
            if (!_route.Provider.IsReady)
                throw new InvalidOperationException("Seek requires a ready path provider.");
            if (_cursor.Segment == segment && Mathf.Abs(_cursor.Normalized - local) <= EPSILON)
                return false;

            _cursor.PendingTime = 0f;
            SeekCount++;
            _changes++;
            if (segment != _cursor.Segment)
                ConfigureSegment(segment, 0f, false);
            _cursor.MoveTo(_route, segment, local);
            SkipEventsUpTo(_route.GetEvents(segment), _cursor.Normalized);
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
            LoopCount++;
            // The rest of this frame's time is dropped at the seam until the queue answers.
            if (Limit.HoldAtLoopSeam)
                Limit = new PathMotionLimit(true, 0f, 0f, true);
        }

        private void EnterSegment(int index)
        {
            // A Single or Aggregate lap keeps the values set during the last one.
            if (_route.IsSequence)
                ConfigureSegment(index, _cursor.GetNominalSpeed(_route), _route.PreservesPreviousSpeed(index));
            _cursor.EnterSegment(_route, index);
            _nextEventIndex = 0;
            if (_route.IsSequence)
                PathExecutionBoundary.PublishSegment(_listeners.SegmentChanged, index);
        }

        private void ConfigureSegment(int index, float previousNominalSpeed, bool preserveSpeed)
        {
            // A blend drives the channel of the segment that started it.
            if (_cursor.ConfigureSegment(_route, index, previousNominalSpeed, preserveSpeed))
                CancelBlend();
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
            PathExecutionBoundary.PublishPlain(_listeners.Completed);
        }

        private bool RefreshRouteIfNeeded()
        {
            IPathProvider provider = _route.Provider;
            if (!provider.IsReady || provider.DefinitionRevision != _route.DefinitionRevision)
            {
                Stop();
                return false;
            }
            if (provider.Revision == _route.Revision)
                return true;

            // Only the geometry changed: keep the local progress on the new shape.
            _route = _route.Refresh();
            _cursor.SyncToProvider(_route);
            _positionDirty = true;
            return true;
        }

        private void HaltMovement()
        {
            IsMoving = false;
            _cursor.PendingTime = 0f;
        }

        private void EndPlayback()
        {
            PlaybackId = 0;
            _delayed.Clear();
            CancelBlend();
        }

        #endregion

        #region Events

        private void DispatchEvents(PathRuntimeEvent[] events, float progress)
        {
            while (_nextEventIndex < events.Length)
            {
                PathRuntimeEvent entry = events[_nextEventIndex];
                if (entry.NormalizedTime > progress + EPSILON)
                    break;
                _nextEventIndex++;
                if (entry.Definition == null)
                    continue;
                // A path event cancels what earlier events left waiting.
                _delayed.Clear();
                ProcessEvent(entry.Definition);
                if (State != EPathFollowerState.Moving)
                    return;
            }
        }

        // A Seek skips the target position and everything before it.
        private void SkipEventsUpTo(PathRuntimeEvent[] events, float progress)
        {
            _nextEventIndex = 0;
            while (_nextEventIndex < events.Length && events[_nextEventIndex].NormalizedTime <= progress + EPSILON)
                _nextEventIndex++;
        }

        private void ProcessEvent(PathEventDefinition definition)
        {
            if (!string.IsNullOrEmpty(definition.EventName))
            {
                PathExecutionBoundary.PublishEventReceiver(_listeners.Receiver, definition.EventName, _listeners.Owner);
                PathExecutionBoundary.PublishEventName(_listeners.EventReached, definition.EventName);
            }

            if (definition.Lifecycle == EPathEventLifecycle.Pause)
            {
                CancelBlend();
                Pause();
            }
            else
            {
                if (definition.Lifecycle == EPathEventLifecycle.Resume)
                    Resume();
                PathValueChange change = definition.GetChange(_cursor.MoveType);
                if (change.Enabled && _route != null)
                    StartBlend(_cursor.MoveType == EPathMoveType.SpeedBased ? EChannel.Speed : EChannel.Duration, change);
            }

            ulong playbackId = PlaybackId;
            IReadOnlyList<PathDelayedEventDefinition> delayed = definition.DelayedEvents;
            for (int i = 0; i < delayed.Count; i++)
            {
                PathDelayedEventDefinition entry = delayed[i];
                if (entry.Delay <= 0f)
                {
                    int before = _changes;
                    ProcessEvent(entry.Definition);
                    if (_changes != before || PlaybackId != playbackId)
                        return;
                    continue;
                }
                _delayed.Add(new DelayedEvent
                {
                    Definition = entry.Definition,
                    PlaybackId = playbackId,
                    RemainingTime = entry.Delay,
                    StartFrame = CurrentEffectsFrame(),
                    Order = ++_delayedOrder,
                });
            }
        }

        // The earliest due event, ties by schedule order.
        private bool TryTakeDueEvent(int frame, out DelayedEvent due)
        {
            int selected = -1;
            for (int i = 0; i < _delayed.Count; i++)
            {
                DelayedEvent candidate = _delayed[i];
                if (candidate.StartFrame >= frame || candidate.RemainingTime > 0f)
                    continue;
                if (selected < 0
                    || candidate.RemainingTime < _delayed[selected].RemainingTime
                    || (Mathf.Approximately(candidate.RemainingTime, _delayed[selected].RemainingTime)
                        && candidate.Order < _delayed[selected].Order))
                    selected = i;
            }
            if (selected < 0)
            {
                due = default(DelayedEvent);
                return false;
            }
            due = _delayed[selected];
            _delayed.RemoveAt(selected);
            return true;
        }

        private void StartBlend(EChannel channel, PathValueChange change)
        {
            if (change.BlendTime <= 0f)
            {
                ApplyValue(channel, change.Target);
                return;
            }
            _blend = new Blend
            {
                Channel = channel,
                StartValue = channel == EChannel.Speed ? _cursor.Speed : _cursor.Duration,
                Change = change,
                Elapsed = 0f,
                StartFrame = CurrentEffectsFrame(),
                PlaybackId = PlaybackId,
            };
        }

        private void TickBlend(float deltaTime, int frame)
        {
            if (_blend.Channel == EChannel.None || _blend.StartFrame >= frame || deltaTime <= 0f)
                return;
            if (_blend.PlaybackId != PlaybackId)
            {
                CancelBlend();
                return;
            }
            _blend.Elapsed += deltaTime;
            float weight = _blend.Change.Evaluate(_blend.Elapsed);
            ApplyValue(_blend.Channel, Mathf.Lerp(_blend.StartValue, _blend.Change.Target, weight));
            if (_blend.Elapsed >= _blend.Change.BlendTime)
            {
                ApplyValue(_blend.Channel, _blend.Change.Target);
                CancelBlend();
            }
        }

        private void CancelBlend() => _blend = default(Blend);

        private bool ApplyValue(EChannel channel, float value)
        {
            if (channel == EChannel.Speed)
            {
                if (Mathf.Approximately(_cursor.Speed, value))
                    return false;
                _cursor.SetSpeed(value);
                _changes++;
                return true;
            }
            if (Mathf.Approximately(_cursor.Duration, value))
                return false;
            _cursor.SetDuration(value);
            _changes++;
            return true;
        }

        // Effects started now begin counting on the next effects tick.
        private int CurrentEffectsFrame() => _effectsFrame > 0 ? _effectsFrame : Time.frameCount;

        #endregion

        private void SetState(EPathFollowerState state)
        {
            if (State == state)
                return;
            State = state;
            _changes++;
            PathExecutionBoundary.PublishState(_listeners.StateChanged, state);
        }

        private void EnsureInitialized()
        {
            if (State == EPathFollowerState.Uninitialized)
                throw new InvalidOperationException("PathFollower is not initialized.");
        }
    }
}
