using UnityEngine;

namespace Common.FlowField.Editor
{
    /// <summary>
    /// Immutable identity of the field source used by a Volume3D view.  The
    /// identity deliberately excludes ordinary Goal, obstacle and Modifier
    /// changes so an already published result can remain visible while the
    /// next result is being built.
    /// </summary>
    internal readonly struct FlowFieldVolumeVisualizationSource
    {
        internal readonly FlowFieldGridSpace Grid;
        internal readonly Bounds WorldBounds;
        internal readonly FlowFieldBakeMode BakeMode;
        internal readonly FlowFieldVolumeBakeData BakeData;
        internal readonly int BakeRevision;
        internal readonly int BakeContentGeneration;
        internal readonly long ResultId;

        internal FlowFieldVolumeVisualizationSource(
            FlowFieldGridSpace grid,
            Bounds worldBounds,
            FlowFieldBakeMode bakeMode,
            FlowFieldVolumeBakeData bakeData,
            int bakeRevision,
            long resultId)
            : this(
                grid,
                worldBounds,
                bakeMode,
                bakeData,
                bakeRevision,
                bakeData != null ? bakeData.ContentGeneration : -1,
                resultId)
        {
        }

        internal FlowFieldVolumeVisualizationSource(
            FlowFieldGridSpace grid,
            Bounds worldBounds,
            FlowFieldBakeMode bakeMode,
            FlowFieldVolumeBakeData bakeData,
            int bakeRevision,
            int bakeContentGeneration,
            long resultId)
        {
            Grid = grid;
            WorldBounds = worldBounds;
            BakeMode = bakeMode;
            BakeData = bakeData;
            BakeRevision = bakeRevision;
            BakeContentGeneration = bakeContentGeneration;
            ResultId = resultId;
        }

        internal bool IsValid => Grid.IsValid
            && Grid.SpaceMode == FlowFieldSpaceMode.Volume3D
            && FlowFieldGridSpace.IsFinite(WorldBounds.center)
            && FlowFieldGridSpace.IsFinite(WorldBounds.size)
            && WorldBounds.size.x > 0f
            && WorldBounds.size.y > 0f
            && WorldBounds.size.z > 0f;

        internal bool HasSameDisplaySource(in FlowFieldVolumeVisualizationSource other)
            => Grid.MatchesBounds(other.Grid)
                && Approximately(WorldBounds, other.WorldBounds)
                && BakeMode == other.BakeMode
                && ReferenceEquals(BakeData, other.BakeData)
                && BakeRevision == other.BakeRevision
                && BakeContentGeneration == other.BakeContentGeneration;

        private static bool Approximately(Bounds left, Bounds right)
            => FlowFieldGridSpace.Approximately(left.center, right.center, 0.00000001d)
                && FlowFieldGridSpace.Approximately(left.size, right.size, 0.00000001d);
    }
}
