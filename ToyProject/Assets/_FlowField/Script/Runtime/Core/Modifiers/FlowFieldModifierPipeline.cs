using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
    internal readonly struct FlowFieldModifierBuildRequest
    {
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
            float obstacleCheckCenterOffset)
        {
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

        internal bool RebuildAreaData(
            in FlowFieldModifierBuildRequest request,
            out bool changed,
            out FlowFieldCellRect changedRegion)
        {
            changed = false;
            changedRegion = FlowFieldCellRect.Invalid;
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
            if (_registry.Entries.Count == 0)
                return false;

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
                return false;

            Physics.SyncTransforms();
            for (int i = 0; i < _registry.Entries.Count; i++)
            {
                FlowFieldModifierRegistry.Entry entry = _registry.Entries[i];
                if (!entry.AreaDirty)
                    continue;

                FlowFieldSurfaceModifierState cache = entry.SurfaceState;
                if (cache == null)
                {
                    cache = new FlowFieldSurfaceModifierState();
                    entry.SurfaceState = cache;
                }

                FlowFieldCellRect previousRect = cache.InfluenceRect;
                ResizeRuntimeMask(cache, request.Grid.CellCount);
                Collider collider = entry.Modifier.InfluenceCollider;

                FlowFieldModifierMaskBuilder.Build(
                    request.Grid,
                    request.Surface,
                    collider,
                    request.ObstacleCheckHeight,
                    request.ObstacleCheckCenterOffset,
                    cache.InfluenceScratch,
                    ref _overlapBuffer);

                bool maskChanged = !FlowFieldObstacleMaskBuilder.AreEqual(
                    cache.InfluenceMask,
                    cache.InfluenceScratch);
                if (maskChanged)
                {
                    Array.Copy(cache.InfluenceScratch, cache.InfluenceMask, cache.InfluenceMask.Length);
                    changed = true;
                }

                if (maskChanged || !cache.InfluenceIndicesBuilt)
                {
                    cache.InfluenceIndices.Clear();
                    for (int index = 0; index < cache.InfluenceMask.Length; index++)
                        if (cache.InfluenceMask[index])
                            cache.InfluenceIndices.Add(index);
                    cache.InfluenceIndicesBuilt = true;
                }

                FlowFieldCellRect currentRect = collider != null
                    ? FlowFieldCellRect.FromBounds(request.Grid, collider.bounds)
                    : FlowFieldCellRect.Invalid;
                cache.InfluenceRect = currentRect;
                if (maskChanged)
                    changedRegion = FlowFieldCellRect.Union(
                        changedRegion,
                        FlowFieldCellRect.Union(previousRect, currentRect));

                entry.InfluenceCollider = collider;
                _registry.UpdateColliderSnapshot(entry, collider);
                entry.AreaDirty = false;
            }

            return changed;
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

                BuildRuntimeLayers(request.Grid.CellCount);
                if (rebuildBase)
                {
                    FlowFieldFinalFieldComposer.Compose(
                        request.Grid,
                        request.Surface,
                        request.Workspace,
                        defaultDirection,
                        _runtimeLayers);
                }
                else
                {
                    FlowFieldFinalFieldComposer.ComposeRegion(
                        request.Grid,
                        request.Surface,
                        request.Workspace,
                        _runtimeLayers,
                        dirty);
                }
                return true;
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

        private void BuildRuntimeLayers(int cellCount)
        {
            _runtimeLayers.Clear();
            for (int i = 0; i < _registry.Entries.Count; i++)
            {
                FlowFieldModifierRegistry.Entry entry = _registry.Entries[i];
                FlowFieldSurfaceModifierState cache = entry.SurfaceState;
                if (IsMissingModifier(entry.Modifier)
                    || cache == null
                    || cache.InfluenceMask == null
                    || cache.InfluenceMask.Length != cellCount)
                    throw new InvalidOperationException("Registered modifier data is inconsistent.");

                if (!cache.InfluenceIndicesBuilt)
                {
                    for (int index = 0; index < cache.InfluenceMask.Length; index++)
                    {
                        if (cache.InfluenceMask[index])
                            cache.InfluenceIndices.Add(index);
                    }
                    cache.InfluenceIndicesBuilt = true;
                }

                IFlowFieldModifierSnapshot snapshot = entry.Modifier.CaptureSnapshot();
                if (snapshot == null)
                    throw new InvalidOperationException(
                        $"FlowField modifier '{entry.Modifier.GetType().Name}' returned a null snapshot.");

                _runtimeLayers.Add(new FlowFieldModifierLayer(
                    snapshot,
                    cache.InfluenceMask,
                    cache.InfluenceIndices));
            }
        }

        private static void ResizeRuntimeMask(
            FlowFieldSurfaceModifierState cache,
            int cellCount)
        {
            if (cache.InfluenceMask != null && cache.InfluenceMask.Length == cellCount)
                return;

            cache.InfluenceMask = new bool[cellCount];
            cache.InfluenceScratch = new bool[cellCount];
            cache.InfluenceIndices.Clear();
            cache.InfluenceIndicesBuilt = false;
        }

        private static bool IsMissingModifier(IFlowFieldVectorModifier modifier)
            => modifier == null || modifier is UnityEngine.Object unityObject && unityObject == null;
    }
}
