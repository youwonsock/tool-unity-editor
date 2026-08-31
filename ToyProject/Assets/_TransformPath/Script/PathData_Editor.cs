#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace Common.TransformPath
{
    public partial class PathData
    {
        #region Member Variables

        public IReadOnlyList<Transform> EditorPathPoints => _pathPoints;

        public bool EditorShowPathInEditor => _showPathInEditor;
        public float EditorLineWidth => _lineWidth;
        public float EditorPointSize => _pointSize;
        public float EditorSamplePointSize => _samplePointSize;
        public float EditorEventPointSize => _eventPointSize;
        public Color EditorPathColor => _pathColor;
        
        private const float DEFAULT_POINT_SIZE = 0.1f;

        [Header("Editor Only (Draw Path)")]
        [SerializeField] private bool _showPathInEditor = true;
        [SerializeField] private Color _pointColor = Color.red;
        [SerializeField] private Color _pathColor = Color.blue;
        [SerializeField] private Color _samplePointColor = Color.yellow;
        [SerializeField] private Color _eventPointColor = Color.green;
        [SerializeField] private float _lineWidth = 2f;
        [SerializeField] private float _pointSize = DEFAULT_POINT_SIZE;
        [SerializeField] private float _samplePointSize = 0.0f;
        [SerializeField] private float _eventPointSize = 0.15f;
        
        private Vector3[] _cachedSamplePoints = null;
        private int _cachedSampleCount = -1;
        private ESamplingType _cachedESamplingType = ESamplingType.Uniform;

        private void OnValidate()
        {
            if (_isInitialized)
                _cachedPathLength = -1f;
        }

        private void OnDrawGizmos()
        {
            if (!_showPathInEditor)
                return;

            if (Event.current != null && Event.current.type != EventType.Repaint)
                return;

            const int MIN_POINTS_FOR_DRAW = 2;

            if (_pathPoints == null || _pathPoints.Count < MIN_POINTS_FOR_DRAW)
                return;

            if (!IsReady)
            {
                DrawSimplePath();
                return;
            }

            DrawPathPoints();
            DrawPath();
            DrawSamplingPreview();
            DrawPathEvents();
        }

        private void DrawPathPoints()
        {
            const float EPSILON = 0.001f;
            
            if (_pointSize > EPSILON)
            {
                Gizmos.color = _pointColor;
                GUIStyle labelStyle = new GUIStyle(GUI.skin.label);
                labelStyle.fontSize = Mathf.RoundToInt(12f * (_pointSize / DEFAULT_POINT_SIZE));

                for (int i = 0; i < _pathPoints.Count; i++)
                {
                    Transform point = _pathPoints[i];
                    if (point != null)
                    {
                        Gizmos.DrawSphere(point.position, _pointSize);
                        
                        Handles.Label(point.position + Vector3.up * 0.3f, $"P{i}", labelStyle);
                    }
                }
            }
        }

        private void DrawPath()
        {
            const int MIN_CACHED_POINTS = 2;
            
            if (_cachedPathPoints == null || _cachedPathPoints.Length < MIN_CACHED_POINTS)
            {
                DrawSimplePath();
                return;
            }

            Handles.color = _pathColor;
            Handles.DrawAAPolyLine(_lineWidth, _cachedPathPoints);
        }

        private void DrawSimplePath()
        {
            Gizmos.color = _pathColor;
            
            for (int i = 0; i < _pathPoints.Count - 1; i++)
            {
                if (_pathPoints[i] != null && _pathPoints[i + 1] != null)
                    Gizmos.DrawLine(_pathPoints[i].position, _pathPoints[i + 1].position);
            }
        }
        
        private void DrawSamplingPreview()
        {
            const float EPSILON = 0.001f;
            
            if (_samplePointSize <= EPSILON || _samplingCount <= 0)
                return;
                
            Vector3[] samplePoints = SamplePointsOnPath(_samplingCount);
            
            if (samplePoints == null || samplePoints.Length == 0)
                return;
                
            Gizmos.color = _samplePointColor;
            GUIStyle labelStyle = new GUIStyle(GUI.skin.label);
            labelStyle.fontSize = Mathf.RoundToInt(12f * (_samplePointSize / DEFAULT_POINT_SIZE));
            for (int i = 0; i < samplePoints.Length; i++)
            {
                Gizmos.DrawSphere(samplePoints[i], _samplePointSize);
                
                Handles.Label(samplePoints[i] + Vector3.up * 0.5f, $"S{i}", labelStyle);
            }
        }

        private void DrawPathEvents()
        {
            const float EPSILON = 0.001f;
            const float LABEL_OFFSET_Y = 0.7f;

            if (_pathEvents == null || _pathEvents.Count == 0)
                return;

            if (!_isInitialized)
                return;

            if (_eventPointSize <= EPSILON)
                return;

            Gizmos.color = _eventPointColor;
            GUIStyle labelStyle = new GUIStyle(GUI.skin.label);
            labelStyle.fontSize = Mathf.RoundToInt(14f * (_eventPointSize / DEFAULT_POINT_SIZE));
            labelStyle.normal.textColor = Color.white;
            labelStyle.fontStyle = FontStyle.Bold;

            foreach (PathEventEntry entry in _pathEvents)
            {
                float normalizedTime = ClampPathEventNormalizedTime(entry.NormalizedTime);
                PathEventSettingSO eventSetting = entry.EventSetting;

                if (eventSetting == null)
                    continue;

                Vector3 eventPosition = GetPointOnPath(normalizedTime);
                Gizmos.DrawSphere(eventPosition, _eventPointSize);

                string eventName = eventSetting.EventName;

                Handles.Label(eventPosition + Vector3.up * LABEL_OFFSET_Y, eventName, labelStyle);
            }
        }

        #endregion


        #region Inner Classes / Structs

        [CustomEditor(typeof(PathData))]
        private class PathDataEditor : Editor
        {
            private const float MAX_RAYCAST_DISTANCE = 1000f;
            private const int MIN_PATH_POINTS_FOR_VALID_PATH = 2;
            private const string PATH_POINT_NAME_PREFIX = "PathPoint";

            public override void OnInspectorGUI()
            {
                DrawDefaultInspector();

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Path Tools", EditorStyles.boldLabel);

                PathData pathData = (PathData)target;

                if (GUILayout.Button("Snap to Ground"))
                    SnapToGround(pathData);

                if (GUILayout.Button("Create Path Points"))
                    CreatePathPoints(pathData);

                if (GUILayout.Button(new GUIContent("Sync Path Points", $"{PATH_POINT_NAME_PREFIX}로 시작하는 이름의 자손 Transform을 형제 순으로 _pathPoints에 반영하고 명시적으로 재빌드합니다.")))
                    SyncPathPoints(pathData);

                if (GUILayout.Button("Sort Path Events by Normalized Time"))
                {
                    foreach (UnityEngine.Object obj in targets)
                    {
                        PathData selectedPathData = obj as PathData;
                        if (selectedPathData != null)
                            SortPathEventsWithUndo(selectedPathData);
                    }
                }
            }

            private static void SortPathEventsWithUndo(PathData pathData)
            {
                if (pathData == null)
                    return;

                Undo.RecordObject(pathData, "Sort Path Events by Normalized Time");

                if (pathData.SortPathEventsByNormalizedTime())
                    EditorUtility.SetDirty(pathData);
            }

            private void SnapToGround(PathData pathData)
            {
                if (pathData._pathPoints == null || pathData._pathPoints.Count == 0)
                {
                    Debug.LogWarning("PathData: 스냅할 경로 포인트가 없습니다!");
                    return;
                }

                Undo.RecordObjects(pathData._pathPoints.ToArray(), "Snap Path Points to Ground");

                int snappedCount = 0;
                int failedCount = 0;

                foreach (Transform point in pathData._pathPoints)
                {
                    if (point == null)
                        continue;

                    Vector3 rayOrigin = point.position;
                    Ray ray = new Ray(rayOrigin, Vector3.down);

                    if (Physics.Raycast(ray, out RaycastHit hit, MAX_RAYCAST_DISTANCE))
                    {
                        point.position = hit.point;
                        snappedCount++;
                    }
                    else
                    {
                        failedCount++;
                    }
                }

                if (snappedCount > 0)
                {
                    EditorUtility.SetDirty(pathData);
                    if (pathData.IsInitialized)
                        pathData.Rebuild();
                    else
                        pathData.Init();
                    Debug.Log($"PathData: {snappedCount}개의 포인트를 지면에 스냅했습니다. (실패: {failedCount}개)");
                }
                else
                {
                    Debug.LogWarning($"PathData: 스냅된 포인트가 없습니다. (실패: {failedCount}개)");
                }
            }

            private void CreatePathPoints(PathData pathData)
            {
                Undo.RecordObject(pathData, "Create Path Points");

                GameObject startPoint = new GameObject("PathPointStart");
                startPoint.transform.SetParent(pathData.transform);
                startPoint.transform.localPosition = Vector3.zero;

                GameObject midPoint = new GameObject("PathPoint");
                midPoint.transform.SetParent(pathData.transform);
                midPoint.transform.localPosition = Vector3.forward * 5f;

                GameObject endPoint = new GameObject("PathPointEnd");
                endPoint.transform.SetParent(pathData.transform);
                endPoint.transform.localPosition = Vector3.forward * 10f;

                Undo.RegisterCreatedObjectUndo(startPoint, "Create PathPointStart");
                Undo.RegisterCreatedObjectUndo(midPoint, "Create PathPoint");
                Undo.RegisterCreatedObjectUndo(endPoint, "Create PathPointEnd");

                pathData._pathPoints.Clear();
                pathData._pathPoints.Add(startPoint.transform);
                pathData._pathPoints.Add(midPoint.transform);
                pathData._pathPoints.Add(endPoint.transform);

                EditorUtility.SetDirty(pathData);
                if (pathData.IsInitialized)
                    pathData.Rebuild();
                else
                    pathData.Init();

                Debug.Log("PathData: PathPointStart, PathPoint, PathPointEnd 생성 완료");
            }

            private void SyncPathPoints(PathData pathData)
            {
                if (pathData == null)
                    return;

                Undo.RecordObject(pathData, "Sync Path Points");

                if (pathData._pathPoints == null)
                    pathData._pathPoints = new List<Transform>();

                List<Transform> collected = new List<Transform>();
                CollectTransformsWithPathPointPrefix(pathData.transform, collected);

                pathData._pathPoints.Clear();
                for (int i = 0; i < collected.Count; i++)
                    pathData._pathPoints.Add(collected[i]);

                EditorUtility.SetDirty(pathData);
                if (pathData.IsInitialized)
                    pathData.Rebuild();
                else
                    pathData.Init();

                if (pathData._pathPoints.Count < MIN_PATH_POINTS_FOR_VALID_PATH)
                {
                    Debug.LogWarning($"PathData: '{PATH_POINT_NAME_PREFIX}'로 시작하는 자손이 {pathData._pathPoints.Count}개입니다. 유효 경로는 최소 {MIN_PATH_POINTS_FOR_VALID_PATH}개가 필요합니다.");
                    return;
                }

                Debug.Log($"PathData: _pathPoints를 자손에서 {pathData._pathPoints.Count}개 갱신했습니다.");
            }

            private static void CollectTransformsWithPathPointPrefix(Transform parent, List<Transform> results)
            {
                if (parent == null || results == null)
                    return;

                for (int i = 0; i < parent.childCount; i++)
                {
                    Transform child = parent.GetChild(i);

                    if (child.name.StartsWith(PATH_POINT_NAME_PREFIX, StringComparison.Ordinal))
                        results.Add(child);

                    CollectTransformsWithPathPointPrefix(child, results);
                }
            }
        }

        #endregion
    }

    /// <summary>
    /// PathData gizmo/인스펙터 SerializedProperty 필드명 상수입니다.
    /// </summary>
    internal static class PathDataEditorProperties
    {
        public const string ShowPathInEditor = "_showPathInEditor";
        public const string PointColor = "_pointColor";
        public const string PathColor = "_pathColor";
        public const string SamplePointColor = "_samplePointColor";
        public const string EventPointColor = "_eventPointColor";
        public const string LineWidth = "_lineWidth";
        public const string PointSize = "_pointSize";
        public const string SamplePointSize = "_samplePointSize";
        public const string EventPointSize = "_eventPointSize";
    }
}
#endif
