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
            FlowFieldSurfaceData surface,
            FlowFieldWorkspace workspace,
            FlowFieldGoalTracker tracker,
            bool forceRebuild = false)
        {
            FlowFieldGoalRequest request = resolution.IsValid
                ? new FlowFieldGoalRequest(
                    true,
                    resolution.SourceCellIndex,
                    resolution.InfluenceRadius)
                : FlowFieldGoalRequest.None;
            if (!forceRebuild && tracker != null && tracker.MatchesLastBuild(request))
                return FlowFieldGoalBuildStatus.Unchanged;

            if (!resolution.IsValid)
            {
                workspace.ClearGoal();
                tracker.Record(FlowFieldGoalRequest.None, FlowFieldGoalBuildStatus.Invalid);
                return FlowFieldGoalBuildStatus.Invalid;
            }

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
        Unchanged,
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
        private bool _hasBuiltRequest;
        private int _lastBuiltSourceCellIndex = -1;
        private float _lastBuiltRadius;
        // Keep an observation separate from the last committed build. A Goal
        // can move more than once while an async BFS is in flight; comparing
        // every poll with the committed request would repeatedly report the
        // same change and would not preserve latest-wins semantics.
        private bool _hasObservedRequest;
        private FlowFieldGoalRequest _lastObservedRequest;
        private bool _missingWalkableWarningIssued;

        internal bool HasBuiltGoal => _lastBuiltHadGoal;

        internal bool MatchesLastBuild(in FlowFieldGoalRequest request)
            => _hasBuiltRequest
                && request.HasGoal == _lastBuiltHadGoal
                && request.SourceCellIndex == _lastBuiltSourceCellIndex
                && System.Math.Abs(request.InfluenceRadius - _lastBuiltRadius) <= VALUE_EPSILON;

        internal FlowFieldGoalChangeStatus DetectChange(
            FlowFieldGridSpace grid,
            bool surfaceReady,
            Transform target,
            bool hasExplicitGoal,
            Vector3 explicitGoal,
            float influenceRadius)
        {
            if (!FlowFieldGoalPipeline.HasActiveGoal(target, hasExplicitGoal))
            {
                FlowFieldGoalRequest none = FlowFieldGoalRequest.None;
                bool changed = !_hasObservedRequest || !RequestsEqual(_lastObservedRequest, none);
                bool hadObservedGoal = _hasObservedRequest && _lastObservedRequest.HasGoal;
                _lastObservedRequest = none;
                _hasObservedRequest = true;
                // A target can disappear while the first asynchronous solve
                // is still in flight, before the Goal tracker has recorded a
                // committed build. The observed request is still an active
                // input in that case, so the pending solve must be invalidated
                // just like a committed Goal would be.
                return changed && (_lastBuiltHadGoal || hadObservedGoal)
                    ? FlowFieldGoalChangeStatus.Changed
                    : FlowFieldGoalChangeStatus.None;
            }
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
            bool changedFromObservation = !_hasObservedRequest
                || !RequestsEqual(_lastObservedRequest, request);
            _lastObservedRequest = request;
            _hasObservedRequest = true;
            return changedFromObservation && HasChanged(request)
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

        internal bool HasChanged(in FlowFieldGoalResolution resolution)
        {
            FlowFieldGoalRequest request = resolution.IsValid
                ? new FlowFieldGoalRequest(
                    true,
                    resolution.SourceCellIndex,
                    resolution.InfluenceRadius)
                : FlowFieldGoalRequest.None;
            return HasChanged(request);
        }

        internal void Record(in FlowFieldGoalRequest request, FlowFieldGoalBuildStatus status)
        {
            _hasBuiltRequest = true;
            _lastBuiltHadGoal = request.HasGoal && status != FlowFieldGoalBuildStatus.Invalid;
            _lastBuiltSourceCellIndex = request.HasGoal ? request.SourceCellIndex : -1;
            _lastBuiltRadius = request.HasGoal ? request.InfluenceRadius : 0f;
            _lastObservedRequest = request;
            _hasObservedRequest = true;
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
            _hasBuiltRequest = false;
            _lastBuiltHadGoal = false;
            _lastBuiltSourceCellIndex = -1;
            _lastBuiltRadius = 0f;
            _hasObservedRequest = false;
            _lastObservedRequest = FlowFieldGoalRequest.None;
            _missingWalkableWarningIssued = false;
        }

        private static bool RequestsEqual(
            in FlowFieldGoalRequest left,
            in FlowFieldGoalRequest right)
            => left.HasGoal == right.HasGoal
                && left.SourceCellIndex == right.SourceCellIndex
                && System.Math.Abs(left.InfluenceRadius - right.InfluenceRadius) <= VALUE_EPSILON;
    }
}
