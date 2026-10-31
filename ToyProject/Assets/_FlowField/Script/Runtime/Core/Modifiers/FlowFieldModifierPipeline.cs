using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
    internal readonly struct FlowFieldModifierBuildRequest
    {
        internal Func<bool> IsCurrent { get; }
        public FlowFieldGridSpace Grid { get; }
        public FlowFieldSurfaceData Surface { get; }
        public FlowFieldWorkspace Workspace { get; }
        public float ObstacleCheckHeight { get; }
        public float ObstacleCheckCenterOffset { get; }

        public FlowFieldModifierBuildRequest(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldWorkspace workspace,
            float obstacleCheckHeight,
            float obstacleCheckCenterOffset,
            Func<bool> isCurrent = null)
        {
            IsCurrent = isCurrent;
            Grid = grid;
            Surface = surface;
            Workspace = workspace;
            ObstacleCheckHeight = obstacleCheckHeight;
            ObstacleCheckCenterOffset = obstacleCheckCenterOffset;
        }
    }

    internal sealed class FlowFieldModifierPipeline
    {
        private readonly FlowFieldModifierRegistry _registry;
        private readonly List<FlowFieldModifierLayer> _runtimeLayers = new List<FlowFieldModifierLayer>(16);
        private Collider[] _overlapBuffer;

        internal FlowFieldModifierPipeline(FlowFieldModifierRegistry registry)
        {
            if (registry == null)
                throw new ArgumentNullException(nameof(registry));
            _registry = registry;
        }

        // A false result means cancellation; an invalid changedRegion means
        // a completed pass found no effective mask change.
        internal bool RebuildAreaData(
            in FlowFieldModifierBuildRequest request,
            out FlowFieldCellRect changedRegion)
        {
            changedRegion = FlowFieldCellRect.Invalid;
            ValidateAreaRequest(request);
            if (_registry.Entries.Count == 0)
                return true;

            bool hasDirtyArea = false;
            for (int i = 0; i < _registry.Entries.Count; i++)
            {
                if (_registry.Entries[i].AreaDirty)
                {
                    hasDirtyArea = true;
                    break;
                }
            }

            if (!hasDirtyArea)
                return true;

            Physics.SyncTransforms();
            for (int i = 0; i < _registry.Entries.Count; i++)
            {
                FlowFieldModifierRegistry.Entry entry = _registry.Entries[i];
                if (!entry.AreaDirty)
                    continue;

                FlowFieldCellRect previousRect = entry.InfluenceRect;
                ResizeRuntimeMask(entry, request.Grid.CellCount);
                Collider collider;
                try { collider = entry.Modifier.InfluenceCollider; }
                catch (Exception) when (request.IsCurrent?.Invoke() == false)
                {
                    return false;
                }
                if (request.IsCurrent?.Invoke() == false)
                    return false;

                FlowFieldModifierMaskBuilder.Build(
                    request.Grid,
                    request.Surface,
                    collider,
                    request.ObstacleCheckHeight,
                    request.ObstacleCheckCenterOffset,
                    entry.InfluenceScratch,
                    ref _overlapBuffer);

                bool maskChanged = !FlowFieldObstacleMaskBuilder.AreEqual(
                    entry.InfluenceMask,
                    entry.InfluenceScratch);
                if (maskChanged)
                {
                    Array.Copy(entry.InfluenceScratch, entry.InfluenceMask, entry.InfluenceMask.Length);
                }

                if (maskChanged || !entry.InfluenceIndicesBuilt)
                {
                    RebuildInfluenceIndices(entry, request.Grid);
                }

                if (maskChanged)
                    changedRegion = FlowFieldCellRect.Union(
                        changedRegion,
                        FlowFieldCellRect.Union(previousRect, entry.InfluenceRect));

                entry.InfluenceCollider = collider;
                _registry.UpdateColliderSnapshot(entry, collider);
                entry.AreaDirty = false;
            }

            return true;
        }

        private static void ValidateAreaRequest(in FlowFieldModifierBuildRequest request)
        {
            if (!request.Grid.IsValid)
                throw new ArgumentException("Modifier rebuild requires a valid grid.", nameof(request));
            if (request.Surface == null || !request.Surface.IsValid)
                throw new ArgumentException("Modifier rebuild requires a valid surface bake.", nameof(request.Surface));
            if (request.Workspace == null)
                throw new ArgumentNullException(nameof(request.Workspace));
            if (request.Workspace.Capacity != request.Grid.CellCount)
                throw new ArgumentException("Modifier workspace capacity must match the grid.", nameof(request.Workspace));
            if (!FlowFieldGridSpace.IsFinite(request.ObstacleCheckHeight) || request.ObstacleCheckHeight <= 0f)
                throw new ArgumentOutOfRangeException(nameof(request.ObstacleCheckHeight));
            if (!FlowFieldGridSpace.IsFinite(request.ObstacleCheckCenterOffset))
                throw new ArgumentOutOfRangeException(nameof(request.ObstacleCheckCenterOffset));
        }

        private static void RebuildInfluenceIndices(FlowFieldModifierRegistry.Entry entry, FlowFieldGridSpace grid)
        {
            entry.InfluenceIndices.Clear();
            // The rect comes from the mask, not the collider bounds: a
            // cell that only touches the collider belongs to the mask,
            // and a partial composition rewrites nothing outside the rect.
            FlowFieldCellRect maskRect = FlowFieldCellRect.Invalid;
            for (int index = 0; index < entry.InfluenceMask.Length; index++)
            {
                if (!entry.InfluenceMask[index])
                    continue;
                entry.InfluenceIndices.Add(index);
                grid.FromFlatIndex(index, out int x, out int z);
                maskRect = FlowFieldCellRect.Union(maskRect, new FlowFieldCellRect
                {
                    MinX = x,
                    MaxX = x,
                    MinY = 0,
                    MaxY = grid.Height - 1,
                    MinZ = z,
                    MaxZ = z,
                });
            }
            entry.InfluenceIndicesBuilt = true;
            entry.InfluenceRect = maskRect;
        }

        internal bool RebuildFinalField(
            in FlowFieldModifierBuildRequest request,
            Vector3 defaultDirection,
            FlowFieldCellRect dirty,
            bool rebuildBase = true)
        {
            if (!request.Grid.IsValid)
                throw new ArgumentException("Modifier compose requires a valid grid.", nameof(request));
            if (request.Surface == null || !request.Surface.IsValid)
                throw new ArgumentException("Modifier compose requires a valid surface bake.", nameof(request.Surface));
            if (request.Workspace == null)
                throw new ArgumentNullException(nameof(request.Workspace));
            if (request.Workspace.Capacity != request.Grid.CellCount)
                throw new ArgumentException("Modifier workspace capacity must match the grid.", nameof(request.Workspace));
            if (!FlowFieldGridSpace.IsFinite(request.ObstacleCheckHeight) || request.ObstacleCheckHeight <= 0f)
                throw new ArgumentOutOfRangeException(nameof(request.ObstacleCheckHeight));
            if (!FlowFieldGridSpace.IsFinite(request.ObstacleCheckCenterOffset))
                throw new ArgumentOutOfRangeException(nameof(request.ObstacleCheckCenterOffset));

            _registry.BeginComposition();
            try
            {
                if (!dirty.IsValid)
                    dirty = FlowFieldCellRect.Full(request.Grid);

                if (!BuildRuntimeLayers(request.Grid.CellCount, request.IsCurrent))
                    return false;
                if (rebuildBase)
                {
                    return FlowFieldFinalFieldComposer.Compose(
                        request.Grid,
                        request.Surface,
                        request.Workspace,
                        defaultDirection,
                        _runtimeLayers,
                        request.IsCurrent);
                }
                else
                {
                    return FlowFieldFinalFieldComposer.ApplyModifiers(
                        request.Grid,
                        request.Surface,
                        request.Workspace,
                        _runtimeLayers,
                        dirty,
                        request.IsCurrent);
                }
            }
            finally
            {
                _registry.EndComposition();
            }
        }

        internal void Clear()
        {
            _runtimeLayers.Clear();
            _overlapBuffer = null;
        }

        private bool BuildRuntimeLayers(int cellCount, Func<bool> isCurrent)
        {
            _runtimeLayers.Clear();
            for (int i = 0; i < _registry.Entries.Count; i++)
            {
                FlowFieldModifierRegistry.Entry entry = _registry.Entries[i];
                if (FlowFieldVectorUtility.IsMissingModifier(entry.Modifier)
                    || entry.InfluenceMask == null
                    || entry.InfluenceMask.Length != cellCount)
                    throw new InvalidOperationException("Registered modifier data is inconsistent.");

                if (!entry.InfluenceIndicesBuilt)
                {
                    for (int index = 0; index < entry.InfluenceMask.Length; index++)
                    {
                        if (entry.InfluenceMask[index])
                            entry.InfluenceIndices.Add(index);
                    }
                    entry.InfluenceIndicesBuilt = true;
                }

                IFlowFieldModifierSnapshot snapshot = entry.Modifier.CaptureSnapshot();
                if (isCurrent?.Invoke() == false)
                    return false;
                if (snapshot == null)
                    throw new InvalidOperationException(
                        $"FlowField modifier '{entry.Modifier.GetType().Name}' returned a null snapshot.");

                _runtimeLayers.Add(new FlowFieldModifierLayer(
                    snapshot,
                    entry.InfluenceMask,
                    entry.InfluenceIndices));
            }
            return true;
        }

        private static void ResizeRuntimeMask(
            FlowFieldModifierRegistry.Entry entry,
            int cellCount)
        {
            if (entry.InfluenceMask != null && entry.InfluenceMask.Length == cellCount)
                return;

            entry.InfluenceMask = new bool[cellCount];
            entry.InfluenceScratch = new bool[cellCount];
            entry.InfluenceIndices ??= new List<int>(64);
            entry.InfluenceIndices.Clear();
            entry.InfluenceIndicesBuilt = false;
        }
    }
}
