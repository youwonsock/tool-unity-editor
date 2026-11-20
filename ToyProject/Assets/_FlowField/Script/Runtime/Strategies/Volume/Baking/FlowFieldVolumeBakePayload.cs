using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// One Volume3D bake: its metadata and its per-cell arrays. It does not
    /// own the arrays. A builder export hands over fresh copies, an asset its
    /// serialized arrays, and the validator reads either without cloning.
    /// </summary>
    internal readonly struct FlowFieldVolumeBakePayload
    {
        internal readonly FlowFieldVolumeBakeMetadata Metadata;
        internal readonly bool[] Blocked;
        internal readonly uint[] TopologyMasks;
        internal readonly FlowFieldGoalFlags[] GoalFlags;
        internal readonly int[] NextCells;
        internal readonly Vector3[] Directions;
        internal readonly float[] Speeds;
        internal readonly Vector3[] EscapeDirections;

        internal FlowFieldVolumeBakePayload(
            FlowFieldVolumeBakeMetadata metadata,
            bool[] blocked,
            uint[] topologyMasks,
            FlowFieldGoalFlags[] goalFlags,
            int[] nextCells,
            Vector3[] directions,
            float[] speeds,
            Vector3[] escapeDirections)
        {
            Metadata = metadata;
            Blocked = blocked;
            TopologyMasks = topologyMasks;
            GoalFlags = goalFlags;
            NextCells = nextCells;
            Directions = directions;
            Speeds = speeds;
            EscapeDirections = escapeDirections;
        }

        internal bool HasArrays => Blocked != null
            && TopologyMasks != null
            && GoalFlags != null
            && NextCells != null
            && Directions != null
            && Speeds != null
            && EscapeDirections != null;

        /// <summary>Whether every array is present and holds one entry per cell.</summary>
        internal bool HasCellCount(int count) => HasArrays
            && Blocked.Length == count
            && TopologyMasks.Length == count
            && GoalFlags.Length == count
            && NextCells.Length == count
            && Directions.Length == count
            && Speeds.Length == count
            && EscapeDirections.Length == count;
    }
}
