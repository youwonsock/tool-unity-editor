using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>The MultiPathData segment list: a reorderable list for one target, a plain field for several.</summary>
    internal sealed class PathSegmentListDrawer
    {
        public const string MOVEMENT_SOURCE_PROPERTY = "_movementSource";
        private const string PRESERVE_PROPERTY = "_preservePreviousSpeed";
        private const float ROW_PADDING = 4f;
        private const int ROWS = 6;

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
            list.drawHeaderCallback = rect => EditorGUI.LabelField(rect, "Segments (Provider and movement source)");
            list.elementHeightCallback = _ =>
                EditorGUIUtility.singleLineHeight * ROWS
                + EditorGUIUtility.standardVerticalSpacing * (ROWS + 1)
                + ROW_PADDING;
            list.drawElementCallback = DrawElement;
            list.onSelectCallback = _ => PathEditorUndoUtility.RepaintScene();
            list.onAddCallback = AddSegment;
            return list;
        }

        // A new segment copies the last one, then starts with the provider's own movement.
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
                    SerializedProperty provider = element.FindPropertyRelative(PathEditorSerializationUtility.PROVIDER_OBJECT_PROPERTY);
                    if (provider != null)
                        provider.objectReferenceValue = null;
                }
                SerializedProperty source = element.FindPropertyRelative(MOVEMENT_SOURCE_PROPERTY);
                if (source != null)
                    source.enumValueIndex = (int)EPathSegmentMovementSource.Provider;
                element.FindPropertyRelative(PRESERVE_PROPERTY).boolValue = false;
            }
            list.index = segments.arraySize - 1;
        }

        private void DrawElement(Rect rect, int index, bool isActive, bool isFocused)
        {
            if (index < 0 || index >= Property.arraySize)
                return;

            SerializedProperty element = Property.GetArrayElementAtIndex(index);
            SerializedProperty provider = element.FindPropertyRelative(PathEditorSerializationUtility.PROVIDER_OBJECT_PROPERTY);
            SerializedProperty movementSource = element.FindPropertyRelative(MOVEMENT_SOURCE_PROPERTY);
            SerializedProperty moveType = element.FindPropertyRelative(PathInspectorGUI.MOVE_TYPE);
            float step = EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
            Rect Row(int row) => new Rect(rect.x, rect.y + row * step, rect.width, EditorGUIUtility.singleLineHeight);

            if (provider != null)
                EditorGUI.PropertyField(Row(0), provider, new GUIContent($"Provider {index}"));
            EditorGUI.PropertyField(Row(1), movementSource, new GUIContent("Movement Source"));
            if (movementSource != null && movementSource.enumValueIndex == (int)EPathSegmentMovementSource.Override)
            {
                EditorGUI.PropertyField(Row(2), moveType, new GUIContent("Mode"));
                EditorGUI.PropertyField(Row(3), element.FindPropertyRelative(PathInspectorGUI.MOVE_VALUE), new GUIContent("Value"));
                if (moveType != null && moveType.enumValueIndex == (int)EPathMoveType.TimeBased)
                    EditorGUI.PropertyField(Row(4), element.FindPropertyRelative(PathInspectorGUI.TIME_CURVE), new GUIContent("Time Curve"));
            }
            EditorGUI.PropertyField(Row(5), element.FindPropertyRelative(PRESERVE_PROPERTY), new GUIContent("Preserve Previous Speed"));
        }
    }
}
