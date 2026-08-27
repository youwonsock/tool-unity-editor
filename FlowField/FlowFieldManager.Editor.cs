#if UNITY_EDITOR
using UnityEngine;

namespace Supercent.Common.FlowField
{
    public partial class FlowFieldManager
    {
        private FlowFieldEditorPreview _editorPreview;

        [ContextMenu("Clear Goal")]
        private void ClearGoalContextMenu() => ClearGoal();

        private void OnDrawGizmos()
        {
            if (_showField)
                DrawFlowFieldGizmos();
        }

        private void OnDrawGizmosSelected()
            => DrawBakeBoundsGizmo();

        private void DrawFlowFieldGizmos()
        {
            EnsureServices();
            FlowFieldGridSpace grid = Application.isPlaying && _context.Grid.IsValid
                ? _context.Grid
                : CreateGridSpace();
            if (!grid.IsValid
                || !FlowFieldBakeBoundsUtility.TryValidateCellCount(grid.Width, grid.Depth, out _)
                || !TryValidateSurfaceBake(out _))
                return;

            if (!Application.isPlaying)
                EnsureEditorPreview(grid);
            FlowFieldGoalResolution goal = FlowFieldGoalPipeline.Resolve(
                grid,
                _goalTransform,
                _hasExplicitGoal,
                _explicitGoalWorld,
                _goalInfluenceRadius);
            FlowFieldGizmoDrawer.Draw(new FlowFieldGizmoRequest(
                grid,
                _surfaceBakeData,
                ResolveGizmoWorkspace(),
                _cellSize,
                goal.IsValid,
                goal.RequestedWorld,
                goal.InfluenceRadius));
        }

        private void DrawBakeBoundsGizmo()
        {
            if (!TryGetBakeLayout(out Bounds worldBounds, out _))
                return;

            FlowFieldGizmoDrawer.DrawBakeBounds(worldBounds, TryValidateSurfaceBake(out _));
        }

        private void EnsureEditorPreview(FlowFieldGridSpace grid)
        {
            EnsureServices();
            FlowFieldGoalResolution goal = FlowFieldGoalPipeline.Resolve(
                grid,
                _goalTransform,
                _hasExplicitGoal,
                _explicitGoalWorld,
                _goalInfluenceRadius);
            _editorPreview.Ensure(
                grid,
                _surfaceBakeData,
                _coarseTopologyData,
                _fineRingCoarseRadius,
                FlowFieldVectorUtility.SanitizeDefaultDirection(_defaultFlowDirection),
                _obstacleLayer,
                _obstacleCheckHeight,
                _obstacleCheckCenterOffset,
                _obstacleClearance,
                _obstaclePipeline,
                _modifierPipeline,
                goal.IsValid,
                goal.LocalX,
                goal.LocalZ,
                goal.InfluenceRadius,
                _refreshRate);
        }

        private FlowFieldWorkspace ResolveGizmoWorkspace()
            => Application.isPlaying ? _context.Workspace : _editorPreview.Workspace;

        private void InvalidateEditorPreview()
            => _editorPreview?.Invalidate();

        private void DisposeEditorPreview()
        {
            _editorPreview?.Dispose();
            _editorPreview = null;
        }
    }
}
#endif
