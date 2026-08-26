using UnityEngine;

namespace Supercent.Common.FlowField
{
    internal readonly struct FlowFieldGoalResolution
    {
        internal bool HasActiveGoal { get; }
        internal bool IsValid { get; }
        internal int LocalX { get; }
        internal int LocalZ { get; }
        internal int SourceCellIndex { get; }
        internal float InfluenceRadius { get; }
        internal Vector3 RequestedWorld { get; }

        internal FlowFieldGoalResolution(
            bool hasActiveGoal,
            bool isValid,
            int localX,
            int localZ,
            int sourceCellIndex,
            float influenceRadius,
            Vector3 requestedWorld)
        {
            HasActiveGoal = hasActiveGoal;
            IsValid = isValid;
            LocalX = localX;
            LocalZ = localZ;
            SourceCellIndex = sourceCellIndex;
            InfluenceRadius = influenceRadius;
            RequestedWorld = requestedWorld;
        }

        internal static FlowFieldGoalResolution None
            => new FlowFieldGoalResolution(false, false, 0, 0, -1, 0f, default);
    }

    internal static class FlowFieldGoalPipeline
    {
        internal static FlowFieldGoalResolution Resolve(
            FlowFieldGridSpace grid,
            Transform target,
            bool hasExplicitGoal,
            Vector3 explicitGoal,
            float influenceRadius)
        {
            bool hasActiveGoal = target != null || hasExplicitGoal;
            if (!grid.IsValid || !hasActiveGoal)
                return new FlowFieldGoalResolution(hasActiveGoal, false, 0, 0, -1, 0f, default);

            Vector3 requestedWorld = target != null ? target.position : explicitGoal;
            if (!IsFiniteWorldXZ(requestedWorld) || !FlowFieldGridSpace.IsFinite(influenceRadius))
                return new FlowFieldGoalResolution(hasActiveGoal, false, 0, 0, -1, 0f, requestedWorld);

            if (!grid.TryWorldToLocalClamped(requestedWorld, out int localX, out int localZ))
                return new FlowFieldGoalResolution(hasActiveGoal, false, 0, 0, -1, 0f, requestedWorld);

            return new FlowFieldGoalResolution(
                true,
                true,
                localX,
                localZ,
                grid.ToFlatIndex(localX, localZ),
                Mathf.Max(0f, influenceRadius),
                requestedWorld);
        }

        internal static FlowFieldGoalBuildStatus Build(
            in FlowFieldGoalResolution resolution,
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
            FlowFieldCoarseTopologyData coarseTopology,
            FlowFieldWorkspace workspace,
            int fineRingCoarseRadius,
            FlowFieldGoalTracker tracker)
        {
            if (!resolution.IsValid)
            {
                workspace.ClearGoal();
                tracker.Record(FlowFieldGoalRequest.None, FlowFieldGoalBuildStatus.Invalid);
                return FlowFieldGoalBuildStatus.Invalid;
            }

            FlowFieldGoalRequest request = new FlowFieldGoalRequest(
                true,
                resolution.SourceCellIndex,
                resolution.InfluenceRadius);
            bool built = FlowFieldHierarchicalGoalSolver.BuildHierarchicalGoal(
                grid,
                surface,
                coarseTopology,
                workspace,
                resolution.LocalX,
                resolution.LocalZ,
                resolution.InfluenceRadius,
                fineRingCoarseRadius,
                out _);
            FlowFieldGoalBuildStatus status = built
                ? FlowFieldGoalBuildStatus.Built
                : FlowFieldGoalBuildStatus.NoWalkableSurface;
            tracker.Record(request, status);
            return status;
        }

        internal static bool HasActiveGoal(Transform target, bool hasExplicitGoal)
            => target != null || hasExplicitGoal;

        internal static bool IsFiniteWorldXZ(Vector3 value)
            => FlowFieldGridSpace.IsFinite(value.x) && FlowFieldGridSpace.IsFinite(value.z);

        internal static float SanitizeInfluenceRadius(float value)
            => FlowFieldGridSpace.IsFinite(value) ? Mathf.Max(0f, value) : 0f;
    }
}
