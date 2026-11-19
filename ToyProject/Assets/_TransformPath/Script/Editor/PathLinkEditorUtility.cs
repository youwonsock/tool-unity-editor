using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath.Editor
{
    /// <summary>Syncs one joint: the end of <paramref name="previous"/> and the start of <paramref name="current"/>.</summary>
    internal delegate bool PathJointSync(int index, PathData previous, PathData current,
        Transform previousEnd, Transform currentStart);

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
            IReadOnlyList<Transform> previousPoints = previous == null ? null : previous.PathPoints;
            IReadOnlyList<Transform> currentPoints = current == null ? null : current.PathPoints;
            if (previousPoints == null || currentPoints == null || previousPoints.Count == 0 || currentPoints.Count == 0)
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

        /// <summary>
        /// Runs <paramref name="sync"/> on every joint between two different PathData in a row and returns
        /// how many it changed. A PathData repeated in a row has no joint; it is skipped with one warning.
        /// </summary>
        public static int SyncJoints(MultiPathData owner, IReadOnlyList<PathData> pathDatas, PathJointSync sync)
        {
            int syncCount = 0;
            for (int i = 1; i < pathDatas.Count; i++)
            {
                PathData previous = pathDatas[i - 1];
                PathData current = pathDatas[i];
                if (previous == null || current == null)
                    continue;
                if (previous == current)
                {
                    WarnDuplicateLink(owner, i);
                    continue;
                }
                if (TryGetJointTransforms(previous, current, out Transform previousEnd, out Transform currentStart)
                    && sync(i, previous, current, previousEnd, currentStart))
                    syncCount++;
            }
            return syncCount;
        }

        /// <summary>Moves every segment start onto the end of the segment before it.</summary>
        public static int AlignAllJoints(MultiPathData multiPathData)
        {
            return multiPathData == null
                ? 0
                : SyncJoints(multiPathData, PathEditorSerializationUtility.GetPathDatas(multiPathData), AlignJoint);
        }

        public static bool AlignJoint(int index, PathData previous, PathData current, Transform previousEnd, Transform currentStart)
        {
            return TryAlignStartToEnd(previous, current, previousEnd, currentStart, POSITION_SYNC_EPSILON_SQR);
        }

        private static void WarnDuplicateLink(MultiPathData multiPathData, int segmentIndex)
        {
            if (multiPathData == null)
                return;

            string key = $"{multiPathData.GetInstanceID()}:{segmentIndex}";
            if (!DUPLICATE_WARNINGS.Add(key))
                return;

            Debug.LogWarning(
                $"MultiPathData: segments {segmentIndex - 1} and {segmentIndex} use the same PathData, so their joint is not linked.",
                multiPathData);
        }

        public static void ClearDuplicateWarnings()
        {
            DUPLICATE_WARNINGS.Clear();
        }

        #endregion
    }
}
