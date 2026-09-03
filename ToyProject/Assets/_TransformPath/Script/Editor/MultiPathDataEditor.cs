#if UNITY_EDITOR
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
        private static readonly HashSet<string> DUPLICATE_WARNINGS =
            new HashSet<string>();

        private const string SEGMENTS_PROPERTY = "_segments";
        private const string PATH_DATA_PROPERTY = "_pathData";
        private const string PRESERVE_PROPERTY = "_preservePreviousSpeed";

        #endregion


        #region Inner Classes / Structs

        private readonly struct DrawingTemplate
        {
            public float LineWidth { get; }
            public float PointSize { get; }
            public float SamplePointSize { get; }
            public float EventPointSize { get; }

            public DrawingTemplate(
                float lineWidth,
                float pointSize,
                float samplePointSize,
                float eventPointSize)
            {
                LineWidth = lineWidth;
                PointSize = pointSize;
                SamplePointSize = samplePointSize;
                EventPointSize = eventPointSize;
            }
        }

        #endregion


        #region Member Variables

        private readonly Dictionary<int, string> _pathConfigsSignatureByTargetId =
            new Dictionary<int, string>();
        private readonly Dictionary<int, DrawingTemplate> _drawingTemplateByTargetId =
            new Dictionary<int, DrawingTemplate>();
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
            CacheInspectorSignatures();
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
            EditorGUILayout.HelpBox(
                "MultiPath drawing settings are applied to every referenced PathData. "
                + "Path references and order changes assign distinct preview colors.",
                MessageType.Info);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Path Link Tools", EditorStyles.boldLabel);
            EditorGUI.BeginDisabledGroup(!authoringToolsEnabled);
            if (GUILayout.Button("Force Sync All Path Points"))
                ForceSyncAllPathPoints();
            if (GUILayout.Button("Sort Path Events (All PathData)"))
                SortAllPathEventsForMultiPath();
            EditorGUI.EndDisabledGroup();
            if (!authoringToolsEnabled)
                EditorGUILayout.HelpBox(
                    "Path authoring tools are disabled in Play Mode.",
                    MessageType.Info);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Sequence Tools", EditorStyles.boldLabel);
            if (GUILayout.Button("Rebuild Sequence"))
                RebuildSequences();

            inspectorChanged |= serializedObject.ApplyModifiedProperties();
            if (inspectorChanged)
                PathEditorUndoUtility.RepaintScene();
            SyncDrawingAndColorsAfterInspectorEdit();
        }

        private void HandleUndoRedo()
        {
            _pathConfigsSignatureByTargetId.Clear();
            _drawingTemplateByTargetId.Clear();
            CacheInspectorSignatures();
            PathEditorUndoUtility.RepaintScene();
        }

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
                    "Segments (PathData owns movement settings)");
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
                    element.FindPropertyRelative(PATH_DATA_PROPERTY).objectReferenceValue = null;
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
            return EditorGUIUtility.singleLineHeight * 2f
                + EditorGUIUtility.standardVerticalSpacing * 3f
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
            SerializedProperty pathData = element.FindPropertyRelative(PATH_DATA_PROPERTY);
            SerializedProperty preserve = element.FindPropertyRelative(PRESERVE_PROPERTY);
            float lineHeight = EditorGUIUtility.singleLineHeight;
            float spacing = EditorGUIUtility.standardVerticalSpacing;
            Rect pathRect = new Rect(
                rect.x,
                rect.y + 2f,
                rect.width,
                lineHeight);
            Rect preserveRect = new Rect(
                rect.x,
                pathRect.yMax + spacing,
                rect.width,
                lineHeight);
            EditorGUI.PropertyField(pathRect, pathData, new GUIContent($"PathData {index}"));
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
            EditorGUILayout.Space();
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(
                $"Selected Segment {_segmentsList.index} Movement",
                EditorStyles.boldLabel);
            if (pathData == null)
            {
                EditorGUILayout.HelpBox(
                    "Assign a PathData to edit its movement settings.",
                    MessageType.Info);
                EditorGUILayout.EndVertical();
                return false;
            }

            SerializedObject child = new SerializedObject(pathData);
            child.Update();
            SerializedProperty moveType = child.FindProperty("_moveType");
            SerializedProperty moveValue = child.FindProperty("_moveValue");
            SerializedProperty timeCurve = child.FindProperty("_timeCurve");
            if (moveType == null || moveValue == null || timeCurve == null)
            {
                EditorGUILayout.HelpBox(
                    "PathData movement properties are unavailable.",
                    MessageType.Warning);
                EditorGUILayout.EndVertical();
                return false;
            }

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(moveType, new GUIContent("Mode"));
            EditorGUILayout.PropertyField(
                moveValue,
                new GUIContent(
                    moveType.enumValueIndex == (int)EPathMoveType.SpeedBased
                        ? "Speed"
                        : "Duration"));
            if (moveType.enumValueIndex == (int)EPathMoveType.TimeBased)
            {
                if (timeCurve.animationCurveValue == null
                    || timeCurve.animationCurveValue.length == 0)
                    timeCurve.animationCurveValue = AnimationCurve.Linear(0f, 0f, 1f, 1f);
                EditorGUILayout.PropertyField(timeCurve, new GUIContent("Time Curve"));
            }
            bool changed = EditorGUI.EndChangeCheck();
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
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Apply movement preset to PathData");
            PathEditorUndoUtility.RecordObjects(
                undoTargets,
                "Apply movement preset to PathData");
            foreach (PathData pathData in _bulkPathDataSet)
            {
                SerializedObject child = new SerializedObject(pathData);
                child.Update();
                SetEnum(child, "_moveType", _bulkMoveType);
                SetFloat(child, "_moveValue", _bulkMoveValue);
                if (_bulkMoveType == EPathMoveType.TimeBased)
                    SetCurve(child, "_timeCurve", _bulkTimeCurve);
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
                property.animationCurveValue = PathMovementSettingsUtility.CloneCurve(value);
        }

        private void CacheInspectorSignatures()
        {
            for (int i = 0; i < targets.Length; i++)
            {
                MultiPathData multiPathData = targets[i] as MultiPathData;
                if (multiPathData == null)
                    continue;
                int id = multiPathData.GetInstanceID();
                _pathConfigsSignatureByTargetId[id] =
                    PathEditorSerializationUtility.BuildPathConfigsSignature(multiPathData);
                _drawingTemplateByTargetId[id] = ReadDrawingTemplate(multiPathData);
            }
        }

        private void SyncDrawingAndColorsAfterInspectorEdit()
        {
            for (int i = 0; i < targets.Length; i++)
            {
                MultiPathData multiPathData = targets[i] as MultiPathData;
                if (multiPathData == null)
                    continue;

                int id = multiPathData.GetInstanceID();
                DrawingTemplate currentTemplate = ReadDrawingTemplate(multiPathData);
                if (!_drawingTemplateByTargetId.TryGetValue(
                        id,
                        out DrawingTemplate previousTemplate))
                    _drawingTemplateByTargetId[id] = currentTemplate;
                else if (!AreSameTemplate(currentTemplate, previousTemplate))
                {
                    ApplySharedDrawingSettings(multiPathData, currentTemplate);
                    _drawingTemplateByTargetId[id] = currentTemplate;
                }

                string currentSignature = PathEditorSerializationUtility.BuildPathConfigsSignature(
                    multiPathData);
                if (!_pathConfigsSignatureByTargetId.TryGetValue(
                        id,
                        out string previousSignature))
                    _pathConfigsSignatureByTargetId[id] = currentSignature;
                else if (!string.Equals(
                             currentSignature,
                             previousSignature,
                             StringComparison.Ordinal))
                {
                    _pathConfigsSignatureByTargetId[id] = currentSignature;
                    AssignDistinctPathColors(multiPathData);
                    PathEditorUndoUtility.RepaintScene();
                }
            }
        }

        private static DrawingTemplate ReadDrawingTemplate(MultiPathData multiPathData)
        {
            SerializedObject serializedMultiPath = new SerializedObject(multiPathData);
            return new DrawingTemplate(
                ReadFloat(serializedMultiPath, "_multiPathLineWidth", 2f),
                ReadFloat(serializedMultiPath, "_multiPathPointSize", 0.1f),
                ReadFloat(serializedMultiPath, "_multiPathSamplePointSize", 0f),
                ReadFloat(serializedMultiPath, "_multiPathEventPointSize", 0.15f));
        }

        private static float ReadFloat(
            SerializedObject serializedObject,
            string propertyName,
            float fallback)
        {
            SerializedProperty property = serializedObject.FindProperty(propertyName);
            return property == null ? fallback : property.floatValue;
        }

        private static bool AreSameTemplate(
            DrawingTemplate left,
            DrawingTemplate right)
        {
            return Mathf.Approximately(left.LineWidth, right.LineWidth)
                && Mathf.Approximately(left.PointSize, right.PointSize)
                && Mathf.Approximately(left.SamplePointSize, right.SamplePointSize)
                && Mathf.Approximately(left.EventPointSize, right.EventPointSize);
        }

        private static void ApplySharedDrawingSettings(
            MultiPathData multiPathData,
            DrawingTemplate template)
        {
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
                    PathDataEditorProperties.LINE_WIDTH,
                    template.LineWidth);
                SetFloat(
                    serializedPathData,
                    PathDataEditorProperties.POINT_SIZE,
                    template.PointSize);
                SetFloat(
                    serializedPathData,
                    PathDataEditorProperties.SAMPLE_POINT_SIZE,
                    template.SamplePointSize);
                SetFloat(
                    serializedPathData,
                    PathDataEditorProperties.EVENT_POINT_SIZE,
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
                    PathDataEditorProperties.PATH_COLOR);
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

                int syncCount = 0;
                for (int i = 1; i < pathDatas.Count; i++)
                {
                    PathData previous = pathDatas[i - 1];
                    PathData current = pathDatas[i];
                    if (previous == null || current == null)
                        continue;
                    if (previous == current)
                    {
                        WarnDuplicateLink(multiPathData, i);
                        continue;
                    }

                    List<Transform> previousPoints = PathEditorSerializationUtility.GetPathPoints(
                        previous);
                    List<Transform> currentPoints = PathEditorSerializationUtility.GetPathPoints(
                        current);
                    if (previousPoints.Count == 0 || currentPoints.Count == 0)
                        continue;
                    Transform previousEnd = previousPoints[previousPoints.Count - 1];
                    Transform currentStart = currentPoints[0];
                    if (previousEnd == null || currentStart == null)
                        continue;

                    PathEditorUndoUtility.Record(currentStart, "Sync Path Points");
                    currentStart.position = previousEnd.position;
                    PathEditorUndoUtility.MarkDirty(currentStart);
                    PathEditorUndoUtility.MarkDirty(current);
                    syncCount++;
                }

                Debug.Log(
                    $"MultiPathData: {syncCount}개의 경로 연결점을 동기화했습니다.",
                    multiPathData);
            }
            PathEditorUndoUtility.RepaintScene();
        }

        private void SortAllPathEventsForMultiPath()
        {
            for (int i = 0; i < targets.Length; i++)
            {
                MultiPathData multiPathData = targets[i] as MultiPathData;
                if (multiPathData == null)
                    continue;
                List<PathData> pathDatas = PathEditorSerializationUtility.CollectUniquePathDatas(
                    multiPathData);
                bool changed = false;
                for (int pathIndex = 0; pathIndex < pathDatas.Count; pathIndex++)
                {
                    PathData pathData = pathDatas[pathIndex];
                    PathEditorUndoUtility.Record(
                        pathData,
                        "Sort Path Events (All PathData)");
                    if (!pathData.SortPathEventsByNormalizedTime())
                        continue;
                    changed = true;
                    PathEditorUndoUtility.MarkDirty(pathData);
                }
                if (changed)
                    PathEditorUndoUtility.RepaintScene();
            }
        }

        private void RebuildSequences()
        {
            for (int i = 0; i < targets.Length; i++)
            {
                MultiPathData multiPathData = targets[i] as MultiPathData;
                if (multiPathData == null)
                    continue;
                PathEditorUndoUtility.Record(multiPathData, "Rebuild Sequence");
                multiPathData.Rebuild();
                PathEditorUndoUtility.MarkDirty(multiPathData);
            }
        }

        private static void WarnDuplicateLink(
            MultiPathData multiPathData,
            int segmentIndex)
        {
            string key = $"{multiPathData.GetInstanceID()}:{segmentIndex}";
            if (!DUPLICATE_WARNINGS.Add(key))
                return;
            Debug.LogWarning(
                $"MultiPathData: segment {segmentIndex - 1}와 {segmentIndex}가 동일한 PathData를 참조해 자동 연결을 건너뜁니다.",
                multiPathData);
        }

        #endregion


    }
}
#endif
