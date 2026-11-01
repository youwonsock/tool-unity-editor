#if UNITY_EDITOR
using System;
using UnityEngine;

namespace Common.FlowField
{
    public partial class FlowFieldManager
    {
        private FlowFieldSession _editorPreviewSession;

        [ContextMenu("Clear Goal")]
        private void ClearGoalContextMenu() => SetGoal(FlowFieldGoalRequest.None);

        [ContextMenu("Refresh Volume3D Visualization")]
        private void RefreshVolumeVisualization()
        {
            if (_spaceMode == FlowFieldSpaceMode.Volume3D)
            {
                FlowFieldEditorVisualizationBridge.RequestRefresh(this);
                FlowFieldEditorVisualizationBridge.RepaintAll();
            }
        }

        private void OnDrawGizmos()
        {
            if (_showField)
                DrawFlowFieldGizmos();
        }

        private void OnDrawGizmosSelected()
            => DrawBakeBoundsGizmo();

        private void DrawFlowFieldGizmos()
        {
            if (_spaceMode == FlowFieldSpaceMode.Volume3D)
            {
                DrawVolumeFlowFieldGizmos();
                return;
            }
            if (!TryGetBakeLayout(out _, out FlowFieldGridSpace grid)
                || !grid.IsValid
                || !FlowFieldBakeBoundsUtility.TryValidateCellCount(grid.Width, grid.Depth, out _)
                || !TryValidateSurfaceBake(
                    out _,
                    includeStaticGoal: BakeMode != FlowFieldBakeMode.StaticBaked))
                return;

            FlowFieldReadView view;
            if (Application.isPlaying)
            {
                if (!_session.IsInitialized || !_session.StagingGrid.IsValid || !_session.IsReady)
                    return;
                view = _session.CommittedView;
                grid = _session.CommittedGrid;
            }
            else
            {
                RefreshEditorPreview(grid);
                if (_editorPreviewSession == null || !_editorPreviewSession.IsReady)
                    return;
                view = _editorPreviewSession.CommittedView;
                grid = _editorPreviewSession.CommittedGrid;
            }

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
            if (!view.IsValid)
                return;

            FlowFieldGizmoDrawer.Draw(new FlowFieldGizmoRequest(
                grid,
                view,
                _cellSize,
                goal.IsValid,
                goal.RequestedWorld,
                goal.InfluenceRadius));
        }

        private void DrawVolumeFlowFieldGizmos()
        {
            FlowFieldEditorVisualizationBridge.DrawVolume(this);
        }

        private void DrawBakeBoundsGizmo()
        {
            Bounds worldBounds;
            FlowFieldGridSpace grid;
            bool validLayout = _spaceMode == FlowFieldSpaceMode.Volume3D
                ? TryGetVolumeLayout(out worldBounds, out grid)
                : TryGetBakeLayout(out worldBounds, out grid);
            if (!validLayout)
                return;
            if (_spaceMode == FlowFieldSpaceMode.Volume3D)
            {
                FlowFieldEditorVisualizationBridge.DrawVolumeBounds(this, worldBounds);
                return;
            }
            FlowFieldGizmoDrawer.DrawBakeBounds(worldBounds, TryValidateSurfaceBake(out _));
        }

        private void RefreshEditorPreview(FlowFieldGridSpace grid)
        {
            if (_editorPreviewSession != null && _editorPreviewSession.IsReady)
                return;

            ReleaseEditorPreview();
            _editorPreviewSession = new FlowFieldSession();
            FlowFieldSessionSourceKind sourceKind = BakeMode == FlowFieldBakeMode.StaticBaked
                ? FlowFieldSessionSourceKind.StaticSnapshot
                : FlowFieldSessionSourceKind.SceneBuild;
            _editorPreviewSession.Initialize(
                BakeMode,
                sourceKind,
                FlowFieldSolvePolicy.Inline);

            RegisterEditorModifiers(_editorPreviewSession);
            FlowFieldSurfaceBakeSettings settings = CreateSurfaceBakeSettings();
            FlowFieldDirtyFlags dirty = _editorPreviewSession.DirtyFlags;
            _editorPreviewSession.Submit(CreateSurfaceRequest(
                settings,
                dirty,
                _editorPreviewSession.DirtyFinalRegion,
                _editorPreviewSession.DirtyObstacleRegion));

            // Editor preview is a managed, short-lived execution owner. Keep
            // the Submit boundary non-recursive while still completing the
            // preview in the same editor repaint/update that requested it.
            _editorPreviewSession.Pump();
        }

        private void RegisterEditorModifiers(FlowFieldSession session)
        {
            FlowFieldVectorModifierVolume[] volumes = Resources.FindObjectsOfTypeAll<FlowFieldVectorModifierVolume>();
            Array.Sort(volumes, (left, right) =>
            {
                int priority = left.Priority.CompareTo(right.Priority);
                return priority != 0
                    ? priority
                    : left.GetInstanceID().CompareTo(right.GetInstanceID());
            });
            for (int i = 0; i < volumes.Length; i++)
            {
                FlowFieldVectorModifierVolume volume = volumes[i];
                if (volume == null
                    || volume.FlowFieldManager != this
                    || !volume.gameObject.scene.IsValid())
                    continue;
                try
                {
                    session.RegisterModifierCommand(volume);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, volume);
                }
            }
        }

        internal void InvalidateEditorPreview()
        {
            ReleaseEditorPreview();
        }

        private void ReleaseEditorPreview()
        {
            if (_editorPreviewSession == null)
                return;
            _editorPreviewSession.DisposePermanently();
            _editorPreviewSession = null;
        }
    }
}
#endif
