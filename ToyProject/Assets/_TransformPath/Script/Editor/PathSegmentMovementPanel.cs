using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Common.TransformPath.Editor
{
    /// <summary>
    /// The MultiPathData movement panels: the selected segment's movement (its
    /// override or its PathData's own) and a preset applied to every PathData.
    /// </summary>
    internal sealed class PathSegmentMovementPanel
    {
        private EPathMoveType _presetMoveType = EPathMoveType.TimeBased;
        private float _presetMoveValue = 5f;
        private AnimationCurve _presetTimeCurve;

        /// <summary>Draws the selected segment's movement. Returns whether it changed.</summary>
        public static bool DrawSelected(SerializedObject serializedObject, SerializedProperty segments, int index)
        {
            if (index < 0)
                return false;

            SerializedProperty element = segments.GetArrayElementAtIndex(index);
            SerializedProperty movementSource = element.FindPropertyRelative(PathSegmentListDrawer.MOVEMENT_SOURCE_PROPERTY);
            EditorGUILayout.Space();
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField($"Selected Segment {index} Movement", EditorStyles.boldLabel);
            bool changed = movementSource != null
                && movementSource.enumValueIndex == (int)EPathSegmentMovementSource.Override
                    ? DrawOverride(serializedObject, element)
                    : DrawPathMovement(PathEditorSerializationUtility.GetPathData(segments, index));
            EditorGUILayout.EndVertical();
            return changed;
        }

        public void DrawPreset(IReadOnlyList<MultiPathData> targets, bool enabled)
        {
            EditorGUILayout.Space();
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Movement Preset", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Editor-only bulk settings for every unique PathData in this MultiPath.", EditorStyles.miniLabel);
            EditorGUI.BeginDisabledGroup(!enabled);
            _presetMoveType = (EPathMoveType)EditorGUILayout.EnumPopup("Mode", _presetMoveType);
            _presetMoveValue = EditorGUILayout.FloatField(
                _presetMoveType == EPathMoveType.SpeedBased ? "Speed" : "Duration", _presetMoveValue);
            if (_presetMoveType == EPathMoveType.TimeBased)
            {
                if (_presetTimeCurve == null)
                    _presetTimeCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f);
                _presetTimeCurve = EditorGUILayout.CurveField("Time Curve", _presetTimeCurve);
            }
            if (GUILayout.Button("Apply To All Paths"))
                ApplyPreset(targets);
            EditorGUI.EndDisabledGroup();
            if (!enabled)
                EditorGUILayout.HelpBox("Movement authoring is disabled in Play Mode.", MessageType.Info);
            EditorGUILayout.EndVertical();
        }

        private static bool DrawOverride(SerializedObject serializedObject, SerializedProperty element)
        {
            bool changed = PathInspectorGUI.DrawMovement(
                element.FindPropertyRelative(PathInspectorGUI.MOVE_TYPE),
                element.FindPropertyRelative(PathInspectorGUI.MOVE_VALUE),
                element.FindPropertyRelative(PathInspectorGUI.TIME_CURVE));
            if (changed)
                serializedObject.ApplyModifiedProperties();
            return changed;
        }

        private static bool DrawPathMovement(PathData pathData)
        {
            if (pathData == null)
            {
                EditorGUILayout.HelpBox("Assign a PathData or choose the Override movement source.", MessageType.Info);
                return false;
            }

            var child = new SerializedObject(pathData);
            child.Update();
            bool changed = PathInspectorGUI.DrawMovement(
                child.FindProperty(PathInspectorGUI.MOVE_TYPE),
                child.FindProperty(PathInspectorGUI.MOVE_VALUE),
                child.FindProperty(PathInspectorGUI.TIME_CURVE));
            if (changed)
            {
                child.ApplyModifiedProperties();
                PathEditorUndoUtility.MarkDirty(pathData);
            }
            return changed;
        }

        private void ApplyPreset(IReadOnlyList<MultiPathData> targets)
        {
            var paths = new HashSet<PathData>();
            for (int i = 0; i < targets.Count; i++)
                paths.UnionWith(PathEditorSerializationUtility.CollectUniquePathDatas(targets[i]));

            var undoTargets = new PathData[paths.Count];
            paths.CopyTo(undoTargets);
            const string undoName = "Apply movement preset to PathData";
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(undoName);
            PathEditorUndoUtility.RecordObjects(undoTargets, undoName);
            foreach (PathData pathData in paths)
            {
                var child = new SerializedObject(pathData);
                child.Update();
                PathEditorSerializationUtility.SetEnumIndex(child, PathInspectorGUI.MOVE_TYPE, (int)_presetMoveType);
                PathEditorSerializationUtility.SetFloat(child, PathInspectorGUI.MOVE_VALUE, _presetMoveValue);
                if (_presetMoveType == EPathMoveType.TimeBased)
                    PathEditorSerializationUtility.SetCurve(child, PathInspectorGUI.TIME_CURVE, _presetTimeCurve);
                child.ApplyModifiedProperties();
                PathEditorUndoUtility.MarkDirty(pathData);
            }
            Undo.CollapseUndoOperations(undoGroup);
            PathEditorUndoUtility.RepaintScene();
        }
    }
}
