using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
    internal readonly struct FlowFieldSurfaceModifierBuildRequest
    {
        public FlowFieldGridSpace Grid { get; }
        public FlowFieldSurfaceData Surface { get; }
        public FlowFieldSurfaceWorkspace Workspace { get; }
        public float ObstacleCheckHeight { get; }
        public float ObstacleCheckCenterOffset { get; }

        public FlowFieldSurfaceModifierBuildRequest(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldSurfaceWorkspace workspace,
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

    internal sealed class FlowFieldSurfaceModifierPipeline
    {
        private readonly FlowFieldModifierRegistry _registry;
        private readonly List<FlowFieldSurfaceModifierLayer> _runtimeLayers = new List<FlowFieldSurfaceModifierLayer>(16);
        private readonly FlowFieldSurfaceComposeRun _composeRun = new FlowFieldSurfaceComposeRun();
        private Collider[] _overlapBuffer;

        internal FlowFieldModifierRegistry Registry => _registry;

        // The cells a Modifier touches, which this pipeline created for it.
        internal static FlowFieldSurfaceModifierArea AreaOf(FlowFieldModifierEntry entry)
            => (FlowFieldSurfaceModifierArea)entry.Area;

        internal FlowFieldSurfaceModifierPipeline(FlowFieldModifierRegistry registry)
        {
            if (registry == null)
                throw new ArgumentNullException(nameof(registry));
            _registry = registry;
        }

        // An invalid result means the pass found no effective mask change.
        internal FlowFieldCellRect RebuildAreaData(in FlowFieldSurfaceModifierBuildRequest request)
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
                FlowFieldModifierEntry entry = _registry.Entries[i];
                if (!entry.AreaDirty)
                    continue;

                FlowFieldCellRect previousRect = AreaOf(entry).InfluenceRect;
                ResizeRuntimeMask(entry, request.Grid.CellCount);
                Collider collider = entry.Modifier.Observe().InfluenceCollider;

                FlowFieldModifierMaskBuilder.Build(
                    request.Grid,
                    request.Surface,
                    collider,
                    request.ObstacleCheckHeight,
                    request.ObstacleCheckCenterOffset,
                    AreaOf(entry).InfluenceScratch,
                    ref _overlapBuffer);

                bool maskChanged = !FlowFieldObstacleMaskBuilder.AreEqual(
                    AreaOf(entry).InfluenceMask,
                    AreaOf(entry).InfluenceScratch);
                if (maskChanged)
                {
                    Array.Copy(AreaOf(entry).InfluenceScratch, AreaOf(entry).InfluenceMask, AreaOf(entry).InfluenceMask.Length);
                }

                if (maskChanged || !AreaOf(entry).InfluenceIndicesBuilt)
                {
                    RebuildInfluenceIndices(entry, request.Grid);
                }

                if (maskChanged)
                    changedRegion = FlowFieldCellRect.Union(
                        changedRegion,
                        FlowFieldCellRect.Union(previousRect, AreaOf(entry).InfluenceRect));

                entry.InfluenceCollider = collider;
                entry.AreaDirty = false;
            }

            return changedRegion;
        }

        private static void ValidateAreaRequest(in FlowFieldSurfaceModifierBuildRequest request)
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

        private static void RebuildInfluenceIndices(FlowFieldModifierEntry entry, FlowFieldGridSpace grid)
        {
            AreaOf(entry).InfluenceIndices.Clear();
            // The rect comes from the mask, not the collider bounds: a
            // cell that only touches the collider belongs to the mask,
            // and a partial composition rewrites nothing outside the rect.
            FlowFieldCellRect maskRect = FlowFieldCellRect.Invalid;
            for (int index = 0; index < AreaOf(entry).InfluenceMask.Length; index++)
            {
                if (!AreaOf(entry).InfluenceMask[index])
                    continue;
                AreaOf(entry).InfluenceIndices.Add(index);
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
            AreaOf(entry).InfluenceIndicesBuilt = true;
            AreaOf(entry).InfluenceRect = maskRect;
        }

        /// <summary>Takes the Modifier snapshots for one composition, then <see cref="StepFinalField"/> runs it.</summary>
        internal void BeginFinalField(
            in FlowFieldSurfaceModifierBuildRequest request,
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
            _registry.Clear();
        }

        private void BuildRuntimeLayers(int cellCount)
        {
            _runtimeLayers.Clear();
            for (int i = 0; i < _registry.Entries.Count; i++)
            {
                FlowFieldModifierEntry entry = _registry.Entries[i];
                if (FlowFieldVectorUtility.IsMissingModifier(entry.Modifier)
                    || AreaOf(entry).InfluenceMask == null
                    || AreaOf(entry).InfluenceMask.Length != cellCount)
                    throw new InvalidOperationException("Registered modifier data is inconsistent.");

                if (!AreaOf(entry).InfluenceIndicesBuilt)
                {
                    for (int index = 0; index < AreaOf(entry).InfluenceMask.Length; index++)
                    {
                        if (AreaOf(entry).InfluenceMask[index])
                            AreaOf(entry).InfluenceIndices.Add(index);
                    }
                    AreaOf(entry).InfluenceIndicesBuilt = true;
                }

                IFlowFieldModifierSnapshot snapshot = FlowFieldModifierRegistry.CaptureSnapshot(entry.Modifier);

                _runtimeLayers.Add(new FlowFieldSurfaceModifierLayer(
                    snapshot,
                    AreaOf(entry).InfluenceMask,
                    AreaOf(entry).InfluenceIndices));
            }
        }

        private static void ResizeRuntimeMask(
            FlowFieldModifierEntry entry,
            int cellCount)
        {
            if (AreaOf(entry).InfluenceMask != null && AreaOf(entry).InfluenceMask.Length == cellCount)
                return;

            AreaOf(entry).InfluenceMask = new bool[cellCount];
            AreaOf(entry).InfluenceScratch = new bool[cellCount];
            AreaOf(entry).InfluenceIndices ??= new List<int>(64);
            AreaOf(entry).InfluenceIndices.Clear();
            AreaOf(entry).InfluenceIndicesBuilt = false;
        }
    }
}
