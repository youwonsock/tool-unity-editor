using System.Collections.Generic;
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

        // What one target's segments were before the plain field drew them.
        private readonly struct Snapshot
        {
            public readonly MultiPathData Target;
            public readonly PathData[] Paths;
            public readonly bool[] Overrides;
            // Preserve flag, move type and value: an append leaves them all as they were.
            public readonly (bool Preserve, int MoveType, float MoveValue)[] Values;
            public readonly AnimationCurve[] Curves;

            public Snapshot(MultiPathData target)
            {
                Target = target;
                using var serialized = new SerializedObject(target);
                SerializedProperty segments = serialized.FindProperty(PathEditorSerializationUtility.SEGMENTS_PROPERTY);
                int count = segments.arraySize;
                Paths = new PathData[count];
                Overrides = new bool[count];
                Values = new (bool, int, float)[count];
                Curves = new AnimationCurve[count];
                for (int i = 0; i < count; i++)
                {
                    SerializedProperty element = segments.GetArrayElementAtIndex(i);
                    Paths[i] = element.FindPropertyRelative(PathEditorSerializationUtility.PATH_PROPERTY).objectReferenceValue as PathData;
                    Overrides[i] = IsOverride(element);
                    Values[i] = ValuesOf(element);
                    Curves[i] = CurveOf(element);
                }
            }
        }

        private readonly SerializedObject _serializedObject;
        private readonly bool _singleTarget;
        private readonly List<Snapshot> _snapshots = new List<Snapshot>();
        private bool _fixUpTargets;
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
            {
                // Layout and Repaint never edit, so the snapshot is taken only for the events that can.
                EventType type = Event.current.type;
                if (type != EventType.Layout && type != EventType.Repaint)
                    CaptureTargets();
                EditorGUILayout.PropertyField(Property, new GUIContent("Segments"), true);
            }
            bool changed = EditorGUI.EndChangeCheck();
            if (changed && !_singleTarget)
                MarkTargetsEdited();
            return changed;
        }

        /// <summary>The plain field changed the targets since <see cref="CaptureTargets"/>.</summary>
        internal void MarkTargetsEdited() => _fixUpTargets = true;

        /// <summary>
        /// Several targets get Unity's plain field, which neither starts new segments
        /// nor seeds an Override as the single-target list does. After the Inspector
        /// applied the field's change, each target gets what the list would have done.
        /// </summary>
        public void ApplyListDefaultsToTargets()
        {
            if (!_fixUpTargets)
                return;
            _fixUpTargets = false;
            for (int t = 0; t < _snapshots.Count; t++)
            {
                Snapshot before = _snapshots[t];
                if (before.Target == null)
                    continue;
                using var serialized = new SerializedObject(before.Target);
                SerializedProperty segments = serialized.FindProperty(PathEditorSerializationUtility.SEGMENTS_PROPERTY);
                int count = segments.arraySize;
                int known = before.Paths.Length;
                // Appended segments only: a duplicate in the middle shifts the old ones
                // down, so one of them reads as changed. A copy inserted before an
                // identical segment looks the same as an append.
                if (count > known && Compare(segments, before, known, out int sources, out _, out bool values)
                    && sources == 0 && !values)
                {
                    for (int i = known; i < count; i++)
                        StartNewSegment(segments.GetArrayElementAtIndex(i), zeroed: known == 0);
                }
                // One segment switching over. A reorder or a delete moves an Override; it does not choose one.
                else if (count == known && Compare(segments, before, count, out sources, out int switched, out _)
                         && sources == 1 && switched >= 0)
                {
                    SerializedProperty element = segments.GetArrayElementAtIndex(switched);
                    SeedOverride(element, element.FindPropertyRelative(PathEditorSerializationUtility.PATH_PROPERTY).objectReferenceValue as PathData);
                }
                serialized.ApplyModifiedProperties();
            }
            _snapshots.Clear();
        }

        // Compares the first count segments with the snapshot. False if one of them
        // has another path. Otherwise sources counts the segments whose movement source
        // changed, switched is the one that turned to Override when exactly one did
        // (else -1), and values tells whether a preserve flag, move type or value changed.
        private static bool Compare(SerializedProperty segments, Snapshot before, int count,
            out int sources, out int switched, out bool values)
        {
            sources = 0;
            switched = -1;
            values = false;
            for (int i = 0; i < count; i++)
            {
                SerializedProperty element = segments.GetArrayElementAtIndex(i);
                if (element.FindPropertyRelative(PathEditorSerializationUtility.PATH_PROPERTY).objectReferenceValue != before.Paths[i])
                    return false;
                values |= ValuesOf(element) != before.Values[i] || !SameCurve(CurveOf(element), before.Curves[i]);
                bool isOverride = IsOverride(element);
                if (isOverride == before.Overrides[i])
                    continue;
                sources++;
                if (isOverride)
                    switched = i;
            }
            if (sources > 1)
                switched = -1;
            return true;
        }

        private static bool IsOverride(SerializedProperty element) =>
            element.FindPropertyRelative(MOVEMENT_SOURCE_PROPERTY).enumValueIndex == (int)EPathSegmentMovementSource.Override;

        private static AnimationCurve CurveOf(SerializedProperty element) =>
            element.FindPropertyRelative(PathInspectorGUI.TIME_CURVE).animationCurveValue;

        private static bool SameCurve(AnimationCurve left, AnimationCurve right)
        {
            int count = left == null ? 0 : left.length;
            if (count != (right == null ? 0 : right.length))
                return false;
            for (int i = 0; i < count; i++)
            {
                Keyframe a = left[i];
                Keyframe b = right[i];
                if (a.time != b.time || a.value != b.value || a.inTangent != b.inTangent || a.outTangent != b.outTangent
                    || a.weightedMode != b.weightedMode || a.inWeight != b.inWeight || a.outWeight != b.outWeight)
                    return false;
            }
            return true;
        }

        private static (bool, int, float) ValuesOf(SerializedProperty element) => (
            element.FindPropertyRelative(PRESERVE_PROPERTY).boolValue,
            element.FindPropertyRelative(PathInspectorGUI.MOVE_TYPE).enumValueIndex,
            element.FindPropertyRelative(PathInspectorGUI.MOVE_VALUE).floatValue);

        /// <summary>Remembers each target's segments before the plain field edits them.</summary>
        internal void CaptureTargets()
        {
            _snapshots.Clear();
            Object[] targets = _serializedObject.targetObjects;
            for (int i = 0; i < targets.Length; i++)
            {
                if (targets[i] is MultiPathData target)
                    _snapshots.Add(new Snapshot(target));
            }
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
                StartNewSegment(element, zeroed: index == 0);
            list.index = segments.arraySize - 1;
        }

        // A first element is zeroed rather than built, and a zero Override value
        // would fail the build once the segment switches to Override. A later one
        // copies the segment before it but starts with its PathData's own movement.
        private static void StartNewSegment(SerializedProperty element, bool zeroed)
        {
            if (zeroed)
            {
                element.FindPropertyRelative(PathEditorSerializationUtility.PATH_PROPERTY).objectReferenceValue = null;
                element.FindPropertyRelative(PathInspectorGUI.MOVE_VALUE).floatValue = PathSegmentAuthoring.DEFAULT_OVERRIDE_VALUE;
            }
            element.FindPropertyRelative(MOVEMENT_SOURCE_PROPERTY).enumValueIndex = (int)EPathSegmentMovementSource.Path;
            element.FindPropertyRelative(PRESERVE_PROPERTY).boolValue = false;
        }

        // An Override starts as the movement the segment had a moment ago: its
        // path's own. The hidden values from an earlier Override are replaced.
        private static void SeedOverride(SerializedProperty element, PathData path)
        {
            if (path == null)
                return;
            using var own = new SerializedObject(path);
            element.FindPropertyRelative(PathInspectorGUI.MOVE_TYPE).enumValueIndex = own.FindProperty(PathInspectorGUI.MOVE_TYPE).enumValueIndex;
            element.FindPropertyRelative(PathInspectorGUI.MOVE_VALUE).floatValue = own.FindProperty(PathInspectorGUI.MOVE_VALUE).floatValue;
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
