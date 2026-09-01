using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
    internal static class FlowFieldFinalFieldComposer
    {
        public static void Compose(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
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
            FlowFieldSurfaceBakeData surface,
            FlowFieldWorkspace workspace,
            IReadOnlyList<FlowFieldModifierLayer> modifierLayers)
        {
            ValidateWorkspace(grid, surface, workspace);
            if (modifierLayers == null)
                throw new ArgumentNullException(nameof(modifierLayers));

            Array.Clear(workspace.ModifierInfluence, 0, grid.CellCount);
            if (modifierLayers.Count == 0)
                return;

            for (int layerIndex = 0; layerIndex < modifierLayers.Count; layerIndex++)
            {
                FlowFieldModifierLayer layer = modifierLayers[layerIndex];
                IFlowFieldVectorModifier modifier = layer.Modifier;
                bool[] influenceMask = layer.InfluenceMask;
                if (modifier == null || influenceMask == null || influenceMask.Length != grid.CellCount)
                    throw new ArgumentException("Modifier layer data is inconsistent.");

                if (layer.InfluenceIndices != null && layer.InfluenceIndices.Count > 0)
                    ApplyModifierIndices(grid, surface, workspace, modifier, layer.InfluenceIndices);
                else
                    ApplyModifierLayer(grid, surface, workspace, modifier, influenceMask);
            }
        }

        private static void ApplyModifierIndices(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
            FlowFieldWorkspace workspace,
            IFlowFieldVectorModifier modifier,
            IReadOnlyList<int> indices)
        {
            if (indices == null)
                throw new ArgumentNullException(nameof(indices));
            for (int i = 0; i < indices.Count; i++)
            {
                if (indices[i] < 0 || indices[i] >= grid.CellCount)
                    throw new ArgumentException("Modifier influence index is outside the grid.", nameof(indices));
                ApplyModifierToCell(grid, surface, workspace, modifier, indices[i]);
            }
        }

        private static void ApplyModifierLayer(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
            FlowFieldWorkspace workspace,
            IFlowFieldVectorModifier modifier,
            bool[] influenceMask)
        {
            for (int index = 0; index < grid.CellCount; index++)
            {
                if (influenceMask[index])
                    ApplyModifierToCell(grid, surface, workspace, modifier, index);
            }
        }

        private static void ApplyModifierToCell(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
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
            FlowFieldSurfaceBakeData surface,
            FlowFieldWorkspace workspace,
            Vector3 defaultDirection)
        {
            Array.Clear(workspace.ModifierInfluence, 0, grid.CellCount);
            for (int index = 0; index < grid.CellCount; index++)
            {
                if (!surface.IsSurfaceValid(index))
                {
                    if (!workspace.HasActiveGoal)
                        workspace.NextCells[index] = -2;
                    workspace.FinalDirections[index] = Vector3.zero;
                    workspace.FinalSpeedMultipliers[index] = 0f;
                    continue;
                }

                Vector3 direction;
                bool isDefaultDirection = false;
                if (workspace.Blocked[index])
                {
                    if (!workspace.HasActiveGoal)
                        workspace.NextCells[index] = -2;
                    direction = workspace.EscapeDirections[index];
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
                    if (!workspace.HasActiveGoal)
                        workspace.NextCells[index] = -1;
                    direction = defaultDirection;
                    isDefaultDirection = true;
                }

                float speed = (workspace.GoalFlags[index] & FlowFieldGoalFlags.Unreachable) != 0
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

                workspace.FinalDirections[index] = baseState.Direction;
                workspace.FinalSpeedMultipliers[index] = baseState.SpeedMultiplier;
            }
        }

        private static void ValidateWorkspace(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
            FlowFieldWorkspace workspace)
        {
            if (!grid.IsValid)
                throw new ArgumentException("FlowField compose requires a valid grid.", nameof(grid));
            if (surface == null)
                throw new ArgumentNullException(nameof(surface));
            if (!surface.HasValidData)
                throw new ArgumentException("FlowField compose requires a valid surface bake.", nameof(surface));
            if (workspace == null)
                throw new ArgumentNullException(nameof(workspace));
            if (workspace.Capacity != grid.CellCount)
                throw new ArgumentException("FlowField compose workspace capacity must match the grid.", nameof(workspace));
            if (workspace.FinalDirections == null
                || workspace.FinalSpeedMultipliers == null
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
            FlowFieldSurfaceBakeData surface,
            FlowFieldWorkspace workspace,
            Vector3 worldPosition,
            out FlowFieldSample sample)
        {
            sample = FlowFieldSample.Stopped;
            if (!grid.IsValid
                || surface == null
                || !surface.HasValidData
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
