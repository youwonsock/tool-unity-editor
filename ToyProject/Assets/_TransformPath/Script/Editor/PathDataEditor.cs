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
            bool canCreatePoints = paths.TrueForAll(PathPointTools.CanCreatePoints);
            EditorGUI.BeginDisabledGroup(!canCreatePoints);
            if (GUILayout.Button("Create Path Points"))
                PathPointTools.CreateDefaultPoints(paths);
            EditorGUI.EndDisabledGroup();
            if (GUILayout.Button(new GUIContent(
                    "Refresh Path Points from Children",
                    "Sets _pathPoints to the descendants whose names start with PathPoint, in hierarchy order. "
                    + "With fewer than two of them the points stay as they are.")))
                PathPointTools.RefreshFromChildren(paths);
            EditorGUI.EndDisabledGroup();

            if (!authoringToolsEnabled)
                EditorGUILayout.HelpBox(
                    "Path authoring tools are disabled in Play Mode.",
                    MessageType.Info);
            else if (!canCreatePoints)
                EditorGUILayout.HelpBox(
                    "Open the prefab to create path points.",
                    MessageType.Info);

            if (GUILayout.Button("Rebuild Runtime Path"))
                PathInspectorGUI.Rebuild(paths);

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
