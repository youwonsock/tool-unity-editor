#if UNITY_EDITOR
using UnityEngine;

namespace Supercent.Common.FlowField
{
    public partial class FlowFieldManager
    {
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
    }
}
#endif
