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
        private readonly FlowFieldModifierRegistry<FlowFieldSurfaceModifierArea> _registry;
        private readonly List<FlowFieldSurfaceModifierLayer> _runtimeLayers = new List<FlowFieldSurfaceModifierLayer>(16);
        private readonly FlowFieldSurfaceComposeRun _composeRun = new FlowFieldSurfaceComposeRun();
        private Collider[] _overlapBuffer;

        internal FlowFieldModifierRegistry<FlowFieldSurfaceModifierArea> Registry => _registry;

        internal FlowFieldSurfaceModifierPipeline(FlowFieldModifierRegistry<FlowFieldSurfaceModifierArea> registry)
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
                FlowFieldModifierEntry<FlowFieldSurfaceModifierArea> entry = _registry.Entries[i];
                if (!entry.AreaDirty)
                    continue;

                FlowFieldCellRect previousRect = entry.Area.InfluenceRect;
                ResizeRuntimeMask(entry, request.Grid.CellCount);
                Collider collider = entry.Modifier.Observe().InfluenceCollider;

                FlowFieldModifierMaskBuilder.Build(
                    request.Grid,
                    request.Surface,
                    collider,
                    request.ObstacleCheckHeight,
                    request.ObstacleCheckCenterOffset,
                    entry.Area.InfluenceScratch,
                    ref _overlapBuffer);

                bool maskChanged = !FlowFieldObstacleMaskBuilder.AreEqual(
                    entry.Area.InfluenceMask,
                    entry.Area.InfluenceScratch);
                if (maskChanged)
                {
                    Array.Copy(entry.Area.InfluenceScratch, entry.Area.InfluenceMask, entry.Area.InfluenceMask.Length);
                }

                if (maskChanged || !entry.Area.InfluenceIndicesBuilt)
                {
                    RebuildInfluenceIndices(entry, request.Grid);
                }

                if (maskChanged)
                    changedRegion = FlowFieldCellRect.Union(
                        changedRegion,
                        FlowFieldCellRect.Union(previousRect, entry.Area.InfluenceRect));

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

        private static void RebuildInfluenceIndices(FlowFieldModifierEntry<FlowFieldSurfaceModifierArea> entry, FlowFieldGridSpace grid)
        {
            entry.Area.InfluenceIndices.Clear();
            // The rect comes from the mask, not the collider bounds: a
            // cell that only touches the collider belongs to the mask,
            // and a partial composition rewrites nothing outside the rect.
            FlowFieldCellRect maskRect = FlowFieldCellRect.Invalid;
            for (int index = 0; index < entry.Area.InfluenceMask.Length; index++)
            {
                if (!entry.Area.InfluenceMask[index])
                    continue;
                entry.Area.InfluenceIndices.Add(index);
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
            entry.Area.InfluenceIndicesBuilt = true;
            entry.Area.InfluenceRect = maskRect;
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
                FlowFieldModifierEntry<FlowFieldSurfaceModifierArea> entry = _registry.Entries[i];
                if (FlowFieldVectorUtility.IsMissingModifier(entry.Modifier)
                    || entry.Area.InfluenceMask == null
                    || entry.Area.InfluenceMask.Length != cellCount)
                    throw new InvalidOperationException("Registered modifier data is inconsistent.");

                if (!entry.Area.InfluenceIndicesBuilt)
                {
                    for (int index = 0; index < entry.Area.InfluenceMask.Length; index++)
                    {
                        if (entry.Area.InfluenceMask[index])
                            entry.Area.InfluenceIndices.Add(index);
                    }
                    entry.Area.InfluenceIndicesBuilt = true;
                }

                IFlowFieldModifierSnapshot snapshot = FlowFieldModifierRegistry<FlowFieldSurfaceModifierArea>.CaptureSnapshot(entry.Modifier);

                _runtimeLayers.Add(new FlowFieldSurfaceModifierLayer(
                    snapshot,
                    entry.Area.InfluenceMask,
                    entry.Area.InfluenceIndices));
            }
        }

        private static void ResizeRuntimeMask(
            FlowFieldModifierEntry<FlowFieldSurfaceModifierArea> entry,
            int cellCount)
        {
            if (entry.Area.InfluenceMask != null && entry.Area.InfluenceMask.Length == cellCount)
                return;

            entry.Area.InfluenceMask = new bool[cellCount];
            entry.Area.InfluenceScratch = new bool[cellCount];
            entry.Area.InfluenceIndices ??= new List<int>(64);
            entry.Area.InfluenceIndices.Clear();
            entry.Area.InfluenceIndicesBuilt = false;
        }
    }
}
