#if UNITY_EDITOR
using System.Collections.Generic;
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
                + "Use PathData 「Refresh from child PathPoints」 to collect child points.",
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
            MultiPathData multiPathData = registry.GetComponent<MultiPathData>();
            Dictionary<Transform, PathData> transformToPathData =
                PathWaypointSyncEditorUtility.BuildTransformToPathDataMap(multiPathData);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Validate"))
                    ValidateGroups(registry);

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

            if (GUILayout.Button("Align Selected Group (first in list)"))
            {
                IReadOnlyList<PathWaypointSyncGroup> groups = registry.Groups;
                if (groups.Count == 0)
                    return;

                PathWaypointSyncGroup firstGroup = groups[0];
                PathEditorUndoUtility.Record(registry, "Align Waypoint Group");
                PathWaypointSyncEditorUtility.AlignGroupToFirstMember(
                    firstGroup,
                    POSITION_SYNC_EPSILON_SQR,
                    transformToPathData,
                    multiPathData);
                PathEditorUndoUtility.RepaintScene();
            }
        }

        private static void ValidateGroups(PathWaypointSyncRegistry registry)
        {
            IReadOnlyList<PathWaypointSyncGroup> groups = registry.Groups;
            int nullMemberCount = 0;
            int duplicateWarningCount = 0;
            HashSet<Transform> seen = new HashSet<Transform>();

            for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
            {
                PathWaypointSyncGroup group = groups[groupIndex];
                if (group == null)
                    continue;

                Transform[] members = group.Members;
                if (members == null)
                    continue;

                for (int memberIndex = 0; memberIndex < members.Length; memberIndex++)
                {
                    Transform member = members[memberIndex];
                    if (member == null)
                    {
                        nullMemberCount++;
                        continue;
                    }

                    if (!seen.Add(member))
                        duplicateWarningCount++;
                }
            }

            if (nullMemberCount == 0 && duplicateWarningCount == 0)
            {
                Debug.Log("PathWaypointSyncRegistry: validation passed.", registry);
                return;
            }

            Debug.LogWarning(
                $"PathWaypointSyncRegistry: null members={nullMemberCount}, "
                + $"duplicate references={duplicateWarningCount}.",
                registry);
        }

        #endregion
    }
}
#endif
