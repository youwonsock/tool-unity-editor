using System;
using Unity.Jobs;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Compact committed representation.  Solver scratch (costs, queues,
    /// obstacle probes and influence masks) remains in the staging workspace;
    /// this store contains only data needed for sampling, diagnostics and a
    /// subsequent final-only composition.
    /// </summary>
    internal sealed class FlowFieldFieldStore
    {
        internal FlowFieldGridSpace Grid { get; private set; }
        internal FlowFieldSurfaceData Surface { get; private set; }
        internal bool HasActiveGoal { get; private set; }
        internal int ResolvedGoalIndex { get; private set; } = -1;
        internal int Capacity => _blocked?.Length ?? 0;
        internal bool IsValid => Capacity > 0 && Grid.IsValid && Surface != null && Surface.IsValid;

        private bool[] _blocked;
        private byte[] _topologyMasks;
        private FlowFieldGoalFlags[] _goalFlags;
        private int[] _nextCells;
        private Vector3[] _baseDirections;
        private float[] _baseSpeeds;
        private Vector3[] _finalDirections;
        private float[] _finalSpeeds;
        private bool[] _modifierInfluence;

        internal int[] NextCells => _nextCells;

        internal FlowFieldReadView CreateReadView()
            => FlowFieldReadView.CreateSurface(
                Grid,
                Surface,
                _blocked,
                _goalFlags,
                _finalDirections,
                _finalSpeeds,
                _modifierInfluence);

        private unsafe bool CopyRegion(
            int width,
            FlowFieldWorkspace workspace,
            bool includeBase,
            int minX,
            int maxX,
            int minZ,
            int maxZ)
        {
            var pins = new FlowFieldPinnedArrays(FlowFieldFieldCommitJob.PinnedArrayCount);
            var changed = new bool[1];
            try
            {
                new FlowFieldFieldCommitJob
                {
                    Blocked = pins.Pin(_blocked),
                    BlockedSource = pins.Pin(workspace.Blocked),
                    Topology = pins.Pin(_topologyMasks),
                    TopologySource = pins.Pin(workspace.TopologyMasks),
                    GoalFlags = pins.Pin(_goalFlags),
                    GoalFlagsSource = pins.Pin(workspace.GoalFlags),
                    Next = pins.Pin(_nextCells),
                    NextSource = pins.Pin(workspace.NextCells),
                    BaseDirections = pins.Pin(_baseDirections),
                    BaseDirectionsSource = pins.Pin(workspace.BaseDirections),
                    BaseSpeeds = pins.Pin(_baseSpeeds),
                    BaseSpeedsSource = pins.Pin(workspace.BaseSpeedMultipliers),
                    FinalDirections = pins.Pin(_finalDirections),
                    FinalDirectionsSource = pins.Pin(workspace.FinalDirections),
                    FinalSpeeds = pins.Pin(_finalSpeeds),
                    FinalSpeedsSource = pins.Pin(workspace.FinalSpeedMultipliers),
                    Influence = pins.Pin(_modifierInfluence),
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
            FlowFieldWorkspace workspace,
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
            bool sameSurface = ReferenceEquals(Surface, surface)
                || Surface != null && Surface.ContentEquals(surface);
            bool full = Capacity != grid.CellCount || !Grid.MatchesBounds(grid)
                || !sameSurface;
            if (full)
            {
                Resize(grid.CellCount);
                region = FlowFieldCellRect.Full(grid);
                includeBase = true;
            }

            bool any = full;
            int minX = 0, maxX = grid.Width - 1, minZ = 0, maxZ = grid.Depth - 1;
            if (!full && region.IsValid)
            {
                minX = Mathf.Clamp(region.MinX, 0, grid.Width - 1);
                maxX = Mathf.Clamp(region.MaxX, 0, grid.Width - 1);
                minZ = Mathf.Clamp(region.MinZ, 0, grid.Depth - 1);
                maxZ = Mathf.Clamp(region.MaxZ, 0, grid.Depth - 1);
            }
            any |= CopyRegion(grid.Width, workspace, includeBase, minX, maxX, minZ, maxZ);

            // Equal geometry may arrive in a new immutable snapshot. Keep
            // published identity/revision aligned without inventing a cell change.
            Grid = grid;
            Surface = surface;
            bool oldGoal = HasActiveGoal;
            int oldIndex = ResolvedGoalIndex;
            HasActiveGoal = workspace.HasActiveGoal;
            ResolvedGoalIndex = workspace.ResolvedGoalIndex;
            any |= oldGoal != HasActiveGoal || oldIndex != ResolvedGoalIndex;
            return any;
        }

        internal FlowFieldWorkspace CreateWorkspaceSnapshot()
        {
            if (!IsValid)
                return null;
            FlowFieldWorkspace workspace = new FlowFieldWorkspace();
            workspace.Resize(Capacity);
            CopyToWorkspace(workspace);
            return workspace;
        }

        internal void CopyToWorkspace(FlowFieldWorkspace workspace)
        {
            if (!IsValid || workspace == null || workspace.Capacity != Capacity)
                throw new ArgumentException("Committed workspace does not match the field store.", nameof(workspace));
            Array.Copy(_blocked, workspace.Blocked, Capacity);
            CopyGoalAndFieldToWorkspace(workspace);
            // Escape vectors are a diagnostic/bake concern rather than part
            // of the compact runtime field. Reconstruct them only when an
            // explicit workspace snapshot is requested.
            FlowFieldSolver.BuildEscapeDirections(Grid, Surface, workspace);
        }

        /// <summary>
        /// Restores the committed Goal/final view while leaving the caller's
        /// currently prepared obstacle layers and escape directions intact.
        /// This is used when a Goal-only request supersedes an in-flight BFS;
        /// the pending Goal will be prepared again without throwing away a
        /// valid obstacle query that was already performed for the active
        /// request.
        /// </summary>
        internal void CopyGoalAndFieldToWorkspace(FlowFieldWorkspace workspace)
        {
            if (!IsValid || workspace == null || workspace.Capacity != Capacity)
                throw new ArgumentException("Committed workspace does not match the field store.", nameof(workspace));
            Array.Copy(_topologyMasks, workspace.TopologyMasks, Capacity);
            Array.Copy(_goalFlags, workspace.GoalFlags, Capacity);
            Array.Copy(_nextCells, workspace.NextCells, Capacity);
            Array.Copy(_baseDirections, workspace.BaseDirections, Capacity);
            Array.Copy(_baseSpeeds, workspace.BaseSpeedMultipliers, Capacity);
            Array.Copy(_finalDirections, workspace.FinalDirections, Capacity);
            Array.Copy(_finalSpeeds, workspace.FinalSpeedMultipliers, Capacity);
            Array.Copy(_modifierInfluence, workspace.ModifierInfluence, Capacity);
            workspace.LoadBakedGoal(HasActiveGoal, ResolvedGoalIndex, _nextCells);
            for (int i = 0; i < Capacity; i++)
            {
                workspace.GoalDirections[i] = (_goalFlags[i] & FlowFieldGoalFlags.Directed) != 0
                    ? _baseDirections[i]
                    : Vector3.zero;
            }
        }

        internal bool TrySample(Vector3 worldPosition, out FlowFieldSample sample)
        {
            sample = FlowFieldSample.Stopped;
            if (!IsValid || !FlowFieldGridSpace.IsFinite(worldPosition)
                || !Grid.ContainsWorldPosition(worldPosition)
                || !Grid.TryWorldToLocal(worldPosition, out int x, out int z))
                return false;
            int index = Grid.ToFlatIndex(x, z);
            if (!Surface.IsSurfaceValid(index))
                return true;
            Vector3 normal = Surface.GetSurfaceNormal(index);
            Vector3 direction = _finalDirections[index];
            if (direction.sqrMagnitude > FlowFieldVectorUtility.DIRECTION_EPSILON_SQR)
                direction = Vector3.ProjectOnPlane(direction, normal).normalized;
            sample = new FlowFieldSample(direction, _finalSpeeds[index], normal, true, true);
            return true;
        }

        private void Resize(int count)
        {
            if (count <= 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            _blocked = new bool[count];
            _topologyMasks = new byte[count];
            _goalFlags = new FlowFieldGoalFlags[count];
            _nextCells = new int[count];
            _baseDirections = new Vector3[count];
            _baseSpeeds = new float[count];
            _finalDirections = new Vector3[count];
            _finalSpeeds = new float[count];
            _modifierInfluence = new bool[count];
            for (int i = 0; i < count; i++) _nextCells[i] = -1;
        }

        internal void Clear()
        {
            _blocked = null;
            _topologyMasks = null;
            _goalFlags = null;
            _nextCells = null;
            _baseDirections = null;
            _baseSpeeds = null;
            _finalDirections = null;
            _finalSpeeds = null;
            _modifierInfluence = null;
            Grid = default;
            Surface = null;
            HasActiveGoal = false;
            ResolvedGoalIndex = -1;
        }

    }
}
