using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Common.TransformPath.Editor
{
    /// <summary>Inspector controls and actions shared by the PathData and MultiPathData editors.</summary>
    internal static class PathInspectorGUI
    {
        public const string MOVE_TYPE = "_moveType";
        public const string MOVE_VALUE = "_moveValue";
        public const string TIME_CURVE = "_timeCurve";

        /// <summary>Mode, Speed or Duration, and a time curve when TimeBased. Returns whether anything changed.</summary>
        public static bool DrawMovement(SerializedProperty moveType, SerializedProperty moveValue, SerializedProperty timeCurve)
        {
            if (moveType == null || moveValue == null || timeCurve == null)
            {
                EditorGUILayout.HelpBox("Movement properties are unavailable.", MessageType.Warning);
                return false;
            }

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(moveType, new GUIContent("Mode"));
            bool speedBased = moveType.enumValueIndex == (int)EPathMoveType.SpeedBased;
            EditorGUILayout.PropertyField(moveValue, new GUIContent(speedBased ? "Speed" : "Duration"));
            bool changed = EditorGUI.EndChangeCheck();
            if (speedBased)
                return changed;

            if (timeCurve.animationCurveValue == null || timeCurve.animationCurveValue.length == 0)
            {
                timeCurve.animationCurveValue = AnimationCurve.Linear(0f, 0f, 1f, 1f);
                changed = true;
            }
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(timeCurve, new GUIContent("Time Curve"));
            return EditorGUI.EndChangeCheck() || changed;
        }

        /// <summary>Rebuilds every provider with one undo step each.</summary>
        public static void Rebuild(IEnumerable<PathProviderBehaviour> providers, string undoName)
        {
            foreach (PathProviderBehaviour provider in providers)
            {
                if (provider == null)
                    continue;
                PathEditorUndoUtility.Record(provider, undoName);
                provider.Rebuild();
                PathEditorUndoUtility.MarkDirty(provider);
            }
            PathEditorUndoUtility.RepaintScene();
        }

        /// <summary>Sorts and clamps the events of every path.</summary>
        public static void SortEvents(IEnumerable<PathData> paths, string undoName)
        {
            bool changed = false;
            foreach (PathData path in paths)
            {
                if (path == null)
                    continue;
                PathEditorUndoUtility.Record(path, undoName);
                if (!path.SortPathEventsByNormalizedTime())
                    continue;
                PathEditorUndoUtility.MarkDirty(path);
                changed = true;
            }
            if (changed)
                PathEditorUndoUtility.RepaintScene();
        }

        public static IEnumerable<T> TargetsOf<T>(Object[] targets) where T : Object
        {
            for (int i = 0; i < targets.Length; i++)
            {
                if (targets[i] is T target)
                    yield return target;
            }
        }
    }
}
