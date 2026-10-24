#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath
{
    internal static class PathWaypointSyncEditorUtility
    {
        #region Constants

        private const string SYNC_WAYPOINT_UNDO = "Sync Waypoint Group";

        #endregion


        #region Public Methods

        public static bool TrySyncGroupByMovement(
            PathWaypointSyncGroup group,
            Vector3[] memberSnapshots,
            float epsilonSqr,
            Dictionary<Transform, PathData> transformToPathData,
            MultiPathData multiPathData)
        {
            if (group == null || !group.Enabled || memberSnapshots == null)
                return false;

            Transform[] members = group.Members;
            if (members == null || members.Length < 2)
                return false;

            int validCount = 0;
            for (int i = 0; i < members.Length; i++)
            {
                if (members[i] != null)
                    validCount++;
            }

            if (validCount < 2)
                return false;

            int soleMoverIndex = -1;
            int maxDeltaIndex = -1;
            float maxDeltaSqr = 0f;
            int movedCount = 0;

            for (int i = 0; i < members.Length; i++)
            {
                Transform member = members[i];
                if (member == null || i >= memberSnapshots.Length)
                    continue;

                float deltaSqr = (member.position - memberSnapshots[i]).sqrMagnitude;
                if (deltaSqr <= epsilonSqr)
                    continue;

                movedCount++;
                soleMoverIndex = i;
                if (deltaSqr > maxDeltaSqr)
                {
                    maxDeltaSqr = deltaSqr;
                    maxDeltaIndex = i;
                }
            }

            if (movedCount == 0)
                return false;

            int sourceIndex = movedCount == 1 ? soleMoverIndex : maxDeltaIndex;
            Transform source = members[sourceIndex];
            if (source == null)
                return false;

            Vector3 sourcePosition = source.position;
            HashSet<PathData> affectedPathDatas = new HashSet<PathData>();
            bool syncedAny = false;

            for (int i = 0; i < members.Length; i++)
            {
                Transform member = members[i];
                if (member == null || member == source)
                    continue;

                if ((member.position - sourcePosition).sqrMagnitude <= epsilonSqr)
                    continue;

                PathEditorUndoUtility.Record(member, SYNC_WAYPOINT_UNDO);
                member.position = sourcePosition;
                PathEditorUndoUtility.MarkDirty(member);
                CollectPathData(member, transformToPathData, affectedPathDatas);
                syncedAny = true;
            }

            if (!syncedAny)
                return false;

            CollectPathData(source, transformToPathData, affectedPathDatas);
            RebuildAffected(affectedPathDatas, multiPathData);
            return true;
        }

        public static bool AlignGroupToFirstMember(
            PathWaypointSyncGroup group,
            float epsilonSqr,
            Dictionary<Transform, PathData> transformToPathData,
            MultiPathData multiPathData)
        {
            if (group == null || !group.Enabled)
                return false;

            Transform[] members = group.Members;
            if (members == null || members.Length < 2)
                return false;

            Transform source = null;
            for (int i = 0; i < members.Length; i++)
            {
                if (members[i] != null)
                {
                    source = members[i];
                    break;
                }
            }

            if (source == null)
                return false;

            return AlignGroupToSource(
                group,
                source,
                epsilonSqr,
                transformToPathData,
                multiPathData);
        }

        public static bool AlignGroupToSource(
            PathWaypointSyncGroup group,
            Transform source,
            float epsilonSqr,
            Dictionary<Transform, PathData> transformToPathData,
            MultiPathData multiPathData)
        {
            if (group == null || source == null)
                return false;

            Transform[] members = group.Members;
            if (members == null || members.Length == 0)
                return false;

            Vector3 sourcePosition = source.position;
            HashSet<PathData> affectedPathDatas = new HashSet<PathData>();
            bool alignedAny = false;

            for (int i = 0; i < members.Length; i++)
            {
                Transform member = members[i];
                if (member == null || member == source)
                    continue;

                if ((member.position - sourcePosition).sqrMagnitude <= epsilonSqr)
                    continue;

                PathEditorUndoUtility.Record(member, SYNC_WAYPOINT_UNDO);
                member.position = sourcePosition;
                PathEditorUndoUtility.MarkDirty(member);
                CollectPathData(member, transformToPathData, affectedPathDatas);
                alignedAny = true;
            }

            if (!alignedAny)
                return false;

            CollectPathData(source, transformToPathData, affectedPathDatas);
            RebuildAffected(affectedPathDatas, multiPathData);
            return true;
        }

        public static Dictionary<Transform, PathData> BuildTransformToPathDataMap(
            MultiPathData multiPathData)
        {
            Dictionary<Transform, PathData> map = new Dictionary<Transform, PathData>();
            if (multiPathData == null)
                return map;

            List<PathData> pathDatas = PathEditorSerializationUtility.GetPathDatas(multiPathData);
            for (int pathIndex = 0; pathIndex < pathDatas.Count; pathIndex++)
            {
                PathData pathData = pathDatas[pathIndex];
                if (pathData == null)
                    continue;

                List<Transform> points = PathEditorSerializationUtility.GetPathPoints(pathData);
                for (int pointIndex = 0; pointIndex < points.Count; pointIndex++)
                {
                    Transform point = points[pointIndex];
                    if (point == null)
                        continue;
                    map[point] = pathData;
                }
            }

            return map;
        }

        public static void RebuildAffected(
            HashSet<PathData> affectedPathDatas,
            MultiPathData multiPathData)
        {
            if (affectedPathDatas != null)
            {
                foreach (PathData pathData in affectedPathDatas)
                {
                    if (pathData == null)
                        continue;
                    pathData.Init();
                    pathData.Rebuild();
                    PathEditorUndoUtility.MarkDirty(pathData);
                }
            }

            if (multiPathData == null)
                return;

            multiPathData.Init();
            multiPathData.Rebuild();
            PathEditorUndoUtility.MarkDirty(multiPathData);
        }

        public static int AlignAllGroups(
            PathWaypointSyncRegistry registry,
            float epsilonSqr)
        {
            if (registry == null)
                return 0;

            MultiPathData multiPathData = registry.GetComponent<MultiPathData>();
            Dictionary<Transform, PathData> transformToPathData =
                BuildTransformToPathDataMap(multiPathData);
            IReadOnlyList<PathWaypointSyncGroup> groups = registry.Groups;
            int alignCount = 0;

            for (int i = 0; i < groups.Count; i++)
            {
                PathWaypointSyncGroup group = groups[i];
                if (group == null || !group.Enabled)
                    continue;

                if (AlignGroupToFirstMember(
                        group,
                        epsilonSqr,
                        transformToPathData,
                        multiPathData))
                    alignCount++;
            }

            PathEditorUndoUtility.RepaintScene();
            return alignCount;
        }

        #endregion


        #region Private Methods

        private static void CollectPathData(
            Transform member,
            Dictionary<Transform, PathData> transformToPathData,
            HashSet<PathData> affectedPathDatas)
        {
            if (member == null || affectedPathDatas == null || transformToPathData == null)
                return;

            if (transformToPathData.TryGetValue(member, out PathData pathData) && pathData != null)
                affectedPathDatas.Add(pathData);
        }

        #endregion
    }
}
#endif
