#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Common.TransformPath
{
    /// <summary>
    /// Edit-mode endpoint synchronizer. The tracked target list is rebuilt only
    /// after structural editor changes; position polling reuses cached references.
    /// </summary>
    [InitializeOnLoad]
    internal static class MultiPathAutoLinker
    {
        #region Constants

        private const double SYNC_INTERVAL = 0.05d;
        private const float POSITION_SYNC_EPSILON_SQR = 0.000001f;

        #endregion


        #region Inner Classes / Structs

        private sealed class TrackedMultiPath
        {
            private readonly List<PathData> _pathDatas = new List<PathData>();
            private readonly List<List<Transform>> _pathPoints =
                new List<List<Transform>>();
            private readonly PathLinkSnapshot _snapshot = new PathLinkSnapshot();
            private bool _autoLinkEnabled;

            public MultiPathData Target { get; }
            public int Id { get; }

            public TrackedMultiPath(MultiPathData target)
            {
                Target = target;
                Id = target.GetInstanceID();
            }

            public void RefreshReferences()
            {
                if (Target == null)
                {
                    _autoLinkEnabled = false;
                    _pathDatas.Clear();
                    _pathPoints.Clear();
                    return;
                }

                _autoLinkEnabled = ReadAutoLinkEnabled(Target);
                List<PathData> pathDatas = PathEditorSerializationUtility.GetPathDatas(Target);
                _pathDatas.Clear();
                _pathDatas.AddRange(pathDatas);
                _pathPoints.Clear();
                for (int i = 0; i < _pathDatas.Count; i++)
                    _pathPoints.Add(PathEditorSerializationUtility.GetPathPoints(_pathDatas[i]));
                _snapshot.Capture(_pathPoints);
            }

            public bool Sync()
            {
                if (!_autoLinkEnabled || _pathDatas.Count < 2)
                    return false;

                bool syncedAny = false;
                for (int i = 1; i < _pathDatas.Count; i++)
                {
                    PathData previous = _pathDatas[i - 1];
                    PathData current = _pathDatas[i];
                    if (previous == null || current == null)
                        continue;
                    if (previous == current)
                    {
                        WarnDuplicateLink(Target, i);
                        continue;
                    }

                    List<Transform> previousPoints = _pathPoints[i - 1];
                    List<Transform> currentPoints = _pathPoints[i];
                    if (previousPoints.Count == 0 || currentPoints.Count == 0)
                        continue;
                    Transform previousEnd = previousPoints[previousPoints.Count - 1];
                    Transform currentStart = currentPoints[0];
                    if (previousEnd == null || currentStart == null)
                        continue;

                    Vector3 previousEndBefore = _snapshot.GetPosition(
                        i - 1,
                        previousPoints.Count - 1);
                    Vector3 currentStartBefore = _snapshot.GetPosition(i, 0);
                    if ((currentStart.position - currentStartBefore).sqrMagnitude
                        > POSITION_SYNC_EPSILON_SQR)
                    {
                        PathEditorUndoUtility.Record(previousEnd, "Sync Path Point");
                        previousEnd.position = currentStart.position;
                        PathEditorUndoUtility.MarkDirty(previousEnd);
                        PathEditorUndoUtility.MarkDirty(previous);
                        syncedAny = true;
                    }
                    else if ((previousEnd.position - previousEndBefore).sqrMagnitude
                        > POSITION_SYNC_EPSILON_SQR)
                    {
                        PathEditorUndoUtility.Record(currentStart, "Sync Path Point");
                        currentStart.position = previousEnd.position;
                        PathEditorUndoUtility.MarkDirty(currentStart);
                        PathEditorUndoUtility.MarkDirty(current);
                        syncedAny = true;
                    }
                }

                _snapshot.Capture(_pathPoints);
                return syncedAny;
            }

            private static bool ReadAutoLinkEnabled(MultiPathData multiPathData)
            {
                SerializedObject serializedMultiPath = new SerializedObject(multiPathData);
                SerializedProperty property = serializedMultiPath.FindProperty("_autoLinkPathPoints");
                return property == null || property.boolValue;
            }
        }

        private sealed class PathLinkSnapshot
        {
            private Vector3[][] _positions = new Vector3[0][];

            public void Capture(List<List<Transform>> pathPoints)
            {
                if (_positions.Length != pathPoints.Count)
                    _positions = new Vector3[pathPoints.Count][];
                for (int i = 0; i < pathPoints.Count; i++)
                {
                    List<Transform> points = pathPoints[i];
                    if (_positions[i] == null || _positions[i].Length != points.Count)
                        _positions[i] = new Vector3[points.Count];
                    for (int j = 0; j < points.Count; j++)
                    {
                        if (points[j] != null)
                            _positions[i][j] = points[j].position;
                    }
                }
            }

            public Vector3 GetPosition(int pathIndex, int pointIndex)
            {
                if (pathIndex < 0 || pathIndex >= _positions.Length)
                    return Vector3.zero;
                Vector3[] pathPositions = _positions[pathIndex];
                if (pathPositions == null
                    || pointIndex < 0
                    || pointIndex >= pathPositions.Length)
                    return Vector3.zero;
                return pathPositions[pointIndex];
            }
        }

        #endregion


        #region Member Variables

        private static readonly List<TrackedMultiPath> TRACKED_MULTI_PATHS =
            new List<TrackedMultiPath>();
        private static readonly Dictionary<int, TrackedMultiPath> TRACKED_BY_ID =
            new Dictionary<int, TrackedMultiPath>();
        private static readonly HashSet<int> LIVE_IDS = new HashSet<int>();
        private static readonly HashSet<string> DUPLICATE_WARNINGS =
            new HashSet<string>();
        private static double _lastSyncTime;
        private static bool _trackedTargetsDirty = true;

        #endregion


        #region Unity Events

        static MultiPathAutoLinker()
        {
            EditorApplication.update += OnEditorUpdate;
            EditorApplication.hierarchyChanged += MarkTrackedTargetsDirty;
            EditorSceneManager.sceneOpened += HandleSceneOpened;
            EditorSceneManager.sceneClosed += HandleSceneClosed;
            PrefabStage.prefabStageOpened += HandlePrefabStageOpened;
            PrefabStage.prefabStageClosing += HandlePrefabStageClosing;
            Undo.undoRedoPerformed += MarkTrackedTargetsDirty;
            ObjectChangeEvents.changesPublished += HandleObjectChanges;
            AssemblyReloadEvents.beforeAssemblyReload += Clear;
        }

        #endregion


        #region Private Methods

        private static void OnEditorUpdate()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode
                || EditorApplication.isCompiling
                || EditorApplication.timeSinceStartup - _lastSyncTime < SYNC_INTERVAL)
                return;

            _lastSyncTime = EditorApplication.timeSinceStartup;
            if (_trackedTargetsDirty)
                RefreshTrackedTargets();

            bool syncedAny = false;
            for (int i = TRACKED_MULTI_PATHS.Count - 1; i >= 0; i--)
            {
                TrackedMultiPath tracked = TRACKED_MULTI_PATHS[i];
                if (tracked.Target == null)
                {
                    TRACKED_BY_ID.Remove(tracked.Id);
                    TRACKED_MULTI_PATHS.RemoveAt(i);
                    continue;
                }

                if (tracked.Sync())
                    syncedAny = true;
            }

            if (syncedAny)
                PathEditorUndoUtility.RepaintScene();
        }

        private static void RefreshTrackedTargets()
        {
            MultiPathData[] allMultiPathData = UnityEngine.Object.FindObjectsByType<MultiPathData>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            LIVE_IDS.Clear();
            for (int i = 0; i < allMultiPathData.Length; i++)
            {
                MultiPathData multiPathData = allMultiPathData[i];
                if (multiPathData == null)
                    continue;

                int id = multiPathData.GetInstanceID();
                LIVE_IDS.Add(id);
                if (!TRACKED_BY_ID.TryGetValue(id, out TrackedMultiPath tracked))
                {
                    tracked = new TrackedMultiPath(multiPathData);
                    TRACKED_BY_ID.Add(id, tracked);
                    TRACKED_MULTI_PATHS.Add(tracked);
                }
                tracked.RefreshReferences();
            }

            for (int i = TRACKED_MULTI_PATHS.Count - 1; i >= 0; i--)
            {
                if (LIVE_IDS.Contains(TRACKED_MULTI_PATHS[i].Id))
                    continue;
                TRACKED_BY_ID.Remove(TRACKED_MULTI_PATHS[i].Id);
                TRACKED_MULTI_PATHS.RemoveAt(i);
            }

            _trackedTargetsDirty = false;
        }

        private static void HandleObjectChanges(ref ObjectChangeEventStream stream)
        {
            MarkTrackedTargetsDirty();
        }

        private static void HandleSceneOpened(Scene scene, OpenSceneMode mode)
        {
            MarkTrackedTargetsDirty();
        }

        private static void HandleSceneClosed(Scene scene)
        {
            MarkTrackedTargetsDirty();
        }

        private static void HandlePrefabStageOpened(PrefabStage prefabStage)
        {
            MarkTrackedTargetsDirty();
        }

        private static void HandlePrefabStageClosing(PrefabStage prefabStage)
        {
            MarkTrackedTargetsDirty();
        }

        private static void MarkTrackedTargetsDirty()
        {
            _trackedTargetsDirty = true;
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

        private static void Clear()
        {
            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.hierarchyChanged -= MarkTrackedTargetsDirty;
            EditorSceneManager.sceneOpened -= HandleSceneOpened;
            EditorSceneManager.sceneClosed -= HandleSceneClosed;
            PrefabStage.prefabStageOpened -= HandlePrefabStageOpened;
            PrefabStage.prefabStageClosing -= HandlePrefabStageClosing;
            Undo.undoRedoPerformed -= MarkTrackedTargetsDirty;
            ObjectChangeEvents.changesPublished -= HandleObjectChanges;
            AssemblyReloadEvents.beforeAssemblyReload -= Clear;
            TRACKED_MULTI_PATHS.Clear();
            TRACKED_BY_ID.Clear();
            LIVE_IDS.Clear();
            DUPLICATE_WARNINGS.Clear();
        }

        #endregion


    }
}
#endif
