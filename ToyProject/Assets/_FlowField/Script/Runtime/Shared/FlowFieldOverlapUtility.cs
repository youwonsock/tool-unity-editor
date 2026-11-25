using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Non-allocating overlap queries both space modes share. A full buffer
    /// grows up to its cap; a query that still fills it reports that its list
    /// would be partial, and the caller decides what that means.
    /// </summary>
    internal static class FlowFieldOverlapUtility
    {
        private const int InitialOverlapCapacity = 8;
        // The cap of the cell and step queries. A Surface2D obstacle query
        // that reaches it faults the session; a Volume3D step falls back to
        // per-cell checks; a single target query throws.
        internal const int MaxOverlapHits = 4096;
        private const float CellQuerySkin = 0.0001f;

        /// <summary>
        /// Half extent of a cell's overlap box. Unity's overlap queries count a
        /// face-touching collider as an overlap; removing a tiny numerical skin
        /// keeps a collider aligned with the cell edges out of its neighbors
        /// while the configured clearance still applies to real intersections.
        /// </summary>
        internal static float CellHalfExtent(float cellSize, float clearance = 0f)
            => Mathf.Max(0.000001f, cellSize * 0.5f + clearance - CellQuerySkin);

        /// <summary>
        /// Whether a non-trigger <paramref name="target"/> overlaps the box. The
        /// cheap CheckBox skips the boxes no Collider of the layer reaches.
        /// </summary>
        internal static bool OverlapsSolidTarget(
            Vector3 center,
            Vector3 halfExtents,
            int layerMask,
            Collider target,
            ref Collider[] overlapBuffer)
            => Physics.CheckBox(center, halfExtents, Quaternion.identity, layerMask, QueryTriggerInteraction.Ignore)
                && OverlapsTarget(center, halfExtents, layerMask, target, QueryTriggerInteraction.Ignore, ref overlapBuffer);

        // Unlike OverlapBox it stops as soon as the target is among the hits.
        public static bool OverlapsTarget(
            Vector3 center,
            Vector3 halfExtents,
            int layerMask,
            Collider target,
            QueryTriggerInteraction triggerInteraction,
            ref Collider[] overlapBuffer)
        {
            EnsureCapacity(ref overlapBuffer, InitialOverlapCapacity, InitialOverlapCapacity, MaxOverlapHits);
            while (true)
            {
                int count = Physics.OverlapBoxNonAlloc(
                    center,
                    halfExtents,
                    overlapBuffer,
                    Quaternion.identity,
                    layerMask,
                    triggerInteraction);

                for (int i = 0; i < count; i++)
                {
                    if (overlapBuffer[i] == target)
                        return true;
                }

                if (count < overlapBuffer.Length)
                    return false;

                if (overlapBuffer.Length >= MaxOverlapHits)
                    throw new System.InvalidOperationException(
                        $"Target overlap count reached the limit of {MaxOverlapHits}.");

                EnsureCapacity(ref overlapBuffer, overlapBuffer.Length * 2, InitialOverlapCapacity, MaxOverlapHits);
            }
        }

        /// <summary>
        /// Overlaps an axis-aligned box into <paramref name="buffer"/>, ignoring
        /// triggers, and doubles the buffer from <paramref name="initialCapacity"/>
        /// until every hit fits. Returns the hit count, or -1 when
        /// <paramref name="maxCapacity"/> hits still fill it: the list would be partial.
        /// </summary>
        internal static int OverlapBox(
            Vector3 center,
            Vector3 halfExtents,
            int layerMask,
            ref Collider[] buffer,
            int initialCapacity,
            int maxCapacity)
        {
            EnsureCapacity(ref buffer, initialCapacity, initialCapacity, maxCapacity);
            while (true)
            {
                int count = Physics.OverlapBoxNonAlloc(
                    center,
                    halfExtents,
                    buffer,
                    Quaternion.identity,
                    layerMask,
                    QueryTriggerInteraction.Ignore);
                if (count < buffer.Length)
                    return count;
                if (buffer.Length >= maxCapacity)
                    return -1;
                EnsureCapacity(ref buffer, buffer.Length * 2, initialCapacity, maxCapacity);
            }
        }

        /// <summary>
        /// Grows a NonAlloc query buffer to at least <paramref name="minimumCapacity"/>,
        /// clamped to [<paramref name="initialCapacity"/>, <paramref name="maxCapacity"/>].
        /// </summary>
        internal static void EnsureCapacity(
            ref Collider[] overlapBuffer,
            int minimumCapacity,
            int initialCapacity,
            int maxCapacity)
        {
            int cappedCapacity = Mathf.Min(
                Mathf.Max(initialCapacity, minimumCapacity),
                maxCapacity);
            if (overlapBuffer != null && overlapBuffer.Length >= cappedCapacity)
                return;

            overlapBuffer = new Collider[cappedCapacity];
        }
    }
}
