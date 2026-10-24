using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>Validates immutable event graphs without a Unity component.</summary>
    internal static class PathEventDefinitionValidator
    {
        public static void ValidateDefinition(
            PathEventDefinition definition,
            IPathPlaybackState follower,
            bool resumeOnly,
            HashSet<PathEventDefinition> stack,
            EPathMoveType? moveTypeOverride = null,
            EPathFollowerState? stateOverride = null)
        {
            if (definition == null)
                throw new ArgumentNullException(nameof(definition));
            if (!stack.Add(definition))
                throw new ArgumentException("Delayed path event settings cannot contain a cycle.");

            if (definition.UseTimeScaleAdjust)
            {
                if (!PathValueUtility.IsInRange(
                         definition.TimeScaleAdjustValue,
                         0f,
                         PathMovementSettingsUtility.MAX_VALUE)
                    || definition.TimeScaleAdjustDuration <= 0f)
                    throw new ArgumentOutOfRangeException(nameof(definition));
                ValidateAdjustment(definition.TimeScaleAdjustDuration, definition.TimeScaleAdjustCurve);
            }

            EPathEventIntent intent = PathEventRuntime.ResolveIntent(
                definition,
                moveTypeOverride ?? follower.MoveType,
                stateOverride ?? follower.State,
                resumeOnly);
            ValidateMovementIntent(
                definition,
                moveTypeOverride ?? follower.MoveType,
                intent);
            if (intent == EPathEventIntent.Pause)
            {
                if (definition.DelayedEvents.Count != 1
                    || definition.DelayedEvents[0].Delay <= 0f
                    || definition.DelayedEvents[0].Definition == null)
                    throw new ArgumentException("A pause requires exactly one delayed Resume event.");
                EPathEventIntent resumeIntent = PathEventRuntime.ResolveIntent(
                    definition.DelayedEvents[0].Definition,
                    moveTypeOverride ?? follower.MoveType,
                    stateOverride ?? follower.State,
                    true);
                if (resumeIntent != EPathEventIntent.Resume
                    || definition.DelayedEvents[0].Definition.DelayedEvents.Count != 0)
                    throw new ArgumentException("A pause delayed event must be a terminal Resume event.");
            }
            else if (intent == EPathEventIntent.Resume
                && definition.DelayedEvents.Count != 0)
            {
                throw new ArgumentException("A Resume event cannot enqueue delayed events.");
            }

            for (int i = 0; i < definition.DelayedEvents.Count; i++)
            {
                PathDelayedEventDefinition delayed = definition.DelayedEvents[i];
                if (!PathValueUtility.IsNonNegativeFinite(delayed.Delay))
                    throw new ArgumentOutOfRangeException(nameof(delayed.Delay));
                ValidateDefinition(
                    delayed.Definition,
                    follower,
                    intent == EPathEventIntent.Pause && i == 0,
                    stack,
                    moveTypeOverride,
                    stateOverride);
            }

            stack.Remove(definition);
        }

        private static void ValidateMovementIntent(
            PathEventDefinition definition,
            EPathMoveType moveType,
            EPathEventIntent intent)
        {
            if (intent == EPathEventIntent.None)
                return;

            switch (moveType)
            {
                case EPathMoveType.SpeedBased:
                    if (!definition.UseModifyPathMoveSpeed)
                        return;
                    if (!PathValueUtility.IsInRange(
                            definition.MoveSpeedTargetValue,
                            0f,
                            PathMovementSettingsUtility.MAX_VALUE))
                        throw new ArgumentOutOfRangeException(
                            nameof(definition.MoveSpeedTargetValue));
                    ValidateNonNegativeDuration(
                        definition.MoveSpeedAdjustDuration,
                        nameof(definition.MoveSpeedAdjustDuration));
                    if (intent == EPathEventIntent.Pause
                        && (!Mathf.Approximately(definition.MoveSpeedTargetValue, 0f)
                            || definition.MoveSpeedAdjustDuration > 0f))
                        throw new ArgumentException(
                            "A zero-speed pause must be applied immediately.");
                    if (intent == EPathEventIntent.Resume
                        && (definition.MoveSpeedTargetValue <= 0f
                            || definition.MoveSpeedAdjustDuration > 0f))
                        throw new ArgumentException(
                            "A Resume event must use an immediate positive speed target.");
                    if (intent == EPathEventIntent.ChangeValue
                        && definition.MoveSpeedTargetValue <= 0f)
                        throw new ArgumentOutOfRangeException(
                            nameof(definition.MoveSpeedTargetValue));
                    if (intent == EPathEventIntent.ChangeValue)
                        ValidateAdjustment(
                            definition.MoveSpeedAdjustDuration,
                            definition.MoveSpeedAdjustCurve);
                    return;

                case EPathMoveType.TimeBased:
                    if (!definition.UseModifyPathMoveDuration)
                        return;
                    if (!PathValueUtility.IsInRange(
                            definition.MoveDurationTargetValue,
                            PathMovementSettingsUtility.MIN_VALUE,
                            PathMovementSettingsUtility.MAX_VALUE))
                        throw new ArgumentOutOfRangeException(
                            nameof(definition.MoveDurationTargetValue));
                    ValidateNonNegativeDuration(
                        definition.MoveDurationAdjustDuration,
                        nameof(definition.MoveDurationAdjustDuration));
                    if (intent == EPathEventIntent.Pause
                        && (!Mathf.Approximately(
                                definition.MoveDurationTargetValue,
                                PathMovementSettingsUtility.MAX_VALUE)
                            || definition.MoveDurationAdjustDuration > 0f))
                        throw new ArgumentException(
                            "A Duration value of 9999 must be applied as an immediate pause.");
                    if (intent == EPathEventIntent.Resume
                        && (definition.MoveDurationTargetValue
                                >= PathMovementSettingsUtility.MAX_VALUE
                            || definition.MoveDurationAdjustDuration > 0f))
                        throw new ArgumentException(
                            "A Resume event must use an immediate Duration target below 9999.");
                    if (intent == EPathEventIntent.ChangeValue
                        && definition.MoveDurationTargetValue
                            >= PathMovementSettingsUtility.MAX_VALUE)
                        throw new ArgumentOutOfRangeException(
                            nameof(definition.MoveDurationTargetValue));
                    if (intent == EPathEventIntent.ChangeValue)
                        ValidateAdjustment(
                            definition.MoveDurationAdjustDuration,
                            definition.MoveDurationAdjustCurve);
                    return;
            }
        }

        private static void ValidateNonNegativeDuration(
            float duration,
            string parameterName)
        {
            if (!PathValueUtility.IsNonNegativeFinite(duration))
                throw new ArgumentOutOfRangeException(parameterName);
        }

        private static void ValidateAdjustment(float duration, AnimationCurve curve)
        {
            if (!PathValueUtility.IsNonNegativeFinite(duration))
                throw new ArgumentOutOfRangeException(nameof(duration));
            if (duration <= 0f)
                return;
            if (curve == null || curve.length == 0)
                throw new ArgumentException("An adjustment requires a non-empty curve.");
        }

    }
}
