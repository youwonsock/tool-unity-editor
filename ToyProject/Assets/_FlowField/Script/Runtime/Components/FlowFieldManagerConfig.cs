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

        internal bool HasValidDefaultDirection
            => FlowFieldGridSpace.IsFinite(Flow.DefaultDirection)
                && Flow.DefaultDirection.sqrMagnitude > ValueEpsilon;
    }
}
