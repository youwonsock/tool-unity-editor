using UnityEditor;
using UnityEngine;

namespace Common.TransformPath.Editor
{
    /// <summary>Inspector of every PathShapeProvider: the shape's own fields, then the shared Movement section.</summary>
    [CustomEditor(typeof(PathShapeProvider), true)]
    [CanEditMultipleObjects]
    internal sealed class PathShapeProviderEditor : UnityEditor.Editor
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
            inspectorChanged |= PathInspectorGUI.DrawMovementSection(serializedObject);
            inspectorChanged |= serializedObject.ApplyModifiedProperties();
            if (inspectorChanged)
                PathEditorUndoUtility.RepaintScene();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Shape Tools", EditorStyles.boldLabel);
            if (GUILayout.Button("Rebuild Runtime Path"))
                PathInspectorGUI.Rebuild(PathInspectorGUI.TargetsOf<PathShapeProvider>(targets));
            EditorGUILayout.HelpBox(
                "The Scene View previews the current settings in Edit Mode. "
                + "In Play Mode it shows the published shape: rebuild after changing what the shape reads.",
                MessageType.Info);
        }
    }
}
