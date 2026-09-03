#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Common.TransformPath
{
    [CustomEditor(typeof(PathData))]
    internal sealed class PathDataEditor : Editor
    {
        private const float DEFAULT_POINT_SIZE = 0.1f;
        private const float EVENT_LABEL_OFFSET = 0.7f;
        private const float SAMPLE_LABEL_OFFSET = 0.5f;
        private const float MAX_RAYCAST_DISTANCE = 1000f;
        private const string PATH_POINT_NAME_PREFIX = "PathPoint";

        private static GUIStyle _pointLabelStyle;
        private static GUIStyle _sampleLabelStyle;
        private static GUIStyle _eventLabelStyle;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUI.BeginChangeCheck();
            DrawPropertiesExcluding(
                serializedObject,
                "_moveType",
                "_moveValue",
                "_timeCurve");
            bool inspectorChanged = EditorGUI.EndChangeCheck();
            inspectorChanged |= DrawMovementSettings();
            inspectorChanged |= serializedObject.ApplyModifiedProperties();
            if (inspectorChanged)
                SceneView.RepaintAll();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Path Tools", EditorStyles.boldLabel);

            bool authoringToolsEnabled = !Application.isPlaying;
            EditorGUI.BeginDisabledGroup(!authoringToolsEnabled);
            if (GUILayout.Button("Snap to Ground"))
                SnapSelectedPathsToGround();
            if (GUILayout.Button("Create Path Points"))
                CreatePathPointsForTargets();
            if (GUILayout.Button(new GUIContent(
                    "Refresh Path Points from Children",
                    "자손 중 PathPoint로 시작하는 Transform을 hierarchy 순서로 _pathPoints에 반영합니다.")))
            {
                RefreshPathPointsForTargets();
            }
            EditorGUI.EndDisabledGroup();

            if (!authoringToolsEnabled)
                EditorGUILayout.HelpBox("Path authoring tools are disabled in Play Mode.", MessageType.Info);

            if (GUILayout.Button("Rebuild Runtime Path"))
                RebuildRuntimePaths();

            EditorGUI.BeginDisabledGroup(!authoringToolsEnabled);
            if (GUILayout.Button("Sort Path Events"))
                SortPathEvents();
            EditorGUI.EndDisabledGroup();
        }

        private bool DrawMovementSettings()
        {
            SerializedProperty moveTypeProperty = serializedObject.FindProperty("_moveType");
            SerializedProperty moveValueProperty = serializedObject.FindProperty("_moveValue");
            SerializedProperty timeCurveProperty = serializedObject.FindProperty("_timeCurve");
            if (moveTypeProperty == null || moveValueProperty == null || timeCurveProperty == null)
                return false;

            bool changed = false;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Movement", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(moveTypeProperty, new GUIContent("Mode"));
            changed |= EditorGUI.EndChangeCheck();

            string valueLabel = moveTypeProperty.enumValueIndex == (int)EPathMoveType.SpeedBased
                ? "Speed"
                : "Duration";
            EditorGUILayout.PropertyField(moveValueProperty, new GUIContent(valueLabel));

            if (moveTypeProperty.enumValueIndex == (int)EPathMoveType.TimeBased)
            {
                if (timeCurveProperty.animationCurveValue == null
                    || timeCurveProperty.animationCurveValue.length == 0)
                {
                    timeCurveProperty.animationCurveValue = AnimationCurve.Linear(0f, 0f, 1f, 1f);
                    changed = true;
                }
                EditorGUILayout.PropertyField(timeCurveProperty, new GUIContent("Time Curve"));
            }
            return changed;
        }

        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected)]
        private static void DrawPath(PathData pathData, GizmoType gizmoType)
        {
            if (pathData == null)
                return;
            if (Event.current != null && Event.current.type != EventType.Repaint)
                return;

            SerializedObject serializedPathData = new SerializedObject(pathData);
            serializedPathData.Update();

            SerializedProperty showPathProperty = serializedPathData.FindProperty(
                PathDataEditorProperties.ShowPathInEditor);
            if (showPathProperty == null || !showPathProperty.boolValue)
                return;

            SerializedProperty pathPointsProperty = serializedPathData.FindProperty("_pathPoints");
            if (pathPointsProperty == null || pathPointsProperty.arraySize == 0)
                return;

            SerializedProperty pointColorProperty = serializedPathData.FindProperty(
                PathDataEditorProperties.PointColor);
            SerializedProperty pathColorProperty = serializedPathData.FindProperty(
                PathDataEditorProperties.PathColor);
            SerializedProperty sampleColorProperty = serializedPathData.FindProperty(
                PathDataEditorProperties.SamplePointColor);
            SerializedProperty eventColorProperty = serializedPathData.FindProperty(
                PathDataEditorProperties.EventPointColor);
            SerializedProperty lineWidthProperty = serializedPathData.FindProperty(
                PathDataEditorProperties.LineWidth);
            SerializedProperty pointSizeProperty = serializedPathData.FindProperty(
                PathDataEditorProperties.PointSize);
            SerializedProperty sampleSizeProperty = serializedPathData.FindProperty(
                PathDataEditorProperties.SamplePointSize);
            SerializedProperty eventSizeProperty = serializedPathData.FindProperty(
                PathDataEditorProperties.EventPointSize);
            SerializedProperty previewTypeProperty = serializedPathData.FindProperty(
                "_previewSamplingType");
            SerializedProperty previewCountProperty = serializedPathData.FindProperty(
                "_previewSampleCount");

            Color pointColor = pointColorProperty == null ? Color.red : pointColorProperty.colorValue;
            Color pathColor = pathColorProperty == null ? Color.blue : pathColorProperty.colorValue;
            Color sampleColor = sampleColorProperty == null ? Color.yellow : sampleColorProperty.colorValue;
            Color eventColor = eventColorProperty == null ? Color.green : eventColorProperty.colorValue;
            float lineWidth = lineWidthProperty == null ? 2f : lineWidthProperty.floatValue;
            float pointSize = pointSizeProperty == null ? DEFAULT_POINT_SIZE : pointSizeProperty.floatValue;
            float sampleSize = sampleSizeProperty == null ? 0f : sampleSizeProperty.floatValue;
            float eventSize = eventSizeProperty == null ? 0.15f : eventSizeProperty.floatValue;

            DrawControlPoints(pathPointsProperty, pointColor, pointSize);

            bool hasPreview = PathDataScenePreviewCache.TryGet(
                pathData,
                out PathDataScenePreview preview);
            if (hasPreview)
            {
                Handles.color = pathColor;
                Handles.DrawAAPolyLine(Mathf.Max(0.1f, lineWidth), preview.SampledPoints);

                int previewType = previewTypeProperty == null ? 0 : previewTypeProperty.enumValueIndex;
                int previewCount = previewCountProperty == null ? 0 : previewCountProperty.intValue;
                DrawSamplingPreview(preview, pathData, previewType, previewCount, sampleColor, sampleSize);
                DrawPathEvents(preview, pathData, eventColor, eventSize);
            }
            else
            {
                DrawFallbackPath(pathPointsProperty, pathColor, lineWidth);
            }
        }

        private static void DrawControlPoints(
            SerializedProperty pathPointsProperty,
            Color pointColor,
            float pointSize)
        {
            if (pathPointsProperty == null || pointSize <= 0f)
                return;

            Gizmos.color = pointColor;
            GUIStyle labelStyle = GetPointLabelStyle(pointSize);
            for (int i = 0; i < pathPointsProperty.arraySize; i++)
            {
                Transform point = GetPathPoint(pathPointsProperty, i);
                if (point == null)
                    continue;

                Gizmos.DrawSphere(point.position, pointSize);
                Handles.Label(point.position + Vector3.up * 0.3f, $"P{i}", labelStyle);
            }
        }

        private static void DrawFallbackPath(
            SerializedProperty pathPointsProperty,
            Color pathColor,
            float lineWidth)
        {
            List<Vector3> points = new List<Vector3>();
            for (int i = 0; i < pathPointsProperty.arraySize; i++)
            {
                Transform point = GetPathPoint(pathPointsProperty, i);
                if (point == null)
                {
                    DrawFallbackPolyline(points, pathColor, lineWidth);
                    points.Clear();
                    continue;
                }
                points.Add(point.position);
            }
            DrawFallbackPolyline(points, pathColor, lineWidth);
        }

        private static void DrawFallbackPolyline(List<Vector3> points, Color color, float lineWidth)
        {
            if (points == null || points.Count < 2)
                return;

            Handles.color = color;
            Handles.DrawAAPolyLine(Mathf.Max(0.1f, lineWidth), points.ToArray());
        }

        private static void DrawSamplingPreview(
            PathDataScenePreview preview,
            PathData pathData,
            int samplingType,
            int sampleCount,
            Color color,
            float size)
        {
            if (preview == null || !preview.IsValid || sampleCount <= 0 || size <= 0f)
                return;

            Gizmos.color = color;
            GUIStyle labelStyle = GetSampleLabelStyle(size);
            uint state = unchecked((uint)pathData.GetInstanceID()) * 747796405u + 2891336453u;
            for (int i = 0; i < sampleCount; i++)
            {
                float normalizedTime;
                if (samplingType == 1)
                {
                    state = unchecked(state * 747796405u + 2891336453u);
                    normalizedTime = (state & 0x00ffffffu) / 16777215f;
                }
                else
                {
                    normalizedTime = sampleCount == 1 ? 0f : (float)i / (sampleCount - 1);
                }

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
            if (preview == null || !preview.IsValid || pathData.EventCount == 0 || size <= 0f)
                return;

            Gizmos.color = color;
            GUIStyle labelStyle = GetEventLabelStyle(size);
            for (int i = 0; i < pathData.EventCount; i++)
            {
                PathEventEntry entry = pathData.GetEvent(i);
                if (entry.EventSetting == null)
                    continue;

                Vector3 eventPosition = preview.Sample(
                    PathData.ClampPathEventNormalizedTime(entry.NormalizedTime));
                Gizmos.DrawSphere(eventPosition, size);

                if (!string.IsNullOrEmpty(entry.EventSetting.EventName))
                {
                    Handles.Label(
                        eventPosition + Vector3.up * EVENT_LABEL_OFFSET,
                        entry.EventSetting.EventName,
                        labelStyle);
                }
            }
        }

        private static GUIStyle GetPointLabelStyle(float size)
        {
            if (_pointLabelStyle == null)
                _pointLabelStyle = new GUIStyle(EditorStyles.label);
            _pointLabelStyle.fontSize = Mathf.Max(1, Mathf.RoundToInt(12f * (size / DEFAULT_POINT_SIZE)));
            _pointLabelStyle.normal.textColor = Color.white;
            return _pointLabelStyle;
        }

        private static GUIStyle GetSampleLabelStyle(float size)
        {
            if (_sampleLabelStyle == null)
                _sampleLabelStyle = new GUIStyle(EditorStyles.label);
            _sampleLabelStyle.fontSize = Mathf.Max(1, Mathf.RoundToInt(12f * (size / DEFAULT_POINT_SIZE)));
            _sampleLabelStyle.normal.textColor = Color.white;
            return _sampleLabelStyle;
        }

        private static GUIStyle GetEventLabelStyle(float size)
        {
            if (_eventLabelStyle == null)
            {
                _eventLabelStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontStyle = FontStyle.Bold,
                };
            }
            _eventLabelStyle.fontSize = Mathf.Max(1, Mathf.RoundToInt(14f * (size / DEFAULT_POINT_SIZE)));
            _eventLabelStyle.normal.textColor = Color.white;
            return _eventLabelStyle;
        }

        private void RebuildRuntimePaths()
        {
            foreach (UnityEngine.Object targetObject in targets)
            {
                PathData pathData = targetObject as PathData;
                if (pathData == null)
                    continue;

                Undo.RecordObject(pathData, "Rebuild Runtime Path");
                pathData.Rebuild();
                EditorUtility.SetDirty(pathData);
                RecordPrefabOverride(pathData);
            }
            SceneView.RepaintAll();
        }

        private void SortPathEvents()
        {
            foreach (UnityEngine.Object targetObject in targets)
            {
                PathData pathData = targetObject as PathData;
                if (pathData == null)
                    continue;

                Undo.RecordObject(pathData, "Sort Path Events");
                if (!pathData.SortPathEventsByNormalizedTime())
                    continue;

                EditorUtility.SetDirty(pathData);
                RecordPrefabOverride(pathData);
            }
            SceneView.RepaintAll();
        }

        private void SnapSelectedPathsToGround()
        {
            foreach (UnityEngine.Object targetObject in targets)
            {
                PathData pathData = targetObject as PathData;
                if (pathData == null)
                    continue;

                List<Transform> pathPoints = GetPathPoints(pathData);
                if (pathPoints.Count == 0)
                {
                    Debug.LogWarning("PathData: 스냅할 경로 포인트가 없습니다.", pathData);
                    continue;
                }

                Undo.RecordObjects(pathPoints.ToArray(), "Snap Path Points to Ground");
                int snappedCount = 0;
                int failedCount = 0;
                for (int i = 0; i < pathPoints.Count; i++)
                {
                    Transform point = pathPoints[i];
                    if (point == null)
                        continue;

                    if (Physics.Raycast(point.position, Vector3.down, out RaycastHit hit, MAX_RAYCAST_DISTANCE))
                    {
                        point.position = hit.point;
                        RecordPrefabOverride(point);
                        snappedCount++;
                    }
                    else
                    {
                        failedCount++;
                    }
                }

                if (snappedCount > 0)
                {
                    EditorUtility.SetDirty(pathData);
                    Debug.Log(
                        $"PathData: {snappedCount}개의 포인트를 지면에 스냅했습니다. (실패: {failedCount}개)",
                        pathData);
                }
                else
                {
                    Debug.LogWarning(
                        $"PathData: 스냅된 포인트가 없습니다. (실패: {failedCount}개)",
                        pathData);
                }
            }
            SceneView.RepaintAll();
        }

        private void CreatePathPointsForTargets()
        {
            foreach (UnityEngine.Object targetObject in targets)
            {
                PathData pathData = targetObject as PathData;
                if (pathData == null)
                    continue;

                int undoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("Create Path Points");
                Undo.RecordObject(pathData, "Create Path Points");

                Transform start = CreatePathPoint(pathData.transform, "PathPointStart", Vector3.zero);
                Transform middle = CreatePathPoint(pathData.transform, "PathPoint", Vector3.forward * 5f);
                Transform end = CreatePathPoint(pathData.transform, "PathPointEnd", Vector3.forward * 10f);
                SetPathPoints(pathData, new List<Transform> { start, middle, end });

                EditorUtility.SetDirty(pathData);
                RecordPrefabOverride(pathData);
                Undo.CollapseUndoOperations(undoGroup);
                Debug.Log("PathData: PathPointStart, PathPoint, PathPointEnd 생성 완료", pathData);
            }
            SceneView.RepaintAll();
        }

        private void RefreshPathPointsForTargets()
        {
            foreach (UnityEngine.Object targetObject in targets)
            {
                PathData pathData = targetObject as PathData;
                if (pathData == null)
                    continue;

                List<Transform> collected = new List<Transform>();
                CollectTransformsWithPathPointPrefix(pathData.transform, collected);
                SetPathPoints(pathData, collected);

                if (collected.Count < 2)
                {
                    Debug.LogWarning(
                        $"PathData: '{PATH_POINT_NAME_PREFIX}'로 시작하는 자손이 {collected.Count}개입니다. 유효 경로는 최소 2개가 필요합니다.",
                        pathData);
                }
                else
                {
                    Debug.Log(
                        $"PathData: _pathPoints를 자손에서 {collected.Count}개 갱신했습니다.",
                        pathData);
                }
            }
            SceneView.RepaintAll();
        }

        private static Transform CreatePathPoint(Transform parent, string pointName, Vector3 localPosition)
        {
            GameObject pointObject = new GameObject(pointName);
            Transform point = pointObject.transform;
            point.SetParent(parent, false);
            point.localPosition = localPosition;
            Undo.RegisterCreatedObjectUndo(pointObject, $"Create {pointName}");
            return point;
        }

        private static List<Transform> GetPathPoints(PathData pathData)
        {
            List<Transform> result = new List<Transform>();
            if (pathData == null)
                return result;

            SerializedObject serializedPathData = new SerializedObject(pathData);
            SerializedProperty pathPointsProperty = serializedPathData.FindProperty("_pathPoints");
            if (pathPointsProperty == null)
                return result;

            for (int i = 0; i < pathPointsProperty.arraySize; i++)
            {
                Transform point = GetPathPoint(pathPointsProperty, i);
                if (point != null)
                    result.Add(point);
            }
            return result;
        }

        private static Transform GetPathPoint(SerializedProperty pathPointsProperty, int index)
        {
            if (pathPointsProperty == null || index < 0 || index >= pathPointsProperty.arraySize)
                return null;
            return pathPointsProperty.GetArrayElementAtIndex(index).objectReferenceValue as Transform;
        }

        private static void SetPathPoints(PathData pathData, List<Transform> points)
        {
            if (pathData == null)
                return;

            Undo.RecordObject(pathData, "Set Path Points");
            SerializedObject serializedPathData = new SerializedObject(pathData);
            SerializedProperty pathPointsProperty = serializedPathData.FindProperty("_pathPoints");
            if (pathPointsProperty == null)
                return;

            serializedPathData.Update();
            pathPointsProperty.ClearArray();
            if (points != null)
            {
                for (int i = 0; i < points.Count; i++)
                {
                    pathPointsProperty.InsertArrayElementAtIndex(i);
                    pathPointsProperty.GetArrayElementAtIndex(i).objectReferenceValue = points[i];
                }
            }
            serializedPathData.ApplyModifiedProperties();
            EditorUtility.SetDirty(pathData);
            RecordPrefabOverride(pathData);
        }

        private static void CollectTransformsWithPathPointPrefix(
            Transform parent,
            List<Transform> results)
        {
            if (parent == null || results == null)
                return;

            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child.name.StartsWith(PATH_POINT_NAME_PREFIX, StringComparison.Ordinal))
                    results.Add(child);
                CollectTransformsWithPathPointPrefix(child, results);
            }
        }

        private static void RecordPrefabOverride(UnityEngine.Object targetObject)
        {
            if (targetObject != null && PrefabUtility.IsPartOfPrefabInstance(targetObject))
                PrefabUtility.RecordPrefabInstancePropertyModifications(targetObject);
        }
    }

    /// <summary>Serialized field names shared by PathData and MultiPathData editors.</summary>
    internal static class PathDataEditorProperties
    {
        public const string ShowPathInEditor = "_showPathInEditor";
        public const string PointColor = "_pointColor";
        public const string PathColor = "_pathColor";
        public const string SamplePointColor = "_samplePointColor";
        public const string EventPointColor = "_eventPointColor";
        public const string LineWidth = "_lineWidth";
        public const string PointSize = "_pointSize";
        public const string SamplePointSize = "_samplePointSize";
        public const string EventPointSize = "_eventPointSize";
    }
}
#endif
