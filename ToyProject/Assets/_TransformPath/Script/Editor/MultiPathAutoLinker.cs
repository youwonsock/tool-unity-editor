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

        private const float POSITION_SYNC_EPSILON_SQR =
            PathLinkEditorUtility.POSITION_SYNC_EPSILON_SQR;

        #endregion


        #region Inner Classes / Structs

        private sealed class TrackedMultiPath
        {
            private readonly List<PathData> _pathDatas = new List<PathData>();
            private readonly List<List<Transform>> _pathPoints =
                new List<List<Transform>>();
            private readonly PathLinkSnapshot _snapshot = new PathLinkSnapshot();
            private string _structureSignature = string.Empty;
            private bool _autoLinkEnabled;
            private bool _wasAutoLinkEnabled;
            private bool _needsInitialSync;

            public MultiPathData Target { get; }
            public int Id { get; }

            public string CachedStructureSignature => _structureSignature;

            public TrackedMultiPath(MultiPathData target)
            {
                Target = target;
                Id = target.GetInstanceID();
            }

            public void RequestInitialSync()
            {
                _needsInitialSync = true;
            }

            public void RefreshReferences()
            {
                if (Target == null)
                {
                    _autoLinkEnabled = false;
                    _wasAutoLinkEnabled = false;
                    _needsInitialSync = false;
                    _structureSignature = string.Empty;
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

                string currentSignature = PathEditorSerializationUtility.BuildPathConfigsSignature(
                    Target);
                bool structureChanged = string.IsNullOrEmpty(_structureSignature)
                    || !string.Equals(
                        _structureSignature,
                        currentSignature,
                        StringComparison.Ordinal);
                bool autoLinkTurnedOn = _autoLinkEnabled && !_wasAutoLinkEnabled;

                if (structureChanged)
                {
                    _structureSignature = currentSignature;
                    _snapshot.Capture(_pathPoints);
                    _needsInitialSync = true;
                }
                else if (autoLinkTurnedOn)
                    _needsInitialSync = true;

                _wasAutoLinkEnabled = _autoLinkEnabled;
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
                        PathLinkEditorUtility.WarnDuplicateLink(Target, i);
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

                    if (PathLinkEditorUtility.TrySyncJointByMovement(
                            previous,
                            current,
                            previousEnd,
                            currentStart,
                            _snapshot.GetPosition(i - 1, previousPoints.Count - 1),
                            _snapshot.GetPosition(i, 0),
                            POSITION_SYNC_EPSILON_SQR))
                        syncedAny = true;
                }

                if (_needsInitialSync)
                {
                    for (int i = 1; i < _pathDatas.Count; i++)
                    {
                        PathData previous = _pathDatas[i - 1];
                        PathData current = _pathDatas[i];
                        if (previous == null || current == null)
                            continue;
                        if (previous == current)
                            continue;

                        if (!PathLinkEditorUtility.TryGetJointTransforms(
                                previous,
                                current,
                                out Transform previousEnd,
                                out Transform currentStart))
                            continue;

                        if (PathLinkEditorUtility.TryAlignStartToEnd(
                                previous,
                                current,
                                previousEnd,
                                currentStart,
                                POSITION_SYNC_EPSILON_SQR))
                            syncedAny = true;
                    }

                    _needsInitialSync = false;
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
        private static bool _trackedTargetsDirty = true;

        #endregion


        #region Unity Events

        static MultiPathAutoLinker()
        {
            PathEditorChangeMonitor.Updated += OnEditorUpdate;
            PathEditorChangeMonitor.StructureChanged += MarkTrackedTargetsDirty;
            PathEditorChangeMonitor.DefinitionChanged += HandleDefinitionChanged;
            PathEditorChangeMonitor.UndoRedo += HandleUndoRedo;
            PathEditorChangeMonitor.BeforeReload += Clear;
        }

        #endregion


        #region Private Methods

        private static void OnEditorUpdate()
        {
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

        private static void HandleDefinitionChanged(UnityEngine.Object changedObject)
        {
            if (changedObject is MultiPathData || changedObject is PathData)
                MarkTrackedTargetsDirty();
        }

        private static void HandleUndoRedo()
        {
            if (TRACKED_MULTI_PATHS.Count == 0)
            {
                MarkTrackedTargetsDirty();
                return;
            }

            for (int i = 0; i < TRACKED_MULTI_PATHS.Count; i++)
            {
                TrackedMultiPath tracked = TRACKED_MULTI_PATHS[i];
                if (tracked.Target == null)
                    continue;

                string liveSignature = PathEditorSerializationUtility.BuildPathConfigsSignature(
                    tracked.Target);
                if (!string.Equals(
                        tracked.CachedStructureSignature,
                        liveSignature,
                        StringComparison.Ordinal))
                {
                    MarkTrackedTargetsDirty();
                    return;
                }
            }

            for (int i = 0; i < TRACKED_MULTI_PATHS.Count; i++)
            {
                if (TRACKED_MULTI_PATHS[i].Target != null)
                    TRACKED_MULTI_PATHS[i].RequestInitialSync();
            }
        }

        private static void MarkTrackedTargetsDirty()
        {
            _trackedTargetsDirty = true;
        }

        private static void Clear()
        {
            PathEditorChangeMonitor.Updated -= OnEditorUpdate;
            PathEditorChangeMonitor.StructureChanged -= MarkTrackedTargetsDirty;
            PathEditorChangeMonitor.DefinitionChanged -= HandleDefinitionChanged;
            PathEditorChangeMonitor.UndoRedo -= HandleUndoRedo;
            PathEditorChangeMonitor.BeforeReload -= Clear;
            TRACKED_MULTI_PATHS.Clear();
            TRACKED_BY_ID.Clear();
            LIVE_IDS.Clear();
            PathLinkEditorUtility.ClearDuplicateWarnings();
        }

        #endregion


    }
}
#endif
