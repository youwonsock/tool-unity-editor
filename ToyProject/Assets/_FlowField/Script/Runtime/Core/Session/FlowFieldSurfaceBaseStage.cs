using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// The Surface2D stages before the solve: the surface, the obstacle masks and
    /// the Goal. Their Physics reads run together, so they see one state of the scene.
    /// </summary>
    internal sealed class FlowFieldSurfaceBaseStage
    {
        private readonly FlowFieldSessionState _state;
        private readonly FlowFieldObstaclePipeline _obstaclePipeline;
        private readonly FlowFieldGoalTracker _goalTracker;
        private readonly IFlowFieldSurfaceSource _surfaceSource;
        private FlowFieldGridSpace? _observedGrid;

        internal FlowFieldSurfaceBaseStage(
            FlowFieldSessionState state,
            FlowFieldObstaclePipeline obstaclePipeline,
            FlowFieldGoalTracker goalTracker,
            IFlowFieldSurfaceSource surfaceSource)
        {
            _state = state;
            _obstaclePipeline = obstaclePipeline;
            _goalTracker = goalTracker;
            _surfaceSource = surfaceSource;
        }

        internal FlowFieldStaticBakeSnapshot LoadedSnapshot { get; set; }

        /// <summary>
        /// Records the latest authoring grid observed by the Unity adapter. The
        /// adapter may call this while a solve is in flight; keeping the
        /// observation here prevents repeated dirty increments for one transform.
        /// </summary>
        internal bool ObserveGrid(in FlowFieldGridSpace grid)
        {
            if (!grid.IsValid)
                return false;
            bool changed = _observedGrid.HasValue && !_observedGrid.Value.MatchesBounds(grid);
            if (changed || !_observedGrid.HasValue)
                _observedGrid = grid;
            return changed;
        }

        internal void Clear()
        {
            _observedGrid = null;
            LoadedSnapshot = null;
        }

        internal bool PrepareSurface(
            in FlowFieldSessionRequest request,
            FlowFieldDirtyFlags pending,
            FlowFieldModifierRegistry modifiers)
        {
            if ((pending & FlowFieldDirtyFlags.Grid) == 0
                && _state.Grid.IsValid
                && _state.SurfaceReady)
                return false;

            // Any Surface/grid rebuild invalidates an obstacle candidate that
            // was measured against the previous cell heights and bounds.
            // Leave the registered collider set intact; the downstream base
            // stage will issue one fresh dynamic query for the new Surface.
            _obstaclePipeline.DiscardStagedDynamicProbe();

            FlowFieldSurfaceData surface = ResolveSurface(request);
            if (!surface.IsValid)
                throw new InvalidOperationException("FlowField surface source returned invalid data.");

            bool boundsChanged = !_state.Grid.MatchesBounds(request.SurfaceSettings.Grid);
            FlowFieldSurfaceData previousSurface = _state.Surface;
            bool staticSnapshotChanged = request.SourceKind == FlowFieldSessionSourceKind.StaticSnapshot
                && !ReferenceEquals(LoadedSnapshot, request.StaticBakeSnapshot);
            bool surfaceChanged = boundsChanged
                || !_state.SurfaceReady
                || staticSnapshotChanged
                || !ReferenceEquals(previousSurface, surface)
                    && !previousSurface.ContentEquals(surface);

            // A full Surface notification may legitimately produce the same
            // immutable hit/normal arrays. Preserve the previous object in
            // that case so Goal/obstacle-only work does not trigger a
            // committed Surface copy or an unnecessary downstream BFS.
            if (!surfaceChanged)
                return false;

            _state.Grid = request.SurfaceSettings.Grid;
            _observedGrid = _state.Grid;
            _state.Workspace.Resize(_state.Grid.CellCount);
            _state.BaseComposed = false;
            _state.LastComposedRegion = FlowFieldCellRect.Invalid;
            _state.Surface = surface;

            if (boundsChanged)
                _state.HasObstacleMask = false;
            _state.DirtyFlags |= FlowFieldDirtyFlags.StaticObstacles
                | FlowFieldDirtyFlags.DynamicObstacles
                | FlowFieldDirtyFlags.Escape
                | FlowFieldDirtyFlags.DefaultDirection
                | FlowFieldDirtyFlags.Goal
                | FlowFieldDirtyFlags.ModifierArea
                | FlowFieldDirtyFlags.FinalRegion;
            _state.DirtyFinalRegion = FlowFieldCellRect.Full(_state.Grid);
            _state.DirtyObstacleRegion = FlowFieldCellRect.Full(_state.Grid);
            modifiers?.MarkAllAreasDirty();
            return true;
        }

        private FlowFieldSurfaceData ResolveSurface(in FlowFieldSessionRequest request)
        {
            if (request.SourceKind != FlowFieldSessionSourceKind.StaticSnapshot)
            {
                return _surfaceSource.Build(request.SurfaceSettings)
                    ?? throw new InvalidOperationException("FlowField surface source returned no surface data.");
            }

            if (request.StaticBakeSnapshot == null || !request.StaticBakeSnapshot.HasValidData)
                throw new InvalidOperationException("Static Flow Bake Asset is missing or invalid.");
            bool surfaceMatches = request.StaticBakeSnapshot.MatchesSurface(
                request.SurfaceSettings,
                out string surfaceMismatch);
            bool obstaclesMatch = request.StaticBakeSnapshot.MatchesObstacles(
                request.ObstacleLayer,
                request.ObstacleCheckHeight,
                request.ObstacleCheckCenterOffset,
                request.ObstacleClearance,
                out string obstacleMismatch);
            if (!surfaceMatches || !obstaclesMatch)
            {
                throw new InvalidOperationException(
                    string.IsNullOrEmpty(surfaceMismatch) ? obstacleMismatch : surfaceMismatch);
            }
            return request.StaticBakeSnapshot.Surface;
        }

        internal void BuildSceneBase(
            in FlowFieldSessionRequest request,
            ref FlowFieldDirtyFlags pending,
            bool surfaceChanged)
        {
            if ((pending & FlowFieldDirtyFlags.DefaultDirection) != 0)
            {
                _state.ResolvedDefaultDirection = FlowFieldVectorUtility.NormalizeDefaultDirection(request.DefaultDirection);
                pending |= FlowFieldDirtyFlags.FinalRegion;
                _state.ExpandFinalDirty(FlowFieldCellRect.Full(_state.Grid));
            }

            bool rebuildStatic = (pending & FlowFieldDirtyFlags.StaticObstacles) != 0;
            bool rebuildDynamic = (pending & FlowFieldDirtyFlags.DynamicObstacles) != 0;
            bool rebuildGoal = (pending & FlowFieldDirtyFlags.Goal) != 0;
            if (!_state.HasObstacleMask)
            {
                // A direct Goal request is allowed before the first obstacle
                // preparation (notably in injected/test sessions). Ensure the
                // solver never consumes the zero-initialized mask.
                rebuildStatic = true;
                rebuildDynamic = true;
            }
            if (!rebuildStatic && !rebuildDynamic && !rebuildGoal)
                return;

            bool hadObstacleMask = _state.HasObstacleMask;
            FlowFieldGoalResolution goal = request.Goal;
            FlowFieldBuildResult prepared = FlowFieldSurfaceBuildPipeline.PrepareBase(
                new FlowFieldSurfaceBuildRequest(
                    new FlowFieldObstacleRequest(
                        _state.Grid,
                        _state.Surface,
                        _state.Workspace,
                        request.ObstacleLayer,
                        request.ObstacleCheckHeight,
                        request.ObstacleCheckCenterOffset,
                        request.ObstacleClearance,
                        request.UseUnregisteredObstacleSweep,
                        _state.DirtyObstacleRegion),
                    goal,
                    surfaceChanged),
                _obstaclePipeline,
                _goalTracker,
                rebuildStatic,
                rebuildDynamic,
                rebuildGoal);

            ApplyPreparedBase(prepared, rebuildStatic, rebuildDynamic, rebuildGoal, hadObstacleMask, ref pending);
        }

        private void ApplyPreparedBase(
            in FlowFieldBuildResult prepared,
            bool rebuildStatic,
            bool rebuildDynamic,
            bool rebuildGoal,
            bool hadObstacleMask,
            ref FlowFieldDirtyFlags pending)
        {
            _state.DirtyObstacleRegion = FlowFieldCellRect.Invalid;
            if (rebuildStatic || rebuildDynamic)
                _state.HasObstacleMask = true;
            if ((rebuildStatic || rebuildDynamic)
                && hadObstacleMask
                && !prepared.ObstacleMaskChanged)
            {
                // A probe that produces the same effective mask is not a
                // base change. Drop the request-only obstacle flags so no
                // BFS, Revision or FieldChanged event is generated.
                pending &= ~(FlowFieldDirtyFlags.StaticObstacles
                    | FlowFieldDirtyFlags.DynamicObstacles
                    | FlowFieldDirtyFlags.Escape);
                if ((pending & ~(FlowFieldDirtyFlags.StaticObstacles
                        | FlowFieldDirtyFlags.DynamicObstacles
                        | FlowFieldDirtyFlags.Escape
                        | FlowFieldDirtyFlags.FinalRegion)) == FlowFieldDirtyFlags.None)
                {
                    pending &= ~FlowFieldDirtyFlags.FinalRegion;
                    _state.DirtyFinalRegion = FlowFieldCellRect.Invalid;
                }
            }
            if (prepared.ObstacleMaskChanged || !hadObstacleMask)
            {
                if (!prepared.ObstacleMaskChanged)
                    _obstaclePipeline.CommitCombinedAndBuildEscape(
                        _state.Grid,
                        _state.Surface,
                        _state.Workspace,
                        out _);
                _state.ExpandObstacleDirty(prepared.ObstacleDirtyRegion);
                UpdateBlockedWarning(prepared.HasWalkableSurface);
                pending |= FlowFieldDirtyFlags.Goal | FlowFieldDirtyFlags.FinalRegion;
                _state.ExpandFinalDirty(FlowFieldCellRect.Full(_state.Grid));
            }
            if (prepared.GoalStatus == FlowFieldGoalBuildStatus.NoWalkableSurface
                && _goalTracker.TryConsumeMissingWalkableWarning())
            {
                Debug.LogWarning("[FlowField] Goal 범위에 이동 가능한 표면 셀이 없습니다.");
            }
            if (rebuildGoal && prepared.GoalStatus == FlowFieldGoalBuildStatus.Unchanged)
            {
                // A repeated Goal request can be produced by an explicit
                // rebuild call even though the resolved source cell and
                // influence radius are identical. The prepared workspace is
                // already the committed Goal graph, so make this a true
                // no-op instead of launching another BFS.
                pending &= ~FlowFieldDirtyFlags.Goal;
                if ((pending & ~(FlowFieldDirtyFlags.Goal | FlowFieldDirtyFlags.FinalRegion))
                    == FlowFieldDirtyFlags.None)
                {
                    pending &= ~FlowFieldDirtyFlags.FinalRegion;
                    _state.DirtyFinalRegion = FlowFieldCellRect.Invalid;
                }
            }
            if ((rebuildGoal && prepared.GoalChanged)
                || prepared.ObstacleMaskChanged)
            {
                pending |= FlowFieldDirtyFlags.FinalRegion;
                _state.ExpandFinalDirty(FlowFieldCellRect.Full(_state.Grid));
            }
        }

        private void UpdateBlockedWarning(bool hasWalkableCell)
        {
            if (!hasWalkableCell && !_obstaclePipeline.AllBlockedWarningIssued)
            {
                _obstaclePipeline.AllBlockedWarningIssued = true;
                Debug.LogWarning("[FlowField] 모든 유효한 Surface 셀이 장애물로 막혔습니다.");
            }
            else if (hasWalkableCell)
            {
                _obstaclePipeline.AllBlockedWarningIssued = false;
            }
        }
    }
}
