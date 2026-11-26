using System;
using Unity.Jobs;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// The published Surface2D field. Solver scratch (costs, queues, obstacle
    /// probes and influence masks) stays in the staging workspace; a commit
    /// copies the composed region into a new <see cref="FlowFieldField"/> and
    /// replaces the published one, as Volume3D does.
    /// </summary>
    internal sealed class FlowFieldSurfaceFieldStore
    {
        private readonly FlowFieldSpareArrays _spares = new FlowFieldSpareArrays();
        private FlowFieldField _field;

        // The request of the last build that published, whether or not its
        // cells changed; null after Clear.
        internal FlowFieldSurfaceRequest? CommittedRequest;

        internal FlowFieldField Field => _field;
        internal FlowFieldGridSpace Grid => _field?.Grid ?? default;
        internal FlowFieldSurfaceData Surface => _field?.Scene.Surface;
        internal bool HasActiveGoal => _field != null && _field.Goal.IsActive;
        internal int ResolvedGoalIndex => _field?.Goal.CellIndex ?? -1;
        internal int Capacity => _field?.Capacity ?? 0;
        internal bool IsValid => _field != null && Grid.IsValid && Surface != null && Surface.IsValid;

        private static unsafe bool CopyRegion(
            int width,
            FlowFieldSurfaceWorkspace workspace,
            FlowFieldField target,
            bool includeBase,
            int minX,
            int maxX,
            int minZ,
            int maxZ)
        {
            var pins = new FlowFieldPinnedArrays(FlowFieldSurfaceFieldCommitJob.PinnedArrayCount);
            var changed = new bool[1];
            try
            {
                new FlowFieldSurfaceFieldCommitJob
                {
                    Blocked = pins.Pin(target.Agent.Blocked),
                    BlockedSource = pins.Pin(workspace.Blocked),
                    Topology = pins.Pin(target.Goal.Topology),
                    TopologySource = pins.Pin(workspace.TopologyMasks),
                    GoalFlags = pins.Pin(target.Goal.Flags),
                    GoalFlagsSource = pins.Pin(workspace.GoalFlags),
                    Next = pins.Pin(target.Goal.NextCells),
                    NextSource = pins.Pin(workspace.NextCells),
                    BaseDirections = pins.Pin(target.Goal.BaseDirections),
                    BaseDirectionsSource = pins.Pin(workspace.BaseDirections),
                    BaseSpeeds = pins.Pin(target.Goal.BaseSpeeds),
                    BaseSpeedsSource = pins.Pin(workspace.BaseSpeedMultipliers),
                    FinalDirections = pins.Pin(target.Goal.FinalDirections),
                    FinalDirectionsSource = pins.Pin(workspace.FinalDirections),
                    FinalSpeeds = pins.Pin(target.Goal.FinalSpeeds),
                    FinalSpeedsSource = pins.Pin(workspace.FinalSpeedMultipliers),
                    Influence = pins.Pin(target.Goal.ModifierInfluence),
                    InfluenceSource = pins.Pin(workspace.ModifierInfluence),
                    Changed = pins.Pin(changed),
                    Width = width,
                    MinX = minX,
                    MaxX = maxX,
                    MinZ = minZ,
                    MaxZ = maxZ,
                    IncludeBase = includeBase,
                    Epsilon = Mathf.Epsilon,
                }.Run();
            }
            finally
            {
                pins.Release();
            }
            return changed[0];
        }

        internal bool CommitFromWorkspace(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldSurfaceWorkspace workspace,
            bool includeBase,
            FlowFieldCellRect region)
        {
            if (workspace == null || !grid.IsValid || surface == null || !surface.IsValid
                || workspace.Capacity != grid.CellCount)
                throw new ArgumentException("Committed field input is invalid.");

            // Runtime Goal/Modifier commits reuse the same immutable Surface
            // instance. Keep that fast path explicit so those commits never
            // scan every Surface sample just to discover that the geometry is
            // unchanged. A content comparison is still required when an
            // adapter supplies a different instance for the same Grid.
            FlowFieldField previous = _field;
            bool sameSurface = ReferenceEquals(Surface, surface)
                || Surface != null && Surface.ContentEquals(surface);
            bool full = previous == null || Capacity != grid.CellCount || !Grid.MatchesBounds(grid)
                || !sameSurface;
            if (full)
            {
                region = FlowFieldCellRect.Full(grid);
                includeBase = true;
            }

            // A full commit writes every cell of each array, and a partial one
            // copies the published field first, so no spare value survives.
            // The commit job compares the old values only to report a change,
            // which a full commit reports anyway.
            FlowFieldField next = TakeField(grid, surface, grid.CellCount, workspace);
            if (!full)
                CopyAll(previous, next);

            int minX = 0, maxX = grid.Width - 1, minZ = 0, maxZ = grid.Depth - 1;
            if (!full && region.IsValid)
            {
                minX = Mathf.Clamp(region.MinX, 0, grid.Width - 1);
                maxX = Mathf.Clamp(region.MaxX, 0, grid.Width - 1);
                minZ = Mathf.Clamp(region.MinZ, 0, grid.Depth - 1);
                maxZ = Mathf.Clamp(region.MaxZ, 0, grid.Depth - 1);
            }
            bool any = full;
            any |= CopyRegion(grid.Width, workspace, next, includeBase, minX, maxX, minZ, maxZ);
            any |= previous == null
                || previous.Goal.IsActive != next.Goal.IsActive
                || previous.Goal.CellIndex != next.Goal.CellIndex;

            if (!any)
            {
                // Equal content may arrive with a new immutable Surface
                // snapshot. Keep the published identity aligned without
                // inventing a cell change, and give the copy back.
                FlowFieldField aligned = Share(previous, grid, surface);
                _spares.Retire(next, aligned);
                _field = aligned;
                return false;
            }

            _spares.Retire(previous, next);
            _field = next;
            return true;
        }

        private FlowFieldField TakeField(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            int count,
            FlowFieldSurfaceWorkspace workspace)
            => new FlowFieldField(
                grid,
                new FlowFieldSceneLayer(surface),
                new FlowFieldAgentLayer(
                    FlowFieldSpareArrays.Take(ref _spares.Blocked, count),
                    escapeDirections: null),
                new FlowFieldGoalLayer(
                    FlowFieldSpareArrays.Take(ref _spares.Topology, count),
                    FlowFieldSpareArrays.Take(ref _spares.GoalFlags, count),
                    FlowFieldSpareArrays.Take(ref _spares.Next, count),
                    FlowFieldSpareArrays.Take(ref _spares.Directions, count),
                    FlowFieldSpareArrays.Take(ref _spares.Speeds, count),
                    FlowFieldSpareArrays.Take(ref _spares.FinalDirections, count),
                    FlowFieldSpareArrays.Take(ref _spares.FinalSpeeds, count),
                    FlowFieldSpareArrays.Take(ref _spares.ModifierInfluence, count),
                    workspace.HasActiveGoal,
                    workspace.ResolvedGoalIndex));

        // The same agent and Goal layers on a new Surface snapshot.
        private static FlowFieldField Share(FlowFieldField field, FlowFieldGridSpace grid, FlowFieldSurfaceData surface)
            => new FlowFieldField(grid, new FlowFieldSceneLayer(surface), field.Agent, field.Goal);

        private static void CopyAll(FlowFieldField from, FlowFieldField to)
        {
            int count = from.Capacity;
            Array.Copy(from.Agent.Blocked, to.Agent.Blocked, count);
            Array.Copy(from.Goal.Topology, to.Goal.Topology, count);
            Array.Copy(from.Goal.Flags, to.Goal.Flags, count);
            Array.Copy(from.Goal.NextCells, to.Goal.NextCells, count);
            Array.Copy(from.Goal.BaseDirections, to.Goal.BaseDirections, count);
            Array.Copy(from.Goal.BaseSpeeds, to.Goal.BaseSpeeds, count);
            Array.Copy(from.Goal.FinalDirections, to.Goal.FinalDirections, count);
            Array.Copy(from.Goal.FinalSpeeds, to.Goal.FinalSpeeds, count);
            Array.Copy(from.Goal.ModifierInfluence, to.Goal.ModifierInfluence, count);
        }

        internal FlowFieldSurfaceWorkspace CreateWorkspaceSnapshot()
        {
            if (!IsValid)
                return null;
            FlowFieldSurfaceWorkspace workspace = new FlowFieldSurfaceWorkspace();
            workspace.Resize(Capacity);
            CopyToWorkspace(workspace);
            return workspace;
        }

        internal void CopyToWorkspace(FlowFieldSurfaceWorkspace workspace)
        {
            if (!IsValid || workspace == null || workspace.Capacity != Capacity)
                throw new ArgumentException("Committed workspace does not match the field store.", nameof(workspace));
            workspace.AssertWritable(nameof(CopyToWorkspace));
            Array.Copy(_field.Agent.Blocked, workspace.Blocked, Capacity);
            CopyGoalAndFieldToWorkspace(workspace);
            // Escape vectors are a diagnostic/bake concern rather than part
            // of the compact runtime field. Reconstruct them only when an
            // explicit workspace snapshot is requested.
            FlowFieldSurfaceNavigationPreparation.BuildEscapeDirections(Grid, Surface, workspace);
        }

        /// <summary>
        /// Copies the published Goal and field into staging and leaves the
        /// staged obstacle masks and escape directions as they are. A rejected
        /// build uses it, so its replacement prepares the Goal again on the
        /// obstacle query the rejected build already made.
        /// </summary>
        internal void CopyGoalAndFieldToWorkspace(FlowFieldSurfaceWorkspace workspace)
        {
            if (!IsValid || workspace == null || workspace.Capacity != Capacity)
                throw new ArgumentException("Committed workspace does not match the field store.", nameof(workspace));
            workspace.AssertWritable(nameof(CopyGoalAndFieldToWorkspace));
            FlowFieldGoalLayer goal = _field.Goal;
            for (int i = 0; i < Capacity; i++)
                workspace.TopologyMasks[i] = (byte)goal.Topology[i];
            Array.Copy(goal.Flags, workspace.GoalFlags, Capacity);
            Array.Copy(goal.NextCells, workspace.NextCells, Capacity);
            Array.Copy(goal.BaseDirections, workspace.BaseDirections, Capacity);
            Array.Copy(goal.BaseSpeeds, workspace.BaseSpeedMultipliers, Capacity);
            Array.Copy(goal.FinalDirections, workspace.FinalDirections, Capacity);
            Array.Copy(goal.FinalSpeeds, workspace.FinalSpeedMultipliers, Capacity);
            Array.Copy(goal.ModifierInfluence, workspace.ModifierInfluence, Capacity);
            workspace.LoadBakedGoal(HasActiveGoal, ResolvedGoalIndex, goal.NextCells);
            for (int i = 0; i < Capacity; i++)
            {
                workspace.GoalDirections[i] = (goal.Flags[i] & FlowFieldGoalFlags.Directed) != 0
                    ? goal.BaseDirections[i]
                    : Vector3.zero;
            }
        }

        internal void Clear()
        {
            CommittedRequest = null;
            _field = null;
            _spares.Clear();
        }
    }
}
