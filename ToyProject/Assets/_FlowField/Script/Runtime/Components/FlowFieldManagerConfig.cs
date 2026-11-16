using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// A read-only copy of a Manager's settings at one moment. The builders
    /// validate it and turn it into requests, so they never read the
    /// component itself.
    /// </summary>
    internal readonly struct FlowFieldManagerConfig
    {
        internal const float ValueEpsilon = 0.0001f;
        internal const float MinRefreshRate = 0.05f;

        internal readonly FlowFieldSpaceMode SpaceMode;
        internal readonly FlowFieldBakeMode BakeMode;
        internal readonly FlowFieldGridSettings Grid;
        internal readonly FlowFieldSurfaceSettings Surface;
        internal readonly FlowFieldObstacleSettings Obstacles;
        internal readonly FlowFieldFlowSettings Flow;
        internal readonly FlowFieldBakeAssets BakeAssets;
        internal readonly bool EnableUnregisteredObstacleSweep;
        internal readonly float RefreshRate;
        internal readonly Vector3 Position;
        internal readonly Quaternion Rotation;
        internal readonly Vector3 LossyScale;
        // A Goal Transform is read where the Goal is resolved, as before.
        internal readonly Transform GoalTransform;
        internal readonly bool HasExplicitGoal;
        internal readonly Vector3 ExplicitGoalWorld;

        internal FlowFieldManagerConfig(
            FlowFieldSpaceMode spaceMode,
            FlowFieldBakeMode bakeMode,
            in FlowFieldGridSettings grid,
            in FlowFieldSurfaceSettings surface,
            in FlowFieldObstacleSettings obstacles,
            in FlowFieldFlowSettings flow,
            in FlowFieldBakeAssets bakeAssets,
            bool enableUnregisteredObstacleSweep,
            float refreshRate,
            Transform managerTransform,
            Transform goalTransform,
            bool hasExplicitGoal,
            Vector3 explicitGoalWorld)
        {
            SpaceMode = spaceMode;
            BakeMode = bakeMode;
            Grid = grid;
            Surface = surface;
            Obstacles = obstacles;
            Flow = flow;
            BakeAssets = bakeAssets;
            EnableUnregisteredObstacleSweep = enableUnregisteredObstacleSweep;
            RefreshRate = refreshRate;
            Position = managerTransform.position;
            Rotation = managerTransform.rotation;
            LossyScale = managerTransform.lossyScale;
            GoalTransform = goalTransform;
            HasExplicitGoal = hasExplicitGoal;
            ExplicitGoalWorld = explicitGoalWorld;
        }

        internal bool HasGoal => GoalTransform != null || HasExplicitGoal;

        internal Vector3 GoalWorld => GoalTransform != null ? GoalTransform.position : ExplicitGoalWorld;

        // The grid is world-axis aligned, so the Manager may not rotate or scale.
        internal bool HasSupportedTransform
            => Quaternion.Angle(Rotation, Quaternion.identity) <= 0.01f
                && (LossyScale - Vector3.one).sqrMagnitude <= 0.0001f;

        /// <summary>
        /// The checks both space modes make first, in one order and with one
        /// message each. <paramref name="bakeModeLocked"/> says a running
        /// session accepted a different Bake Mode.
        /// </summary>
        internal void ValidateShared(bool bakeModeLocked)
        {
            if (!FlowFieldGridSpace.IsFinite(Grid.CellSize) || Grid.CellSize < FlowFieldBakeBoundsUtility.MinCellSize)
                throw new ArgumentOutOfRangeException("_cellSize", Grid.CellSize, "Cell Size must be finite and positive.");
            Bounds bounds = Grid.BakeBoundsLocal;
            if (!FlowFieldGridSpace.IsFinite(bounds.center)
                || !FlowFieldGridSpace.IsFinite(bounds.size)
                || bounds.size.x <= 0f
                || bounds.size.y <= 0f
                || bounds.size.z <= 0f)
                throw new ArgumentOutOfRangeException("_bakeBoundsLocal", "Bake Bounds must be finite and positive.");
            if (!HasValidDefaultDirection)
                throw new ArgumentOutOfRangeException("_defaultFlowDirection", "Default Flow Direction must be finite and non-zero.");
            if (!HasSupportedTransform)
                throw new ArgumentException("FlowField Manager must use an unrotated transform with unit scale.", "transform");
            if (Obstacles.Layer.value == 0)
                throw new ArgumentException("Obstacle LayerMask must contain at least one layer.", "_obstacleLayer");
            if (!FlowFieldGridSpace.IsFinite(Obstacles.Clearance) || Obstacles.Clearance < 0f)
                throw new ArgumentOutOfRangeException("_obstacleClearance", "Obstacle Clearance must be finite and not negative.");
            if (!FlowFieldGridSpace.IsFinite(Flow.GoalInfluenceRadius) || Flow.GoalInfluenceRadius < 0f)
                throw new ArgumentOutOfRangeException("_goalInfluenceRadius", "Goal Influence Radius must be finite and not negative.");
            if (!FlowFieldGridSpace.IsFinite(RefreshRate) || RefreshRate < MinRefreshRate)
                throw new ArgumentOutOfRangeException("_refreshRate", $"Refresh Rate must be at least {MinRefreshRate}.");
            if (!Enum.IsDefined(typeof(FlowFieldBakeMode), BakeMode))
                throw new ArgumentOutOfRangeException("_bakeMode");
            if (bakeModeLocked)
                throw new InvalidOperationException("FlowField Bake Mode cannot change during an active Init session. Release and Init again.");
        }

        internal bool HasValidDefaultDirection
            => FlowFieldGridSpace.IsFinite(Flow.DefaultDirection)
                && Flow.DefaultDirection.sqrMagnitude > ValueEpsilon;
    }
}
