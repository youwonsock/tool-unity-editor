using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Common.TransformPath.Editor
{
    /// <summary>Scene View preview of a PathData: control points, the sampled line, sample points and events.</summary>
    internal static class PathDataGizmoDrawer
    {
        private const float DEFAULT_POINT_SIZE = 0.1f;
        private const float EVENT_LABEL_OFFSET = 0.7f;
        private const float SAMPLE_LABEL_OFFSET = 0.5f;

        private static GUIStyle _pointLabelStyle;
        private static GUIStyle _sampleLabelStyle;
        private static GUIStyle _eventLabelStyle;

        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected)]
        private static void DrawPath(PathData pathData, GizmoType gizmoType)
        {
            if (pathData == null)
                return;
            if (Event.current != null && Event.current.type != EventType.Repaint)
                return;

            if (!pathData.ShowPathInEditor)
                return;
            IReadOnlyList<Transform> pathPoints = pathData.PathPoints;
            if (pathPoints == null || pathPoints.Count == 0)
                return;

            PathPreviewStyle style = pathData.PreviewStyle;
            DrawControlPoints(pathPoints, pathData.PointColor, style.PointSize);
            if (!PathDataScenePreviewCache.TryGet(pathData, out PathDataScenePreview preview))
            {
                DrawFallbackPath(pathPoints, pathData.PathColor, style.LineWidth);
                return;
            }

            Handles.color = pathData.PathColor;
            Handles.DrawAAPolyLine(Mathf.Max(0.1f, style.LineWidth), preview.SampledPoints);
            DrawSamplingPreview(preview, pathData, pathData.PreviewSamplingType, pathData.PreviewSampleCount,
                pathData.SamplePointColor, style.SamplePointSize);
            DrawPathEvents(preview, pathData, pathData.EventPointColor, style.EventPointSize);
        }

        private static void DrawControlPoints(IReadOnlyList<Transform> points, Color pointColor, float pointSize)
        {
            if (pointSize <= 0f)
                return;
            Gizmos.color = pointColor;
            GUIStyle labelStyle = GetPointLabelStyle(pointSize);
            for (int i = 0; i < points.Count; i++)
            {
                if (points[i] == null)
                    continue;
                Gizmos.DrawSphere(points[i].position, pointSize);
                Handles.Label(points[i].position + Vector3.up * 0.3f, $"P{i}", labelStyle);
            }
        }

        // Without a valid build, draw the straight runs between the points that exist.
        private static void DrawFallbackPath(IReadOnlyList<Transform> points, Color pathColor, float lineWidth)
        {
            var run = new List<Vector3>();
            for (int i = 0; i < points.Count; i++)
            {
                if (points[i] == null)
                {
                    DrawFallbackPolyline(run, pathColor, lineWidth);
                    run.Clear();
                    continue;
                }
                run.Add(points[i].position);
            }
            DrawFallbackPolyline(run, pathColor, lineWidth);
        }

        private static void DrawFallbackPolyline(List<Vector3> points, Color color, float lineWidth)
        {
            if (points.Count < 2)
                return;
            Handles.color = color;
            Handles.DrawAAPolyLine(Mathf.Max(0.1f, lineWidth), points.ToArray());
        }

        private static void DrawSamplingPreview(
            PathDataScenePreview preview,
            PathData pathData,
            PathData.EPreviewSamplingType samplingType,
            int sampleCount,
            Color color,
            float size)
        {
            if (preview == null || !preview.IsValid || sampleCount <= 0 || size <= 0f)
                return;

            Gizmos.color = color;
            GUIStyle labelStyle = GetSampleLabelStyle(size);
            uint state = unchecked((uint)pathData.GetInstanceID())
                * 747796405u
                + 2891336453u;
            for (int i = 0; i < sampleCount; i++)
            {
                float normalizedTime;
                if (samplingType == PathData.EPreviewSamplingType.DeterministicRandom)
                {
                    state = unchecked(state * 747796405u + 2891336453u);
                    normalizedTime = (state & 0x00ffffffu) / 16777215f;
                }
                else
                    normalizedTime = sampleCount == 1 ? 0f : (float)i / (sampleCount - 1);

                Vector3 samplePoint = preview.Sample(normalizedTime);
                Gizmos.DrawSphere(samplePoint, size);
                Handles.Label(samplePoint + Vector3.up * SAMPLE_LABEL_OFFSET, $"S{i}", labelStyle);
            }
        }

        private static void DrawPathEvents(
            PathDataScenePreview preview,
            PathData pathData,
            Color color,
            float size)
        {
            if (preview == null || !preview.IsValid || pathData.AuthoringEventCount == 0 || size <= 0f)
                return;

            Gizmos.color = color;
            GUIStyle labelStyle = GetEventLabelStyle(size);
            for (int i = 0; i < pathData.AuthoringEventCount; i++)
            {
                PathEventEntry entry = pathData.GetAuthoringEvent(i);
                if (entry.EventSetting == null)
                    continue;

                Vector3 eventPosition = preview.Sample(
                    PathData.ClampPathEventNormalizedTime(entry.NormalizedTime));
                Gizmos.DrawSphere(eventPosition, size);
                if (!string.IsNullOrEmpty(entry.EventSetting.EventName))
                    Handles.Label(
                        eventPosition + Vector3.up * EVENT_LABEL_OFFSET,
                        entry.EventSetting.EventName,
                        labelStyle);
            }
        }

        private static GUIStyle GetPointLabelStyle(float size) =>
            GetLabelStyle(ref _pointLabelStyle, EditorStyles.label, 12f, size);

        private static GUIStyle GetSampleLabelStyle(float size) =>
            GetLabelStyle(ref _sampleLabelStyle, EditorStyles.label, 12f, size);

        private static GUIStyle GetEventLabelStyle(float size) =>
            GetLabelStyle(ref _eventLabelStyle, EditorStyles.boldLabel, 14f, size);

        // Labels grow with the point they name.
        private static GUIStyle GetLabelStyle(ref GUIStyle cache, GUIStyle baseStyle, float baseFontSize, float size)
        {
            if (cache == null)
                cache = new GUIStyle(baseStyle);
            cache.fontSize = Mathf.Max(1, Mathf.RoundToInt(baseFontSize * (size / DEFAULT_POINT_SIZE)));
            cache.normal.textColor = Color.white;
            return cache;
        }
    }
}
