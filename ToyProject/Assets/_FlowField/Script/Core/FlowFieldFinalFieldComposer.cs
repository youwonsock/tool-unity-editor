using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
    internal static class FlowFieldFinalFieldComposer
    {
        public static void Compose(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldWorkspace workspace,
            Vector3 defaultDirection,
            IReadOnlyList<FlowFieldModifierLayer> modifierLayers
            )
        {
            ValidateWorkspace(grid, surface, workspace);
            if (modifierLayers == null)
                throw new ArgumentNullException(nameof(modifierLayers));
            if (!FlowFieldGridSpace.IsFinite(defaultDirection)
                || defaultDirection.sqrMagnitude <= FlowFieldVectorUtility.DIRECTION_EPSILON_SQR)
                throw new ArgumentOutOfRangeException(nameof(defaultDirection));

            BuildBaseField(grid, surface, workspace, defaultDirection);
            ApplyModifiers(
                grid,
                surface,
                workspace,
                modifierLayers);
        }

        public static void ApplyModifiers(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldWorkspace workspace,
            IReadOnlyList<FlowFieldModifierLayer> modifierLayers)
            => ApplyModifiers(grid, surface, workspace, modifierLayers, FlowFieldCellRect.Full(grid));

        public static void ApplyModifiers(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldWorkspace workspace,
            IReadOnlyList<FlowFieldModifierLayer> modifierLayers,
            FlowFieldCellRect dirtyRegion)
        {
            ValidateWorkspace(grid, surface, workspace);
            if (modifierLayers == null)
                throw new ArgumentNullException(nameof(modifierLayers));

            if (!dirtyRegion.IsValid)
                dirtyRegion = FlowFieldCellRect.Full(grid);
            ClearFinalRegionFromBase(grid, workspace, dirtyRegion);
            if (modifierLayers.Count == 0)
                return;

            for (int layerIndex = 0; layerIndex < modifierLayers.Count; layerIndex++)
            {
                FlowFieldModifierLayer layer = modifierLayers[layerIndex];
                IFlowFieldVectorModifier modifier = layer.Modifier;
                bool[] influenceMask = layer.InfluenceMask;
                if (modifier == null || influenceMask == null || influenceMask.Length != grid.CellCount)
                    throw new ArgumentException("Modifier layer data is inconsistent.");

                if (layer.InfluenceIndices != null)
                    ApplyModifierIndices(grid, surface, workspace, modifier, layer.InfluenceIndices, dirtyRegion);
                else
                    ApplyModifierLayer(grid, surface, workspace, modifier, influenceMask, dirtyRegion);
            }
        }

        private static void ApplyModifierIndices(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldWorkspace workspace,
            IFlowFieldVectorModifier modifier,
            IReadOnlyList<int> indices,
            FlowFieldCellRect dirtyRegion)
        {
            if (indices == null)
                throw new ArgumentNullException(nameof(indices));
            for (int i = 0; i < indices.Count; i++)
            {
                if (indices[i] < 0 || indices[i] >= grid.CellCount)
                    throw new ArgumentException("Modifier influence index is outside the grid.", nameof(indices));
                int index = indices[i];
                grid.FromFlatIndex(index, out int x, out int z);
                if (x >= dirtyRegion.MinX && x <= dirtyRegion.MaxX
                    && z >= dirtyRegion.MinZ && z <= dirtyRegion.MaxZ)
                    ApplyModifierToCell(grid, surface, workspace, modifier, index);
            }
        }

        private static void ApplyModifierLayer(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldWorkspace workspace,
            IFlowFieldVectorModifier modifier,
            bool[] influenceMask,
            FlowFieldCellRect dirtyRegion)
        {
            for (int index = 0; index < grid.CellCount; index++)
            {
                if (!influenceMask[index])
                    continue;
                grid.FromFlatIndex(index, out int x, out int z);
                if (x >= dirtyRegion.MinX && x <= dirtyRegion.MaxX
                    && z >= dirtyRegion.MinZ && z <= dirtyRegion.MaxZ)
                    ApplyModifierToCell(grid, surface, workspace, modifier, index);
            }
        }

        private static void ApplyModifierToCell(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldWorkspace workspace,
            IFlowFieldVectorModifier modifier,
            int index)
        {
            if (!surface.IsSurfaceValid(index)
                || workspace.Blocked[index]
                || (workspace.GoalFlags[index] & (FlowFieldGoalFlags.Anchor | FlowFieldGoalFlags.Unreachable)) != 0)
                return;

            workspace.ModifierInfluence[index] = true;
            grid.FromFlatIndex(index, out int cellX, out int cellZ);
            Vector3 surfaceNormal = surface.GetSurfaceNormal(index);
            var context = new FlowFieldVectorModifierContext(
                index,
                cellX,
                cellZ,
                surface.GetCellCenter(grid, index),
                surfaceNormal,
                grid,
                (workspace.GoalFlags[index] & FlowFieldGoalFlags.Directed) != 0);
            var current = new FlowFieldVectorState(
                workspace.FinalDirections[index],
                workspace.FinalSpeedMultipliers[index]);
            FlowFieldVectorState candidate = modifier.Modify(in current, in context);
            FlowFieldVectorUtility.ValidateModifierOutput(in candidate, surfaceNormal);

            workspace.FinalDirections[index] = candidate.Direction;
            workspace.FinalSpeedMultipliers[index] = candidate.SpeedMultiplier;
        }

        private static void BuildBaseField(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldWorkspace workspace,
            Vector3 defaultDirection)
        {
            Array.Clear(workspace.ModifierInfluence, 0, grid.CellCount);
            for (int index = 0; index < grid.CellCount; index++)
            {
                if (!surface.IsSurfaceValid(index))
                {
                    workspace.BaseDirections[index] = Vector3.zero;
                    workspace.BaseSpeedMultipliers[index] = 0f;
                    workspace.FinalDirections[index] = Vector3.zero;
                    workspace.FinalSpeedMultipliers[index] = 0f;
                    continue;
                }

                Vector3 direction;
                bool isDefaultDirection = false;
                bool hasEscapeDirection = false;
                if (workspace.Blocked[index])
                {
                    direction = workspace.EscapeDirections[index];
                    hasEscapeDirection = FlowFieldGridSpace.IsFinite(direction)
                        && direction.sqrMagnitude > FlowFieldVectorUtility.DIRECTION_EPSILON_SQR;
                    if (!hasEscapeDirection)
                        direction = Vector3.zero;
                }
                else if ((workspace.GoalFlags[index] & FlowFieldGoalFlags.Unreachable) != 0)
                {
                    direction = Vector3.zero;
                }
                else if ((workspace.GoalFlags[index] & FlowFieldGoalFlags.Directed) != 0)
                {
                    direction = workspace.GoalDirections[index];
                }
                else
                {
                    direction = defaultDirection;
                    isDefaultDirection = true;
                }

                float speed = workspace.Blocked[index]
                    ? hasEscapeDirection ? 1f : 0f
                    : (workspace.GoalFlags[index] & FlowFieldGoalFlags.Unreachable) != 0
                        ? 0f
                        : 1f;
                var baseState = new FlowFieldVectorState(direction, speed);
                if (isDefaultDirection)
                {
                    direction = FlowFieldVectorUtility.ProjectDefaultOnSurface(
                        direction,
                        surface.GetSurfaceNormal(index));
                    baseState = new FlowFieldVectorState(direction, 1f);
                }
                if (!FlowFieldGridSpace.IsFinite(baseState.Direction)
                    || !FlowFieldGridSpace.IsFinite(baseState.SpeedMultiplier)
                    || baseState.SpeedMultiplier < 0f)
                    throw new ArgumentException("Base FlowField vector state is invalid.");

                workspace.BaseDirections[index] = baseState.Direction;
                workspace.BaseSpeedMultipliers[index] = baseState.SpeedMultiplier;
                workspace.FinalDirections[index] = baseState.Direction;
                workspace.FinalSpeedMultipliers[index] = baseState.SpeedMultiplier;
            }
        }

        internal static void ComposeRegion(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldWorkspace workspace,
            IReadOnlyList<FlowFieldModifierLayer> modifierLayers,
            FlowFieldCellRect dirtyRegion)
        {
            ValidateWorkspace(grid, surface, workspace);
            if (modifierLayers == null)
                throw new ArgumentNullException(nameof(modifierLayers));
            if (!dirtyRegion.IsValid)
                dirtyRegion = FlowFieldCellRect.Full(grid);
            ApplyModifiers(grid, surface, workspace, modifierLayers, dirtyRegion);
        }

        private static void ClearFinalRegionFromBase(
            FlowFieldGridSpace grid,
            FlowFieldWorkspace workspace,
            FlowFieldCellRect region)
        {
            int minX = Mathf.Clamp(region.MinX, 0, grid.Width - 1);
            int maxX = Mathf.Clamp(region.MaxX, 0, grid.Width - 1);
            int minZ = Mathf.Clamp(region.MinZ, 0, grid.Depth - 1);
            int maxZ = Mathf.Clamp(region.MaxZ, 0, grid.Depth - 1);
            for (int z = minZ; z <= maxZ; z++)
            for (int x = minX; x <= maxX; x++)
            {
                int index = grid.ToFlatIndex(x, z);
                workspace.FinalDirections[index] = workspace.BaseDirections[index];
                workspace.FinalSpeedMultipliers[index] = workspace.BaseSpeedMultipliers[index];
                workspace.ModifierInfluence[index] = false;
            }
        }

        private static void ValidateWorkspace(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldWorkspace workspace)
        {
            if (!grid.IsValid)
                throw new ArgumentException("FlowField compose requires a valid grid.", nameof(grid));
            if (!surface.IsValid)
                throw new ArgumentException("FlowField compose requires a valid surface bake.", nameof(surface));
            if (workspace == null)
                throw new ArgumentNullException(nameof(workspace));
            if (workspace.Capacity != grid.CellCount)
                throw new ArgumentException("FlowField compose workspace capacity must match the grid.", nameof(workspace));
            if (workspace.FinalDirections == null
                || workspace.FinalSpeedMultipliers == null
                || workspace.BaseDirections == null
                || workspace.BaseSpeedMultipliers == null
                || workspace.ModifierInfluence == null)
                throw new ArgumentException("FlowField compose workspace arrays are not initialized.", nameof(workspace));
        }
    }

    /// <summary>
    /// Direct cell sampler for the committed field. A position is resolved to
    /// its owning cell; no interpolation can leak a direction across a wall.
    /// </summary>
    internal static class FlowFieldCellSampler
    {
        internal static bool TrySample(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldWorkspace workspace,
            Vector3 worldPosition,
            out FlowFieldSample sample)
        {
            sample = FlowFieldSample.Stopped;
            if (!grid.IsValid
                || surface == null
                || !surface.IsValid
                || workspace == null
                || workspace.Capacity != grid.CellCount
                || !grid.TryWorldToLocal(worldPosition, out int x, out int z))
                return false;

            int index = grid.ToFlatIndex(x, z);
            if (!surface.IsSurfaceValid(index))
                return true;

            Vector3 normal = FlowFieldVectorUtility.ValidateSurfaceNormal(surface.GetSurfaceNormal(index));
            Vector3 direction = workspace.FinalDirections[index];
            if (direction.sqrMagnitude > FlowFieldVectorUtility.DIRECTION_EPSILON_SQR)
                direction = Vector3.ProjectOnPlane(direction, normal).normalized;
            else
                direction = Vector3.zero;

            sample = new FlowFieldSample(
                direction,
                workspace.FinalSpeedMultipliers[index],
                normal,
                true);
            return true;
        }

        internal static bool TrySampleEscape(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldWorkspace workspace,
            Vector3 worldPosition,
            out FlowFieldSample sample)
        {
            sample = FlowFieldSample.Stopped;
            if (!grid.IsValid
                || surface == null
                || !surface.IsValid
                || workspace == null
                || workspace.Capacity != grid.CellCount
                || workspace.EscapeDirections == null
                || workspace.EscapeDirections.Length != grid.CellCount
                || !grid.TryWorldToLocal(worldPosition, out int x, out int z))
                return false;

            int index = grid.ToFlatIndex(x, z);
            if (!workspace.Blocked[index])
                return false;
            if (!surface.IsSurfaceValid(index))
                return true;

            Vector3 normal = FlowFieldVectorUtility.ValidateSurfaceNormal(surface.GetSurfaceNormal(index));
            Vector3 direction = workspace.EscapeDirections[index];
            bool hasEscapeDirection = FlowFieldGridSpace.IsFinite(direction)
                && direction.sqrMagnitude > FlowFieldVectorUtility.DIRECTION_EPSILON_SQR;
            if (!hasEscapeDirection)
            {
                sample = new FlowFieldSample(Vector3.zero, 0f, normal, true);
                return true;
            }

            direction = Vector3.ProjectOnPlane(direction, normal);
            if (direction.sqrMagnitude > FlowFieldVectorUtility.DIRECTION_EPSILON_SQR)
                direction.Normalize();
            else
                direction = Vector3.zero;

            sample = new FlowFieldSample(direction, 1f, normal, true);
            return true;
        }
    }

    internal readonly struct FlowFieldModifierLayer
    {
        public IFlowFieldVectorModifier Modifier { get; }
        public bool[] InfluenceMask { get; }
        public IReadOnlyList<int> InfluenceIndices { get; }

        public FlowFieldModifierLayer(IFlowFieldVectorModifier modifier, bool[] influenceMask)
            : this(modifier, influenceMask, null)
        {
        }

        public FlowFieldModifierLayer(
            IFlowFieldVectorModifier modifier,
            bool[] influenceMask,
            IReadOnlyList<int> influenceIndices)
        {
            Modifier = modifier;
            InfluenceMask = influenceMask;
            InfluenceIndices = influenceIndices;
        }
    }
}
