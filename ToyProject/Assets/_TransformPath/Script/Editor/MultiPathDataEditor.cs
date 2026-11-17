using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Common.TransformPath
{
    [CustomEditor(typeof(MultiPathData))]
    [CanEditMultipleObjects]
    internal sealed class MultiPathDataEditor : Editor
    {
        #region Constants

        private const float GOLDEN_RATIO_CONJUGATE = 0.618033988749895f;
        private const float PATH_COLOR_SATURATION = 0.72f;
        private const float PATH_COLOR_VALUE = 1f;
        private const float SEGMENT_ROW_PADDING = 4f;
        private const string SEGMENTS_PROPERTY = "_segments";
        private const string PROVIDER_OBJECT_PROPERTY = "_providerObject";
        private const string MOVEMENT_SOURCE_PROPERTY = "_movementSource";
        private const string MOVE_TYPE_PROPERTY = PathInspectorGUI.MOVE_TYPE;
        private const string MOVE_VALUE_PROPERTY = PathInspectorGUI.MOVE_VALUE;
        private const string TIME_CURVE_PROPERTY = PathInspectorGUI.TIME_CURVE;
        // PathData preview fields the drawing tools write.
        private const string PATH_COLOR_PROPERTY = "_pathColor";
        private const string LINE_WIDTH_PROPERTY = "_lineWidth";
        private const string POINT_SIZE_PROPERTY = "_pointSize";
        private const string SAMPLE_POINT_SIZE_PROPERTY = "_samplePointSize";
        private const string EVENT_POINT_SIZE_PROPERTY = "_eventPointSize";
        private const string PRESERVE_PROPERTY = "_preservePreviousSpeed";

        #endregion


        #region Member Variables

        private readonly HashSet<PathData> _bulkPathDataSet = new HashSet<PathData>();

        private ReorderableList _segmentsList;
        private SerializedProperty _segmentsProperty;
        private EPathMoveType _bulkMoveType = EPathMoveType.TimeBased;
        private float _bulkMoveValue = 5f;
        private AnimationCurve _bulkTimeCurve;

        #endregion


        #region Unity Events

        private void OnEnable()
        {
            Undo.undoRedoPerformed += HandleUndoRedo;
            SetupSegmentsList();
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= HandleUndoRedo;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUI.BeginChangeCheck();
            DrawPropertiesExcluding(serializedObject, SEGMENTS_PROPERTY);
            bool inspectorChanged = EditorGUI.EndChangeCheck();
            inspectorChanged |= DrawSegments();
            inspectorChanged |= DrawSelectedMovementPanel();
            bool authoringToolsEnabled = !Application.isPlaying;
            DrawMovementBulkTools(authoringToolsEnabled);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Drawing Tools", EditorStyles.boldLabel);
            if (GUILayout.Button("Apply Drawing Sizes To All Paths"))
                ForEachTarget(ApplySharedDrawingSettings);
            if (GUILayout.Button("Assign Distinct Path Colors"))
                ForEachTarget(AssignDistinctPathColors);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Path Link Tools", EditorStyles.boldLabel);
            EditorGUI.BeginDisabledGroup(!authoringToolsEnabled);
            if (GUILayout.Button("Force Sync All Path Points"))
                ForceSyncAllPathPoints();
            if (GUILayout.Button("Sort Path Events (All PathData)"))
                PathInspectorGUI.SortEvents(CollectTargetPaths(), "Sort Path Events (All PathData)");
            EditorGUI.EndDisabledGroup();
            if (!authoringToolsEnabled)
                EditorGUILayout.HelpBox(
                    "Path authoring tools are disabled in Play Mode.",
                    MessageType.Info);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Sequence Tools", EditorStyles.boldLabel);
            if (GUILayout.Button("Rebuild Sequence"))
                PathInspectorGUI.Rebuild(PathInspectorGUI.TargetsOf<MultiPathData>(targets), "Rebuild Sequence");

            inspectorChanged |= serializedObject.ApplyModifiedProperties();
            if (inspectorChanged)
                PathEditorUndoUtility.RepaintScene();
        }

        private void HandleUndoRedo() => PathEditorUndoUtility.RepaintScene();

        #endregion


        #region Private Methods

        private void SetupSegmentsList()
        {
            _segmentsProperty = serializedObject.FindProperty(SEGMENTS_PROPERTY);
            if (_segmentsProperty == null || targets.Length != 1)
            {
                _segmentsList = null;
                return;
            }

            _segmentsList = new ReorderableList(
                serializedObject,
                _segmentsProperty,
                true,
                true,
                true,
                true);
            _segmentsList.drawHeaderCallback = rect =>
                EditorGUI.LabelField(
                    rect,
                    "Segments (Provider and movement source)");
            _segmentsList.elementHeightCallback = GetSegmentElementHeight;
            _segmentsList.drawElementCallback = DrawSegmentElement;
            _segmentsList.onSelectCallback = list =>
                PathEditorUndoUtility.RepaintScene();
            _segmentsList.onAddCallback = AddSegment;
        }

        private void AddSegment(ReorderableList list)
        {
            int index = list.serializedProperty.arraySize;
            list.serializedProperty.InsertArrayElementAtIndex(Mathf.Max(0, index - 1));
            SerializedProperty element = list.serializedProperty.GetArrayElementAtIndex(
                list.serializedProperty.arraySize - 1);
            if (element != null)
            {
                if (index == 0)
                {
                    SerializedProperty provider = element.FindPropertyRelative(
                        PROVIDER_OBJECT_PROPERTY);
                    if (provider != null)
                        provider.objectReferenceValue = null;
                }
                SerializedProperty source = element.FindPropertyRelative(
                    MOVEMENT_SOURCE_PROPERTY);
                if (source != null)
                    source.enumValueIndex = (int)EPathSegmentMovementSource.Provider;
                element.FindPropertyRelative(PRESERVE_PROPERTY).boolValue = false;
            }
            list.index = list.serializedProperty.arraySize - 1;
        }

        private bool DrawSegments()
        {
            if (_segmentsProperty == null)
                return false;

            if (targets.Length == 1)
            {
                if (_segmentsList == null)
                    SetupSegmentsList();
                EditorGUI.BeginChangeCheck();
                _segmentsList?.DoLayoutList();
                return EditorGUI.EndChangeCheck();
            }

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(
                _segmentsProperty,
                new GUIContent("Segments"),
                true);
            return EditorGUI.EndChangeCheck();
        }

        private float GetSegmentElementHeight(int index)
        {
            return EditorGUIUtility.singleLineHeight * 6f
                + EditorGUIUtility.standardVerticalSpacing * 7f
                + SEGMENT_ROW_PADDING;
        }

        private void DrawSegmentElement(
            Rect rect,
            int index,
            bool isActive,
            bool isFocused)
        {
            if (_segmentsProperty == null
                || index < 0
                || index >= _segmentsProperty.arraySize)
                return;

            SerializedProperty element = _segmentsProperty.GetArrayElementAtIndex(index);
            SerializedProperty provider = element.FindPropertyRelative(
                PROVIDER_OBJECT_PROPERTY);
            SerializedProperty movementSource = element.FindPropertyRelative(
                MOVEMENT_SOURCE_PROPERTY);
            SerializedProperty moveType = element.FindPropertyRelative(MOVE_TYPE_PROPERTY);
            SerializedProperty moveValue = element.FindPropertyRelative(MOVE_VALUE_PROPERTY);
            SerializedProperty timeCurve = element.FindPropertyRelative(TIME_CURVE_PROPERTY);
            SerializedProperty preserve = element.FindPropertyRelative(PRESERVE_PROPERTY);
            float lineHeight = EditorGUIUtility.singleLineHeight;
            float spacing = EditorGUIUtility.standardVerticalSpacing;
            Rect providerRect = new Rect(
                rect.x,
                rect.y,
                rect.width,
                lineHeight);
            Rect sourceRect = new Rect(
                rect.x,
                providerRect.yMax + spacing,
                rect.width,
                lineHeight);
            Rect moveTypeRect = new Rect(
                rect.x,
                sourceRect.yMax + spacing,
                rect.width,
                lineHeight);
            Rect moveValueRect = new Rect(
                rect.x,
                moveTypeRect.yMax + spacing,
                rect.width,
                lineHeight);
            Rect curveRect = new Rect(
                rect.x,
                moveValueRect.yMax + spacing,
                rect.width,
                lineHeight);
            Rect preserveRect = new Rect(
                rect.x,
                curveRect.yMax + spacing,
                rect.width,
                lineHeight);
            if (provider != null)
                EditorGUI.PropertyField(
                    providerRect,
                    provider,
                    new GUIContent($"Provider {index}"));
            EditorGUI.PropertyField(
                sourceRect,
                movementSource,
                new GUIContent("Movement Source"));
            if (movementSource != null
                && movementSource.enumValueIndex == (int)EPathSegmentMovementSource.Override)
            {
                EditorGUI.PropertyField(moveTypeRect, moveType, new GUIContent("Mode"));
                EditorGUI.PropertyField(moveValueRect, moveValue, new GUIContent("Value"));
                if (moveType != null
                    && moveType.enumValueIndex == (int)EPathMoveType.TimeBased)
                    EditorGUI.PropertyField(
                        curveRect,
                        timeCurve,
                        new GUIContent("Time Curve"));
            }
            EditorGUI.PropertyField(
                preserveRect,
                preserve,
                new GUIContent("Preserve Previous Speed"));
        }

        private bool DrawSelectedMovementPanel()
        {
            if (targets.Length != 1 || _segmentsList == null)
                return false;
            if (_segmentsList.index < 0
                || _segmentsList.index >= _segmentsProperty.arraySize)
                return false;

            PathData pathData = PathEditorSerializationUtility.GetPathData(
                _segmentsProperty,
                _segmentsList.index);
            SerializedProperty element = _segmentsProperty.GetArrayElementAtIndex(
                _segmentsList.index);
            SerializedProperty movementSource = element.FindPropertyRelative(
                MOVEMENT_SOURCE_PROPERTY);
            EditorGUILayout.Space();
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(
                $"Selected Segment {_segmentsList.index} Movement",
                EditorStyles.boldLabel);
            if (movementSource != null
                && movementSource.enumValueIndex == (int)EPathSegmentMovementSource.Override)
            {
                bool overrideChanged = PathInspectorGUI.DrawMovement(
                    element.FindPropertyRelative(MOVE_TYPE_PROPERTY),
                    element.FindPropertyRelative(MOVE_VALUE_PROPERTY),
                    element.FindPropertyRelative(TIME_CURVE_PROPERTY));
                if (overrideChanged)
                    serializedObject.ApplyModifiedProperties();
                EditorGUILayout.EndVertical();
                return overrideChanged;
            }

            if (pathData == null)
            {
                EditorGUILayout.HelpBox(
                    "Assign a provider or choose an override movement source.",
                    MessageType.Info);
                EditorGUILayout.EndVertical();
                return false;
            }

            SerializedObject child = new SerializedObject(pathData);
            child.Update();
            bool changed = PathInspectorGUI.DrawMovement(
                child.FindProperty(MOVE_TYPE_PROPERTY),
                child.FindProperty(MOVE_VALUE_PROPERTY),
                child.FindProperty(TIME_CURVE_PROPERTY));
            if (changed)
            {
                child.ApplyModifiedProperties();
                PathEditorUndoUtility.MarkDirty(pathData);
            }
            EditorGUILayout.EndVertical();
            return changed;
        }

        private void DrawMovementBulkTools(bool enabled)
        {
            EditorGUILayout.Space();
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Movement Preset", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Editor-only bulk settings for every unique PathData in this MultiPath.",
                EditorStyles.miniLabel);
            EditorGUI.BeginDisabledGroup(!enabled);
            _bulkMoveType = (EPathMoveType)EditorGUILayout.EnumPopup(
                "Mode",
                _bulkMoveType);
            _bulkMoveValue = EditorGUILayout.FloatField(
                _bulkMoveType == EPathMoveType.SpeedBased ? "Speed" : "Duration",
                _bulkMoveValue);
            if (_bulkMoveType == EPathMoveType.TimeBased)
            {
                if (_bulkTimeCurve == null)
                    _bulkTimeCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f);
                _bulkTimeCurve = EditorGUILayout.CurveField(
                    "Time Curve",
                    _bulkTimeCurve);
            }
            if (GUILayout.Button("Apply To All Paths"))
                ApplyMovementToAllPaths();
            EditorGUI.EndDisabledGroup();
            if (!enabled)
                EditorGUILayout.HelpBox(
                    "Movement authoring is disabled in Play Mode.",
                    MessageType.Info);
            EditorGUILayout.EndVertical();
        }

        private void ApplyMovementToAllPaths()
        {
            _bulkPathDataSet.Clear();
            for (int i = 0; i < targets.Length; i++)
            {
                MultiPathData multiPathData = targets[i] as MultiPathData;
                if (multiPathData == null)
                    continue;
                List<PathData> pathDatas = PathEditorSerializationUtility.CollectUniquePathDatas(
                    multiPathData);
                for (int pathIndex = 0; pathIndex < pathDatas.Count; pathIndex++)
                    _bulkPathDataSet.Add(pathDatas[pathIndex]);
            }

            PathData[] undoTargets = new PathData[_bulkPathDataSet.Count];
            _bulkPathDataSet.CopyTo(undoTargets);
            const string undoName = "Apply movement preset to PathData";
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(undoName);
            PathEditorUndoUtility.RecordObjects(undoTargets, undoName);
            foreach (PathData pathData in _bulkPathDataSet)
            {
                SerializedObject child = new SerializedObject(pathData);
                child.Update();
                SetEnum(child, MOVE_TYPE_PROPERTY, _bulkMoveType);
                SetFloat(child, MOVE_VALUE_PROPERTY, _bulkMoveValue);
                if (_bulkMoveType == EPathMoveType.TimeBased)
                    SetCurve(child, TIME_CURVE_PROPERTY, _bulkTimeCurve);
                child.ApplyModifiedProperties();
                PathEditorUndoUtility.MarkDirty(pathData);
            }
            Undo.CollapseUndoOperations(undoGroup);
            PathEditorUndoUtility.RepaintScene();
        }

        private static void SetEnum(
            SerializedObject serializedObject,
            string propertyName,
            EPathMoveType value)
        {
            SerializedProperty property = serializedObject.FindProperty(propertyName);
            if (property != null)
                property.enumValueIndex = (int)value;
        }

        private static void SetFloat(
            SerializedObject serializedObject,
            string propertyName,
            float value)
        {
            SerializedProperty property = serializedObject.FindProperty(propertyName);
            if (property != null)
                property.floatValue = value;
        }

        private static void SetCurve(
            SerializedObject serializedObject,
            string propertyName,
            AnimationCurve value)
        {
            SerializedProperty property = serializedObject.FindProperty(propertyName);
            if (property != null)
                property.animationCurveValue = PathValueUtility.CloneCurve(value);
        }

        private void ForEachTarget(System.Action<MultiPathData> action)
        {
            foreach (MultiPathData multiPathData in PathInspectorGUI.TargetsOf<MultiPathData>(targets))
                action(multiPathData);
        }

        private IEnumerable<PathData> CollectTargetPaths()
        {
            var paths = new HashSet<PathData>();
            foreach (MultiPathData multiPathData in PathInspectorGUI.TargetsOf<MultiPathData>(targets))
                paths.UnionWith(PathEditorSerializationUtility.CollectUniquePathDatas(multiPathData));
            return paths;
        }

        // Copies this MultiPath's drawing sizes to every PathData it plays.
        private static void ApplySharedDrawingSettings(MultiPathData multiPathData)
        {
            PathPreviewStyle template = multiPathData.PreviewStyleTemplate;
            List<PathData> pathDatas = PathEditorSerializationUtility.CollectUniquePathDatas(
                multiPathData);
            UnityEngine.Object[] undoTargets = new UnityEngine.Object[pathDatas.Count];
            for (int i = 0; i < pathDatas.Count; i++)
                undoTargets[i] = pathDatas[i];
            PathEditorUndoUtility.RecordObjects(
                undoTargets,
                "Apply MultiPath drawing settings");
            for (int i = 0; i < pathDatas.Count; i++)
            {
                SerializedObject serializedPathData = new SerializedObject(pathDatas[i]);
                serializedPathData.Update();
                SetFloat(
                    serializedPathData,
                    LINE_WIDTH_PROPERTY,
                    template.LineWidth);
                SetFloat(
                    serializedPathData,
                    POINT_SIZE_PROPERTY,
                    template.PointSize);
                SetFloat(
                    serializedPathData,
                    SAMPLE_POINT_SIZE_PROPERTY,
                    template.SamplePointSize);
                SetFloat(
                    serializedPathData,
                    EVENT_POINT_SIZE_PROPERTY,
                    template.EventPointSize);
                serializedPathData.ApplyModifiedProperties();
                PathEditorUndoUtility.MarkDirty(pathDatas[i]);
            }
            PathEditorUndoUtility.RepaintScene();
        }

        private static void AssignDistinctPathColors(MultiPathData multiPathData)
        {
            List<PathData> pathDatas = PathEditorSerializationUtility.GetPathDatas(multiPathData);
            List<PathData> uniquePathDatas = PathEditorSerializationUtility.CollectUniquePathDatas(
                multiPathData);
            if (uniquePathDatas.Count == 0)
                return;

            UnityEngine.Object[] undoTargets = new UnityEngine.Object[uniquePathDatas.Count];
            for (int i = 0; i < uniquePathDatas.Count; i++)
                undoTargets[i] = uniquePathDatas[i];
            PathEditorUndoUtility.RecordObjects(undoTargets, "Assign distinct path colors");
            for (int i = 0; i < uniquePathDatas.Count; i++)
            {
                float hue = (i * GOLDEN_RATIO_CONJUGATE) % 1f;
                Color pathColor = Color.HSVToRGB(
                    hue,
                    PATH_COLOR_SATURATION,
                    PATH_COLOR_VALUE);
                SerializedObject serializedPathData = new SerializedObject(uniquePathDatas[i]);
                serializedPathData.Update();
                SerializedProperty colorProperty = serializedPathData.FindProperty(
                    PATH_COLOR_PROPERTY);
                if (colorProperty != null)
                    colorProperty.colorValue = pathColor;
                serializedPathData.ApplyModifiedProperties();
                PathEditorUndoUtility.MarkDirty(uniquePathDatas[i]);
            }

            if (pathDatas.Count != uniquePathDatas.Count)
                Debug.LogWarning(
                    "MultiPathData: 동일한 PathData가 여러 슬롯에 있어 첫 번째 슬롯의 색을 공유합니다.",
                    multiPathData);
        }

        private void ForceSyncAllPathPoints()
        {
            for (int targetIndex = 0; targetIndex < targets.Length; targetIndex++)
            {
                MultiPathData multiPathData = targets[targetIndex] as MultiPathData;
                if (multiPathData == null)
                    continue;
                List<PathData> pathDatas = PathEditorSerializationUtility.GetPathDatas(
                    multiPathData);
                if (pathDatas.Count < 2)
                {
                    Debug.LogWarning(
                        "MultiPathData: 연결할 PathData가 충분하지 않습니다.",
                        multiPathData);
                    continue;
                }

                int syncCount = PathLinkEditorUtility.SyncAllJoints(
                    multiPathData,
                    EPathLinkSyncMode.AlignStartToEnd,
                    PathLinkEditorUtility.POSITION_SYNC_EPSILON_SQR);
                Debug.Log(
                    $"MultiPathData: {syncCount}개의 경로 연결점을 동기화했습니다.",
                    multiPathData);
            }
            PathEditorUndoUtility.RepaintScene();
        }

        #endregion


    }
}
