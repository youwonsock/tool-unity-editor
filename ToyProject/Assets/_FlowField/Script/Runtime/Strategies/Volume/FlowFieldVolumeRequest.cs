using UnityEngine;

namespace Common.FlowField
{
    internal readonly struct FlowFieldVolumeRequest
    {
        internal FlowFieldGridSpace Grid { get; }
        internal Bounds WorldBounds { get; }
        internal FlowFieldBakeMode BakeMode { get; }
        internal LayerMask ObstacleLayer { get; }
        internal float ObstacleClearance { get; }
        // Whether the session sweeps unregistered obstacles. The obstacle
        // query reads every Collider of the layer either way, so it is not an
        // obstacle input; turning it on or off re-queries every cell once.
        internal bool SweepUnregistered { get; }
        internal bool HasGoal { get; }
        internal Vector3 GoalWorld { get; }
        internal float GoalInfluenceRadius { get; }
        internal Vector3 DefaultDirection { get; }
        internal FlowFieldVolumeBakeData BakeData { get; }
        internal int BakeRevision { get; }
        internal int BakeContentGeneration { get; }

        internal FlowFieldVolumeRequest(
            FlowFieldGridSpace grid,
            Bounds worldBounds,
            FlowFieldBakeMode bakeMode,
            LayerMask obstacleLayer,
            float obstacleClearance,
            bool sweepUnregistered,
            bool hasGoal,
            Vector3 goalWorld,
            float goalInfluenceRadius,
            Vector3 defaultDirection,
            FlowFieldVolumeBakeData bakeData)
        {
            Grid = grid;
            WorldBounds = worldBounds;
            BakeMode = bakeMode;
            ObstacleLayer = obstacleLayer;
            ObstacleClearance = obstacleClearance;
            SweepUnregistered = sweepUnregistered;
            HasGoal = hasGoal;
            GoalWorld = goalWorld;
            GoalInfluenceRadius = goalInfluenceRadius;
            DefaultDirection = defaultDirection;
            BakeData = bakeData;
            BakeRevision = bakeData != null ? bakeData.Revision : -1;
            BakeContentGeneration = bakeData != null
                ? bakeData.ContentGeneration
                : -1;
        }

        internal bool HasSameGrid(in FlowFieldVolumeRequest other)
            => Grid.MatchesBounds(other.Grid)
                && FlowFieldLayoutUtility.Approximately(WorldBounds, other.WorldBounds)
                && BakeMode == other.BakeMode
                && ReferenceEquals(BakeData, other.BakeData)
                && BakeRevision == other.BakeRevision
                && BakeContentGeneration == other.BakeContentGeneration;

        internal bool HasSameObstacleInputs(in FlowFieldVolumeRequest other)
            => HasSameGrid(other)
                && ObstacleLayer.value == other.ObstacleLayer.value
                && Mathf.Abs(ObstacleClearance - other.ObstacleClearance) <= 0.0001f;

        internal bool HasSameGoal(in FlowFieldVolumeRequest other)
            => HasGoal == other.HasGoal
                && (!HasGoal || FlowFieldGridSpace.Approximately(
                        GoalWorld,
                        other.GoalWorld,
                        0.00000001d)
                    && Mathf.Abs(GoalInfluenceRadius - other.GoalInfluenceRadius) <= 0.0001f);

        internal bool HasSameInputs(in FlowFieldVolumeRequest other)
        {
            if (!HasSameObstacleInputs(other))
                return false;

            if (BakeMode != FlowFieldBakeMode.StaticBaked && !HasSameGoal(other))
                return false;

            return SweepUnregistered == other.SweepUnregistered && HasSameFlowSettings(other);
        }

        /// <summary>
        /// A build only uses the cell the Goal resolves to, so a Goal moved
        /// within that cell yields the same field.
        /// </summary>
        internal bool HasSameResult(in FlowFieldVolumeRequest other)
        {
            if (!HasSameObstacleInputs(other))
                return false;

            if (BakeMode != FlowFieldBakeMode.StaticBaked && !HasSameGoalCell(other))
                return false;

            return HasSameFlowSettings(other);
        }

        private bool HasSameFlowSettings(in FlowFieldVolumeRequest other)
            => FlowFieldVectorUtility.SameDefaultDirection(DefaultDirection, other.DefaultDirection);

        private bool HasSameGoalCell(in FlowFieldVolumeRequest other)
            => HasGoal == other.HasGoal
                && (!HasGoal || GoalCell == other.GoalCell
                    && Mathf.Abs(GoalInfluenceRadius - other.GoalInfluenceRadius) <= 0.0001f);

        internal int GoalCell
        {
            get
            {
                Grid.TryWorldToLocalClamped(Grid.ClampWorldXYZ(GoalWorld), out int x, out int y, out int z);
                return Grid.ToFlatIndex(x, y, z);
            }
        }
    }
}
