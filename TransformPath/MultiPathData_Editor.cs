#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEditor;

namespace Supercent.Common.TransformPath
{
    public partial class MultiPathData
    {
        #region Member Variables

        private const float DEFAULT_MULTI_PATH_POINT_SIZE = 0.1f;

        [Header("Editor Only")]
        [SerializeField] private bool _autoLinkPathPoints = true;

        [Header("MultiPath → all PathData drawing")]
        [SerializeField] [Range(0.1f, 20f)] private float _multiPathLineWidth = 2f;
        [SerializeField] [Range(0f, 1f)] private float _multiPathPointSize = DEFAULT_MULTI_PATH_POINT_SIZE;
        [SerializeField] [Range(0f, 1f)] private float _multiPathSamplePointSize = 0f;
        [SerializeField] [Range(0f, 1f)] private float _multiPathEventPointSize = 0.15f;

        public bool AutoLinkPathPoints => _autoLinkPathPoints;
        public Vector3[][] LastPathPointPositions { get; set; }

        public IReadOnlyList<Transform> GetPathPoints(PathData pathData)
        {
            if (pathData == null)
                return null;

            return pathData.EditorPathPoints;
        }

        #endregion


        #region Inner Classes / Structs

        [InitializeOnLoad]
        private static class MultiPathDataAutoLinker
        {
            private const float SYNC_INTERVAL = 0.05f;
            private const float POSITION_SYNC_EPSILON_SQR = 0.000001f;

            private static double _lastSyncTime;
            private static readonly Dictionary<MultiPathData, Vector3[][]> _cachedPositions = new Dictionary<MultiPathData, Vector3[][]>();

            static MultiPathDataAutoLinker()
            {
                EditorApplication.update += OnGlobalEditorUpdate;
            }

            private static void OnGlobalEditorUpdate()
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode)
                    return;

                if (EditorApplication.timeSinceStartup - _lastSyncTime < SYNC_INTERVAL)
                    return;

                _lastSyncTime = EditorApplication.timeSinceStartup;

                MultiPathData[] allMultiPathData = GameObject.FindObjectsByType<MultiPathData>(FindObjectsSortMode.None);
                foreach (var multiPathData in allMultiPathData)
                {
                    if (multiPathData == null || !multiPathData.AutoLinkPathPoints)
                        continue;

                    SyncPathPoints(multiPathData);
                }
            }

            private static void SyncPathPoints(MultiPathData multiPathData)
            {
                if (multiPathData._pathDataConfigs == null || multiPathData._pathDataConfigs.Count < 2)
                    return;

                if (!_cachedPositions.TryGetValue(multiPathData, out var lastPositions))
                {
                    lastPositions = CapturePositions(multiPathData);
                    _cachedPositions[multiPathData] = lastPositions;
                    return;
                }

                if (lastPositions == null || lastPositions.Length != multiPathData._pathDataConfigs.Count)
                {
                    _cachedPositions[multiPathData] = CapturePositions(multiPathData);
                    return;
                }

                bool hasSyncedAnyPoint = false;

                for (int i = 1; i < multiPathData._pathDataConfigs.Count; i++)
                {
                    var currentConfig = multiPathData._pathDataConfigs[i];
                    var prevConfig = multiPathData._pathDataConfigs[i - 1];

                    if (currentConfig?.PathData == null || prevConfig?.PathData == null)
                        continue;

                    var currentPoints = multiPathData.GetPathPoints(currentConfig.PathData);
                    var prevPoints = multiPathData.GetPathPoints(prevConfig.PathData);

                    if (currentPoints == null || currentPoints.Count == 0 || prevPoints == null || prevPoints.Count == 0)
                        continue;

                    Transform currentStart = currentPoints[0];
                    Transform prevEnd = prevPoints[prevPoints.Count - 1];

                    if (currentStart == null || prevEnd == null)
                        continue;

                    Vector3 lastCurrentStart = GetCachedPosition(lastPositions, i, 0);
                    Vector3 lastPrevEnd = GetCachedPosition(lastPositions, i - 1, prevPoints.Count - 1);

                    if (HasPositionChanged(currentStart.position, lastCurrentStart))
                    {
                        Undo.RecordObject(prevEnd, "Sync Path Point");
                        prevEnd.position = currentStart.position;
                        EditorUtility.SetDirty(prevConfig.PathData);
                        hasSyncedAnyPoint = true;
                    }
                    else if (HasPositionChanged(prevEnd.position, lastPrevEnd))
                    {
                        Undo.RecordObject(currentStart, "Sync Path Point");
                        currentStart.position = prevEnd.position;
                        EditorUtility.SetDirty(currentConfig.PathData);
                        hasSyncedAnyPoint = true;
                    }
                }

                if (hasSyncedAnyPoint)
                    _cachedPositions[multiPathData] = CapturePositions(multiPathData);
            }

            private static Vector3[][] CapturePositions(MultiPathData multiPathData)
            {
                var positions = new Vector3[multiPathData._pathDataConfigs.Count][];

                for (int i = 0; i < multiPathData._pathDataConfigs.Count; i++)
                {
                    var config = multiPathData._pathDataConfigs[i];
                    if (config?.PathData == null)
                        continue;

                    var points = multiPathData.GetPathPoints(config.PathData);
                    if (points == null)
                        continue;

                    positions[i] = new Vector3[points.Count];
                    for (int j = 0; j < points.Count; j++)
                    {
                        if (points[j] != null)
                            positions[i][j] = points[j].position;
                    }
                }

                return positions;
            }

            private static Vector3 GetCachedPosition(Vector3[][] positions, int pathIndex, int pointIndex)
            {
                if (positions == null || pathIndex >= positions.Length)
                    return Vector3.zero;

                var pathPositions = positions[pathIndex];
                if (pathPositions == null || pointIndex >= pathPositions.Length)
                    return Vector3.zero;

                return pathPositions[pointIndex];
            }

            private static bool HasPositionChanged(Vector3 current, Vector3 cached)
            {
                return (current - cached).sqrMagnitude > POSITION_SYNC_EPSILON_SQR;
            }
        }

        [CustomEditor(typeof(MultiPathData))]
        private class MultiPathDataEditor : Editor
        {
            private const float GOLDEN_RATIO_CONJUGATE = 0.618033988749895f;
            private const float PATH_COLOR_SATURATION = 0.72f;
            private const float PATH_COLOR_VALUE = 1f;

            private readonly Dictionary<int, string> _pathConfigsSignatureByTargetId = new Dictionary<int, string>();
            private readonly Dictionary<int, MultiPathDrawingTemplateCache> _drawingTemplateByTargetId = new Dictionary<int, MultiPathDrawingTemplateCache>();

            private struct MultiPathDrawingTemplateCache
            {
                public float LineWidth;
                public float PointSize;
                public float SamplePointSize;
                public float EventPointSize;
            }

            private void OnEnable()
            {
                foreach (UnityEngine.Object obj in targets)
                {
                    MultiPathData multiPathData = obj as MultiPathData;
                    if (multiPathData == null)
                        continue;

                    int id = multiPathData.GetInstanceID();
                    _pathConfigsSignatureByTargetId[id] = BuildPathConfigsSignature(multiPathData);
                    _drawingTemplateByTargetId[id] = CreateDrawingTemplateCache(multiPathData);
                }
            }

            public override void OnInspectorGUI()
            {
                serializedObject.Update();
                DrawDefaultInspector();
                serializedObject.ApplyModifiedProperties();

                EditorGUILayout.HelpBox(
                    "MultiPath → all PathData drawing: 슬라이더 값은 할당된 모든 PathData에 즉시 반영됩니다. Path Data 목록(참조·순서·크기)이 바뀌면 경로 색이 자동으로 구분됩니다.",
                    MessageType.Info);

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Path Link Tools", EditorStyles.boldLabel);

                MultiPathData multiPathData = (MultiPathData)target;

                if (GUILayout.Button("Force Sync All Path Points"))
                    ForceSyncAllPathPoints(multiPathData);

                if (GUILayout.Button("Sort Path Events (All PathData)"))
                    SortAllPathEventsForMultiPath();

                SyncDrawingAndColorsAfterInspectorEdit();
            }

            private void SyncDrawingAndColorsAfterInspectorEdit()
            {
                foreach (UnityEngine.Object obj in targets)
                {
                    MultiPathData multiPathData = obj as MultiPathData;
                    if (multiPathData == null)
                        continue;

                    int id = multiPathData.GetInstanceID();

                    if (!_drawingTemplateByTargetId.TryGetValue(id, out MultiPathDrawingTemplateCache templateCache))
                        _drawingTemplateByTargetId[id] = CreateDrawingTemplateCache(multiPathData);
                    else if (HasDrawingTemplateChanged(multiPathData, templateCache))
                    {
                        ApplySharedDrawingSettingsToAllPathData(multiPathData);
                        _drawingTemplateByTargetId[id] = CreateDrawingTemplateCache(multiPathData);
                    }

                    string signature = BuildPathConfigsSignature(multiPathData);
                    if (!_pathConfigsSignatureByTargetId.TryGetValue(id, out string cachedSignature))
                        _pathConfigsSignatureByTargetId[id] = signature;
                    else if (signature != cachedSignature)
                    {
                        _pathConfigsSignatureByTargetId[id] = signature;
                        if (HasAnyAssignedPathData(multiPathData))
                            AssignDistinctPathColors(multiPathData);
                    }
                }
            }

            private static MultiPathDrawingTemplateCache CreateDrawingTemplateCache(MultiPathData multiPathData)
            {
                return new MultiPathDrawingTemplateCache
                {
                    LineWidth = multiPathData._multiPathLineWidth,
                    PointSize = multiPathData._multiPathPointSize,
                    SamplePointSize = multiPathData._multiPathSamplePointSize,
                    EventPointSize = multiPathData._multiPathEventPointSize
                };
            }

            private static bool HasDrawingTemplateChanged(MultiPathData multiPathData, MultiPathDrawingTemplateCache cache)
            {
                if (!Mathf.Approximately(multiPathData._multiPathLineWidth, cache.LineWidth))
                    return true;

                if (!Mathf.Approximately(multiPathData._multiPathPointSize, cache.PointSize))
                    return true;

                if (!Mathf.Approximately(multiPathData._multiPathSamplePointSize, cache.SamplePointSize))
                    return true;

                if (!Mathf.Approximately(multiPathData._multiPathEventPointSize, cache.EventPointSize))
                    return true;

                return false;
            }

            private static string BuildPathConfigsSignature(MultiPathData multiPathData)
            {
                if (multiPathData._pathDataConfigs == null)
                    return "null";

                StringBuilder sb = new StringBuilder();
                sb.Append(multiPathData._pathDataConfigs.Count);
                sb.Append('|');

                for (int i = 0; i < multiPathData._pathDataConfigs.Count; i++)
                {
                    PathData pathData = multiPathData._pathDataConfigs[i]?.PathData;
                    sb.Append(pathData != null ? pathData.GetInstanceID().ToString() : "0");
                    sb.Append(',');
                }

                return sb.ToString();
            }

            private static bool HasAnyAssignedPathData(MultiPathData multiPathData)
            {
                if (multiPathData._pathDataConfigs == null || multiPathData._pathDataConfigs.Count == 0)
                    return false;

                for (int i = 0; i < multiPathData._pathDataConfigs.Count; i++)
                {
                    if (multiPathData._pathDataConfigs[i]?.PathData != null)
                        return true;
                }

                return false;
            }

            private static void ApplySharedDrawingSettingsToAllPathData(MultiPathData multiPathData)
            {
                PathData[] uniquePathDatas = CollectUniquePathDatas(multiPathData);
                if (uniquePathDatas == null || uniquePathDatas.Length == 0)
                    return;

                Undo.RecordObjects(uniquePathDatas, "Apply MultiPath drawing settings");

                float lineWidth = multiPathData._multiPathLineWidth;
                float pointSize = multiPathData._multiPathPointSize;
                float samplePointSize = multiPathData._multiPathSamplePointSize;
                float eventPointSize = multiPathData._multiPathEventPointSize;

                for (int i = 0; i < uniquePathDatas.Length; i++)
                {
                    PathData pathData = uniquePathDatas[i];
                    if (pathData == null)
                        continue;

                    SerializedObject so = new SerializedObject(pathData);
                    SerializedProperty lineWidthProp = so.FindProperty(PathDataEditorProperties.LineWidth);
                    SerializedProperty pointSizeProp = so.FindProperty(PathDataEditorProperties.PointSize);
                    SerializedProperty samplePointSizeProp = so.FindProperty(PathDataEditorProperties.SamplePointSize);
                    SerializedProperty eventPointSizeProp = so.FindProperty(PathDataEditorProperties.EventPointSize);

                    if (lineWidthProp != null)
                        lineWidthProp.floatValue = lineWidth;

                    if (pointSizeProp != null)
                        pointSizeProp.floatValue = pointSize;

                    if (samplePointSizeProp != null)
                        samplePointSizeProp.floatValue = samplePointSize;

                    if (eventPointSizeProp != null)
                        eventPointSizeProp.floatValue = eventPointSize;

                    so.ApplyModifiedProperties();
                    EditorUtility.SetDirty(pathData);
                }
            }

            private static void AssignDistinctPathColors(MultiPathData multiPathData)
            {
                if (multiPathData._pathDataConfigs == null || multiPathData._pathDataConfigs.Count == 0)
                    return;

                PathData[] uniqueForUndo = CollectUniquePathDatas(multiPathData);
                if (uniqueForUndo.Length == 0)
                    return;

                int nonNullSlotCount = 0;
                for (int i = 0; i < multiPathData._pathDataConfigs.Count; i++)
                {
                    if (multiPathData._pathDataConfigs[i]?.PathData != null)
                        nonNullSlotCount++;
                }

                bool hasDuplicateReference = nonNullSlotCount > uniqueForUndo.Length;

                Undo.RecordObjects(uniqueForUndo, "Assign distinct path colors");

                int colorIndex = 0;
                for (int i = 0; i < multiPathData._pathDataConfigs.Count; i++)
                {
                    PathData pathData = multiPathData._pathDataConfigs[i]?.PathData;
                    if (pathData == null)
                        continue;

                    float hue = (colorIndex * GOLDEN_RATIO_CONJUGATE) % 1f;
                    Color pathColor = Color.HSVToRGB(hue, PATH_COLOR_SATURATION, PATH_COLOR_VALUE);
                    colorIndex++;

                    SerializedObject so = new SerializedObject(pathData);
                    SerializedProperty pathColorProp = so.FindProperty(PathDataEditorProperties.PathColor);
                    if (pathColorProp != null)
                        pathColorProp.colorValue = pathColor;

                    so.ApplyModifiedProperties();
                    EditorUtility.SetDirty(pathData);
                }

                if (hasDuplicateReference)
                    Debug.LogWarning("MultiPathData: 동일한 PathData가 여러 슬롯에 있어 마지막 슬롯의 색으로 덮어씌워졌습니다.");
            }

            private static PathData[] CollectUniquePathDatas(MultiPathData multiPathData)
            {
                if (multiPathData._pathDataConfigs == null || multiPathData._pathDataConfigs.Count == 0)
                    return Array.Empty<PathData>();

                HashSet<PathData> set = new HashSet<PathData>();
                for (int i = 0; i < multiPathData._pathDataConfigs.Count; i++)
                {
                    PathData pathData = multiPathData._pathDataConfigs[i]?.PathData;
                    if (pathData != null)
                        set.Add(pathData);
                }

                PathData[] result = new PathData[set.Count];
                set.CopyTo(result);
                return result;
            }

            private void ForceSyncAllPathPoints(MultiPathData multiPathData)
            {
                if (multiPathData._pathDataConfigs == null || multiPathData._pathDataConfigs.Count < 2)
                {
                    Debug.LogWarning("MultiPathData: 연결할 PathData가 충분하지 않습니다!");
                    return;
                }

                int syncCount = 0;

                for (int i = 1; i < multiPathData._pathDataConfigs.Count; i++)
                {
                    var currentConfig = multiPathData._pathDataConfigs[i];
                    var prevConfig = multiPathData._pathDataConfigs[i - 1];

                    if (currentConfig?.PathData == null || prevConfig?.PathData == null)
                        continue;

                    var currentPoints = multiPathData.GetPathPoints(currentConfig.PathData);
                    var prevPoints = multiPathData.GetPathPoints(prevConfig.PathData);

                    if (currentPoints == null || currentPoints.Count == 0 || prevPoints == null || prevPoints.Count == 0)
                        continue;

                    Transform currentStart = currentPoints[0];
                    Transform prevEnd = prevPoints[prevPoints.Count - 1];

                    if (currentStart == null || prevEnd == null)
                        continue;

                    Undo.RecordObject(currentStart, "Sync Path Points");
                    currentStart.position = prevEnd.position;
                    EditorUtility.SetDirty(currentConfig.PathData);
                    syncCount++;
                }

                Debug.Log($"MultiPathData: {syncCount}개의 경로 연결점을 동기화했습니다.");
            }

            private void SortAllPathEventsForMultiPath()
            {
                foreach (UnityEngine.Object obj in targets)
                {
                    MultiPathData selected = obj as MultiPathData;
                    if (selected == null)
                        continue;

                    PathData[] uniquePathDatas = CollectUniquePathDatas(selected);
                    if (uniquePathDatas.Length == 0)
                        continue;

                    foreach (PathData pathData in uniquePathDatas)
                        Undo.RecordObject(pathData, "Sort Path Events (All PathData)");

                    if (selected.SortAllPathEventsByNormalizedTime())
                    {
                        foreach (PathData pathData in uniquePathDatas)
                            EditorUtility.SetDirty(pathData);
                    }
                }
            }
        }

        #endregion
    }
}
#endif
