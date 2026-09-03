#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace Common.FlowField
{
    public partial class FlowFieldManager
    {
        private FlowFieldSession _editorPreviewSession;

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
            if (!TryGetBakeLayout(out _, out FlowFieldGridSpace grid)
                || !grid.IsValid
                || !FlowFieldBakeBoundsUtility.TryValidateCellCount(grid.Width, grid.Depth, out _)
                || !TryValidateSurfaceBake(
                    out _,
                    includeStaticGoal: BakeMode != FlowFieldBakeMode.StaticBaked))
                return;

            FlowFieldWorkspace workspace;
            if (Application.isPlaying)
            {
                if (!_session.IsInitialized || !_session.StagingGrid.IsValid || !_session.IsReady)
                    return;
                workspace = _session.CommittedWorkspace;
                grid = _session.CommittedGrid;
            }
            else
            {
                RefreshEditorPreview(grid);
                if (_editorPreviewSession == null || !_editorPreviewSession.IsReady)
                    return;
                workspace = _editorPreviewSession.CommittedWorkspace;
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
            FlowFieldSurfaceData surface = Application.isPlaying
                ? _session.CommittedSurface
                : _editorPreviewSession.CommittedSurface;
            if (surface == null || !surface.IsValid)
                return;

            FlowFieldGizmoDrawer.Draw(new FlowFieldGizmoRequest(
                grid,
                surface,
                workspace,
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
                FlowFieldBfsBackendPolicy.ManagedOnly,
                null);

            RegisterEditorModifiers(_editorPreviewSession);
            FlowFieldSurfaceBakeSettings settings = CreateSurfaceBakeSettings();
            FlowFieldDirtyFlags dirty = _editorPreviewSession.DirtyFlags;
            int maxWaves = Mathf.Min(grid.CellCount, Mathf.Max(64, _maxGpuWaves));
            if (BakeMode == FlowFieldBakeMode.StaticBaked)
            {
                FlowFieldStaticBakeSnapshot snapshot = CreateStaticBakeSnapshot(settings);
                _editorPreviewSession.Submit(
                    FlowFieldSessionRequest.ForStaticSnapshot(
                        settings,
                        snapshot,
                        _obstacleLayer,
                        _obstacleCheckHeight,
                        _obstacleCheckCenterOffset,
                        _obstacleClearance,
                        _defaultFlowDirection,
                        dirty,
                        maxWaves,
                        $"{name}_EditorStaticSurface"));
            }
            else
            {
                FlowFieldGoalResolution goal = ResolveConfiguredGoal(grid);
                _editorPreviewSession.Submit(
                    FlowFieldSessionRequest.ForSceneBuild(
                        settings,
                        _obstacleLayer,
                        _obstacleCheckHeight,
                        _obstacleCheckCenterOffset,
                        _obstacleClearance,
                        _enableUnregisteredObstacleSweep,
                        goal,
                        _defaultFlowDirection,
                        dirty,
                        _editorPreviewSession.DirtyFinalRegion,
                        _editorPreviewSession.DirtyObstacleRegion,
                        maxWaves,
                        $"{name}_EditorSurface"));
            }
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
                    || EditorUtility.IsPersistent(volume)
                    || !volume.gameObject.scene.IsValid())
                    continue;
                try
                {
                    session.RegisterModifier(volume);
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
