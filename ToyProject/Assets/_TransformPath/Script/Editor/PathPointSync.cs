using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath.Editor
{
    /// <summary>
    /// The one rule both joint links and waypoint groups follow: points that
    /// should coincide follow the one the user moved. A joint is a group of two.
    /// </summary>
    internal static class PathPointSync
    {
        /// <summary>
        /// The index of the point to follow: the one that moved the most since
        /// the snapshot, the first on a tie. -1 when none moved.
        /// </summary>
        public static int FindMovedSource(IReadOnlyList<Transform> points, IReadOnlyList<Vector3> snapshots, float epsilonSqr)
        {
            int source = -1;
            float maxDeltaSqr = 0f;
            for (int i = 0; i < points.Count; i++)
            {
                if (points[i] == null || i >= snapshots.Count)
                    continue;
                float deltaSqr = (points[i].position - snapshots[i]).sqrMagnitude;
                if (deltaSqr <= epsilonSqr || deltaSqr <= maxDeltaSqr)
                    continue;
                maxDeltaSqr = deltaSqr;
                source = i;
            }
            return source;
        }

        /// <summary>Moves every other point onto the source. Adds the points it moved to <paramref name="moved"/>.</summary>
        public static int AlignTo(IReadOnlyList<Transform> points, Transform source, float epsilonSqr, string undoName,
            List<Transform> moved = null)
        {
            Vector3 target = source.position;
            int count = 0;
            for (int i = 0; i < points.Count; i++)
            {
                Transform point = points[i];
                if (point == null || point == source || (point.position - target).sqrMagnitude <= epsilonSqr)
                    continue;
                MovePoint(point, target, undoName);
                moved?.Add(point);
                count++;
            }
            return count;
        }

        public static void MovePoint(Transform point, Vector3 position, string undoName)
        {
            PathEditorUndoUtility.Record(point, undoName);
            point.position = position;
            PathEditorUndoUtility.MarkDirty(point);
        }
    }
}
