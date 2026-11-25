namespace Common.FlowField
{
    /// <summary>
    /// The Goal the last Surface2D build solved for, so an unchanged Goal
    /// skips its solve, and the one-time warning for a Goal range without
    /// walkable ground.
    /// </summary>
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
