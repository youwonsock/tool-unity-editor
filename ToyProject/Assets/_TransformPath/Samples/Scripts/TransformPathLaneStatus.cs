using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath.Samples
{
    /// <summary>The status board text of each lane, read from the public API only.</summary>
    internal static class TransformPathLaneStatus
    {
        public static string Normal(PathData path, PathFollower follower)
        {
            return $"NORMAL  ready={path.IsReady}  curve={path.CurveType}\n"
                + $"length={Length(path)}  progress={follower.Progress:F2}  "
                + $"{DescribeMovement(follower)}\n"
                + $"samples={(path.IsReady ? path.SamplePointCount.ToString() : "n/a")}  state={follower.State}\n"
                + $"events={DescribeEvents(path, follower.MoveType)}";
        }

        public static string Multi(MultiPathData sequence, PathFollower follower)
        {
            int count = sequence.IsReady ? sequence.SegmentCount : 0;
            int index = count == 0 ? 0 : Mathf.Clamp(follower.SegmentIndex + 1, 1, count);
            return $"MULTI  ready={sequence.IsReady}  segment={index}/{count}\n"
                + $"global={follower.Progress:F2}  local={follower.SegmentProgress:F2}  "
                + $"{DescribeMovement(follower)}\n"
                + $"length={Length(sequence)}  state={follower.State}\n"
                + $"events={DescribeSequenceEvents(sequence)}";
        }

        public static string Queue(TransformPathQueueLane lane, bool visible, bool paused)
        {
            QueuedPathManager manager = lane.Manager;
            PathQueueMember leader = lane.Leader;
            string leaderText = leader == null
                ? "none"
                : leader.IsBlocked ? "BLOCKED" : leader.Follower.IsMoving ? "moving" : "stopped";
            string multiplier = leader == null ? "1.00" : leader.CurrentSpeedMultiplier.ToString("F2");
            // The member right behind the leader keeps the gap the spacing policy widened.
            string behindLeader = "none";
            if (manager.MemberCount > 1 && manager.TryGetMemberStatus(manager.GetMember(1), out PathQueueStatus second))
            {
                string distance = second.DistanceToAhead.HasValue ? $"{second.DistanceToAhead.Value:F2}m" : "none";
                behindLeader = $"{second.Spacing:F2}m (now {distance})";
            }
            string policy = manager.SpacingPolicy is TransformPathGapPolicy gap
                ? $"{gap.name} x{gap.LeaderGapScale:F1}"
                : manager.SpacingPolicy == null ? "none" : manager.SpacingPolicy.name;
            return $"QUEUE  agents={manager.MemberCount}/{lane.MemberCount}  visible={visible}  "
                + $"leader={leaderText}  state={(paused ? "paused" : "running")}\n"
                + $"spacing={manager.DefaultSpacing:F2}m  policy={policy}\n"
                + $"behind leader={behindLeader}  speed x{multiplier}  routeRev={manager.RouteRevision}\n"
                + $"length={Length(lane.Path)}  "
                + $"{(leader == null ? "movement=none" : DescribeMovement(leader.Follower))}\n"
                + $"events={DescribeSequenceEvents(lane.Path)}";
        }

        public static string Shape(TransformPathCircleShape circle, PathFollower follower)
        {
            return $"SHAPE  ready={circle.IsReady}  circle radius={circle.Radius:F2}m\n"
                + $"length={Length(circle)}  progress={follower.Progress:F2}  "
                + $"{DescribeMovement(follower)}\n"
                + $"state={follower.State}  loop  started by Play On Start\n"
                + "events=none (a shape has no events)";
        }

        // A provider that is not ready has no length; the board says so instead of throwing.
        private static string Length(PathProviderBehaviour provider) =>
            provider != null && provider.IsReady ? $"{provider.PathLength:F2}m" : "n/a";

        private static string DescribeMovement(PathFollower follower)
        {
            if (follower == null)
                return "movement=none";
            return follower.MoveType == EPathMoveType.SpeedBased
                ? $"speed={follower.Speed:F2}"
                : $"duration={follower.Duration:F2}s";
        }

        private static string DescribeEvents(PathData path, EPathMoveType moveType)
        {
            List<string> descriptions = new List<string>();
            AddEventDescriptions(path, moveType, string.Empty, descriptions);
            return descriptions.Count == 0 ? "none" : string.Join(" · ", descriptions);
        }

        private static string DescribeSequenceEvents(MultiPathData sequence)
        {
            if (sequence == null || !sequence.IsReady)
                return "none";

            List<string> descriptions = new List<string>();
            for (int segment = 0; segment < sequence.SegmentCount; segment++)
            {
                EPathMoveType moveType = sequence.GetSegmentMovement(segment).MoveType;
                AddEventDescriptions(sequence.GetSegmentPath(segment), moveType, $"S{segment + 1} ", descriptions);
            }
            return descriptions.Count == 0 ? "none" : string.Join(" · ", descriptions);
        }

        // The authored events in position order, as playback reaches them.
        private static void AddEventDescriptions(PathData path, EPathMoveType moveType, string prefix, List<string> descriptions)
        {
            if (path == null || path.AuthoringEventCount == 0)
                return;
            List<PathEventEntry> entries = new List<PathEventEntry>(path.AuthoringEventCount);
            for (int i = 0; i < path.AuthoringEventCount; i++)
            {
                PathEventEntry entry = path.GetAuthoringEvent(i);
                int at = entries.Count;
                while (at > 0 && entries[at - 1].NormalizedTime > entry.NormalizedTime)
                    at--;
                entries.Insert(at, entry);
            }
            for (int i = 0; i < entries.Count; i++)
                descriptions.Add(prefix + DescribeEvent(entries[i], moveType));
        }

        private static string DescribeEvent(PathEventEntry pathEvent, EPathMoveType moveType)
        {
            PathEventSettingSO setting = pathEvent.EventSetting;
            if (setting == null)
                return $"event @ {pathEvent.NormalizedTime:F2}";

            string action = "notify";
            bool speed = moveType == EPathMoveType.SpeedBased;
            if (setting.Lifecycle == EPathEventLifecycle.Pause)
                action = "Pause";
            else if (speed && setting.UseSpeedChange)
                action = $"Speed {setting.SpeedTarget:F2}";
            else if (!speed && setting.UseDurationChange)
                action = $"Duration {setting.DurationTarget:F2}s";

            string delayed = setting.DelayedEvents == null || setting.DelayedEvents.Count == 0
                ? string.Empty
                : $" + delay {setting.DelayedEvents[0].Delay:F2}s";
            return $"{setting.EventName} @ {pathEvent.NormalizedTime:F2} ({action}{delayed})";
        }
    }
}
