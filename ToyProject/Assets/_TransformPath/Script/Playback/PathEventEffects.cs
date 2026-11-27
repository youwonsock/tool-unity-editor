using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath
{
    internal enum EPathEffectChannel
    {
        None,
        Speed,
        Duration,
    }

    /// <summary>What a playback lets its event effects read and do.</summary>
    internal interface IPathEffectTarget
    {
        EPathMoveType MoveType { get; }
        /// <summary>0 when no playback runs.</summary>
        ulong PlaybackId { get; }
        bool HasRoute { get; }
        float Speed { get; }
        float Duration { get; }
        // Each returns whether it changed the state or the value. These are an
        // event's Pause and Resume: the Resume wakes only what an event paused.
        bool Pause();
        bool Resume();
        bool ApplyValue(EPathEffectChannel channel, float value);
        void PublishEvent(string eventName);
    }

    /// <summary>
    /// What path events do to a playback: names, Pause and Resume, value blends
    /// and delayed events. The playback decides when an event is reached; this
    /// decides what follows from it.
    /// </summary>
    internal sealed class PathEventEffects
    {
        // The playback clears its effects whenever a playback ends or another
        // starts, so a blend or a delayed event always belongs to the current one.
        private struct Blend
        {
            public EPathEffectChannel Channel;
            public float StartValue;
            public PathValueChange Change;
            public float Elapsed;
            public int StartFrame;
        }

        private struct DelayedEvent
        {
            public PathEventDefinition Definition;
            public float RemainingTime;
            public int StartFrame;
            public ulong Order;
        }

        private readonly IPathEffectTarget _target;
        private readonly List<DelayedEvent> _delayed = new List<DelayedEvent>();
        private Blend _blend;
        private ulong _delayedOrder;
        private int _effectsFrame;

        public PathEventEffects(IPathEffectTarget target)
        {
            _target = target;
        }

        /// <summary>Whether a blend or a delayed event waits for the effects tick.</summary>
        public bool HasPending => _blend.Channel != EPathEffectChannel.None || _delayed.Count > 0;

        /// <summary>
        /// Runs an event the follower reached or that was triggered. An event with a
        /// lifecycle, a value change or delayed events cancels the delayed events
        /// earlier ones left waiting, even when it changes nothing now; one that
        /// only announces its name keeps them.
        /// </summary>
        public void Run(PathEventDefinition definition)
        {
            if (definition.ChangesPlayback)
                _delayed.Clear();
            Process(definition);
        }

        /// <summary>Advances the blend and the delayed events. Runs every frame, before any queue or movement.</summary>
        public void Tick(float deltaTime, int frame)
        {
            _effectsFrame = frame;
            if (!HasPending)
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

            // A delayed event that changed something ends this frame's delayed events.
            while (TryTakeDueEvent(frame, out DelayedEvent due))
            {
                if (Process(due.Definition))
                    break;
            }
        }

        public void CancelBlend() => _blend = default(Blend);

        /// <summary>Cancels the blend only when it drives this channel.</summary>
        public void CancelBlend(EPathEffectChannel channel)
        {
            if (_blend.Channel == channel)
                CancelBlend();
        }

        /// <summary>The playback ended: nothing waiting belongs to the next one.</summary>
        public void Clear()
        {
            _delayed.Clear();
            CancelBlend();
        }

        // Returns whether the event changed the state or a value. Only the calls
        // made here can: listeners run after the boundary, never in between.
        private bool Process(PathEventDefinition definition)
        {
            if (!string.IsNullOrEmpty(definition.EventName))
                _target.PublishEvent(definition.EventName);

            bool changed = false;
            // Pause cancels the blend itself, moving or not.
            if (definition.Lifecycle == EPathEventLifecycle.Pause)
                changed = _target.Pause();
            else
            {
                if (definition.Lifecycle == EPathEventLifecycle.Resume)
                    changed = _target.Resume();
                PathValueChange change = definition.GetChange(_target.MoveType);
                if (change.Enabled && _target.HasRoute)
                    changed |= StartBlend(_target.MoveType == EPathMoveType.SpeedBased ? EPathEffectChannel.Speed : EPathEffectChannel.Duration, change);
            }

            // Pause, Resume and value changes keep the playback, so this holds for the whole chain.
            bool hasPlayback = _target.PlaybackId != 0;
            bool zeroDelayChanged = false;
            IReadOnlyList<PathDelayedEventDefinition> delayed = definition.DelayedEvents;
            for (int i = 0; i < delayed.Count; i++)
            {
                PathDelayedEventDefinition entry = delayed[i];
                // A zero-delay entry runs now. After one that changed something, the
                // later ones wait for the next tick, as a frame's delayed events do;
                // the rest of the list is still scheduled.
                if (entry.Delay <= 0f && !zeroDelayChanged)
                {
                    if (Process(entry.Definition))
                    {
                        changed = true;
                        // Without a playback nothing ticks, and nothing moves between them.
                        zeroDelayChanged = hasPlayback;
                    }
                    continue;
                }
                // Without a playback nothing would ever tick the delay.
                if (!hasPlayback)
                    continue;
                _delayed.Add(new DelayedEvent
                {
                    Definition = entry.Definition,
                    RemainingTime = Mathf.Max(0f, entry.Delay),
                    StartFrame = CurrentEffectsFrame(),
                    Order = ++_delayedOrder,
                });
            }
            return changed;
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

        // Returns whether the value changed now. A blend changes it from the next tick.
        private bool StartBlend(EPathEffectChannel channel, PathValueChange change)
        {
            // A finished playback no longer moves, so its final value is the target.
            if (change.BlendTime <= 0f || _target.PlaybackId == 0)
                return _target.ApplyValue(channel, change.Target);
            _blend = new Blend
            {
                Channel = channel,
                StartValue = channel == EPathEffectChannel.Speed ? _target.Speed : _target.Duration,
                Change = change,
                Elapsed = 0f,
                StartFrame = CurrentEffectsFrame(),
            };
            return false;
        }

        private void TickBlend(float deltaTime, int frame)
        {
            if (_blend.Channel == EPathEffectChannel.None || _blend.StartFrame >= frame || deltaTime <= 0f)
                return;
            _blend.Elapsed += deltaTime;
            float weight = _blend.Change.Evaluate(_blend.Elapsed);
            _target.ApplyValue(_blend.Channel, Mathf.Lerp(_blend.StartValue, _blend.Change.Target, weight));
            if (_blend.Elapsed >= _blend.Change.BlendTime)
            {
                _target.ApplyValue(_blend.Channel, _blend.Change.Target);
                CancelBlend();
            }
        }

        // Effects started now carry the last tick's frame, so they begin counting on the next effects tick.
        private int CurrentEffectsFrame() => _effectsFrame;
    }
}
