using UnityEditor;
using UnityEngine;

namespace Common.FlowField.Editor
{
    /// <summary>Scene View drawing of a Manager's field and Bake Bounds.</summary>
    internal static class FlowFieldManagerGizmos
    {
        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected)]
        private static void DrawField(FlowFieldManager manager, GizmoType gizmoType)
        {
            if (!FlowFieldDisplay.Of(manager).ShowField)
                return;
            if (manager.SpaceMode == FlowFieldSpaceMode.Volume3D)
            {
                FlowFieldVolumeVisualizationEditor.Draw(manager);
                return;
            }
            DrawSurfaceField(manager);
        }

        [DrawGizmo(GizmoType.Selected | GizmoType.Active)]
        private static void DrawBakeBounds(FlowFieldManager manager, GizmoType gizmoType)
        {
            if (!manager.TryGetLayout(out Bounds worldBounds, out _))
                return;
            if (manager.SpaceMode == FlowFieldSpaceMode.Volume3D)
                FlowFieldVolumeVisualizationEditor.DrawBounds(manager, worldBounds);
            else
                FlowFieldGizmoDrawer.DrawBakeBounds(worldBounds, manager.TryValidateConfig(out _));
        }

        private static void DrawSurfaceField(FlowFieldManager manager)
        {
            // A StaticBaked field draws the Goal it was baked with.
            if (!manager.TryGetLayout(out _, out FlowFieldGridSpace grid)
                || !grid.IsValid
                || !FlowFieldLayoutUtility.TryValidateCellCount(grid.Width, grid.Depth, out _)
                || !manager.TryValidateConfig(
                    out _,
                    includeStaticGoal: manager.BakeMode != FlowFieldBakeMode.StaticBaked))
                return;

            FlowFieldReadView view;
            if (Application.isPlaying)
            {
                if (!manager.TryGetPublishedField(out FlowFieldField field) || field.Surface == null)
                    return;
                view = FlowFieldReadView.FromSurfaceField(field);
                grid = field.Grid;
            }
            else if (!FlowFieldEditorPreview.TryGetView(manager, out view, out grid))
            {
                return;
            }
            if (!view.IsValid)
                return;

            FlowFieldSurfaceBakeData bake = manager.CaptureConfig().BakeAssets.Surface;
            FlowFieldSurfaceGoalResolution goal = manager.BakeMode == FlowFieldBakeMode.StaticBaked && bake != null
                ? new FlowFieldSurfaceGoalResolution(
                    bake.HasGoal,
                    bake.HasGoal,
                    0,
                    0,
                    bake.ResolvedGoalIndex,
                    bake.GoalInfluenceRadius,
                    bake.RequestedGoalWorld)
                : manager.ResolveConfiguredGoal(grid);

            FlowFieldGizmoDrawer.Draw(new FlowFieldGizmoRequest(
                grid,
                view,
                manager.CaptureConfig().Grid.CellSize,
                goal.IsValid,
                goal.RequestedWorld,
                goal.InfluenceRadius));
        }
    }
}
