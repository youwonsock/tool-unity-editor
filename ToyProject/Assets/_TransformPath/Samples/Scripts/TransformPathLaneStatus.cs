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
                + $"length={path.PathLength:F2}m  progress={follower.Progress:F2}  "
                + $"{DescribeMovement(follower)}\n"
                + $"samples={path.SamplePointCount}  state={follower.State}\n"
                + $"events={DescribeEvents(path, follower.MoveType)}";
        }

        public static string Multi(MultiPathData sequence, PathFollower follower)
        {
            int count = sequence.IsReady ? sequence.SegmentCount : 0;
            int index = count == 0 ? 0 : Mathf.Clamp(follower.SegmentIndex + 1, 1, count);
            return $"MULTI  ready={sequence.IsReady}  segment={index}/{count}\n"
                + $"global={follower.Progress:F2}  local={follower.SegmentProgress:F2}  "
                + $"{DescribeMovement(follower)}\n"
                + $"length={sequence.PathLength:F2}m  state={follower.State}\n"
                + $"events={DescribeSequenceEvents(sequence)}";
        }

        public static string Queue(TransformPathQueueLane lane, bool visible, bool paused)
        {
            QueuedPathManager manager = lane.Manager;
            PathQueueMember leader = lane.Leader;
            string leaderText = leader == null
                ? "none"
                : leader.IsBlocked ? "BLOCKED" : leader.Follower.IsMoving ? "moving" : "stopped";
            string ahead = "none";
            string multiplier = "1.00";
            if (leader != null && manager.TryGetMemberStatus(leader, out PathQueueStatus state))
            {
                ahead = state.DistanceToAhead.HasValue ? $"{state.DistanceToAhead.Value:F2}m" : "none";
                multiplier = leader.CurrentSpeedMultiplier.ToString("F2");
            }
            return $"QUEUE  agents={manager.MemberCount}/{lane.MemberCount}  visible={visible}\n"
                + $"spacing={manager.DefaultSpacing:F2}m  leader={leaderText}  ahead={ahead}\n"
                + $"speed x{multiplier}  routeRev={manager.RouteRevision}\n"
                + $"length={lane.Path.PathLength:F2}m  state={(paused ? "paused" : "running")}  "
                + $"{(leader == null ? "movement=none" : DescribeMovement(leader.Follower))}\n"
                + $"events={DescribeSequenceEvents(lane.Path)}";
        }

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
