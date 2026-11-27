using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>What an event does to the playback's state.</summary>
    public enum EPathEventLifecycle
    {
        None = 0,
        Pause = 1,
        /// <summary>Resumes a playback that an event paused. A command Pause and a moving playback ignore it.</summary>
        Resume = 2,
    }

    /// <summary>An event placed on a path, copied from its authoring asset at build time.</summary>
    internal readonly struct PathRuntimeEvent
    {
        public float NormalizedTime { get; }
        public PathEventDefinition Definition { get; }

        public PathRuntimeEvent(float normalizedTime, PathEventDefinition definition)
        {
            NormalizedTime = normalizedTime;
            Definition = definition;
        }

        internal static bool AreSame(PathRuntimeEvent[] left, PathRuntimeEvent[] right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null || left.Length != right.Length)
                return false;
            for (int i = 0; i < left.Length; i++)
            {
                if (!Mathf.Approximately(left[i].NormalizedTime, right[i].NormalizedTime)
                    || !PathEventDefinition.AreSame(left[i].Definition, right[i].Definition))
                    return false;
            }
            return true;
        }

        internal static void SortByPosition(PathRuntimeEvent[] events)
        {
            // Events at the same position keep their authoring order.
            PathSortUtility.StableInsertionSort(events, ComparePosition);
        }

        private static readonly Comparison<PathRuntimeEvent> ComparePosition =
            (left, right) => left.NormalizedTime.CompareTo(right.NormalizedTime);
    }

    /// <summary>A Speed or Duration change, immediately or blended over game time.</summary>
    internal readonly struct PathValueChange
    {
        private readonly AnimationCurve _curve;

        public bool Enabled { get; }
        public float Target { get; }
        public float BlendTime { get; }

        internal PathValueChange(bool enabled, float target, float blendTime, AnimationCurve curve)
        {
            Enabled = enabled;
            Target = target;
            BlendTime = blendTime;
            _curve = enabled && blendTime > 0f ? PathValueUtility.CloneCurve(curve) : null;
        }

        /// <summary>Blend weight of 0..1 after the elapsed time.</summary>
        internal float Evaluate(float elapsed)
        {
            if (BlendTime <= 0f)
                return 1f;
            float t = Mathf.Clamp01(elapsed / BlendTime);
            return Mathf.Clamp01(_curve == null ? t : _curve.Evaluate(t));
        }

        internal static void Validate(bool enabled, float target, float blendTime, AnimationCurve curve, string name)
        {
            if (!enabled)
                return;
            if (!PathValueUtility.IsInRange(target, PathConstants.MIN_MOVE_VALUE, PathConstants.MAX_MOVE_VALUE))
                throw new ArgumentOutOfRangeException(name,
                    $"{name} target must be within {PathConstants.MIN_MOVE_VALUE}..{PathConstants.MAX_MOVE_VALUE}.");
            if (!PathValueUtility.IsNonNegativeFinite(blendTime))
                throw new ArgumentOutOfRangeException(name, $"{name} blend time must be finite and non-negative.");
            if (blendTime > 0f && !PathValueUtility.IsUsableCurve(curve))
                throw new ArgumentException($"{name} blend requires a curve with at least one key.", name);
        }

        internal static bool AreSame(PathValueChange left, PathValueChange right)
        {
            if (left.Enabled != right.Enabled)
                return false;
            // A disabled change plays nothing, so editing its values changes no playback.
            if (!left.Enabled)
                return true;
            return Mathf.Approximately(left.Target, right.Target)
                && Mathf.Approximately(left.BlendTime, right.BlendTime)
                && PathValueUtility.AreSameCurve(left._curve, right._curve);
        }
    }

    internal readonly struct PathDelayedEventDefinition
    {
        public float Delay { get; }
        public PathEventDefinition Definition { get; }

        public PathDelayedEventDefinition(float delay, PathEventDefinition definition)
        {
            Delay = delay;
            Definition = definition;
        }
    }

    /// <summary>
    /// Immutable copy of a <see cref="PathEventSettingSO"/>, validated once when
    /// it is made. Playback applies it in this order: name notification,
    /// lifecycle, the value change of the current movement type, delayed events.
    /// </summary>
    internal sealed class PathEventDefinition
    {
        private readonly PathDelayedEventDefinition[] _delayedEvents;

        public string EventName { get; }
        public EPathEventLifecycle Lifecycle { get; }
        public PathValueChange SpeedChange { get; }
        public PathValueChange DurationChange { get; }
        public IReadOnlyList<PathDelayedEventDefinition> DelayedEvents => _delayedEvents;

        /// <summary>
        /// Whether the authoring asks for anything but the name: a lifecycle, a
        /// value change or delayed events. Only such an event cancels the delayed
        /// events earlier ones left waiting.
        /// </summary>
        public bool ChangesPlayback => Lifecycle != EPathEventLifecycle.None
            || SpeedChange.Enabled
            || DurationChange.Enabled
            || _delayedEvents.Length > 0;

        private PathEventDefinition(string eventName, EPathEventLifecycle lifecycle,
            PathValueChange speedChange, PathValueChange durationChange, PathDelayedEventDefinition[] delayedEvents)
        {
            EventName = eventName ?? string.Empty;
            Lifecycle = lifecycle;
            SpeedChange = speedChange;
            DurationChange = durationChange;
            _delayedEvents = delayedEvents;
        }

        /// <summary>The change this event makes to a segment that moves this way.</summary>
        internal PathValueChange GetChange(EPathMoveType moveType)
        {
            return moveType == EPathMoveType.SpeedBased ? SpeedChange : DurationChange;
        }

        /// <summary>Copies and validates an asset and its delayed events. Throws on an invalid or cyclic graph.</summary>
        public static PathEventDefinition FromAuthoring(PathEventSettingSO setting)
        {
            if (setting == null)
                throw new ArgumentNullException(nameof(setting));
            return FromAuthoring(setting, new HashSet<PathEventSettingSO>());
        }

        private static PathEventDefinition FromAuthoring(PathEventSettingSO setting, HashSet<PathEventSettingSO> stack)
        {
            if (!stack.Add(setting))
                throw new ArgumentException("Delayed path event settings cannot contain a cycle.", nameof(setting));
            if (!Enum.IsDefined(typeof(EPathEventLifecycle), setting.Lifecycle))
                throw new ArgumentOutOfRangeException(nameof(setting), "Lifecycle is invalid.");
            PathValueChange.Validate(setting.UseSpeedChange, setting.SpeedTarget, setting.SpeedBlendTime,
                setting.SpeedBlendCurve, "Speed");
            PathValueChange.Validate(setting.UseDurationChange, setting.DurationTarget, setting.DurationBlendTime,
                setting.DurationBlendCurve, "Duration");

            PathDelayedEventDefinition[] delayed = Array.Empty<PathDelayedEventDefinition>();
            if (setting.DelayedEvents != null && setting.DelayedEvents.Count > 0)
            {
                delayed = new PathDelayedEventDefinition[setting.DelayedEvents.Count];
                for (int i = 0; i < delayed.Length; i++)
                {
                    PathEventSettingSO.DelayedEventEntry entry = setting.DelayedEvents[i];
                    if (entry == null || entry.EventSetting == null)
                        throw new ArgumentException("Delayed event entries require an EventSetting.", nameof(setting));
                    if (!PathValueUtility.IsNonNegativeFinite(entry.Delay))
                        throw new ArgumentOutOfRangeException(nameof(setting), "Delays must be finite and non-negative.");
                    delayed[i] = new PathDelayedEventDefinition(entry.Delay, FromAuthoring(entry.EventSetting, stack));
                }
            }

            stack.Remove(setting);
            return new PathEventDefinition(
                setting.EventName,
                setting.Lifecycle,
                new PathValueChange(setting.UseSpeedChange, setting.SpeedTarget, setting.SpeedBlendTime, setting.SpeedBlendCurve),
                new PathValueChange(setting.UseDurationChange, setting.DurationTarget, setting.DurationBlendTime, setting.DurationBlendCurve),
                delayed);
        }

        internal static bool AreSame(PathEventDefinition left, PathEventDefinition right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null
                || left.EventName != right.EventName
                || left.Lifecycle != right.Lifecycle
                || !PathValueChange.AreSame(left.SpeedChange, right.SpeedChange)
                || !PathValueChange.AreSame(left.DurationChange, right.DurationChange)
                || left._delayedEvents.Length != right._delayedEvents.Length)
                return false;
            for (int i = 0; i < left._delayedEvents.Length; i++)
            {
                if (!Mathf.Approximately(left._delayedEvents[i].Delay, right._delayedEvents[i].Delay)
                    || !AreSame(left._delayedEvents[i].Definition, right._delayedEvents[i].Definition))
                    return false;
            }
            return true;
        }
    }
}
