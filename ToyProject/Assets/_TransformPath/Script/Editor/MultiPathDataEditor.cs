using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// MultiPathData Inspector. It only lays the sections out: the segment list
    /// (<see cref="PathSegmentListDrawer"/>), movement (<see cref="PathSegmentMovementPanel"/>),
    /// drawing (<see cref="PathDrawingTools"/>) and joint links (<see cref="PathLinkEditorUtility"/>).
    /// </summary>
    [CustomEditor(typeof(MultiPathData))]
    [CanEditMultipleObjects]
    internal sealed class MultiPathDataEditor : Editor
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
            if (GUILayout.Button("Apply Drawing Sizes To All Paths"))
                sequences.ForEach(PathDrawingTools.ApplySharedSizes);
            if (GUILayout.Button("Assign Distinct Path Colors"))
                sequences.ForEach(PathDrawingTools.AssignDistinctColors);

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
            if (GUILayout.Button("Rebuild Sequence"))
                PathInspectorGUI.Rebuild(sequences, "Rebuild Sequence");

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
                    Debug.LogWarning("MultiPathData: 연결할 PathData가 충분하지 않습니다.", sequence);
                    continue;
                }
                int syncCount = PathLinkEditorUtility.SyncAllJoints(
                    sequence, EPathLinkSyncMode.AlignStartToEnd, PathLinkEditorUtility.POSITION_SYNC_EPSILON_SQR);
                Debug.Log($"MultiPathData: {syncCount}개의 경로 연결점을 동기화했습니다.", sequence);
            }
            PathEditorUndoUtility.RepaintScene();
        }
    }
}
