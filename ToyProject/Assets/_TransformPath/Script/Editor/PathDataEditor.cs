#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Common.TransformPath
{
    [CustomEditor(typeof(PathData))]
    internal sealed class PathDataEditor : Editor
    {
        private const float DEFAULT_EVENT_POINT_SIZE = 0.1f;
        private const float EVENT_LABEL_OFFSET = 0.7f;
        private const string SHOW_PATH_PROPERTY = "_showPathInEditor";
        private const string EVENT_POINT_SIZE_PROPERTY = "_eventPointSize";

        private static GUIStyle _eventLabelStyle;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            EditorGUILayout.Space();
            if (GUILayout.Button("Rebuild Runtime Path"))
            {
                foreach (Object targetObject in targets)
                {
                    PathData pathData = targetObject as PathData;
                    if (pathData == null)
                        continue;
                    Undo.RecordObject(pathData, "Rebuild Runtime Path");
                    pathData.Rebuild();
                    EditorUtility.SetDirty(pathData);
                }
            }
            if (GUILayout.Button("Sort Path Events"))
            {
                foreach (Object targetObject in targets)
                {
                    PathData pathData = targetObject as PathData;
                    if (pathData == null)
                        continue;
                    Undo.RecordObject(pathData, "Sort Path Events");
                    if (pathData.SortPathEventsByNormalizedTime())
                        EditorUtility.SetDirty(pathData);
                }
            }
        }

        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected)]
        private static void DrawPathEventLabels(PathData pathData, GizmoType gizmoType)
        {
            if (pathData == null || !pathData.IsReady || pathData.EventCount == 0)
                return;

            SerializedObject serializedPathData = new SerializedObject(pathData);
            SerializedProperty showPathProperty = serializedPathData.FindProperty(SHOW_PATH_PROPERTY);
            SerializedProperty eventPointSizeProperty = serializedPathData.FindProperty(EVENT_POINT_SIZE_PROPERTY);
            if (showPathProperty == null || !showPathProperty.boolValue
                || eventPointSizeProperty == null || eventPointSizeProperty.floatValue <= 0f)
                return;

            if (_eventLabelStyle == null)
            {
                _eventLabelStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    normal = { textColor = Color.white },
                    fontStyle = FontStyle.Bold,
                };
            }

            _eventLabelStyle.fontSize = Mathf.Max(
                1,
                Mathf.RoundToInt(14f * (eventPointSizeProperty.floatValue / DEFAULT_EVENT_POINT_SIZE)));

            for (int i = 0; i < pathData.EventCount; i++)
            {
                PathEventEntry entry = pathData.GetEvent(i);
                if (entry.EventSetting == null || string.IsNullOrEmpty(entry.EventSetting.EventName))
                    continue;

                Vector3 eventPosition = pathData.Sample(
                    PathData.ClampPathEventNormalizedTime(entry.NormalizedTime));
                Handles.Label(
                    eventPosition + Vector3.up * EVENT_LABEL_OFFSET,
                    entry.EventSetting.EventName,
                    _eventLabelStyle);
            }
        }
    }
}
#endif
