using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// The reads both space modes share: samples, clamps and field info, and how
    /// a failed read becomes an exception.
    /// </summary>
    internal sealed partial class FlowFieldSession
    {
        // The read both modes share; it never throws.
        private FlowFieldSampleStatus TrySampleStatus(Vector3 worldPosition, out FlowFieldSample sample)
        {
            sample = FlowFieldSample.Stopped;
            if (!IsReady)
                return FlowFieldSampleStatus.NotReady;
            return FlowFieldSampler.Sample(_builder.PublishedField, worldPosition, Safety, _builder.LatestCells, out sample);
        }

        internal bool TrySample(Vector3 worldPosition, out FlowFieldSample sample)
            => TrySampleStatus(worldPosition, out sample) == FlowFieldSampleStatus.Sampled;

        internal FlowFieldSample Sample(Vector3 worldPosition)
        {
            FlowFieldSampleStatus status = TrySampleStatus(worldPosition, out FlowFieldSample sample);
            if (status != FlowFieldSampleStatus.Sampled)
                throw FlowFieldFailures.ForSample(status, nameof(worldPosition));
            return sample;
        }

        // The clamp both modes share; it never throws. It uses the latest
        // grid: Surface2D grids clamp X and Z, Volume3D grids all three axes.
        private FlowFieldClampStatus TryClampStatus(Vector3 worldPosition, out FlowFieldClampResult result)
        {
            result = default;
            if (!IsInitialized)
                return FlowFieldClampStatus.NotInitialized;
            if (!FlowFieldGridSpace.IsFinite(worldPosition))
                return FlowFieldClampStatus.NonFinite;
            FlowFieldGridSpace grid = _builder.LatestGrid;
            if (!grid.IsValid)
                return FlowFieldClampStatus.NoGrid;
            bool volume = grid.SpaceMode == FlowFieldSpaceMode.Volume3D;
            Vector3 clamped = volume ? grid.ClampWorldXYZ(worldPosition) : grid.ClampWorldXZ(worldPosition);
            result = new FlowFieldClampResult(
                clamped,
                !Mathf.Approximately(worldPosition.x, clamped.x),
                volume && !Mathf.Approximately(worldPosition.y, clamped.y),
                !Mathf.Approximately(worldPosition.z, clamped.z));
            return FlowFieldClampStatus.Clamped;
        }

        internal bool TryClampPositionToGrid(Vector3 worldPosition, out FlowFieldClampResult result)
            => TryClampStatus(worldPosition, out result) == FlowFieldClampStatus.Clamped;

        /// <summary>The nearest point inside the latest grid.</summary>
        internal FlowFieldClampResult ClampPositionToGrid(Vector3 worldPosition)
        {
            FlowFieldClampStatus status = TryClampStatus(worldPosition, out FlowFieldClampResult result);
            if (status != FlowFieldClampStatus.Clamped)
                throw FlowFieldFailures.ForClamp(status, nameof(worldPosition));
            return result;
        }

        // Only a session that can sample describes its field.
        internal bool TryGetFieldInfo(out FlowFieldFieldInfo info)
        {
            info = default;
            return IsReady && _builder.TryGetFieldInfo(Revision, out info);
        }

        /// <summary>
        /// The published field, for display and bakes. It is valid until the
        /// next pump, after which its arrays may be reused.
        /// </summary>
        internal bool TryGetPublishedField(out FlowFieldField field)
        {
            field = IsReady ? _builder.PublishedField : null;
            return field != null;
        }

        // ----- Additional Goals ---------------------------------------------
        // A Goal's field reads like the default one, with the same safety rule
        // and uncertain regions, from its own Goal layer.

        /// <summary>Whether the Goal's field answers samples.</summary>
        internal bool IsGoalReady(int id) => GoalField(id) != null;

        internal bool TrySampleGoal(int id, Vector3 worldPosition, out FlowFieldSample sample)
        {
            sample = FlowFieldSample.Stopped;
            FlowFieldField field = GoalField(id);
            return field != null
                && FlowFieldSampler.Sample(field, worldPosition, Safety, _builder.LatestCells, out sample)
                    == FlowFieldSampleStatus.Sampled;
        }

        /// <summary>
        /// Samples the shared Goal the fewest steps away from the position's
        /// cell, the earliest added on a tie. Where no shared Goal reaches the
        /// cell (blocked, outside every reach), the earliest added shared Goal
        /// whose grid holds the position answers, as its own sample would.
        /// </summary>
        internal bool TrySampleNearestShared(Vector3 worldPosition, out FlowFieldSample sample, out int goalId)
        {
            sample = FlowFieldSample.Stopped;
            goalId = 0;
            if (!CanPump || !FlowFieldGridSpace.IsFinite(worldPosition))
                return false;
            FlowFieldField nearest = null;
            FlowFieldField fallback = null;
            int fallbackId = 0;
            int best = FlowFieldGoalLayer.Unreachable;
            FlowFieldField published = _builder.PublishedField;
            IReadOnlyList<FlowFieldGoalSet.Entry> entries = Goals.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                FlowFieldGoalSet.Entry entry = entries[i];
                FlowFieldField field = Readable(entry.Field, published);
                if (entry.Kind != FlowFieldGoalKind.Shared
                    || field == null
                    || !FlowFieldSampler.TryLocate(field, worldPosition, out int index))
                    continue;
                if (fallback == null)
                {
                    fallback = field;
                    fallbackId = entry.Id;
                }
                int[] distances = field.Goal.Distances;
                if (distances == null || distances[index] >= best)
                    continue;
                best = distances[index];
                nearest = field;
                goalId = entry.Id;
            }
            if (nearest == null)
            {
                nearest = fallback;
                goalId = fallbackId;
            }
            if (nearest == null)
                return false;
            return FlowFieldSampler.Sample(nearest, worldPosition, Safety, _builder.LatestCells, out sample)
                == FlowFieldSampleStatus.Sampled;
        }

        /// <summary>The Goal's field, valid until the next pump, or null.</summary>
        internal FlowFieldField GoalField(int id)
            => CanPump ? Readable(Goals.Find(id)?.Field, _builder.PublishedField) : null;

        // A Goal's field answers while the default Goal's field is published
        // on the same grid: a new grid hides both until each is built on it.
        private static FlowFieldField Readable(FlowFieldField field, FlowFieldField published)
            => field != null && published != null && field.Grid.MatchesBounds(published.Grid) ? field : null;
    }
}
