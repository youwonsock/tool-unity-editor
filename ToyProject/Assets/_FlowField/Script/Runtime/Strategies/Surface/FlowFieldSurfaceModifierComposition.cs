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

    internal sealed class FlowFieldSurfaceModifierComposition
    {
        private readonly IFlowFieldModifierView _registry;
        private readonly List<FlowFieldSurfaceModifierLayer> _runtimeLayers = new List<FlowFieldSurfaceModifierLayer>(16);
        private readonly FlowFieldSurfaceComposeRun _composeRun = new FlowFieldSurfaceComposeRun();
        private Collider[] _overlapBuffer;

        // The cells a Modifier touches, which this composition created for it.
        internal static FlowFieldSurfaceModifierArea AreaOf(FlowFieldModifierEntry entry)
            => (FlowFieldSurfaceModifierArea)entry.Area;

        internal FlowFieldSurfaceModifierComposition(IFlowFieldModifierView registry)
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
                if (AreaOf(_registry.Entries[i]).AreaDirty)
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
                // Destroyed while this build ran: the session removes it.
                if (!AreaOf(entry).AreaDirty || FlowFieldVectorUtility.IsMissingModifier(entry.Modifier))
                    continue;

                FlowFieldCellRect previousRect = AreaOf(entry).InfluenceRect;
                ResizeRuntimeMask(entry, request.Grid.CellCount);
                Collider collider = entry.Modifier.Observe().InfluenceCollider;

                FlowFieldCellRect queried = FlowFieldSurfaceModifierMaskBuilder.Build(
                    request.Grid,
                    request.Surface,
                    collider,
                    request.ObstacleCheckHeight,
                    request.ObstacleCheckCenterOffset,
                    AreaOf(entry).InfluenceScratch,
                    ref _overlapBuffer);

                bool maskChanged = !FlowFieldSurfaceObstacleMaskBuilder.AreEqual(
                    AreaOf(entry).InfluenceMask,
                    AreaOf(entry).InfluenceScratch);
                if (maskChanged)
                {
                    Array.Copy(AreaOf(entry).InfluenceScratch, AreaOf(entry).InfluenceMask, AreaOf(entry).InfluenceMask.Length);
                }

                if (maskChanged || !AreaOf(entry).InfluenceIndicesBuilt)
                {
                    RebuildInfluenceIndices(entry, request.Grid, queried);
                }

                if (maskChanged)
                    changedRegion = FlowFieldCellRect.Union(
                        changedRegion,
                        FlowFieldCellRect.Union(previousRect, AreaOf(entry).InfluenceRect));

                AreaOf(entry).AreaDirty = false;
            }

            return changedRegion;
        }

        private static void ValidateAreaRequest(in FlowFieldSurfaceModifierBuildRequest request)
        {
            if (!request.Grid.IsValid)
                throw new ArgumentException("Modifier rebuild requires a valid grid.", nameof(request));
            if (request.Surface == null || !request.Surface.IsValid)
                throw new ArgumentException("Modifier rebuild requires valid Surface data.", nameof(request.Surface));
            if (request.Workspace == null)
                throw new ArgumentNullException(nameof(request.Workspace));
            if (request.Workspace.Capacity != request.Grid.CellCount)
                throw new ArgumentException("Modifier workspace capacity must match the grid.", nameof(request.Workspace));
            if (!FlowFieldGridSpace.IsFinite(request.ObstacleCheckHeight) || request.ObstacleCheckHeight <= 0f)
                throw new ArgumentOutOfRangeException(nameof(request.ObstacleCheckHeight));
            if (!FlowFieldGridSpace.IsFinite(request.ObstacleCheckCenterOffset))
                throw new ArgumentOutOfRangeException(nameof(request.ObstacleCheckCenterOffset));
        }

        // Only the cells the mask build queried can be set, so only they are
        // scanned, row by row in index order.
        private static void RebuildInfluenceIndices(
            FlowFieldModifierEntry entry,
            FlowFieldGridSpace grid,
            FlowFieldCellRect queried)
        {
            FlowFieldSurfaceModifierArea area = AreaOf(entry);
            area.InfluenceIndices.Clear();
            // The rect comes from the mask, not the collider bounds: a
            // cell that only touches the collider belongs to the mask,
            // and a partial composition rewrites nothing outside the rect.
            FlowFieldCellRect maskRect = FlowFieldCellRect.Invalid;
            queried = FlowFieldCellRect.Intersect(queried, FlowFieldCellRect.Full(grid));
            for (int z = queried.MinZ; queried.IsValid && z <= queried.MaxZ; z++)
            {
                for (int x = queried.MinX; x <= queried.MaxX; x++)
                {
                    int index = grid.ToFlatIndex(x, z);
                    if (!area.InfluenceMask[index])
                        continue;
                    area.InfluenceIndices.Add(index);
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
            }
            area.InfluenceIndicesBuilt = true;
            area.InfluenceRect = maskRect;
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
                throw new ArgumentException("Modifier compose requires valid Surface data.", nameof(request.Surface));
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

        /// <summary>
        /// A Modifier destroyed without being unregistered is gone, so this
        /// composition leaves it out and starts again without it; the session
        /// removes it when the build ends. One that unregistered first already
        /// owes a recompose, so this composition finishes with it. The commit
        /// follows the last step in the same pump, so each step's check covers
        /// the publish.
        /// </summary>
        internal bool StepFinalField(in FlowFieldBuildBudget budget)
        {
            for (int i = 0; i < _runtimeLayers.Count; i++)
            {
                if (!IsGone(_runtimeLayers[i]))
                    continue;
                _runtimeLayers.RemoveAll(IsGone);
                _composeRun.Restart();
                break;
            }
            return _composeRun.Step(in budget);
        }

        // Destroyed while still registered: never unregistered.
        private bool IsGone(FlowFieldSurfaceModifierLayer layer)
            => FlowFieldVectorUtility.IsMissingModifier(layer.Modifier) && _registry.Contains(layer.Modifier);

        // The session clears its registry before the builder releases this.
        internal void Clear()
        {
            _runtimeLayers.Clear();
            _overlapBuffer = null;
        }

        /// <summary>A new Surface: every Modifier finds its cells again.</summary>
        internal void MarkAllAreasDirty()
        {
            for (int i = 0; i < _registry.Entries.Count; i++)
                AreaOf(_registry.Entries[i]).AreaDirty = true;
        }

        private void BuildRuntimeLayers(int cellCount)
        {
            _runtimeLayers.Clear();
            for (int i = 0; i < _registry.Entries.Count; i++)
            {
                FlowFieldModifierEntry entry = _registry.Entries[i];
                // The session removes destroyed Modifiers before a build
                // begins and again when it ends. One destroyed while this build
                // ran is gone, so the build leaves it out, as Volume3D does,
                // and its removal recomposes the cells outside this build.
                if (FlowFieldVectorUtility.IsMissingModifier(entry.Modifier))
                    continue;
                // The area stage builds the mask and its indices together.
                if (AreaOf(entry).InfluenceMask == null
                    || AreaOf(entry).InfluenceMask.Length != cellCount
                    || !AreaOf(entry).InfluenceIndicesBuilt)
                    throw new InvalidOperationException("Registered modifier data is inconsistent.");

                IFlowFieldModifierSnapshot snapshot = FlowFieldModifierRegistry.CaptureSnapshot(entry.Modifier);

                _runtimeLayers.Add(new FlowFieldSurfaceModifierLayer(
                    entry.Modifier,
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
