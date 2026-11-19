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
            => new FlowFieldSurfaceGoalResolution(false, false, 0, 0, -1, 0f, default);
    }

    internal static class FlowFieldSurfaceGoalResolver
    {
        internal static FlowFieldSurfaceGoalResolution Resolve(
            FlowFieldGridSpace grid,
            Transform target,
            bool hasExplicitGoal,
            Vector3 explicitGoal,
            float influenceRadius)
        {
            bool hasActiveGoal = target != null || hasExplicitGoal;
            if (!grid.IsValid)
                throw new System.InvalidOperationException("Goal resolution requires a valid grid.");
            if (!hasActiveGoal)
                return new FlowFieldSurfaceGoalResolution(hasActiveGoal, false, 0, 0, -1, 0f, default);

            Vector3 requestedWorld = target != null ? target.position : explicitGoal;
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

            bool built = FlowFieldSurfaceSolver.PrepareGoal(
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

        internal static bool HasActiveGoal(Transform target, bool hasExplicitGoal)
            => target != null || hasExplicitGoal;

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
            => new FlowFieldSurfaceGoalSolveInput(false, -1, 0f);
    }

    internal sealed class FlowFieldSurfaceGoalTracker
    {
        private const float ValueEpsilon = 0.0001f;
        private bool _lastBuiltHadGoal;
        private bool _hasBuiltRequest;
        private int _lastBuiltSourceCellIndex = -1;
        private float _lastBuiltRadius;
        private bool _missingWalkableWarningIssued;


        internal bool MatchesLastBuild(in FlowFieldSurfaceGoalSolveInput request)
            => _hasBuiltRequest
                && request.HasGoal == _lastBuiltHadGoal
                && request.SourceCellIndex == _lastBuiltSourceCellIndex
                && System.Math.Abs(request.InfluenceRadius - _lastBuiltRadius) <= ValueEpsilon;

        internal bool HasChanged(in FlowFieldSurfaceGoalSolveInput request)
        {
            if (!request.HasGoal)
                return _lastBuiltHadGoal;

            return !_lastBuiltHadGoal
                || request.SourceCellIndex != _lastBuiltSourceCellIndex
                || System.Math.Abs(request.InfluenceRadius - _lastBuiltRadius) > ValueEpsilon;
        }

        internal bool HasChanged(in FlowFieldSurfaceGoalResolution resolution)
        {
            FlowFieldSurfaceGoalSolveInput request = resolution.IsValid
                ? new FlowFieldSurfaceGoalSolveInput(
                    true,
                    resolution.SourceCellIndex,
                    resolution.InfluenceRadius)
                : FlowFieldSurfaceGoalSolveInput.None;
            return HasChanged(request);
        }

        internal void Record(in FlowFieldSurfaceGoalSolveInput request, FlowFieldSurfaceGoalBuildStatus status)
        {
            _hasBuiltRequest = true;
            _lastBuiltHadGoal = request.HasGoal && status != FlowFieldSurfaceGoalBuildStatus.Invalid;
            _lastBuiltSourceCellIndex = request.HasGoal ? request.SourceCellIndex : -1;
            _lastBuiltRadius = request.HasGoal ? request.InfluenceRadius : 0f;
            if (status != FlowFieldSurfaceGoalBuildStatus.NoWalkableSurface)
                _missingWalkableWarningIssued = false;
        }

        /// <summary>
        /// Takes the no-walkable-surface warning once. A warning already taken
        /// has nothing more to show, so this returns false.
        /// </summary>
        /// <returns>True when this call took the warning, false when it was already taken.</returns>
        internal bool TryConsumeMissingWalkableWarning()
        {
            if (_missingWalkableWarningIssued)
                return false;

            _missingWalkableWarningIssued = true;
            return true;
        }

        internal void ResetWarning()
            => _missingWalkableWarningIssued = false;

        /// <summary>
        /// Forgets the last build so the same Goal is solved again, keeping
        /// the one-shot warning state of the unchanged Goal.
        /// </summary>
        internal void ForgetLastBuild()
        {
            bool warned = _missingWalkableWarningIssued;
            Clear();
            _missingWalkableWarningIssued = warned;
        }

        internal void Clear()
        {
            _hasBuiltRequest = false;
            _lastBuiltHadGoal = false;
            _lastBuiltSourceCellIndex = -1;
            _lastBuiltRadius = 0f;
            _missingWalkableWarningIssued = false;
        }
    }
}
