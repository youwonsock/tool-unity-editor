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
        internal bool HasGoal { get; }
        internal Vector3 GoalWorld { get; }
        internal float GoalInfluenceRadius { get; }
        internal Vector3 DefaultDirection { get; }
        internal int MaxGpuWaves { get; }
        internal ComputeShader ComputeShader { get; }
        internal FlowFieldVolumeBakeData StaticBakeData { get; }
        internal int StaticBakeRevision { get; }
        internal int StaticBakeContentGeneration { get; }
        internal int BakeInputGeneration { get; }

        internal FlowFieldVolumeRequest(
            FlowFieldGridSpace grid,
            Bounds worldBounds,
            FlowFieldBakeMode bakeMode,
            LayerMask obstacleLayer,
            float obstacleClearance,
            bool hasGoal,
            Vector3 goalWorld,
            float goalInfluenceRadius,
            Vector3 defaultDirection,
            int maxGpuWaves,
            ComputeShader computeShader,
            FlowFieldVolumeBakeData staticBakeData,
            int bakeInputGeneration = 0)
        {
            Grid = grid;
            WorldBounds = worldBounds;
            BakeMode = bakeMode;
            ObstacleLayer = obstacleLayer;
            ObstacleClearance = obstacleClearance;
            HasGoal = hasGoal;
            GoalWorld = goalWorld;
            GoalInfluenceRadius = goalInfluenceRadius;
            DefaultDirection = defaultDirection;
            MaxGpuWaves = maxGpuWaves;
            ComputeShader = computeShader;
            StaticBakeData = staticBakeData;
            StaticBakeRevision = staticBakeData != null ? staticBakeData.Revision : -1;
            StaticBakeContentGeneration = staticBakeData != null
                ? staticBakeData.ContentGeneration
                : -1;
            BakeInputGeneration = bakeInputGeneration;
        }

        internal bool HasSameGrid(in FlowFieldVolumeRequest other)
            => Grid.MatchesBounds(other.Grid)
                && Approximately(WorldBounds, other.WorldBounds)
                && BakeMode == other.BakeMode
                && ReferenceEquals(StaticBakeData, other.StaticBakeData)
                && StaticBakeRevision == other.StaticBakeRevision
                && StaticBakeContentGeneration == other.StaticBakeContentGeneration
                && ReferenceEquals(ComputeShader, other.ComputeShader);

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

            return FlowFieldGridSpace.Approximately(
                       DefaultDirection,
                       other.DefaultDirection,
                       0.00000001d)
                && MaxGpuWaves == other.MaxGpuWaves;
        }

        private static bool Approximately(Bounds left, Bounds right)
            => FlowFieldGridSpace.Approximately(left.center, right.center, 0.00000001d)
                && FlowFieldGridSpace.Approximately(left.size, right.size, 0.00000001d);
    }
}
