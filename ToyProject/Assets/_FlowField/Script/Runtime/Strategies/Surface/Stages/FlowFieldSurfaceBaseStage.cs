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
        private readonly FlowFieldSurfaceState _state;
        private readonly FlowFieldSurfaceObstacleMasks _obstacleMasks;
        private readonly FlowFieldSurfaceGoalTracker _goalTracker;
        private readonly IFlowFieldSurfaceSource _surfaceSource;
        private FlowFieldGridSpace? _observedGrid;

        internal FlowFieldSurfaceBaseStage(
            FlowFieldSurfaceState state,
            FlowFieldSurfaceObstacleMasks obstacleMasks,
            FlowFieldSurfaceGoalTracker goalTracker,
            IFlowFieldSurfaceSource surfaceSource)
        {
            _state = state;
            _obstacleMasks = obstacleMasks;
            _goalTracker = goalTracker;
            _surfaceSource = surfaceSource;
        }

        internal FlowFieldSurfaceBakeSnapshot LoadedSnapshot { get; set; }

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
            in FlowFieldSurfaceRequest request,
            FlowFieldSurfaceDirtyFlags pending,
            FlowFieldModifierRegistry modifiers)
        {
            if ((pending & FlowFieldSurfaceDirtyFlags.Grid) == 0
                && _state.Grid.IsValid
                && _state.SurfaceReady)
                return false;

            // Any Surface/grid rebuild invalidates an obstacle candidate that
            // was measured against the previous cell heights and bounds.
            // Leave the registered collider set intact; the downstream base
            // stage will issue one fresh dynamic query for the new Surface.

            FlowFieldSurfaceData surface = ResolveSurface(request);
            if (!surface.IsValid)
                throw new InvalidOperationException("FlowField surface source returned invalid data.");

            bool boundsChanged = !_state.Grid.MatchesBounds(request.SurfaceSettings.Grid);
            FlowFieldSurfaceData previousSurface = _state.Surface;
            bool snapshotChanged = request.SourceKind != FlowFieldSurfaceSourceKind.SceneBuild
                && !ReferenceEquals(LoadedSnapshot, request.BakeSnapshot);
            bool surfaceChanged = boundsChanged
                || !_state.SurfaceReady
                || snapshotChanged
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
            // A StaticBaked build records its snapshot once it loads the whole
            // field; a BakedBase build has loaded what it uses from it here.
            if (request.SourceKind == FlowFieldSurfaceSourceKind.BakedBase)
                LoadedSnapshot = request.BakeSnapshot;
            _state.Workspace.Resize(_state.Grid.CellCount);
            _state.BaseComposed = false;
            _state.LastComposedRegion = FlowFieldCellRect.Invalid;
            _state.Surface = surface;

            if (boundsChanged)
                _state.HasObstacleMask = false;
            _state.DirtyFlags |= FlowFieldSurfaceDirtyFlags.StaticObstacles
                | FlowFieldSurfaceDirtyFlags.DynamicObstacles
                | FlowFieldSurfaceDirtyFlags.Escape
                | FlowFieldSurfaceDirtyFlags.DefaultDirection
                | FlowFieldSurfaceDirtyFlags.Goal
                | FlowFieldSurfaceDirtyFlags.ModifierArea
                | FlowFieldSurfaceDirtyFlags.FinalRegion;
            _state.DirtyFinalRegion = FlowFieldCellRect.Full(_state.Grid);
            _state.DirtyObstacleRegion = FlowFieldCellRect.Full(_state.Grid);
            modifiers?.MarkAllAreasDirty();
            return true;
        }

        private FlowFieldSurfaceData ResolveSurface(in FlowFieldSurfaceRequest request)
        {
            if (request.SourceKind == FlowFieldSurfaceSourceKind.SceneBuild)
            {
                return _surfaceSource.Build(request.SurfaceSettings)
                    ?? throw new InvalidOperationException("FlowField surface source returned no surface data.");
            }

            if (request.BakeSnapshot == null || !request.BakeSnapshot.HasValidData)
                throw new InvalidOperationException("Static Flow Bake Asset is missing or invalid.");
            bool surfaceMatches = request.BakeSnapshot.MatchesSurface(
                request.SurfaceSettings,
                out string surfaceMismatch);
            bool obstaclesMatch = request.BakeSnapshot.MatchesObstacles(
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
            return request.BakeSnapshot.Surface;
        }

        internal void BuildSceneBase(
            in FlowFieldSurfaceRequest request,
            ref FlowFieldSurfaceDirtyFlags pending,
            bool surfaceChanged,
            bool sweepsObstacles)
        {
            if ((pending & FlowFieldSurfaceDirtyFlags.DefaultDirection) != 0)
            {
                _state.ResolvedDefaultDirection = FlowFieldVectorUtility.NormalizeDefaultDirection(request.DefaultDirection);
                pending |= FlowFieldSurfaceDirtyFlags.FinalRegion;
                _state.ExpandFinalDirty(FlowFieldCellRect.Full(_state.Grid));
            }

            bool rebuildStatic = (pending & FlowFieldSurfaceDirtyFlags.StaticObstacles) != 0;
            bool rebuildDynamic = (pending & FlowFieldSurfaceDirtyFlags.DynamicObstacles) != 0;
            bool rebuildGoal = (pending & FlowFieldSurfaceDirtyFlags.Goal) != 0;
            if (!_state.HasObstacleMask)
            {
                // A Goal can be requested before the first obstacle
                // preparation; the solver must never read the zero-initialized
                // mask.
                rebuildStatic = true;
                rebuildDynamic = true;
            }
            if (!rebuildStatic && !rebuildDynamic && !rebuildGoal)
                return;

            bool hadObstacleMask = _state.HasObstacleMask;
            FlowFieldSurfaceGoalResolution goal = request.Goal;
            FlowFieldSurfaceBuildResult prepared = FlowFieldSurfaceBaseFieldBuild.PrepareBase(
                new FlowFieldSurfaceBuildRequest(
                    new FlowFieldSurfaceObstacleRequest(
                        _state.Grid,
                        _state.Surface,
                        _state.Workspace,
                        request.ObstacleLayer,
                        request.ObstacleCheckHeight,
                        request.ObstacleCheckCenterOffset,
                        request.ObstacleClearance,
                        _state.DirtyObstacleRegion,
                        request.SourceKind == FlowFieldSurfaceSourceKind.BakedBase ? request.BakeSnapshot : null,
                        _state.BakedBaseTouched,
                        sweepsObstacles),
                    goal,
                    surfaceChanged),
                _obstacleMasks,
                _goalTracker,
                rebuildStatic,
                rebuildDynamic,
                rebuildGoal);

            ApplyPreparedBase(prepared, rebuildStatic, rebuildDynamic, rebuildGoal, hadObstacleMask, ref pending);
        }

        private void ApplyPreparedBase(
            in FlowFieldSurfaceBuildResult prepared,
            bool rebuildStatic,
            bool rebuildDynamic,
            bool rebuildGoal,
            bool hadObstacleMask,
            ref FlowFieldSurfaceDirtyFlags pending)
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
                pending &= ~(FlowFieldSurfaceDirtyFlags.StaticObstacles
                    | FlowFieldSurfaceDirtyFlags.DynamicObstacles
                    | FlowFieldSurfaceDirtyFlags.Escape);
                if ((pending & ~(FlowFieldSurfaceDirtyFlags.StaticObstacles
                        | FlowFieldSurfaceDirtyFlags.DynamicObstacles
                        | FlowFieldSurfaceDirtyFlags.Escape
                        | FlowFieldSurfaceDirtyFlags.FinalRegion)) == FlowFieldSurfaceDirtyFlags.None)
                {
                    pending &= ~FlowFieldSurfaceDirtyFlags.FinalRegion;
                    _state.DirtyFinalRegion = FlowFieldCellRect.Invalid;
                }
            }
            if (prepared.ObstacleMaskChanged || !hadObstacleMask)
            {
                if (!prepared.ObstacleMaskChanged)
                    _obstacleMasks.CommitCombinedAndBuildEscape(
                        _state.Grid,
                        _state.Surface,
                        _state.Workspace,
                        out _);
                _state.ExpandObstacleDirty(prepared.ObstacleDirtyRegion);
                UpdateBlockedWarning(prepared.HasWalkableSurface);
                pending |= FlowFieldSurfaceDirtyFlags.Goal | FlowFieldSurfaceDirtyFlags.FinalRegion;
                _state.ExpandFinalDirty(FlowFieldCellRect.Full(_state.Grid));
            }
            if (prepared.GoalStatus == FlowFieldSurfaceGoalBuildStatus.NoWalkableSurface
                && _goalTracker.TryConsumeMissingWalkableWarning())
            {
                Debug.LogWarning("[FlowField] The Goal range has no walkable surface cell.");
            }
            if (rebuildGoal && prepared.GoalStatus == FlowFieldSurfaceGoalBuildStatus.Unchanged)
            {
                // A repeated Goal request can be produced by an explicit
                // rebuild call even though the resolved source cell and
                // influence radius are identical. The prepared workspace is
                // already the committed Goal graph, so make this a true
                // no-op instead of launching another BFS.
                pending &= ~FlowFieldSurfaceDirtyFlags.Goal;
                if ((pending & ~(FlowFieldSurfaceDirtyFlags.Goal | FlowFieldSurfaceDirtyFlags.FinalRegion))
                    == FlowFieldSurfaceDirtyFlags.None)
                {
                    pending &= ~FlowFieldSurfaceDirtyFlags.FinalRegion;
                    _state.DirtyFinalRegion = FlowFieldCellRect.Invalid;
                }
            }
            if ((rebuildGoal && prepared.GoalChanged)
                || prepared.ObstacleMaskChanged)
            {
                pending |= FlowFieldSurfaceDirtyFlags.FinalRegion;
                _state.ExpandFinalDirty(FlowFieldCellRect.Full(_state.Grid));
            }
        }

        private void UpdateBlockedWarning(bool hasWalkableCell)
        {
            if (!hasWalkableCell && !_obstacleMasks.AllBlockedWarningIssued)
            {
                _obstacleMasks.AllBlockedWarningIssued = true;
                Debug.LogWarning("[FlowField] Obstacles block every valid Surface cell.");
            }
            else if (hasWalkableCell)
            {
                _obstacleMasks.AllBlockedWarningIssued = false;
            }
        }
    }
}
