using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Common.TransformPath.Editor
{
    /// <summary>PathData Inspector. Drawing is <see cref="PathDataGizmoDrawer"/>, point tools <see cref="PathPointTools"/>.</summary>
    [CustomEditor(typeof(PathData))]
    [CanEditMultipleObjects]
    internal sealed class PathDataEditor : UnityEditor.Editor
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
            if (HasRuntimeControlPoints())
                EditorGUILayout.HelpBox(
                    "Runtime control points from SetControlPoints replace the Path Points list until SetPathPoints is called.",
                    MessageType.Info);
            inspectorChanged |= PathInspectorGUI.DrawMovementSection(serializedObject);
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
                    "Sets _pathPoints to the descendants whose names start with PathPoint, in hierarchy order.")))
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

        private bool HasRuntimeControlPoints()
        {
            for (int i = 0; i < targets.Length; i++)
            {
                if (targets[i] is PathData path && path.RuntimeControlPoints != null)
                    return true;
            }
            return false;
        }
    }
}
