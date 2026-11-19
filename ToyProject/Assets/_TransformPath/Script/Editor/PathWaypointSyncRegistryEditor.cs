using UnityEditor;
using UnityEngine;

namespace Common.TransformPath
{
    [CustomEditor(typeof(PathWaypointSyncRegistry))]
    internal sealed class PathWaypointSyncRegistryEditor : Editor
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
            }

            if (GUILayout.Button("Align First Group") && registry.Groups.Count > 0)
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
