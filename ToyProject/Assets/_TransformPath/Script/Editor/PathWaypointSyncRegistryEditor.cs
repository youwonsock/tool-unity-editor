using UnityEditor;
using UnityEngine;

namespace Common.TransformPath.Editor
{
    [CustomEditor(typeof(PathWaypointSyncRegistry))]
    internal sealed class PathWaypointSyncRegistryEditor : UnityEditor.Editor
    {
        #region Constants

        private const float POSITION_SYNC_EPSILON_SQR =
            PathLinkEditorUtility.POSITION_SYNC_EPSILON_SQR;

        #endregion


        #region Member Variables

        private SerializedProperty _syncInEditorProperty;
        private SerializedProperty _groupsProperty;

        #endregion


        #region Unity Events

        private void OnEnable()
        {
            _syncInEditorProperty = serializedObject.FindProperty("_syncInEditor");
            _groupsProperty = serializedObject.FindProperty("_groups");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.PropertyField(_syncInEditorProperty);
            EditorGUILayout.Space(4f);
            EditorGUILayout.HelpBox(
                "Edit-mode only: moving one member syncs the group. "
                + "Use PathData's \"Refresh Path Points from Children\" to collect child points.",
                MessageType.Info);
            EditorGUILayout.PropertyField(_groupsProperty, true);

            EditorGUILayout.Space(8f);
            DrawActionButtons();

            serializedObject.ApplyModifiedProperties();
        }

        #endregion


        #region Private Methods

        private void DrawActionButtons()
        {
            PathWaypointSyncRegistry registry = (PathWaypointSyncRegistry)target;
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Validate"))
                    PathWaypointSyncEditorUtility.LogValidation(registry);

                // Aligning moves scene points, which Play Mode would throw away.
                EditorGUI.BeginDisabledGroup(Application.isPlaying);
                if (GUILayout.Button("Align All Groups"))
                {
                    PathEditorUndoUtility.Record(registry, "Align All Waypoint Groups");
                    int count = PathWaypointSyncEditorUtility.AlignAllGroups(
                        registry,
                        POSITION_SYNC_EPSILON_SQR);
                    Debug.Log(
                        $"PathWaypointSyncRegistry: aligned {count} group(s).",
                        registry);
                }
                EditorGUI.EndDisabledGroup();
            }

            EditorGUI.BeginDisabledGroup(Application.isPlaying);
            bool alignFirst = GUILayout.Button("Align First Group");
            EditorGUI.EndDisabledGroup();
            if (Application.isPlaying)
                EditorGUILayout.HelpBox("Path authoring tools are disabled in Play Mode.", MessageType.Info);
            if (alignFirst && registry.Groups.Count > 0)
            {
                MultiPathData multiPathData = registry.GetComponent<MultiPathData>();
                PathEditorUndoUtility.Record(registry, "Align Waypoint Group");
                PathWaypointSyncEditorUtility.AlignGroupToFirstMember(
                    registry.Groups[0],
                    POSITION_SYNC_EPSILON_SQR,
                    PathWaypointSyncEditorUtility.BuildTransformToPathDataMap(multiPathData),
                    multiPathData);
                PathEditorUndoUtility.RepaintScene();
            }
        }

        #endregion
    }
}
