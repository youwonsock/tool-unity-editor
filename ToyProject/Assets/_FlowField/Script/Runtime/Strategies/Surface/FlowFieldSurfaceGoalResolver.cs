using UnityEngine;

namespace Common.FlowField
{
    internal readonly struct FlowFieldSurfaceGoalResolution
    {
        internal bool HasActiveGoal { get; }
        internal bool IsValid { get; }
        internal int LocalX { get; }
        internal int LocalZ { get; }
        internal int SourceCellIndex { get; }
        internal float InfluenceRadius { get; }
        internal Vector3 RequestedWorld { get; }

        internal FlowFieldSurfaceGoalResolution(
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

        internal static FlowFieldSurfaceGoalResolution None
            => new FlowFieldSurfaceGoalResolution(hasActiveGoal: false, isValid: false, 0, 0, -1, 0f, default);
    }

    internal static class FlowFieldSurfaceGoalResolver
    {
        internal static FlowFieldSurfaceGoalResolution Resolve(
            FlowFieldGridSpace grid,
            bool hasGoal,
            Vector3 requestedWorld,
            float influenceRadius)
        {
            if (!grid.IsValid)
                throw new System.InvalidOperationException("Goal resolution requires a valid grid.");
            if (!hasGoal)
                return FlowFieldSurfaceGoalResolution.None;

            if (!IsFiniteWorldXZ(requestedWorld))
                throw new System.ArgumentOutOfRangeException(nameof(requestedWorld));
            if (!FlowFieldGridSpace.IsFinite(influenceRadius) || influenceRadius < 0f)
                throw new System.ArgumentOutOfRangeException(nameof(influenceRadius));

            // A goal may be authored just outside the bake volume. Resolve it
            // to the nearest in-bounds cell before applying the invalid/blocked
            // surface fallback and deterministic flat-index tie break.
            if (!grid.TryWorldToLocalClamped(requestedWorld, out int localX, out int localZ))
                throw new System.ArgumentOutOfRangeException(nameof(requestedWorld), "Goal cannot be mapped to the FlowField grid.");

            return new FlowFieldSurfaceGoalResolution(
                true,
                true,
                localX,
                localZ,
                grid.ToFlatIndex(localX, localZ),
                influenceRadius,
                requestedWorld);
        }

        internal static FlowFieldSurfaceGoalBuildStatus Build(
            in FlowFieldSurfaceGoalResolution resolution,
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldSurfaceWorkspace workspace,
            FlowFieldSurfaceGoalTracker tracker,
            bool forceRebuild = false)
        {
            FlowFieldSurfaceGoalSolveInput request = resolution.IsValid
                ? new FlowFieldSurfaceGoalSolveInput(
                    true,
                    resolution.SourceCellIndex,
                    resolution.InfluenceRadius)
                : FlowFieldSurfaceGoalSolveInput.None;
            if (!forceRebuild && tracker != null && tracker.MatchesLastBuild(request))
                return FlowFieldSurfaceGoalBuildStatus.Unchanged;

            if (!resolution.IsValid)
            {
                workspace.ClearGoal();
                tracker.Record(FlowFieldSurfaceGoalSolveInput.None, FlowFieldSurfaceGoalBuildStatus.Invalid);
                return FlowFieldSurfaceGoalBuildStatus.Invalid;
            }

            bool built = FlowFieldSurfaceNavigationPreparation.PrepareGoal(
                grid,
                surface,
                workspace,
                resolution.LocalX,
                resolution.LocalZ,
                resolution.InfluenceRadius,
                out _);
            FlowFieldSurfaceGoalBuildStatus status = built
                ? FlowFieldSurfaceGoalBuildStatus.Built
                : FlowFieldSurfaceGoalBuildStatus.NoWalkableSurface;
            tracker.Record(request, status);
            return status;
        }

        internal static bool IsFiniteWorldXZ(Vector3 value)
            => FlowFieldGridSpace.IsFinite(value.x) && FlowFieldGridSpace.IsFinite(value.z);

    }

    internal enum FlowFieldSurfaceGoalBuildStatus
    {
        None,
        Unchanged,
        Built,
        NoWalkableSurface,
        Invalid,
    }

    internal readonly struct FlowFieldSurfaceGoalSolveInput
    {
        public bool HasGoal { get; }
        public int SourceCellIndex { get; }
        public float InfluenceRadius { get; }

        internal FlowFieldSurfaceGoalSolveInput(bool hasGoal, int sourceCellIndex, float influenceRadius)
        {
            HasGoal = hasGoal;
            SourceCellIndex = sourceCellIndex;
            InfluenceRadius = influenceRadius;
        }

        internal static FlowFieldSurfaceGoalSolveInput None
            => new FlowFieldSurfaceGoalSolveInput(hasGoal: false, -1, 0f);
    }
}
