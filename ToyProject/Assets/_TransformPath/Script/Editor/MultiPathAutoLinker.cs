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

        private sealed class TrackedMultiPath : IPathTrackedTarget
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
            public bool IsAlive => Target != null;

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

                _autoLinkEnabled = Target.AutoLinkPathPoints;
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

        private static readonly PathTrackedTargets<MultiPathData, TrackedMultiPath> TARGETS =
            new PathTrackedTargets<MultiPathData, TrackedMultiPath>(
                target => new TrackedMultiPath(target),
                changed => changed is MultiPathData || changed is PathData,
                HandleUndoRedo);

        #endregion


        #region Private Methods

        static MultiPathAutoLinker()
        {
            TARGETS.Cleared += PathLinkEditorUtility.ClearDuplicateWarnings;
        }

        // An undo that kept every segment only needs the joints aligned again.
        private static void HandleUndoRedo(PathTrackedTargets<MultiPathData, TrackedMultiPath> targets)
        {
            IReadOnlyList<TrackedMultiPath> tracked = targets.Tracked;
            if (tracked.Count == 0)
            {
                targets.MarkDirty();
                return;
            }
            for (int i = 0; i < tracked.Count; i++)
            {
                if (tracked[i].Target == null)
                    continue;
                string liveSignature = PathEditorSerializationUtility.BuildPathConfigsSignature(tracked[i].Target);
                if (!string.Equals(tracked[i].CachedStructureSignature, liveSignature, StringComparison.Ordinal))
                {
                    targets.MarkDirty();
                    return;
                }
            }
            for (int i = 0; i < tracked.Count; i++)
            {
                if (tracked[i].Target != null)
                    tracked[i].RequestInitialSync();
            }
        }

        #endregion
    }
}
