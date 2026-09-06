using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// Immutable runtime event copied from a PathEventSettingSO at rebuild time.
    /// The runtime path never reads authoring ScriptableObjects while ticking.
    /// </summary>
    public readonly struct PathRuntimeEvent
    {
        public float NormalizedTime { get; }
        public PathEventDefinition Definition { get; }

        public PathRuntimeEvent(float normalizedTime, PathEventDefinition definition)
        {
            NormalizedTime = normalizedTime;
            Definition = definition;
        }
    }

    public readonly struct PathDelayedEventDefinition
    {
        public float Delay { get; }
        public PathEventDefinition Definition { get; }

        public PathDelayedEventDefinition(float delay, PathEventDefinition definition)
        {
            Delay = delay;
            Definition = definition;
        }
    }

    public sealed class PathEventDefinition
    {
        private readonly PathDelayedEventDefinition[] _delayedEvents;
        private readonly IReadOnlyList<PathDelayedEventDefinition> _delayedEventsView;

        public string EventName { get; }
        public bool UseModifyPathMoveSpeed { get; }
        public float MoveSpeedTargetValue { get; }
        public float MoveSpeedAdjustDuration { get; }
        private readonly AnimationCurve _moveSpeedAdjustCurve;
        public bool UseModifyPathMoveDuration { get; }
        public float MoveDurationTargetValue { get; }
        public float MoveDurationAdjustDuration { get; }
        private readonly AnimationCurve _moveDurationAdjustCurve;
        public bool UseTimeScaleAdjust { get; }
        public float TimeScaleAdjustValue { get; }
        public float TimeScaleAdjustDuration { get; }
        private readonly AnimationCurve _timeScaleAdjustCurve;
        internal AnimationCurve MoveSpeedAdjustCurve => CloneCurve(_moveSpeedAdjustCurve);
        internal AnimationCurve MoveDurationAdjustCurve => CloneCurve(_moveDurationAdjustCurve);
        internal AnimationCurve TimeScaleAdjustCurve => CloneCurve(_timeScaleAdjustCurve);
        public IReadOnlyList<PathDelayedEventDefinition> DelayedEvents => _delayedEventsView;

        public PathEventDefinition(
            string eventName,
            bool useModifyPathMoveSpeed,
            float moveSpeedTargetValue,
            float moveSpeedAdjustDuration,
            AnimationCurve moveSpeedAdjustCurve,
            bool useModifyPathMoveDuration,
            float moveDurationTargetValue,
            float moveDurationAdjustDuration,
            AnimationCurve moveDurationAdjustCurve,
            bool useTimeScaleAdjust,
            float timeScaleAdjustValue,
            float timeScaleAdjustDuration,
            AnimationCurve timeScaleAdjustCurve,
            IReadOnlyList<PathDelayedEventDefinition> delayedEvents = null)
        {
            EventName = eventName ?? string.Empty;
            UseModifyPathMoveSpeed = useModifyPathMoveSpeed;
            MoveSpeedTargetValue = moveSpeedTargetValue;
            MoveSpeedAdjustDuration = moveSpeedAdjustDuration;
            _moveSpeedAdjustCurve = CloneCurve(moveSpeedAdjustCurve);
            UseModifyPathMoveDuration = useModifyPathMoveDuration;
            MoveDurationTargetValue = moveDurationTargetValue;
            MoveDurationAdjustDuration = moveDurationAdjustDuration;
            _moveDurationAdjustCurve = CloneCurve(moveDurationAdjustCurve);
            UseTimeScaleAdjust = useTimeScaleAdjust;
            TimeScaleAdjustValue = timeScaleAdjustValue;
            TimeScaleAdjustDuration = timeScaleAdjustDuration;
            _timeScaleAdjustCurve = CloneCurve(timeScaleAdjustCurve);

            if (delayedEvents == null || delayedEvents.Count == 0)
            {
                _delayedEvents = Array.Empty<PathDelayedEventDefinition>();
                _delayedEventsView = Array.AsReadOnly(_delayedEvents);
                return;
            }

            _delayedEvents = new PathDelayedEventDefinition[delayedEvents.Count];
            for (int i = 0; i < delayedEvents.Count; i++)
                _delayedEvents[i] = delayedEvents[i];
            _delayedEventsView = Array.AsReadOnly(_delayedEvents);
        }

        public static PathEventDefinition FromAuthoring(
            PathEventSettingSO setting,
            HashSet<PathEventSettingSO> stack = null)
        {
            if (setting == null)
                return null;

            stack ??= new HashSet<PathEventSettingSO>();
            if (!stack.Add(setting))
                throw new ArgumentException("Delayed path event settings cannot contain a cycle.", nameof(setting));

            List<PathDelayedEventDefinition> delayed = null;
            if (setting.UseDelayedEvents && setting.DelayedEvents != null)
            {
                delayed = new List<PathDelayedEventDefinition>(setting.DelayedEvents.Count);
                for (int i = 0; i < setting.DelayedEvents.Count; i++)
                {
                    PathEventSettingSO.DelayedEventEntry entry = setting.DelayedEvents[i];
                    if (entry == null || entry.EventSetting == null)
                        throw new ArgumentException("Delayed event entries require an EventSetting.", nameof(setting));
                    if (!PathValueUtility.IsNonNegativeFinite(entry.Delay))
                        throw new ArgumentOutOfRangeException(nameof(entry.Delay));

                    PathEventDefinition child = FromAuthoring(entry.EventSetting, stack);
                    delayed.Add(new PathDelayedEventDefinition(entry.Delay, child));
                }
            }

            stack.Remove(setting);
            return new PathEventDefinition(
                setting.EventName,
                setting.UseModifyPathMoveSpeed,
                setting.MoveSpeedTargetValue,
                setting.MoveSpeedAdjustDuration,
                setting.MoveSpeedAdjustCurve,
                setting.UseModifyPathMoveDuration,
                setting.MoveDurationTargetValue,
                setting.MoveDurationAdjustDuration,
                setting.MoveDurationAdjustCurve,
                setting.UseTimeScaleAdjust,
                setting.TimeScaleAdjustValue,
                setting.TimeScaleAdjustDuration,
                setting.TimeScaleAdjustCurve,
                delayed);
        }

        private static AnimationCurve CloneCurve(AnimationCurve source)
        {
            return source == null ? null : new AnimationCurve(source.keys)
            {
                preWrapMode = source.preWrapMode,
                postWrapMode = source.postWrapMode,
            };
        }

    }

    /// <summary>
    /// Immutable event source captured with a playback snapshot. Providers can
    /// publish a new event array on a later revision without changing an
    /// already prepared playback.
    /// </summary>
    internal sealed class PathEventSourceSnapshot : IPathEventSource
    {
        private readonly PathRuntimeEvent[] _events;

        public int EventCount => _events.Length;

        private PathEventSourceSnapshot(PathRuntimeEvent[] events)
        {
            _events = events ?? Array.Empty<PathRuntimeEvent>();
        }

        public PathRuntimeEvent GetEvent(int index)
        {
            if (index < 0 || index >= _events.Length)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _events[index];
        }

        public static PathEventSourceSnapshot Create(IPathEventSource source)
        {
            if (source == null || source.EventCount == 0)
                return new PathEventSourceSnapshot(Array.Empty<PathRuntimeEvent>());

            PathRuntimeEvent[] events = new PathRuntimeEvent[source.EventCount];
            for (int i = 0; i < events.Length; i++)
                events[i] = source.GetEvent(i);

            // Providers publish an ordered event stream. Normalize external
            // implementations as well while preserving equal-position order.
            for (int sortIndex = 1; sortIndex < events.Length; sortIndex++)
            {
                PathRuntimeEvent current = events[sortIndex];
                int previousIndex = sortIndex - 1;
                while (previousIndex >= 0
                    && events[previousIndex].NormalizedTime > current.NormalizedTime)
                {
                    events[previousIndex + 1] = events[previousIndex];
                    previousIndex--;
                }
                events[previousIndex + 1] = current;
            }
            return new PathEventSourceSnapshot(events);
        }

        public bool Matches(IPathEventSource source)
        {
            if (source == null)
                return _events.Length == 0;
            if (source.EventCount != _events.Length)
                return false;
            for (int i = 0; i < _events.Length; i++)
            {
                PathRuntimeEvent candidate = source.GetEvent(i);
                if (!AreSame(candidate, _events[i]))
                    return false;
            }
            return true;
        }

        internal bool HasSameContent(PathEventSourceSnapshot other)
        {
            if (other == null || other._events.Length != _events.Length)
                return false;
            for (int i = 0; i < _events.Length; i++)
            {
                if (!AreSame(_events[i], other._events[i]))
                    return false;
            }
            return true;
        }

        private static bool AreSame(PathRuntimeEvent left, PathRuntimeEvent right)
        {
            return Mathf.Approximately(left.NormalizedTime, right.NormalizedTime)
                && AreSame(left.Definition, right.Definition);
        }

        private static bool AreSame(
            PathEventDefinition left,
            PathEventDefinition right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null
                || left.EventName != right.EventName
                || left.UseModifyPathMoveSpeed != right.UseModifyPathMoveSpeed
                || !Mathf.Approximately(left.MoveSpeedTargetValue, right.MoveSpeedTargetValue)
                || !Mathf.Approximately(left.MoveSpeedAdjustDuration, right.MoveSpeedAdjustDuration)
                || !PathMovementSettingsUtility.AreSameCurve(
                    left.MoveSpeedAdjustCurve,
                    right.MoveSpeedAdjustCurve)
                || left.UseModifyPathMoveDuration != right.UseModifyPathMoveDuration
                || !Mathf.Approximately(left.MoveDurationTargetValue, right.MoveDurationTargetValue)
                || !Mathf.Approximately(left.MoveDurationAdjustDuration, right.MoveDurationAdjustDuration)
                || !PathMovementSettingsUtility.AreSameCurve(
                    left.MoveDurationAdjustCurve,
                    right.MoveDurationAdjustCurve)
                || left.UseTimeScaleAdjust != right.UseTimeScaleAdjust
                || !Mathf.Approximately(left.TimeScaleAdjustValue, right.TimeScaleAdjustValue)
                || !Mathf.Approximately(left.TimeScaleAdjustDuration, right.TimeScaleAdjustDuration)
                || !PathMovementSettingsUtility.AreSameCurve(
                    left.TimeScaleAdjustCurve,
                    right.TimeScaleAdjustCurve)
                || left.DelayedEvents.Count != right.DelayedEvents.Count)
                return false;

            for (int i = 0; i < left.DelayedEvents.Count; i++)
            {
                PathDelayedEventDefinition leftDelayed = left.DelayedEvents[i];
                PathDelayedEventDefinition rightDelayed = right.DelayedEvents[i];
                if (!Mathf.Approximately(leftDelayed.Delay, rightDelayed.Delay)
                    || !AreSame(leftDelayed.Definition, rightDelayed.Definition))
                    return false;
            }
            return true;
        }
    }

    public interface IPathEventSource
    {
        int EventCount { get; }
        PathRuntimeEvent GetEvent(int index);
    }

    public interface IPathEventReceiver
    {
        void ReceivePathEvent(string eventName, IPathPlaybackState playback);
    }
}
