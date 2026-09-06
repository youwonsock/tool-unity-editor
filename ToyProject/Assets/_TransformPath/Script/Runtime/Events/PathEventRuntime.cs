using UnityEngine;

namespace Common.TransformPath
{
    internal enum EPathEventIntent
    {
        None,
        Pause,
        Resume,
        ChangeValue,
    }

    /// <summary>
    /// Pure event intent and lifecycle execution used by the Unity event
    /// bridge. It has no scheduler, component, or ScriptableObject ownership.
    /// </summary>
    internal sealed class PathEventRuntime
    {
        public EPathEventIntent ResolveIntent(
            PathEventDefinition definition,
            IPathPlaybackState playback,
            bool resumeOnly)
        {
            if (definition == null || playback == null)
                return EPathEventIntent.None;

            return ResolveIntent(
                definition,
                playback.MoveType,
                playback.State,
                resumeOnly);
        }

        public EPathEventIntent ResolveIntent(
            PathEventDefinition definition,
            EPathMoveType moveType,
            EPathFollowerState state,
            bool resumeOnly)
        {
            if (definition == null)
                return EPathEventIntent.None;

            if (moveType == EPathMoveType.SpeedBased
                && definition.UseModifyPathMoveSpeed)
            {
                if (Mathf.Approximately(definition.MoveSpeedTargetValue, 0f))
                    return EPathEventIntent.Pause;
                if (resumeOnly || state == EPathFollowerState.Paused)
                    return EPathEventIntent.Resume;
                return EPathEventIntent.ChangeValue;
            }

            if (moveType == EPathMoveType.TimeBased
                && definition.UseModifyPathMoveDuration)
            {
                if (Mathf.Approximately(definition.MoveDurationTargetValue, 9999f))
                    return EPathEventIntent.Pause;
                if (resumeOnly || state == EPathFollowerState.Paused)
                    return EPathEventIntent.Resume;
                return EPathEventIntent.ChangeValue;
            }

            return EPathEventIntent.None;
        }

        public PathCommandReceipt ApplyLifecycle(
            EPathEventIntent intent,
            IPathFollower follower)
        {
            if (follower == null)
                return default(PathCommandReceipt);

            switch (intent)
            {
                case EPathEventIntent.Pause:
                    return follower.PauseMove();
                case EPathEventIntent.Resume:
                    if (follower.State == EPathFollowerState.Paused)
                        return follower.ResumeMove();
                    break;
            }

            return new PathCommandReceipt(
                follower.PlaybackId,
                follower.StateRevision,
                false);
        }
    }
}
