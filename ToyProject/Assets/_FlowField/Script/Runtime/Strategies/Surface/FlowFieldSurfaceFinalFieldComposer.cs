using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Composes the final field in pieces, so a large field can spread over
    /// several frames. The workspace must stay untouched until it finishes.
    /// </summary>
    internal sealed class FlowFieldSurfaceComposeRun
    {
        private const int CellsPerCheck = 1024;

        private enum Stage
        {
            BaseField,
            ClearRegion,
            Layers,
            Done,
        }

        private FlowFieldGridSpace _grid;
        private FlowFieldSurfaceData _surface;
        private FlowFieldSurfaceWorkspace _workspace;
        private Vector3 _defaultDirection;
        private IReadOnlyList<FlowFieldSurfaceModifierLayer> _layers;
        private FlowFieldCellRect _region;
        private bool _rebuildBase;
        private Stage _stage;
        private int _cursor;
        private int _layerIndex;

        internal void Begin(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldSurfaceWorkspace workspace,
            Vector3 defaultDirection,
            IReadOnlyList<FlowFieldSurfaceModifierLayer> layers,
            FlowFieldCellRect region,
            bool rebuildBase)
        {
            FlowFieldSurfaceFinalFieldComposer.ValidateWorkspace(grid, surface, workspace);
            _grid = grid;
            _surface = surface;
            _workspace = workspace;
            _defaultDirection = defaultDirection;
            _layers = layers ?? throw new ArgumentNullException(nameof(layers));
            _region = region.IsValid ? region : FlowFieldCellRect.Full(grid);
            _rebuildBase = rebuildBase;
            if (rebuildBase
                && (!FlowFieldGridSpace.IsFinite(defaultDirection)
                    || defaultDirection.sqrMagnitude <= FlowFieldVectorUtility.DirectionEpsilonSqr))
                throw new ArgumentOutOfRangeException(nameof(defaultDirection));
            Restart();
        }

        /// <summary>
        /// Composes again from the start with the same inputs. The layers may
        /// have lost entries: what earlier steps applied is undone, because
        /// the base or the region is rebuilt first.
        /// </summary>
        internal void Restart()
        {
            _stage = _rebuildBase ? Stage.BaseField : Stage.ClearRegion;
            _cursor = 0;
            _layerIndex = 0;
            if (_rebuildBase)
                Array.Clear(_workspace.ModifierInfluence, 0, _grid.CellCount);
        }

        /// <summary>Returns true once the whole field is composed.</summary>
        internal bool Step(in FlowFieldBuildBudget budget)
        {
            while (_stage != Stage.Done)
            {
                switch (_stage)
                {
                    case Stage.BaseField:
                        StepBaseField();
                        break;
                    case Stage.ClearRegion:
                        StepClearRegion();
                        break;
                    case Stage.Layers:
                        StepLayers();
                        break;
                }
                if (_stage != Stage.Done && budget.IsExpired)
                    return false;
            }
            return true;
        }

        private void StepBaseField()
        {
            int end = Math.Min(_cursor + CellsPerCheck, _grid.CellCount);
            for (; _cursor < end; _cursor++)
                FlowFieldSurfaceFinalFieldComposer.BuildBaseCell(_surface, _workspace, _defaultDirection, _cursor);
            if (_cursor < _grid.CellCount)
                return;
            _stage = Stage.Layers;
            _cursor = 0;
        }

        // The cursor counts rows: a region's cells are contiguous only along X.
        private void StepClearRegion()
        {
            int minX = Mathf.Clamp(_region.MinX, 0, _grid.Width - 1);
            int maxX = Mathf.Clamp(_region.MaxX, 0, _grid.Width - 1);
            int minZ = Mathf.Clamp(_region.MinZ, 0, _grid.Depth - 1);
            int maxZ = Mathf.Clamp(_region.MaxZ, 0, _grid.Depth - 1);
            int rowsPerCheck = Math.Max(1, CellsPerCheck / (maxX - minX + 1));
            int z = minZ + _cursor;
            int end = Math.Min(z + rowsPerCheck, maxZ + 1);
            for (; z < end; z++)
            for (int x = minX; x <= maxX; x++)
            {
                int index = _grid.ToFlatIndex(x, z);
                _workspace.FinalDirections[index] = _workspace.BaseDirections[index];
                _workspace.FinalSpeedMultipliers[index] = _workspace.BaseSpeedMultipliers[index];
                _workspace.ModifierInfluence[index] = false;
            }
            _cursor = z - minZ;
            if (z <= maxZ)
                return;
            _stage = Stage.Layers;
            _cursor = 0;
        }

        private void StepLayers()
        {
            if (_layerIndex >= _layers.Count)
            {
                _stage = Stage.Done;
                return;
            }

            FlowFieldSurfaceModifierLayer layer = _layers[_layerIndex];
            if (layer.Snapshot == null
                || layer.InfluenceMask == null
                || layer.InfluenceMask.Length != _grid.CellCount
                || layer.InfluenceIndices == null)
                throw new ArgumentException("Modifier layer data is inconsistent.");

            if (StepIndexedLayer(layer))
            {
                _layerIndex++;
                _cursor = 0;
            }
        }

        private bool StepIndexedLayer(in FlowFieldSurfaceModifierLayer layer)
        {
            IReadOnlyList<int> indices = layer.InfluenceIndices;
            int end = Math.Min(_cursor + CellsPerCheck, indices.Count);
            for (; _cursor < end; _cursor++)
            {
                int index = indices[_cursor];
                if (index < 0 || index >= _grid.CellCount)
                    throw new ArgumentException("Modifier influence index is outside the grid.", nameof(layer));
                ApplyIfInRegion(layer.Snapshot, index);
            }
            return _cursor >= indices.Count;
        }

        private void ApplyIfInRegion(IFlowFieldModifierSnapshot modifier, int index)
        {
            _grid.FromFlatIndex(index, out int x, out int z);
            if (x >= _region.MinX && x <= _region.MaxX && z >= _region.MinZ && z <= _region.MaxZ)
                FlowFieldSurfaceFinalFieldComposer.ApplyModifierToCell(_grid, _surface, _workspace, modifier, index);
        }
    }

    internal static class FlowFieldSurfaceFinalFieldComposer
    {
        internal static void ApplyModifierToCell(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldSurfaceWorkspace workspace,
            IFlowFieldModifierSnapshot modifier,
            int index)
        {
            if (!surface.IsSurfaceValid(index)
                || !FlowFieldVectorUtility.CanApplyModifier(
                    workspace.Blocked[index],
                    workspace.GoalFlags[index],
                    workspace.BaseDirections[index]))
                return;

            grid.FromFlatIndex(index, out int cellX, out int cellZ);
            Vector3 surfaceNormal = surface.GetSurfaceNormal(index);
            var context = new FlowFieldVectorModifierContext(
                index,
                cellX,
                0,
                cellZ,
                surface.GetCellCenter(grid, index),
                surfaceNormal,
                grid,
                (workspace.GoalFlags[index] & FlowFieldGoalFlags.Directed) != 0);
            var current = new FlowFieldVectorState(
                workspace.FinalDirections[index],
                workspace.FinalSpeedMultipliers[index]);
            FlowFieldVectorState candidate = FlowFieldVectorUtility.ApplyModifier(modifier, in current, in context);

            workspace.ModifierInfluence[index] = true;
            workspace.FinalDirections[index] = candidate.Direction;
            workspace.FinalSpeedMultipliers[index] = candidate.SpeedMultiplier;
        }

        internal static void BuildBaseCell(
            FlowFieldSurfaceData surface,
            FlowFieldSurfaceWorkspace workspace,
            Vector3 defaultDirection,
            int index)
        {
            if (!surface.IsSurfaceValid(index))
            {
                workspace.BaseDirections[index] = Vector3.zero;
                workspace.BaseSpeedMultipliers[index] = 0f;
                workspace.FinalDirections[index] = Vector3.zero;
                workspace.FinalSpeedMultipliers[index] = 0f;
                return;
            }

            Vector3 direction;
            if (workspace.Blocked[index])
            {
                direction = workspace.EscapeDirections[index];
                if (!FlowFieldGridSpace.IsFinite(direction)
                    || direction.sqrMagnitude <= FlowFieldVectorUtility.DirectionEpsilonSqr)
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
                direction = FlowFieldVectorUtility.ProjectDefaultOnSurface(
                    defaultDirection,
                    surface.GetSurfaceNormal(index));
            }

            // Same rule as Volume3D: a cell without a base direction is stopped.
            float speed = direction.sqrMagnitude > FlowFieldVectorUtility.DirectionEpsilonSqr ? 1f : 0f;
            if (!FlowFieldGridSpace.IsFinite(direction))
                throw new ArgumentException("Base FlowField vector state is invalid.");

            workspace.BaseDirections[index] = direction;
            workspace.BaseSpeedMultipliers[index] = speed;
            workspace.FinalDirections[index] = direction;
            workspace.FinalSpeedMultipliers[index] = speed;
        }

        internal static void ValidateWorkspace(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldSurfaceWorkspace workspace)
        {
            if (!grid.IsValid)
                throw new ArgumentException("FlowField compose requires a valid grid.", nameof(grid));
            if (!surface.IsValid)
                throw new ArgumentException("FlowField compose requires valid Surface data.", nameof(surface));
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

    internal readonly struct FlowFieldSurfaceModifierLayer
    {
        public IFlowFieldVectorModifier Modifier { get; }
        public IFlowFieldModifierSnapshot Snapshot { get; }
        public bool[] InfluenceMask { get; }
        public IReadOnlyList<int> InfluenceIndices { get; }

        public FlowFieldSurfaceModifierLayer(
            IFlowFieldVectorModifier modifier,
            IFlowFieldModifierSnapshot snapshot,
            bool[] influenceMask,
            IReadOnlyList<int> influenceIndices)
        {
            Modifier = modifier;
            Snapshot = snapshot;
            InfluenceMask = influenceMask;
            InfluenceIndices = influenceIndices;
        }
    }
}
