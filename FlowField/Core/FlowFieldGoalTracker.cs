using UnityEngine;

namespace Supercent.Common.FlowField
{
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
