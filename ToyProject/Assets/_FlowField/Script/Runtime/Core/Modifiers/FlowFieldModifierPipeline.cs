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
        private readonly FlowFieldComposeRun _composeRun = new FlowFieldComposeRun();
        private Collider[] _overlapBuffer;

        internal FlowFieldModifierPipeline(FlowFieldModifierRegistry registry)
        {
            if (registry == null)
                throw new ArgumentNullException(nameof(registry));
            _registry = registry;
        }

        // An invalid result means the pass found no effective mask change.
        internal FlowFieldCellRect RebuildAreaData(in FlowFieldModifierBuildRequest request)
        {
            FlowFieldCellRect changedRegion = FlowFieldCellRect.Invalid;
            ValidateAreaRequest(request);
            if (_registry.Entries.Count == 0)
                return changedRegion;

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
                return changedRegion;

            Physics.SyncTransforms();
            for (int i = 0; i < _registry.Entries.Count; i++)
            {
                FlowFieldModifierRegistry.Entry entry = _registry.Entries[i];
                if (!entry.AreaDirty)
                    continue;

                FlowFieldCellRect previousRect = entry.InfluenceRect;
                ResizeRuntimeMask(entry, request.Grid.CellCount);
                Collider collider = entry.Modifier.Observe().InfluenceCollider;

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

            return changedRegion;
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

        /// <summary>Takes the Modifier snapshots for one composition, then <see cref="StepFinalField"/> runs it.</summary>
        internal void BeginFinalField(
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

            BuildRuntimeLayers(request.Grid.CellCount);
            _composeRun.Begin(
                request.Grid,
                request.Surface,
                request.Workspace,
                defaultDirection,
                _runtimeLayers,
                dirty,
                rebuildBase);
        }

        internal bool StepFinalField(in FlowFieldBuildBudget budget) => _composeRun.Step(in budget);

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
                if (snapshot == null)
                    throw new InvalidOperationException(
                        $"FlowField modifier '{entry.Modifier.GetType().Name}' returned a null snapshot.");

                _runtimeLayers.Add(new FlowFieldModifierLayer(
                    snapshot,
                    entry.InfluenceMask,
                    entry.InfluenceIndices));
            }
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
