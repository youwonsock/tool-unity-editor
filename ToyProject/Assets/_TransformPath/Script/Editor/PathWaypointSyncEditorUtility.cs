using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath
{
    internal static class PathWaypointSyncEditorUtility
    {
        #region Constants

        private const string SYNC_WAYPOINT_UNDO = "Sync Waypoint Group";

        private static readonly List<Transform> Moved = new List<Transform>();

        #endregion


        #region Public Methods

        /// <summary>After a drag, the group follows the member that moved the most.</summary>
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
            if (members == null || CountValid(members) < 2)
                return false;

            int source = PathPointSync.FindMovedSource(members, memberSnapshots, epsilonSqr);
            return source >= 0
                && AlignGroupToSource(group, members[source], epsilonSqr, transformToPathData, multiPathData);
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

            for (int i = 0; i < members.Length; i++)
            {
                if (members[i] != null)
                    return AlignGroupToSource(group, members[i], epsilonSqr, transformToPathData, multiPathData);
            }
            return false;
        }

        /// <summary>Moves every member onto the source and rebuilds the paths that changed.</summary>
        public static bool AlignGroupToSource(
            PathWaypointSyncGroup group,
            Transform source,
            float epsilonSqr,
            Dictionary<Transform, PathData> transformToPathData,
            MultiPathData multiPathData)
        {
            if (group == null || source == null || group.Members == null)
                return false;

            Moved.Clear();
            if (PathPointSync.AlignTo(group.Members, source, epsilonSqr, SYNC_WAYPOINT_UNDO, Moved) == 0)
                return false;
            var affectedPathDatas = new HashSet<PathData>();
            for (int i = 0; i < Moved.Count; i++)
                CollectPathData(Moved[i], transformToPathData, affectedPathDatas);
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
                    pathData.Rebuild();
                    PathEditorUndoUtility.MarkDirty(pathData);
                }
            }

            if (multiPathData == null)
                return;

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

        private static int CountValid(Transform[] members)
        {
            int count = 0;
            for (int i = 0; i < members.Length; i++)
            {
                if (members[i] != null)
                    count++;
            }
            return count;
        }

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
