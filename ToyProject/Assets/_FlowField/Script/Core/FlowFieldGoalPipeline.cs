using UnityEngine;

namespace Common.FlowField
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
            if (!grid.IsValid)
                throw new System.InvalidOperationException("Goal resolution requires a valid grid.");
            if (!hasActiveGoal)
                return new FlowFieldGoalResolution(hasActiveGoal, false, 0, 0, -1, 0f, default);

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

            return new FlowFieldGoalResolution(
                true,
                true,
                localX,
                localZ,
                grid.ToFlatIndex(localX, localZ),
                influenceRadius,
                requestedWorld);
        }

        internal static FlowFieldGoalBuildStatus Build(
            in FlowFieldGoalResolution resolution,
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
            FlowFieldWorkspace workspace,
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
            bool built = FlowFieldSolver.PrepareGoal(
                grid,
                surface,
                workspace,
                resolution.LocalX,
                resolution.LocalZ,
                resolution.InfluenceRadius,
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

    }

    internal enum FlowFieldGoalBuildStatus
    {
        None,
        Built,
        NoWalkableSurface,
        Invalid,
    }

    internal enum FlowFieldGoalChangeStatus
    {
        None,
        Changed,
        Invalid,
    }

    internal readonly struct FlowFieldGoalRequest
    {
        public bool HasGoal { get; }
        public int SourceCellIndex { get; }
        public float InfluenceRadius { get; }

        internal FlowFieldGoalRequest(bool hasGoal, int sourceCellIndex, float influenceRadius)
        {
            HasGoal = hasGoal;
            SourceCellIndex = sourceCellIndex;
            InfluenceRadius = influenceRadius;
        }

        internal static FlowFieldGoalRequest None
            => new FlowFieldGoalRequest(false, -1, 0f);
    }

    internal sealed class FlowFieldGoalTracker
    {
        private const float VALUE_EPSILON = 0.0001f;
        private bool _lastBuiltHadGoal;
        private int _lastBuiltSourceCellIndex = -1;
        private float _lastBuiltRadius;
        private bool _missingWalkableWarningIssued;

        internal bool HasBuiltGoal => _lastBuiltHadGoal;

        internal FlowFieldGoalChangeStatus DetectChange(
            FlowFieldGridSpace grid,
            bool surfaceReady,
            Transform target,
            bool hasExplicitGoal,
            Vector3 explicitGoal,
            float influenceRadius)
        {
            if (!FlowFieldGoalPipeline.HasActiveGoal(target, hasExplicitGoal))
                return _lastBuiltHadGoal
                    ? FlowFieldGoalChangeStatus.Changed
                    : FlowFieldGoalChangeStatus.None;
            if (!surfaceReady || !grid.IsValid)
                return FlowFieldGoalChangeStatus.None;

            FlowFieldGoalResolution resolution = FlowFieldGoalPipeline.Resolve(
                grid,
                target,
                hasExplicitGoal,
                explicitGoal,
                influenceRadius);
            if (!resolution.IsValid)
                return resolution.HasActiveGoal
                    ? FlowFieldGoalChangeStatus.Invalid
                    : FlowFieldGoalChangeStatus.None;

            FlowFieldGoalRequest request = new FlowFieldGoalRequest(
                true,
                resolution.SourceCellIndex,
                resolution.InfluenceRadius);
            return HasChanged(request)
                ? FlowFieldGoalChangeStatus.Changed
                : FlowFieldGoalChangeStatus.None;
        }

        internal bool HasChanged(in FlowFieldGoalRequest request)
        {
            if (!request.HasGoal)
                return _lastBuiltHadGoal;

            return !_lastBuiltHadGoal
                || request.SourceCellIndex != _lastBuiltSourceCellIndex
                || System.Math.Abs(request.InfluenceRadius - _lastBuiltRadius) > VALUE_EPSILON;
        }

        internal void Record(in FlowFieldGoalRequest request, FlowFieldGoalBuildStatus status)
        {
            _lastBuiltHadGoal = request.HasGoal && status != FlowFieldGoalBuildStatus.Invalid;
            _lastBuiltSourceCellIndex = request.HasGoal ? request.SourceCellIndex : -1;
            _lastBuiltRadius = request.HasGoal ? request.InfluenceRadius : 0f;
            if (status != FlowFieldGoalBuildStatus.NoWalkableSurface)
                _missingWalkableWarningIssued = false;
        }

        /// <summary>
        /// 동일한 No-Walkable 경고를 한 번만 소비합니다. 이미 소비한 경고는
        /// 추가로 표시할 결과가 없으므로 false를 정상적으로 반환합니다.
        /// </summary>
        /// <returns>이번 호출에서 경고를 소비했으면 true, 이미 소비했으면 false입니다.</returns>
        internal bool TryConsumeMissingWalkableWarning()
        {
            if (_missingWalkableWarningIssued)
                return false;

            _missingWalkableWarningIssued = true;
            return true;
        }

        internal void ResetWarning()
            => _missingWalkableWarningIssued = false;

        internal void Clear()
        {
            _lastBuiltHadGoal = false;
            _lastBuiltSourceCellIndex = -1;
            _lastBuiltRadius = 0f;
            _missingWalkableWarningIssued = false;
        }
    }
}
