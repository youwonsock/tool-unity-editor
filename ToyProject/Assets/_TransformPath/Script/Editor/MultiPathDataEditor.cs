#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Common.TransformPath
{
    [CustomEditor(typeof(MultiPathData))]
    internal sealed class MultiPathDataEditor : Editor
    {
        private const float GOLDEN_RATIO_CONJUGATE = 0.618033988749895f;
        private const float PATH_COLOR_SATURATION = 0.72f;
        private const float PATH_COLOR_VALUE = 1f;

        private const string SEGMENTS_PROPERTY = "_segments";
        private const string PATH_DATA_PROPERTY = "_pathData";
        private const string AUTO_LINK_PROPERTY = "_autoLinkPathPoints";
        private const string LINE_WIDTH_PROPERTY = "_multiPathLineWidth";
        private const string POINT_SIZE_PROPERTY = "_multiPathPointSize";
        private const string SAMPLE_POINT_SIZE_PROPERTY = "_multiPathSamplePointSize";
        private const string EVENT_POINT_SIZE_PROPERTY = "_multiPathEventPointSize";

        private readonly Dictionary<int, string> _pathConfigsSignatureByTargetId =
            new Dictionary<int, string>();
        private readonly Dictionary<int, DrawingTemplate> _drawingTemplateByTargetId =
            new Dictionary<int, DrawingTemplate>();

        private struct DrawingTemplate
        {
            public float LineWidth;
            public float PointSize;
            public float SamplePointSize;
            public float EventPointSize;
        }

        private void OnEnable()
        {
            Undo.undoRedoPerformed += HandleUndoRedo;
            foreach (UnityEngine.Object targetObject in targets)
            {
                MultiPathData multiPathData = targetObject as MultiPathData;
                if (multiPathData == null)
                    continue;

                int id = multiPathData.GetInstanceID();
                _pathConfigsSignatureByTargetId[id] = BuildPathConfigsSignature(multiPathData);
                _drawingTemplateByTargetId[id] = ReadDrawingTemplate(multiPathData);
            }
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= HandleUndoRedo;
        }

        private void HandleUndoRedo()
        {
            _pathConfigsSignatureByTargetId.Clear();
            _drawingTemplateByTargetId.Clear();
            SceneView.RepaintAll();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            bool inspectorChanged = DrawDefaultInspector();
            inspectorChanged |= serializedObject.ApplyModifiedProperties();
            if (inspectorChanged)
                SceneView.RepaintAll();

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "MultiPath drawing settings are applied to every referenced PathData. "
                + "Path references and order changes assign distinct preview colors.",
                MessageType.Info);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Path Link Tools", EditorStyles.boldLabel);
            bool authoringToolsEnabled = !Application.isPlaying;
            EditorGUI.BeginDisabledGroup(!authoringToolsEnabled);
            if (GUILayout.Button("Force Sync All Path Points"))
                ForceSyncAllPathPoints();
            if (GUILayout.Button("Sort Path Events (All PathData)"))
                SortAllPathEventsForMultiPath();
            EditorGUI.EndDisabledGroup();

            if (!authoringToolsEnabled)
                EditorGUILayout.HelpBox("Path authoring tools are disabled in Play Mode.", MessageType.Info);

            if (GUILayout.Button("Rebuild Sequence"))
                RebuildSequences();

            SyncDrawingAndColorsAfterInspectorEdit();
        }

        private void SyncDrawingAndColorsAfterInspectorEdit()
        {
            foreach (UnityEngine.Object targetObject in targets)
            {
                MultiPathData multiPathData = targetObject as MultiPathData;
                if (multiPathData == null)
                    continue;

                int id = multiPathData.GetInstanceID();
                DrawingTemplate currentTemplate = ReadDrawingTemplate(multiPathData);
                if (!_drawingTemplateByTargetId.TryGetValue(id, out DrawingTemplate previousTemplate))
                {
                    _drawingTemplateByTargetId[id] = currentTemplate;
                }
                else if (!AreSameTemplate(currentTemplate, previousTemplate))
                {
                    ApplySharedDrawingSettings(multiPathData, currentTemplate);
                    _drawingTemplateByTargetId[id] = currentTemplate;
                }

                string currentSignature = BuildPathConfigsSignature(multiPathData);
                if (!_pathConfigsSignatureByTargetId.TryGetValue(id, out string previousSignature))
                {
                    _pathConfigsSignatureByTargetId[id] = currentSignature;
                }
                else if (!string.Equals(currentSignature, previousSignature, StringComparison.Ordinal))
                {
                    _pathConfigsSignatureByTargetId[id] = currentSignature;
                    AssignDistinctPathColors(multiPathData);
                    SceneView.RepaintAll();
                }
            }
        }

        private static DrawingTemplate ReadDrawingTemplate(MultiPathData multiPathData)
        {
            SerializedObject serializedMultiPath = new SerializedObject(multiPathData);
            return new DrawingTemplate
            {
                LineWidth = ReadFloat(serializedMultiPath, LINE_WIDTH_PROPERTY, 2f),
                PointSize = ReadFloat(serializedMultiPath, POINT_SIZE_PROPERTY, 0.1f),
                SamplePointSize = ReadFloat(serializedMultiPath, SAMPLE_POINT_SIZE_PROPERTY, 0f),
                EventPointSize = ReadFloat(serializedMultiPath, EVENT_POINT_SIZE_PROPERTY, 0.15f),
            };
        }

        private static float ReadFloat(SerializedObject serializedObject, string propertyName, float fallback)
        {
            SerializedProperty property = serializedObject.FindProperty(propertyName);
            return property == null ? fallback : property.floatValue;
        }

        private static bool AreSameTemplate(DrawingTemplate left, DrawingTemplate right)
        {
            return Mathf.Approximately(left.LineWidth, right.LineWidth)
                && Mathf.Approximately(left.PointSize, right.PointSize)
                && Mathf.Approximately(left.SamplePointSize, right.SamplePointSize)
                && Mathf.Approximately(left.EventPointSize, right.EventPointSize);
        }

        private static string BuildPathConfigsSignature(MultiPathData multiPathData)
        {
            SerializedObject serializedMultiPath = new SerializedObject(multiPathData);
            SerializedProperty segments = serializedMultiPath.FindProperty(SEGMENTS_PROPERTY);
            if (segments == null)
                return "null";

            StringBuilder signature = new StringBuilder();
            signature.Append(segments.arraySize);
            signature.Append('|');
            for (int i = 0; i < segments.arraySize; i++)
            {
                PathData pathData = GetPathData(segments, i);
                signature.Append(pathData == null ? 0 : pathData.GetInstanceID());
                signature.Append(':');
                AppendPathPointSignature(signature, pathData);
                signature.Append(',');
            }
            return signature.ToString();
        }

        private static List<PathData> GetPathDatas(MultiPathData multiPathData)
        {
            List<PathData> result = new List<PathData>();
            if (multiPathData == null)
                return result;

            SerializedObject serializedMultiPath = new SerializedObject(multiPathData);
            SerializedProperty segments = serializedMultiPath.FindProperty(SEGMENTS_PROPERTY);
            if (segments == null)
                return result;

            for (int i = 0; i < segments.arraySize; i++)
                result.Add(GetPathData(segments, i));
            return result;
        }

        private static PathData GetPathData(SerializedProperty segments, int index)
        {
            if (segments == null || index < 0 || index >= segments.arraySize)
                return null;

            SerializedProperty segment = segments.GetArrayElementAtIndex(index);
            SerializedProperty pathDataProperty = segment.FindPropertyRelative(PATH_DATA_PROPERTY);
            return pathDataProperty == null ? null : pathDataProperty.objectReferenceValue as PathData;
        }

        private static List<PathData> CollectUniquePathDatas(MultiPathData multiPathData)
        {
            List<PathData> result = new List<PathData>();
            HashSet<PathData> seen = new HashSet<PathData>();
            List<PathData> pathDatas = GetPathDatas(multiPathData);
            for (int i = 0; i < pathDatas.Count; i++)
            {
                PathData pathData = pathDatas[i];
                if (pathData != null && seen.Add(pathData))
                    result.Add(pathData);
            }
            return result;
        }

        private static void ApplySharedDrawingSettings(
            MultiPathData multiPathData,
            DrawingTemplate template)
        {
            List<PathData> pathDatas = CollectUniquePathDatas(multiPathData);
            if (pathDatas.Count == 0)
                return;

            UnityEngine.Object[] undoTargets = new UnityEngine.Object[pathDatas.Count];
            for (int i = 0; i < pathDatas.Count; i++)
                undoTargets[i] = pathDatas[i];
            Undo.RecordObjects(undoTargets, "Apply MultiPath drawing settings");

            for (int i = 0; i < pathDatas.Count; i++)
            {
                PathData pathData = pathDatas[i];
                SerializedObject serializedPathData = new SerializedObject(pathData);
                serializedPathData.Update();
                SetFloat(serializedPathData, PathDataEditorProperties.LineWidth, template.LineWidth);
                SetFloat(serializedPathData, PathDataEditorProperties.PointSize, template.PointSize);
                SetFloat(serializedPathData, PathDataEditorProperties.SamplePointSize, template.SamplePointSize);
                SetFloat(serializedPathData, PathDataEditorProperties.EventPointSize, template.EventPointSize);
                serializedPathData.ApplyModifiedProperties();
                EditorUtility.SetDirty(pathData);
                RecordPrefabOverride(pathData);
            }
            SceneView.RepaintAll();
        }

        private static void SetFloat(SerializedObject serializedObject, string propertyName, float value)
        {
            SerializedProperty property = serializedObject.FindProperty(propertyName);
            if (property != null)
                property.floatValue = value;
        }

        private static void AssignDistinctPathColors(MultiPathData multiPathData)
        {
            List<PathData> pathDatas = GetPathDatas(multiPathData);
            List<PathData> uniquePathDatas = CollectUniquePathDatas(multiPathData);
            if (uniquePathDatas.Count == 0)
                return;

            UnityEngine.Object[] undoTargets = new UnityEngine.Object[uniquePathDatas.Count];
            for (int i = 0; i < uniquePathDatas.Count; i++)
                undoTargets[i] = uniquePathDatas[i];
            Undo.RecordObjects(undoTargets, "Assign distinct path colors");

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
                    PathDataEditorProperties.PathColor);
                if (colorProperty != null)
                    colorProperty.colorValue = pathColor;
                serializedPathData.ApplyModifiedProperties();
                EditorUtility.SetDirty(uniquePathDatas[i]);
                RecordPrefabOverride(uniquePathDatas[i]);
            }

            if (pathDatas.Count != uniquePathDatas.Count)
            {
                Debug.LogWarning(
                    "MultiPathData: 동일한 PathData가 여러 슬롯에 있어 첫 번째 슬롯의 색을 공유합니다.",
                    multiPathData);
            }
        }

        private void ForceSyncAllPathPoints()
        {
            foreach (UnityEngine.Object targetObject in targets)
            {
                MultiPathData multiPathData = targetObject as MultiPathData;
                if (multiPathData == null)
                    continue;

                List<PathData> pathDatas = GetPathDatas(multiPathData);
                if (pathDatas.Count < 2)
                {
                    Debug.LogWarning("MultiPathData: 연결할 PathData가 충분하지 않습니다.", multiPathData);
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

                    List<Transform> previousPoints = GetPathPoints(previous);
                    List<Transform> currentPoints = GetPathPoints(current);
                    if (previousPoints.Count == 0 || currentPoints.Count == 0)
                        continue;

                    Transform previousEnd = previousPoints[previousPoints.Count - 1];
                    Transform currentStart = currentPoints[0];
                    if (previousEnd == null || currentStart == null)
                        continue;

                    Undo.RecordObject(currentStart, "Sync Path Points");
                    currentStart.position = previousEnd.position;
                    RecordPrefabOverride(currentStart);
                    EditorUtility.SetDirty(current);
                    syncCount++;
                }

                Debug.Log($"MultiPathData: {syncCount}개의 경로 연결점을 동기화했습니다.", multiPathData);
            }
            SceneView.RepaintAll();
        }

        private void SortAllPathEventsForMultiPath()
        {
            foreach (UnityEngine.Object targetObject in targets)
            {
                MultiPathData multiPathData = targetObject as MultiPathData;
                if (multiPathData == null)
                    continue;

                List<PathData> pathDatas = CollectUniquePathDatas(multiPathData);
                bool changed = false;
                for (int i = 0; i < pathDatas.Count; i++)
                {
                    PathData pathData = pathDatas[i];
                    Undo.RecordObject(pathData, "Sort Path Events (All PathData)");
                    if (!pathData.SortPathEventsByNormalizedTime())
                        continue;

                    changed = true;
                    EditorUtility.SetDirty(pathData);
                    RecordPrefabOverride(pathData);
                }

                if (changed)
                    SceneView.RepaintAll();
            }
        }

        private void RebuildSequences()
        {
            foreach (UnityEngine.Object targetObject in targets)
            {
                MultiPathData multiPathData = targetObject as MultiPathData;
                if (multiPathData == null)
                    continue;

                Undo.RecordObject(multiPathData, "Rebuild Sequence");
                multiPathData.Rebuild();
                EditorUtility.SetDirty(multiPathData);
                RecordPrefabOverride(multiPathData);
            }
        }

        private static List<Transform> GetPathPoints(PathData pathData)
        {
            List<Transform> result = new List<Transform>();
            if (pathData == null)
                return result;

            SerializedObject serializedPathData = new SerializedObject(pathData);
            SerializedProperty points = serializedPathData.FindProperty("_pathPoints");
            if (points == null)
                return result;

            for (int i = 0; i < points.arraySize; i++)
            {
                Transform point = points.GetArrayElementAtIndex(i).objectReferenceValue as Transform;
                result.Add(point);
            }
            return result;
        }

        private static void AppendPathPointSignature(StringBuilder signature, PathData pathData)
        {
            List<Transform> points = GetPathPoints(pathData);
            signature.Append(points.Count);
            signature.Append('[');
            for (int i = 0; i < points.Count; i++)
            {
                signature.Append(points[i] == null ? 0 : points[i].GetInstanceID());
                signature.Append(';');
            }
            signature.Append(']');
        }

        private static void WarnDuplicateLink(MultiPathData multiPathData, int segmentIndex)
        {
            string key = $"{multiPathData.GetInstanceID()}:{segmentIndex}";
            if (DuplicateWarnings.Add(key))
            {
                Debug.LogWarning(
                    $"MultiPathData: segment {segmentIndex - 1}와 {segmentIndex}가 동일한 PathData를 참조해 자동 연결을 건너뜁니다.",
                    multiPathData);
            }
        }

        private static void RecordPrefabOverride(UnityEngine.Object targetObject)
        {
            if (targetObject != null && PrefabUtility.IsPartOfPrefabInstance(targetObject))
                PrefabUtility.RecordPrefabInstancePropertyModifications(targetObject);
        }

        private static readonly HashSet<string> DuplicateWarnings = new HashSet<string>();

        [InitializeOnLoad]
        private static class MultiPathAutoLinker
        {
            private const double SYNC_INTERVAL = 0.05d;
            private const float POSITION_SYNC_EPSILON_SQR = 0.000001f;

            private static double _lastSyncTime;
            private static readonly Dictionary<int, PositionSnapshot> Snapshots =
                new Dictionary<int, PositionSnapshot>();

            static MultiPathAutoLinker()
            {
                EditorApplication.update += OnEditorUpdate;
                AssemblyReloadEvents.beforeAssemblyReload += Clear;
            }

            private static void OnEditorUpdate()
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode
                    || EditorApplication.isCompiling
                    || EditorApplication.timeSinceStartup - _lastSyncTime < SYNC_INTERVAL)
                {
                    return;
                }

                _lastSyncTime = EditorApplication.timeSinceStartup;
                MultiPathData[] allMultiPathData = UnityEngine.Object.FindObjectsByType<MultiPathData>(
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None);
                HashSet<int> liveIds = new HashSet<int>();

                for (int i = 0; i < allMultiPathData.Length; i++)
                {
                    MultiPathData multiPathData = allMultiPathData[i];
                    if (multiPathData == null)
                        continue;

                    int id = multiPathData.GetInstanceID();
                    liveIds.Add(id);
                    if (!IsAutoLinkEnabled(multiPathData))
                    {
                        Snapshots.Remove(id);
                        continue;
                    }

                    SyncPathPoints(multiPathData, id);
                }

                RemoveDeadSnapshots(liveIds);
            }

            private static bool IsAutoLinkEnabled(MultiPathData multiPathData)
            {
                SerializedObject serializedMultiPath = new SerializedObject(multiPathData);
                SerializedProperty property = serializedMultiPath.FindProperty(AUTO_LINK_PROPERTY);
                return property == null || property.boolValue;
            }

            private static void SyncPathPoints(MultiPathData multiPathData, int id)
            {
                List<PathData> pathDatas = GetPathDatas(multiPathData);
                string signature = BuildPathConfigsSignature(multiPathData);
                if (pathDatas.Count < 2)
                {
                    Snapshots[id] = CaptureSnapshot(signature, pathDatas);
                    return;
                }

                if (!Snapshots.TryGetValue(id, out PositionSnapshot previousSnapshot)
                    || !string.Equals(signature, previousSnapshot.Signature, StringComparison.Ordinal))
                {
                    Snapshots[id] = CaptureSnapshot(signature, pathDatas);
                    return;
                }

                bool syncedAny = false;
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

                    List<Transform> previousPoints = GetPathPoints(previous);
                    List<Transform> currentPoints = GetPathPoints(current);
                    if (previousPoints.Count == 0 || currentPoints.Count == 0)
                        continue;

                    Transform previousEnd = previousPoints[previousPoints.Count - 1];
                    Transform currentStart = currentPoints[0];
                    if (previousEnd == null || currentStart == null)
                        continue;

                    Vector3 previousEndBefore = previousSnapshot.GetPosition(i - 1, previousPoints.Count - 1);
                    Vector3 currentStartBefore = previousSnapshot.GetPosition(i, 0);

                    // Preserve the legacy priority: if both endpoints moved,
                    // the later segment's start point is authoritative.
                    if ((currentStart.position - currentStartBefore).sqrMagnitude > POSITION_SYNC_EPSILON_SQR)
                    {
                        Undo.RecordObject(previousEnd, "Sync Path Point");
                        previousEnd.position = currentStart.position;
                        RecordPrefabOverride(previousEnd);
                        EditorUtility.SetDirty(previous);
                        syncedAny = true;
                    }
                    else if ((previousEnd.position - previousEndBefore).sqrMagnitude > POSITION_SYNC_EPSILON_SQR)
                    {
                        Undo.RecordObject(currentStart, "Sync Path Point");
                        currentStart.position = previousEnd.position;
                        RecordPrefabOverride(currentStart);
                        EditorUtility.SetDirty(current);
                        syncedAny = true;
                    }
                }

                Snapshots[id] = CaptureSnapshot(signature, pathDatas);
                if (syncedAny)
                    SceneView.RepaintAll();
            }

            private static PositionSnapshot CaptureSnapshot(string signature, List<PathData> pathDatas)
            {
                PositionSnapshot snapshot = new PositionSnapshot(signature, pathDatas.Count);
                for (int i = 0; i < pathDatas.Count; i++)
                {
                    List<Transform> points = GetPathPoints(pathDatas[i]);
                    snapshot.Positions[i] = new Vector3[points.Count];
                    for (int j = 0; j < points.Count; j++)
                    {
                        if (points[j] != null)
                            snapshot.Positions[i][j] = points[j].position;
                    }
                }
                return snapshot;
            }

            private static void RemoveDeadSnapshots(HashSet<int> liveIds)
            {
                List<int> deadIds = new List<int>();
                foreach (KeyValuePair<int, PositionSnapshot> pair in Snapshots)
                {
                    if (!liveIds.Contains(pair.Key))
                        deadIds.Add(pair.Key);
                }
                for (int i = 0; i < deadIds.Count; i++)
                    Snapshots.Remove(deadIds[i]);
            }

            private static void Clear()
            {
                Snapshots.Clear();
                DuplicateWarnings.Clear();
                EditorApplication.update -= OnEditorUpdate;
            }

            private sealed class PositionSnapshot
            {
                public readonly string Signature;
                public readonly Vector3[][] Positions;

                public PositionSnapshot(string signature, int pathCount)
                {
                    Signature = signature;
                    Positions = new Vector3[pathCount][];
                }

                public Vector3 GetPosition(int pathIndex, int pointIndex)
                {
                    if (pathIndex < 0 || pathIndex >= Positions.Length)
                        return Vector3.zero;
                    Vector3[] pathPositions = Positions[pathIndex];
                    if (pathPositions == null || pointIndex < 0 || pointIndex >= pathPositions.Length)
                        return Vector3.zero;
                    return pathPositions[pointIndex];
                }
            }

            private static List<PathData> GetPathDatas(MultiPathData multiPathData)
            {
                List<PathData> result = new List<PathData>();
                SerializedObject serializedMultiPath = new SerializedObject(multiPathData);
                SerializedProperty segments = serializedMultiPath.FindProperty(SEGMENTS_PROPERTY);
                if (segments == null)
                    return result;
                for (int i = 0; i < segments.arraySize; i++)
                    result.Add(GetPathData(segments, i));
                return result;
            }

            private static PathData GetPathData(SerializedProperty segments, int index)
            {
                if (segments == null || index < 0 || index >= segments.arraySize)
                    return null;
                SerializedProperty segment = segments.GetArrayElementAtIndex(index);
                SerializedProperty pathDataProperty = segment.FindPropertyRelative(PATH_DATA_PROPERTY);
                return pathDataProperty == null ? null : pathDataProperty.objectReferenceValue as PathData;
            }

            private static string BuildPathConfigsSignature(MultiPathData multiPathData)
            {
                SerializedObject serializedMultiPath = new SerializedObject(multiPathData);
                SerializedProperty segments = serializedMultiPath.FindProperty(SEGMENTS_PROPERTY);
                if (segments == null)
                    return "null";

                StringBuilder signature = new StringBuilder();
                signature.Append(segments.arraySize);
                signature.Append('|');
                for (int i = 0; i < segments.arraySize; i++)
                {
                    PathData pathData = GetPathData(segments, i);
                    signature.Append(pathData == null ? 0 : pathData.GetInstanceID());
                    signature.Append(':');
                    AppendPathPointSignature(signature, pathData);
                    signature.Append(',');
                }
                return signature.ToString();
            }

            private static List<Transform> GetPathPoints(PathData pathData)
            {
                List<Transform> result = new List<Transform>();
                if (pathData == null)
                    return result;

                SerializedObject serializedPathData = new SerializedObject(pathData);
                SerializedProperty points = serializedPathData.FindProperty("_pathPoints");
                if (points == null)
                    return result;
                for (int i = 0; i < points.arraySize; i++)
                {
                    Transform point = points.GetArrayElementAtIndex(i).objectReferenceValue as Transform;
                    result.Add(point);
                }
                return result;
            }

            private static void AppendPathPointSignature(StringBuilder signature, PathData pathData)
            {
                List<Transform> points = GetPathPoints(pathData);
                signature.Append(points.Count);
                signature.Append('[');
                for (int i = 0; i < points.Count; i++)
                {
                    signature.Append(points[i] == null ? 0 : points[i].GetInstanceID());
                    signature.Append(';');
                }
                signature.Append(']');
            }

            private static void WarnDuplicateLink(MultiPathData multiPathData, int segmentIndex)
            {
                string key = $"{multiPathData.GetInstanceID()}:{segmentIndex}";
                if (!DuplicateWarnings.Add(key))
                    return;
                Debug.LogWarning(
                    $"MultiPathData: segment {segmentIndex - 1}와 {segmentIndex}가 동일한 PathData를 참조해 자동 연결을 건너뜁니다.",
                    multiPathData);
            }

            private static void RecordPrefabOverride(UnityEngine.Object targetObject)
            {
                if (targetObject != null && PrefabUtility.IsPartOfPrefabInstance(targetObject))
                    PrefabUtility.RecordPrefabInstancePropertyModifications(targetObject);
            }
        }
    }
}
#endif
