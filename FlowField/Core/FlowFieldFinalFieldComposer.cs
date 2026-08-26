using System;
using System.Collections.Generic;
using UnityEngine;

namespace Supercent.Common.FlowField
{
    internal static class FlowFieldFinalFieldComposer
    {
        public static bool TryCompose(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
            FlowFieldWorkspace workspace,
            Vector3 defaultDirection,
            IReadOnlyList<FlowFieldModifierLayer> modifierLayers,
            out IFlowFieldVectorModifier faultedModifier,
            out Exception exception)
        {
            faultedModifier = null;
            exception = null;
            if (!IsWorkspaceValid(grid, surface, workspace))
                return false;

            InitializeBaseField(grid, surface, workspace, defaultDirection);
            return TryApplyModifiers(
                grid,
                surface,
                workspace,
                modifierLayers,
                out faultedModifier,
                out exception);
        }

        public static bool TryApplyModifiers(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
            FlowFieldWorkspace workspace,
            IReadOnlyList<FlowFieldModifierLayer> modifierLayers,
            out IFlowFieldVectorModifier faultedModifier,
            out Exception exception)
        {
            faultedModifier = null;
            exception = null;
            if (!IsWorkspaceValid(grid, surface, workspace))
                return false;

            Array.Clear(workspace.ModifierInfluence, 0, grid.CellCount);
            if (modifierLayers == null || modifierLayers.Count == 0)
                return true;

            for (int layerIndex = 0; layerIndex < modifierLayers.Count; layerIndex++)
            {
                FlowFieldModifierLayer layer = modifierLayers[layerIndex];
                IFlowFieldVectorModifier modifier = layer.Modifier;
                bool[] influenceMask = layer.InfluenceMask;
                if (modifier == null || influenceMask == null || influenceMask.Length != grid.CellCount)
                    continue;

                try
                {
                    if (layer.InfluenceIndices != null && layer.InfluenceIndices.Count > 0)
                        ApplyModifierIndices(grid, surface, workspace, modifier, layer.InfluenceIndices);
                    else
                        ApplyModifierLayer(grid, surface, workspace, modifier, influenceMask);
                }
                catch (Exception caught)
                {
                    faultedModifier = modifier;
                    exception = caught;
                    return false;
                }
            }

            return true;
        }

        private static void ApplyModifierIndices(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
            FlowFieldWorkspace workspace,
            IFlowFieldVectorModifier modifier,
            IReadOnlyList<int> indices)
        {
            for (int i = 0; i < indices.Count; i++)
                ApplyModifierToCell(grid, surface, workspace, modifier, indices[i]);
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
                || (workspace.GoalFlags[index] & FlowFieldGoalFlags.Anchor) != 0)
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
            if (!FlowFieldVectorUtility.TrySanitizeOnSurface(candidate, surfaceNormal, out FlowFieldVectorState next))
                return;

            workspace.FinalDirections[index] = next.Direction;
            workspace.FinalSpeedMultipliers[index] = next.SpeedMultiplier;
        }

        private static void InitializeBaseField(
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
                    workspace.FinalDirections[index] = Vector3.zero;
                    workspace.FinalSpeedMultipliers[index] = 1f;
                    continue;
                }

                Vector3 direction;
                bool isDefaultDirection = false;
                if (workspace.Blocked[index])
                {
                    direction = workspace.EscapeDirections[index];
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

                var baseState = new FlowFieldVectorState(direction, 1f);
                FlowFieldVectorState sanitized;
                if (isDefaultDirection)
                {
                    direction = FlowFieldVectorUtility.ProjectDefaultOnSurface(
                        direction,
                        surface.GetSurfaceNormal(index));
                    baseState = new FlowFieldVectorState(direction, 1f);
                }

                if (!FlowFieldVectorUtility.TrySanitizeOnSurface(
                        baseState,
                        surface.GetSurfaceNormal(index),
                        out sanitized))
                    sanitized = FlowFieldVectorState.Stopped;

                workspace.FinalDirections[index] = sanitized.Direction;
                workspace.FinalSpeedMultipliers[index] = sanitized.SpeedMultiplier;
            }
        }

        private static bool IsWorkspaceValid(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
            FlowFieldWorkspace workspace)
            => grid.IsValid
                && surface != null
                && surface.HasValidData
                && workspace != null
                && workspace.Capacity == grid.CellCount
                && workspace.FinalDirections != null
                && workspace.FinalSpeedMultipliers != null
                && workspace.ModifierInfluence != null;
    }
}
