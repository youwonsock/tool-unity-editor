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
        internal const string SpaceModeLockedMessage
            = "FlowField Space Mode cannot change during an active Init session. Release and Init again.";

        internal readonly FlowFieldSpaceMode SpaceMode;
        internal readonly FlowFieldBakeMode BakeMode;
        internal readonly FlowFieldGridSettings Grid;
        internal readonly FlowFieldSurfaceSettings Surface;
        internal readonly FlowFieldObstacleSettings Obstacles;
        internal readonly FlowFieldFlowSettings Flow;
        internal readonly FlowFieldBakeAssets BakeAssets;
        internal readonly Vector3 Position;
        internal readonly Quaternion Rotation;
        internal readonly Vector3 LossyScale;
        internal readonly bool HasGoal;
        internal readonly Vector3 GoalWorld;

        internal FlowFieldManagerConfig(
            FlowFieldSpaceMode spaceMode,
            FlowFieldBakeMode bakeMode,
            in FlowFieldGridSettings grid,
            in FlowFieldSurfaceSettings surface,
            in FlowFieldObstacleSettings obstacles,
            in FlowFieldFlowSettings flow,
            in FlowFieldBakeAssets bakeAssets,
            Transform managerTransform,
            bool hasGoal,
            Vector3 goalWorld)
        {
            SpaceMode = spaceMode;
            BakeMode = bakeMode;
            Grid = grid;
            Surface = surface;
            Obstacles = obstacles;
            Flow = flow;
            BakeAssets = bakeAssets;
            Position = managerTransform.position;
            Rotation = managerTransform.rotation;
            LossyScale = managerTransform.lossyScale;
            HasGoal = hasGoal;
            GoalWorld = goalWorld;
        }

        // The grid is world-axis aligned, so the Manager may not rotate or scale.
        internal bool HasSupportedTransform
            => Quaternion.Angle(Rotation, Quaternion.identity) <= 0.01f
                && (LossyScale - Vector3.one).sqrMagnitude <= 0.0001f;

        /// <summary>
        /// The checks both space modes make first, in one order and with one
        /// message each. <paramref name="bakeModeLocked"/> says a
        /// running session accepted a different Bake Mode. A Space Mode edit
        /// is never checked: it waits for Release.
        /// </summary>
        internal bool TryValidateShared(bool bakeModeLocked, out string reason)
        {
            if (!Enum.IsDefined(typeof(FlowFieldSpaceMode), SpaceMode))
                return Fail(out reason, "Space Mode must be Surface2D or Volume3D.");
            if (!FlowFieldGridSpace.IsFinite(Grid.CellSize) || Grid.CellSize < FlowFieldLayoutUtility.MinCellSize)
                return Fail(out reason, "Cell Size must be finite and positive.");
            Bounds bounds = Grid.BakeBoundsLocal;
            if (!FlowFieldGridSpace.IsFinite(bounds.center)
                || !FlowFieldGridSpace.IsFinite(bounds.size)
                || bounds.size.x <= 0f
                || bounds.size.y <= 0f
                || bounds.size.z <= 0f)
                return Fail(out reason, "Bake Bounds must be finite and positive.");
            if (!HasValidDefaultDirection)
                return Fail(out reason, "Default Flow Direction must be finite and non-zero.");
            if (!HasSupportedTransform)
                return Fail(out reason, "FlowField Manager must use an unrotated transform with unit scale.");
            if (Obstacles.Layer.value == 0)
                return Fail(out reason, "Obstacle LayerMask must contain at least one layer.");
            if (!FlowFieldGridSpace.IsFinite(Obstacles.Clearance) || Obstacles.Clearance < 0f)
                return Fail(out reason, "Obstacle Clearance must be finite and not negative.");
            if (!FlowFieldGridSpace.IsFinite(Obstacles.SweepInterval) || Obstacles.SweepInterval < 0f)
                return Fail(out reason, "Sweep Interval must be finite and not negative.");
            if (!FlowFieldGridSpace.IsFinite(Flow.GoalInfluenceRadius) || Flow.GoalInfluenceRadius < 0f)
                return Fail(out reason, "Goal Influence Radius must be finite and not negative.");
            if (!Enum.IsDefined(typeof(FlowFieldBakeMode), BakeMode))
                return Fail(out reason, "Bake Mode must be RuntimeDynamic, StaticBaked or BakedBase.");
            if (Obstacles.SweepUnregistered && BakeMode == FlowFieldBakeMode.StaticBaked)
                return Fail(out reason, "Sweep Unregistered is not allowed in StaticBaked mode, whose obstacles stay as baked.");
            if (bakeModeLocked)
                return Fail(out reason, "FlowField Bake Mode cannot change during an active Init session. Release and Init again.");
            reason = string.Empty;
            return true;
        }

        /// <summary>A failed check: false with its reason.</summary>
        internal static bool Fail(out string reason, string message)
        {
            reason = message;
            return false;
        }

        internal bool HasValidDefaultDirection
            => FlowFieldGridSpace.IsFinite(Flow.DefaultDirection)
                && Flow.DefaultDirection.sqrMagnitude > ValueEpsilon;
    }
}
