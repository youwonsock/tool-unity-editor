using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// An immutable copy of a validated Surface bake asset, made once per
    /// Revision. Builds read the bake through it; a request keeps the asset
    /// reference only to tell, with its Revision and content generation,
    /// whether two requests use the same bake.
    /// </summary>
    internal sealed class FlowFieldSurfaceBakeSnapshot
    {
        private readonly FlowFieldSurfaceSignature _signature;
        private readonly FlowFieldSurfaceData _surface;
        private readonly bool[] _blocked;
        private readonly Vector3[] _escapeDirections;
        private readonly byte[] _topologyMasks;
        private readonly Vector3[] _goalDirections;
        private readonly int[] _nextCells;

        internal int Revision { get; }
        internal bool HasGoal { get; }
        internal Vector3 RequestedGoalWorld { get; }
        internal float GoalInfluenceRadius { get; }
        internal int ResolvedGoalIndex { get; }
        internal bool HasValidData => _surface != null
            && _surface.IsValid
            && _blocked != null
            && _blocked.Length == _surface.Grid.CellCount;
        internal FlowFieldSurfaceData Surface => _surface;

        internal FlowFieldSurfaceBakeSnapshot(
            in FlowFieldSurfaceSignature signature,
            int revision,
            bool hasGoal,
            Vector3 requestedGoalWorld,
            float goalInfluenceRadius,
            int resolvedGoalIndex,
            FlowFieldSurfaceData surface,
            bool[] blocked,
            Vector3[] escapeDirections,
            byte[] topologyMasks,
            Vector3[] goalDirections,
            int[] nextCells)
        {
            if (!signature.Surface.IsValid || surface == null || !surface.IsValid)
                throw new ArgumentException("Surface2D Bake snapshot Surface is invalid.", nameof(surface));
            int count = signature.Surface.Grid.CellCount;
            if (!MatchesLength(blocked, count)
                || !MatchesLength(escapeDirections, count)
                || !MatchesLength(topologyMasks, count)
                || !MatchesLength(goalDirections, count)
                || !MatchesLength(nextCells, count))
                throw new ArgumentException("Surface2D Bake snapshot arrays do not match the grid.");
            if (!FlowFieldGridSpace.IsFinite(requestedGoalWorld)
                || !FlowFieldGridSpace.IsFinite(goalInfluenceRadius)
                || goalInfluenceRadius < 0f
                || signature.ObstacleLayer.value == 0
                || !FlowFieldGridSpace.IsFinite(signature.ObstacleCheckHeight)
                || signature.ObstacleCheckHeight <= 0f
                || !FlowFieldGridSpace.IsFinite(signature.ObstacleCheckCenterOffset)
                || !FlowFieldGridSpace.IsFinite(signature.ObstacleClearance)
                || signature.ObstacleClearance < 0f
                || hasGoal && (resolvedGoalIndex < 0 || resolvedGoalIndex >= count)
                || !hasGoal && resolvedGoalIndex != -1)
                throw new ArgumentException("Surface2D Bake snapshot Goal data is invalid.");

            _signature = signature;
            _surface = surface;
            Revision = revision;
            HasGoal = hasGoal;
            RequestedGoalWorld = requestedGoalWorld;
            GoalInfluenceRadius = goalInfluenceRadius;
            ResolvedGoalIndex = hasGoal ? resolvedGoalIndex : -1;
            _blocked = Clone(blocked);
            _escapeDirections = Clone(escapeDirections);
            _topologyMasks = Clone(topologyMasks);
            _goalDirections = Clone(goalDirections);
            _nextCells = Clone(nextCells);
        }

        /// <summary>
        /// True when the snapshot was made with these settings and holds
        /// valid data; otherwise the reason, the surface part first.
        /// </summary>
        internal bool Matches(in FlowFieldSurfaceSignature signature, out string reason)
        {
            if (!_signature.TryMatchBake(in signature, out reason))
                return false;
            if (!HasValidData)
                reason = "Surface2D Bake data is missing or invalid.";
            return HasValidData;
        }

        internal void CopyToWorkspace(FlowFieldGridSpace grid, FlowFieldSurfaceWorkspace workspace)
        {
            if (!HasValidData
                || workspace == null
                || !grid.IsValid
                || !grid.MatchesBounds(_signature.Surface.Grid)
                || workspace.Capacity != grid.CellCount)
                throw new ArgumentException("Surface2D Bake snapshot workspace does not match the grid.", nameof(workspace));

            Array.Copy(_blocked, workspace.StaticBlocked, _blocked.Length);
            Array.Clear(workspace.DynamicBlocked, 0, workspace.DynamicBlocked.Length);
            workspace.RebuildCombinedBlocked();
            Array.Copy(_escapeDirections, workspace.EscapeDirections, _escapeDirections.Length);
            Array.Copy(_topologyMasks, workspace.TopologyMasks, _topologyMasks.Length);
            // The Goal flags follow from NextCells, which LoadBakedGoal reads.
            Array.Copy(_goalDirections, workspace.GoalDirections, _goalDirections.Length);
            Array.Copy(_nextCells, workspace.NextCells, _nextCells.Length);
            workspace.LoadBakedGoal(HasGoal, ResolvedGoalIndex, _nextCells);
        }

        /// <summary>The baked obstacle mask, which a BakedBase static mask starts from.</summary>
        internal void CopyBlockedTo(bool[] destination)
        {
            if (destination == null || destination.Length != _blocked.Length)
                throw new ArgumentException("The destination does not match the baked grid.", nameof(destination));
            Array.Copy(_blocked, destination, _blocked.Length);
        }

        private static bool MatchesLength<T>(T[] values, int count)
            => values != null && values.Length == count;

        // A private copy, so a snapshot or an asset never shares an array with
        // a workspace; a missing array becomes an empty one, which validation rejects.
        internal static T[] Clone<T>(T[] source)
            => source == null ? Array.Empty<T>() : (T[])source.Clone();
    }
}
