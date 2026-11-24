using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Common.TransformPath.Editor
{
    /// <summary>
    /// The MultiPathData segment list: a reorderable list for one target, a plain field for several.
    /// An Override segment's movement is edited in the selected-segment panel.
    /// </summary>
    internal sealed class PathSegmentListDrawer
    {
        public const string MOVEMENT_SOURCE_PROPERTY = "_movementSource";
        private const string PRESERVE_PROPERTY = "_preservePreviousSpeed";
        private const float ROW_PADDING = 4f;
        private const int ROWS = 3;

        private readonly SerializedObject _serializedObject;
        private readonly bool _singleTarget;
        private ReorderableList _list;

        public PathSegmentListDrawer(SerializedObject serializedObject, bool singleTarget)
        {
            _serializedObject = serializedObject;
            _singleTarget = singleTarget;
            Property = serializedObject.FindProperty(PathEditorSerializationUtility.SEGMENTS_PROPERTY);
            if (Property != null && singleTarget)
                _list = CreateList();
        }

        public SerializedProperty Property { get; }

        /// <summary>The selected segment, or -1. Only a single target has a selection.</summary>
        public int SelectedIndex =>
            _list != null && _list.index >= 0 && _list.index < Property.arraySize ? _list.index : -1;

        public bool Draw()
        {
            if (Property == null)
                return false;
            EditorGUI.BeginChangeCheck();
            if (_singleTarget)
                _list?.DoLayoutList();
            else
                EditorGUILayout.PropertyField(Property, new GUIContent("Segments"), true);
            return EditorGUI.EndChangeCheck();
        }

        private ReorderableList CreateList()
        {
            var list = new ReorderableList(_serializedObject, Property, true, true, true, true);
            list.drawHeaderCallback = rect => EditorGUI.LabelField(rect, "Segments (PathData and movement source)");
            list.elementHeightCallback = _ =>
                EditorGUIUtility.singleLineHeight * ROWS
                + EditorGUIUtility.standardVerticalSpacing * (ROWS + 1)
                + ROW_PADDING;
            list.drawElementCallback = DrawElement;
            list.onSelectCallback = _ => PathEditorUndoUtility.RepaintScene();
            list.onAddCallback = AddSegment;
            return list;
        }

        // A new segment copies the last one, then starts with its PathData's own movement.
        private static void AddSegment(ReorderableList list)
        {
            SerializedProperty segments = list.serializedProperty;
            int index = segments.arraySize;
            segments.InsertArrayElementAtIndex(Mathf.Max(0, index - 1));
            SerializedProperty element = segments.GetArrayElementAtIndex(segments.arraySize - 1);
            if (element != null)
            {
                if (index == 0)
                {
                    SerializedProperty path = element.FindPropertyRelative(PathEditorSerializationUtility.PATH_PROPERTY);
                    if (path != null)
                        path.objectReferenceValue = null;
                }
                SerializedProperty source = element.FindPropertyRelative(MOVEMENT_SOURCE_PROPERTY);
                if (source != null)
                    source.enumValueIndex = (int)EPathSegmentMovementSource.Path;
                element.FindPropertyRelative(PRESERVE_PROPERTY).boolValue = false;
            }
            list.index = segments.arraySize - 1;
        }

        // An Override without a usable value, such as an unset 0, starts as the
        // path's own movement. A value set before is kept.
        private static void SeedOverride(SerializedProperty element, PathData path)
        {
            SerializedProperty value = element.FindPropertyRelative(PathInspectorGUI.MOVE_VALUE);
            if (path == null || value == null || value.floatValue >= PathConstants.MIN_MOVE_VALUE)
                return;
            var own = new SerializedObject(path);
            element.FindPropertyRelative(PathInspectorGUI.MOVE_TYPE).enumValueIndex = own.FindProperty(PathInspectorGUI.MOVE_TYPE).enumValueIndex;
            value.floatValue = own.FindProperty(PathInspectorGUI.MOVE_VALUE).floatValue;
            element.FindPropertyRelative(PathInspectorGUI.TIME_CURVE).animationCurveValue =
                own.FindProperty(PathInspectorGUI.TIME_CURVE).animationCurveValue;
        }

        private void DrawElement(Rect rect, int index, bool isActive, bool isFocused)
        {
            if (index < 0 || index >= Property.arraySize)
                return;

            SerializedProperty element = Property.GetArrayElementAtIndex(index);
            SerializedProperty path = element.FindPropertyRelative(PathEditorSerializationUtility.PATH_PROPERTY);
            float step = EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
            Rect Row(int row) => new Rect(rect.x, rect.y + row * step, rect.width, EditorGUIUtility.singleLineHeight);

            EditorGUI.PropertyField(Row(0), path, new GUIContent($"Path {index}"));
            SerializedProperty source = element.FindPropertyRelative(MOVEMENT_SOURCE_PROPERTY);
            EditorGUI.BeginChangeCheck();
            EditorGUI.PropertyField(Row(1), source, new GUIContent("Movement Source"));
            if (EditorGUI.EndChangeCheck() && source.enumValueIndex == (int)EPathSegmentMovementSource.Override)
                SeedOverride(element, path.objectReferenceValue as PathData);
            EditorGUI.PropertyField(Row(2), element.FindPropertyRelative(PRESERVE_PROPERTY), new GUIContent("Preserve Previous Speed"));
        }
    }
}
