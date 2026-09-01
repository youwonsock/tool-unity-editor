#if UNITY_EDITOR
using UnityEngine;

namespace Common.FlowField
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
            InitServices();
            FlowFieldGridSpace grid;
            if (Application.isPlaying)
            {
                // A faulted or not-yet-built runtime manager has no field to
                // draw.  Gizmo rendering must not turn an inspector state into
                // a hidden recovery path or an editor exception.
                if (_lifecycleState != LifecycleState.Initialized || !_context.Grid.IsValid)
                    return;
                grid = _context.Grid;
            }
            else if (!TryGetBakeLayout(out _, out grid))
            {
                return;
            }

            if (!grid.IsValid
                || !FlowFieldBakeBoundsUtility.TryValidateCellCount(grid.Width, grid.Depth, out _)
                || !TryValidateSurfaceBake(
                    out _,
                    includeStaticGoal: BakeMode != FlowFieldBakeMode.StaticBaked))
                return;

            if (!FlowFieldGridSpace.IsFinite(_defaultFlowDirection)
                || _defaultFlowDirection.sqrMagnitude <= FlowFieldVectorUtility.DIRECTION_EPSILON_SQR
                || !FlowFieldGridSpace.IsFinite(_goalInfluenceRadius)
                || _goalInfluenceRadius < 0f)
                return;

            if (!Application.isPlaying)
                RefreshEditorPreview(grid);
            FlowFieldGoalResolution goal = BakeMode == FlowFieldBakeMode.StaticBaked
                && _staticBakeData != null
                ? new FlowFieldGoalResolution(
                    _staticBakeData.HasGoal,
                    _staticBakeData.HasGoal,
                    0,
                    0,
                    _staticBakeData.ResolvedGoalIndex,
                    _staticBakeData.GoalInfluenceRadius,
                    _staticBakeData.RequestedGoalWorld)
                : FlowFieldGoalPipeline.Resolve(
                    grid,
                    _goalTransform,
                    _hasExplicitGoal,
                    _explicitGoalWorld,
                    _goalInfluenceRadius);
            FlowFieldSurfaceBakeData gizmoSurface = EditorSurfaceBakeData;
            if (gizmoSurface == null || !gizmoSurface.HasValidData)
                return;
            FlowFieldGizmoDrawer.Draw(new FlowFieldGizmoRequest(
                grid,
                gizmoSurface,
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

        private void RefreshEditorPreview(FlowFieldGridSpace grid)
        {
            InitServices();
            if (BakeMode == FlowFieldBakeMode.StaticBaked)
            {
                if (_staticBakeData == null || !_staticBakeData.HasValidData)
                    return;
                _editorPreview.LoadStatic(
                    grid,
                    _staticBakeData,
                    EditorSurfaceBakeData,
                    FlowFieldVectorUtility.NormalizeDefaultDirection(_defaultFlowDirection),
                    _obstacleCheckHeight,
                    _obstacleCheckCenterOffset,
                    _modifierPipeline);
                return;
            }

            FlowFieldGoalResolution goal = FlowFieldGoalPipeline.Resolve(
                grid,
                _goalTransform,
                _hasExplicitGoal,
                _explicitGoalWorld,
                _goalInfluenceRadius);
            _editorPreview.Refresh(
                grid,
                EditorSurfaceBakeData,
                FlowFieldVectorUtility.NormalizeDefaultDirection(_defaultFlowDirection),
                _obstacleLayer,
                _obstacleCheckHeight,
                _obstacleCheckCenterOffset,
                _obstacleClearance,
                _enableUnregisteredObstacleSweep,
                _obstaclePipeline,
                _modifierPipeline,
                goal.IsValid,
                goal.LocalX,
                goal.LocalZ,
                goal.RequestedWorld,
                goal.InfluenceRadius,
                _refreshRate);
        }

        private FlowFieldWorkspace ResolveGizmoWorkspace()
            => Application.isPlaying ? _context.Workspace : _editorPreview.Workspace;

        private void InvalidateEditorPreview()
            => _editorPreview?.Invalidate();

        private void ReleaseEditorPreview()
        {
            _editorPreview?.Release();
            _editorPreview = null;
        }
    }
}
#endif
