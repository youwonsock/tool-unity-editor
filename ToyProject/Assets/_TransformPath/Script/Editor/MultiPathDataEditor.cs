using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Common.TransformPath.Editor
{
    /// <summary>
    /// MultiPathData Inspector. It only lays the sections out: the segment list
    /// (<see cref="PathSegmentListDrawer"/>), movement (<see cref="PathSegmentMovementPanel"/>),
    /// drawing (<see cref="PathDrawingTools"/>) and joint links (<see cref="PathLinkEditorUtility"/>).
    /// </summary>
    [CustomEditor(typeof(MultiPathData))]
    [CanEditMultipleObjects]
    internal sealed class MultiPathDataEditor : UnityEditor.Editor
    {
        private PathSegmentListDrawer _segments;
        private readonly PathSegmentMovementPanel _movement = new PathSegmentMovementPanel();

        private void OnEnable()
        {
            Undo.undoRedoPerformed += HandleUndoRedo;
            _segments = new PathSegmentListDrawer(serializedObject, targets.Length == 1);
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= HandleUndoRedo;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var sequences = new List<MultiPathData>(PathInspectorGUI.TargetsOf<MultiPathData>(targets));
            bool authoringToolsEnabled = !Application.isPlaying;

            EditorGUI.BeginChangeCheck();
            DrawPropertiesExcluding(serializedObject, PathEditorSerializationUtility.SEGMENTS_PROPERTY);
            bool inspectorChanged = EditorGUI.EndChangeCheck();
            inspectorChanged |= _segments.Draw();
            if (_segments.Property != null)
                inspectorChanged |= PathSegmentMovementPanel.DrawSelected(serializedObject, _segments.Property, _segments.SelectedIndex);
            _movement.DrawPreset(sequences, authoringToolsEnabled);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Drawing Tools", EditorStyles.boldLabel);
            // Play Mode would drop these edits when it ends.
            EditorGUI.BeginDisabledGroup(!authoringToolsEnabled);
            if (GUILayout.Button("Apply Drawing Sizes To All Paths"))
                sequences.ForEach(PathDrawingTools.ApplySharedSizes);
            if (GUILayout.Button("Assign Distinct Path Colors"))
                sequences.ForEach(PathDrawingTools.AssignDistinctColors);
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Path Link Tools", EditorStyles.boldLabel);
            EditorGUI.BeginDisabledGroup(!authoringToolsEnabled);
            if (GUILayout.Button("Force Sync All Path Points"))
                ForceSyncAllPathPoints(sequences);
            if (GUILayout.Button("Sort Path Events (All PathData)"))
                PathInspectorGUI.SortEvents(CollectPaths(sequences), "Sort Path Events (All PathData)");
            EditorGUI.EndDisabledGroup();
            if (!authoringToolsEnabled)
                EditorGUILayout.HelpBox("Path authoring tools are disabled in Play Mode.", MessageType.Info);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Sequence Tools", EditorStyles.boldLabel);
            // The segment panel edits the paths' own movement; their builds come
            // first so the sequence reads what the panel shows.
            if (GUILayout.Button("Rebuild Sequence"))
            {
                PathInspectorGUI.Rebuild(CollectPaths(sequences));
                PathInspectorGUI.Rebuild(sequences);
            }

            inspectorChanged |= serializedObject.ApplyModifiedProperties();
            if (inspectorChanged)
                PathEditorUndoUtility.RepaintScene();
        }

        private void HandleUndoRedo() => PathEditorUndoUtility.RepaintScene();

        private static IEnumerable<PathData> CollectPaths(List<MultiPathData> sequences)
        {
            var paths = new HashSet<PathData>();
            for (int i = 0; i < sequences.Count; i++)
                paths.UnionWith(PathEditorSerializationUtility.CollectUniquePathDatas(sequences[i]));
            return paths;
        }

        private static void ForceSyncAllPathPoints(List<MultiPathData> sequences)
        {
            for (int i = 0; i < sequences.Count; i++)
            {
                MultiPathData sequence = sequences[i];
                if (PathEditorSerializationUtility.GetPathDatas(sequence).Count < 2)
                {
                    Debug.LogWarning("MultiPathData: at least two PathData are needed to link joints.", sequence);
                    continue;
                }
                int syncCount = PathLinkEditorUtility.AlignAllJoints(sequence);
                Debug.Log($"MultiPathData: synced {syncCount} path joint(s).", sequence);
            }
            PathEditorUndoUtility.RepaintScene();
        }
    }
}
