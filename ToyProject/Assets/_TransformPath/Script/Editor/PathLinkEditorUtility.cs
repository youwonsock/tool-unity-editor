using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath
{
    internal enum EPathLinkSyncMode
    {
        FollowMovement = 0,
        AlignStartToEnd = 1,
    }

    /// <summary>Shared edit-mode joint sync helpers for MultiPath path linking.</summary>
    internal static class PathLinkEditorUtility
    {
        #region Constants

        public const float POSITION_SYNC_EPSILON_SQR = 0.000001f;
        private const string SYNC_PATH_POINT_UNDO = "Sync Path Point";
        private const string SYNC_PATH_POINTS_UNDO = "Sync Path Points";

        #endregion


        #region Member Variables

        private static readonly HashSet<string> DUPLICATE_WARNINGS = new HashSet<string>();
        private static readonly Transform[] JointPoints = new Transform[2];
        private static readonly Vector3[] JointSnapshots = new Vector3[2];

        #endregion


        #region Public Methods

        public static bool TryGetJointTransforms(
            PathData previous,
            PathData current,
            out Transform previousEnd,
            out Transform currentStart)
        {
            previousEnd = null;
            currentStart = null;
            if (previous == null || current == null)
                return false;

            List<Transform> previousPoints = PathEditorSerializationUtility.GetPathPoints(previous);
            List<Transform> currentPoints = PathEditorSerializationUtility.GetPathPoints(current);
            if (previousPoints.Count == 0 || currentPoints.Count == 0)
                return false;

            previousEnd = previousPoints[previousPoints.Count - 1];
            currentStart = currentPoints[0];
            return previousEnd != null && currentStart != null;
        }

        /// <summary>After a drag, the joint's other point follows the one that moved (the start on a tie).</summary>
        public static bool TrySyncJointByMovement(
            PathData previous,
            PathData current,
            Transform previousEnd,
            Transform currentStart,
            Vector3 previousEndSnapshot,
            Vector3 currentStartSnapshot,
            float epsilonSqr)
        {
            if (previous == null || current == null || previousEnd == null || currentStart == null)
                return false;

            JointPoints[0] = currentStart;
            JointPoints[1] = previousEnd;
            JointSnapshots[0] = currentStartSnapshot;
            JointSnapshots[1] = previousEndSnapshot;
            int source = PathPointSync.FindMovedSource(JointPoints, JointSnapshots, epsilonSqr);
            if (source < 0)
                return false;
            if (PathPointSync.AlignTo(JointPoints, JointPoints[source], epsilonSqr, SYNC_PATH_POINT_UNDO) == 0)
                return false;
            PathEditorUndoUtility.MarkDirty(source == 0 ? previous : current);
            return true;
        }

        /// <summary>Moves the start of <paramref name="current"/> onto the end of <paramref name="previous"/>.</summary>
        public static bool TryAlignStartToEnd(
            PathData previous,
            PathData current,
            Transform previousEnd,
            Transform currentStart,
            float epsilonSqr)
        {
            if (previous == null || current == null || previousEnd == null || currentStart == null)
                return false;
            if ((currentStart.position - previousEnd.position).sqrMagnitude <= epsilonSqr)
                return false;

            PathPointSync.MovePoint(currentStart, previousEnd.position, SYNC_PATH_POINTS_UNDO);
            PathEditorUndoUtility.MarkDirty(current);
            return true;
        }

        public static int SyncAllJoints(
            MultiPathData multiPathData,
            EPathLinkSyncMode mode,
            float epsilonSqr,
            Func<int, int, Vector3> getSnapshotPosition = null)
        {
            if (multiPathData == null)
                return 0;

            List<PathData> pathDatas = PathEditorSerializationUtility.GetPathDatas(multiPathData);
            if (pathDatas.Count < 2)
                return 0;

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

                if (!TryGetJointTransforms(previous, current, out Transform previousEnd, out Transform currentStart))
                    continue;

                bool synced = false;
                if (mode == EPathLinkSyncMode.FollowMovement)
                {
                    if (getSnapshotPosition == null)
                        continue;

                    List<Transform> previousPoints = PathEditorSerializationUtility.GetPathPoints(previous);
                    synced = TrySyncJointByMovement(
                        previous,
                        current,
                        previousEnd,
                        currentStart,
                        getSnapshotPosition(i - 1, previousPoints.Count - 1),
                        getSnapshotPosition(i, 0),
                        epsilonSqr);
                }
                else if (mode == EPathLinkSyncMode.AlignStartToEnd)
                {
                    synced = TryAlignStartToEnd(
                        previous,
                        current,
                        previousEnd,
                        currentStart,
                        epsilonSqr);
                }

                if (synced)
                    syncCount++;
            }

            return syncCount;
        }

        public static void WarnDuplicateLink(
            MultiPathData multiPathData,
            int segmentIndex)
        {
            if (multiPathData == null)
                return;

            string key = $"{multiPathData.GetInstanceID()}:{segmentIndex}";
            if (!DUPLICATE_WARNINGS.Add(key))
                return;

            Debug.LogWarning(
                $"MultiPathData: segment {segmentIndex - 1}와 {segmentIndex}가 동일한 PathData를 참조해 자동 연결을 건너뜁니다.",
                multiPathData);
        }

        public static void ClearDuplicateWarnings()
        {
            DUPLICATE_WARNINGS.Clear();
        }

        #endregion
    }
}
