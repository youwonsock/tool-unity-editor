using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Common.TransformPath.Editor
{
    /// <summary>Scene View preview of a PathShapeProvider: the sampled line and its start.</summary>
    internal static class PathShapeGizmoDrawer
    {
        private const int SAMPLE_COUNT = 128;
        private const float LINE_WIDTH = 2f;
        private const float START_POINT_SIZE = 0.12f;
        private static readonly Color SELECTED_COLOR = new Color(0.2f, 0.9f, 1f, 1f);
        private static readonly Color UNSELECTED_COLOR = new Color(0.2f, 0.9f, 1f, 0.5f);
        private static readonly Vector3[] POINTS = new Vector3[SAMPLE_COUNT + 1];
        // Shapes whose sampling threw. Each logs once until it draws again, not on every repaint.
        private static readonly HashSet<int> FAILED = new HashSet<int>();

        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected)]
        private static void DrawShape(PathShapeProvider shape, GizmoType gizmoType)
        {
            if (shape == null)
                return;
            if (Event.current != null && Event.current.type != EventType.Repaint)
                return;
            // Edit Mode shows the current settings; Play Mode shows what playback uses.
            if (!Application.isPlaying)
                shape.Rebuild();
            if (!shape.IsReady)
                return;
            try
            {
                for (int i = 0; i <= SAMPLE_COUNT; i++)
                    POINTS[i] = shape.Sample(i / (float)SAMPLE_COUNT);
            }
            catch (System.Exception exception)
            {
                if (FAILED.Add(shape.GetInstanceID()))
                    Debug.LogException(exception, shape);
                return;
            }
            FAILED.Remove(shape.GetInstanceID());
            Color color = (gizmoType & GizmoType.Selected) != 0 ? SELECTED_COLOR : UNSELECTED_COLOR;
            Handles.color = color;
            Handles.DrawAAPolyLine(LINE_WIDTH, POINTS);
            Gizmos.color = color;
            Gizmos.DrawSphere(POINTS[0], START_POINT_SIZE);
        }
    }
}
