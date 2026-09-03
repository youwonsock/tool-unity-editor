using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath
{
    internal enum EMoveControlChannel
    {
        None,
        Speed,
        Duration,
    }

    internal enum EMoveEventIntent
    {
        None,
        Pause,
        Resume,
        ChangeValue,
    }

    internal readonly struct PathEventContext
    {
        public EMoveControlChannel Channel { get; }
        public EMoveEventIntent Intent { get; }
        public float TargetValue { get; }
        public float AdjustDuration { get; }
        public AnimationCurve AdjustCurve { get; }

        public PathEventContext(
            EMoveControlChannel channel,
            EMoveEventIntent intent,
            float targetValue,
            float adjustDuration,
            AnimationCurve adjustCurve)
        {
            Channel = channel;
            Intent = intent;
            TargetValue = targetValue;
            AdjustDuration = adjustDuration;
            AdjustCurve = adjustCurve;
        }
    }

    /// <summary>Pure validation and mode resolution for PathEventHandler.</summary>
    internal static class PathEventValidator
    {
        private const float TIME_BASED_PAUSE_VALUE = 9999f;

        public static PathEventContext ValidateSetting(
            PathEventSettingSO setting,
            PathFollower pathFollower,
            HashSet<PathEventSettingSO> validationStack,
            bool resumeOnly)
        {
            if (setting == null)
                throw new ArgumentNullException(nameof(setting));
            if (validationStack == null)
                throw new ArgumentNullException(nameof(validationStack));
            if (!validationStack.Add(setting))
                throw new ArgumentException(
                    "Delayed path event settings cannot contain a cycle.",
                    nameof(setting));

            ValidateNonNegativeFinite(
                setting.TimeScaleAdjustDuration,
                nameof(setting.TimeScaleAdjustDuration));
            if (setting.UseDelayedEvents && setting.DelayedEvents == null)
                throw new ArgumentException(
                    "Delayed event list is required when delayed events are enabled.",
                    nameof(setting));

            PathEventContext context = ResolveMoveEventContext(
                setting,
                pathFollower,
                resumeOnly);
            ValidateMoveEventContext(setting, pathFollower, context);
            ValidateTimeScale(setting);

            if (setting.UseDelayedEvents)
            {
                for (int i = 0; i < setting.DelayedEvents.Count; i++)
                {
                    PathEventSettingSO.DelayedEventEntry entry = setting.DelayedEvents[i];
                    if (entry == null || entry.EventSetting == null)
                        throw new ArgumentException(
                            "Delayed event entries require an EventSetting.",
                            nameof(setting));
                    ValidateNonNegativeFinite(entry.Delay, nameof(entry.Delay));
                    ValidateSetting(
                        entry.EventSetting,
                        pathFollower,
                        validationStack,
                        context.Intent == EMoveEventIntent.Pause);
                }
            }

            validationStack.Remove(setting);
            return context;
        }

        private static PathEventContext ResolveMoveEventContext(
            PathEventSettingSO setting,
            PathFollower pathFollower,
            bool resumeOnly)
        {
            bool hasMovementControl = setting.UseModifyPathMoveSpeed
                || setting.UseModifyPathMoveDuration;
            if (pathFollower == null)
            {
                if (hasMovementControl)
                    throw new ArgumentNullException(nameof(pathFollower));
                return default(PathEventContext);
            }

            switch (pathFollower.MoveType)
            {
                case EPathMoveType.SpeedBased:
                    if (!setting.UseModifyPathMoveSpeed)
                        return default(PathEventContext);
                    return new PathEventContext(
                        EMoveControlChannel.Speed,
                        ResolveSpeedIntent(
                            setting.MoveSpeedTargetValue,
                            pathFollower,
                            resumeOnly),
                        setting.MoveSpeedTargetValue,
                        setting.MoveSpeedAdjustDuration,
                        setting.MoveSpeedAdjustCurve);
                case EPathMoveType.TimeBased:
                    if (!setting.UseModifyPathMoveDuration)
                        return default(PathEventContext);
                    return new PathEventContext(
                        EMoveControlChannel.Duration,
                        ResolveDurationIntent(
                            setting.MoveDurationTargetValue,
                            pathFollower,
                            resumeOnly),
                        setting.MoveDurationTargetValue,
                        setting.MoveDurationAdjustDuration,
                        setting.MoveDurationAdjustCurve);
                default:
                    throw new InvalidOperationException(
                        "Path follower has an unsupported move mode.");
            }
        }

        private static EMoveEventIntent ResolveSpeedIntent(
            float targetValue,
            PathFollower pathFollower,
            bool resumeOnly)
        {
            if (targetValue == 0f)
                return EMoveEventIntent.Pause;
            if (resumeOnly || pathFollower.State == EPathFollowerState.Paused)
                return EMoveEventIntent.Resume;
            return EMoveEventIntent.ChangeValue;
        }

        private static EMoveEventIntent ResolveDurationIntent(
            float targetValue,
            PathFollower pathFollower,
            bool resumeOnly)
        {
            if (targetValue == TIME_BASED_PAUSE_VALUE)
                return EMoveEventIntent.Pause;
            if (resumeOnly || pathFollower.State == EPathFollowerState.Paused)
                return EMoveEventIntent.Resume;
            return EMoveEventIntent.ChangeValue;
        }

        private static void ValidateMoveEventContext(
            PathEventSettingSO setting,
            PathFollower pathFollower,
            PathEventContext context)
        {
            if (context.Channel == EMoveControlChannel.None)
                return;
            if (pathFollower == null)
                throw new ArgumentNullException(nameof(pathFollower));

            ValidateNonNegativeFinite(context.AdjustDuration, nameof(context.AdjustDuration));
            switch (context.Channel)
            {
                case EMoveControlChannel.Speed:
                    ValidateSpeedContext(setting, pathFollower, context);
                    break;
                case EMoveControlChannel.Duration:
                    ValidateDurationContext(setting, pathFollower, context);
                    break;
            }
        }

        private static void ValidateSpeedContext(
            PathEventSettingSO setting,
            PathFollower pathFollower,
            PathEventContext context)
        {
            if (!PathValueUtility.IsInRange(context.TargetValue, 0f, PathMovementSettingsUtility.MAX_VALUE))
                throw new ArgumentOutOfRangeException(nameof(setting.MoveSpeedTargetValue));

            switch (context.Intent)
            {
                case EMoveEventIntent.Pause:
                    if (context.TargetValue != 0f || context.AdjustDuration > 0f)
                        throw new ArgumentException(
                            "A zero-speed pause must be applied immediately.",
                            nameof(setting));
                    ValidatePauseEvent(setting, pathFollower, EPathMoveType.SpeedBased);
                    break;
                case EMoveEventIntent.Resume:
                    if (context.TargetValue <= 0f || context.AdjustDuration > 0f)
                        throw new ArgumentException(
                            "A Resume event must use an immediate positive speed target.",
                            nameof(setting));
                    break;
                case EMoveEventIntent.ChangeValue:
                    if (context.TargetValue <= 0f)
                        throw new ArgumentOutOfRangeException(nameof(setting.MoveSpeedTargetValue));
                    ValidateAdjustment(
                        setting.MoveSpeedAdjustDuration,
                        setting.MoveSpeedAdjustCurve,
                        nameof(setting.MoveSpeedAdjustCurve));
                    break;
            }
        }

        private static void ValidateDurationContext(
            PathEventSettingSO setting,
            PathFollower pathFollower,
            PathEventContext context)
        {
            if (!PathValueUtility.IsInRange(
                    context.TargetValue,
                    PathMovementSettingsUtility.MIN_VALUE,
                    PathMovementSettingsUtility.MAX_VALUE))
                throw new ArgumentOutOfRangeException(nameof(setting.MoveDurationTargetValue));

            switch (context.Intent)
            {
                case EMoveEventIntent.Pause:
                    if (context.TargetValue != TIME_BASED_PAUSE_VALUE
                        || context.AdjustDuration > 0f)
                        throw new ArgumentException(
                            "A Duration value of 9999 must be applied as an immediate pause.",
                            nameof(setting));
                    ValidatePauseEvent(setting, pathFollower, EPathMoveType.TimeBased);
                    break;
                case EMoveEventIntent.Resume:
                    if (context.TargetValue >= TIME_BASED_PAUSE_VALUE
                        || context.AdjustDuration > 0f)
                        throw new ArgumentException(
                            "A Resume event must use an immediate Duration target below 9999.",
                            nameof(setting));
                    break;
                case EMoveEventIntent.ChangeValue:
                    if (context.TargetValue >= TIME_BASED_PAUSE_VALUE)
                        throw new ArgumentOutOfRangeException(nameof(setting.MoveDurationTargetValue));
                    ValidateAdjustment(
                        setting.MoveDurationAdjustDuration,
                        setting.MoveDurationAdjustCurve,
                        nameof(setting.MoveDurationAdjustCurve));
                    break;
            }
        }

        private static void ValidateAdjustment(
            float duration,
            AnimationCurve curve,
            string curveParameterName)
        {
            if (duration <= 0f)
                return;
            if (curve == null || curve.length == 0)
                throw new ArgumentException(
                    "Movement adjustment requires a non-empty curve.",
                    curveParameterName);
        }

        private static void ValidatePauseEvent(
            PathEventSettingSO setting,
            PathFollower pathFollower,
            EPathMoveType pausedMoveType)
        {
            if (!setting.UseDelayedEvents
                || setting.DelayedEvents == null
                || setting.DelayedEvents.Count != 1)
                throw new ArgumentException(
                    "A pause requires exactly one delayed Resume event.",
                    nameof(setting));

            PathEventSettingSO.DelayedEventEntry entry = setting.DelayedEvents[0];
            if (entry == null || entry.EventSetting == null)
                throw new ArgumentException(
                    "A pause requires a delayed Resume event.",
                    nameof(setting));
            ValidateNonNegativeFinite(entry.Delay, nameof(entry.Delay));
            if (entry.Delay <= 0f)
                throw new ArgumentOutOfRangeException(
                    nameof(entry.Delay),
                    "Pause duration must be greater than zero.");

            PathEventContext resumeContext = ResolveMoveEventContext(
                entry.EventSetting,
                pathFollower,
                true);
            EMoveControlChannel expectedChannel = pausedMoveType == EPathMoveType.SpeedBased
                ? EMoveControlChannel.Speed
                : EMoveControlChannel.Duration;
            if (resumeContext.Channel != expectedChannel
                || resumeContext.Intent != EMoveEventIntent.Resume)
                throw new ArgumentException(
                    "A pause delayed event must provide a positive Resume value for the current follower mode.",
                    nameof(setting));

            ValidateMoveEventContext(entry.EventSetting, pathFollower, resumeContext);
            if (entry.EventSetting.DelayedEvents != null
                && entry.EventSetting.DelayedEvents.Count > 0)
                throw new ArgumentException(
                    "A delayed Resume event cannot enqueue another delayed event.",
                    nameof(setting));
        }

        private static void ValidateTimeScale(PathEventSettingSO setting)
        {
            if (!setting.UseTimeScaleAdjust)
                return;
            if (!PathValueUtility.IsInRange(
                    setting.TimeScaleAdjustValue,
                    PathMovementSettingsUtility.MIN_VALUE,
                    PathMovementSettingsUtility.MAX_VALUE)
                || setting.TimeScaleAdjustDuration <= 0f)
                throw new ArgumentOutOfRangeException(nameof(setting));
            if (setting.TimeScaleAdjustCurve == null
                || setting.TimeScaleAdjustCurve.length == 0)
                throw new ArgumentException(
                    "Time scale adjustment requires a non-empty curve.",
                    nameof(setting.TimeScaleAdjustCurve));
        }

        private static void ValidateNonNegativeFinite(float value, string parameterName)
        {
            if (!PathValueUtility.IsNonNegativeFinite(value))
                throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
