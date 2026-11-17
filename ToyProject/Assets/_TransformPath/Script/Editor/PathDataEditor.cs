using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>PathData Inspector. Drawing is <see cref="PathDataGizmoDrawer"/>, point tools <see cref="PathPointTools"/>.</summary>
    [CustomEditor(typeof(PathData))]
    internal sealed class PathDataEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUI.BeginChangeCheck();
            DrawPropertiesExcluding(
                serializedObject,
                PathInspectorGUI.MOVE_TYPE,
                PathInspectorGUI.MOVE_VALUE,
                PathInspectorGUI.TIME_CURVE);
            bool inspectorChanged = EditorGUI.EndChangeCheck();
            inspectorChanged |= DrawMovementSettings();
            inspectorChanged |= serializedObject.ApplyModifiedProperties();
            if (inspectorChanged)
                PathEditorUndoUtility.RepaintScene();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Path Tools", EditorStyles.boldLabel);
            bool authoringToolsEnabled = !Application.isPlaying;
            var paths = new List<PathData>(PathInspectorGUI.TargetsOf<PathData>(targets));
            EditorGUI.BeginDisabledGroup(!authoringToolsEnabled);
            if (GUILayout.Button("Snap to Ground"))
                PathPointTools.SnapToGround(paths);
            if (GUILayout.Button("Create Path Points"))
                PathPointTools.CreateDefaultPoints(paths);
            if (GUILayout.Button(new GUIContent(
                    "Refresh Path Points from Children",
                    "자손 중 PathPoint로 시작하는 Transform을 hierarchy 순서로 _pathPoints에 반영합니다.")))
                PathPointTools.RefreshFromChildren(paths);
            EditorGUI.EndDisabledGroup();

            if (!authoringToolsEnabled)
                EditorGUILayout.HelpBox(
                    "Path authoring tools are disabled in Play Mode.",
                    MessageType.Info);

            if (GUILayout.Button("Rebuild Runtime Path"))
                PathInspectorGUI.Rebuild(paths, "Rebuild Runtime Path");

            EditorGUI.BeginDisabledGroup(!authoringToolsEnabled);
            if (GUILayout.Button("Sort Path Events"))
                PathInspectorGUI.SortEvents(paths, "Sort Path Events");
            EditorGUI.EndDisabledGroup();
        }

        private bool DrawMovementSettings()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Movement", EditorStyles.boldLabel);
            return PathInspectorGUI.DrawMovement(
                serializedObject.FindProperty(PathInspectorGUI.MOVE_TYPE),
                serializedObject.FindProperty(PathInspectorGUI.MOVE_VALUE),
                serializedObject.FindProperty(PathInspectorGUI.TIME_CURVE));
        }
    }
}
