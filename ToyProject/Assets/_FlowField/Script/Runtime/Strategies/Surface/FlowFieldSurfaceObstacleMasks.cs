using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
    internal readonly struct FlowFieldSurfaceObstacleRequest
    {
        public FlowFieldGridSpace Grid { get; }
        public FlowFieldSurfaceData Surface { get; }
        public FlowFieldSurfaceWorkspace Workspace { get; }
        public LayerMask ObstacleLayer { get; }
        public float CheckHeight { get; }
        public float CenterOffset { get; }
        public float Clearance { get; }
        public FlowFieldCellRect DirtyRegion { get; }

        public FlowFieldSurfaceObstacleRequest(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldSurfaceWorkspace workspace,
            LayerMask obstacleLayer,
            float checkHeight,
            float centerOffset,
            float clearance,
            FlowFieldCellRect dirtyRegion)
        {
            Grid = grid;
            Surface = surface;
            Workspace = workspace;
            ObstacleLayer = obstacleLayer;
            CheckHeight = checkHeight;
            CenterOffset = centerOffset;
            Clearance = clearance;
            DirtyRegion = dirtyRegion;
        }
    }

    internal readonly struct FlowFieldSurfaceObstacleResult
    {
        public bool MaskChanged { get; }
        public FlowFieldCellRect DirtyRegion { get; }

        public FlowFieldSurfaceObstacleResult(
            bool maskChanged,
            FlowFieldCellRect dirtyRegion)
        {
            MaskChanged = maskChanged;
            DirtyRegion = dirtyRegion;
        }
    }

    internal sealed class FlowFieldSurfaceObstacleMasks
    {
        private Collider[] _overlapBuffer;
        private Collider[] _targetOverlapBuffer;
        // The session's registered obstacles; the dynamic mask is built from them.
        private readonly FlowFieldObstacleRegistry _registry;

        internal FlowFieldSurfaceObstacleMasks(FlowFieldObstacleRegistry registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        public bool AllBlockedWarningIssued;

        internal FlowFieldSurfaceObstacleResult RebuildMasks(
            in FlowFieldSurfaceObstacleRequest request,
            bool rebuildStatic,
            bool rebuildDynamic)
        {
            ValidateRequest(request);
            bool staticChanged = false;
            bool dynamicChanged = false;
            FlowFieldCellRect dynamicDirty = FlowFieldCellRect.Invalid;
            if (rebuildStatic)
            {
                staticChanged = BuildStaticScratch(
                    request.Grid,
                    request.Surface,
                    request.Workspace,
                    request.ObstacleLayer,
                    request.CheckHeight,
                    request.CenterOffset,
                    request.Clearance,
                    _registry.Set,
                    ref _overlapBuffer,
                    ref _targetOverlapBuffer);
            }

            if (!rebuildDynamic)
            {
                bool effectiveMaskChangedWithoutDynamic = CompareEffectiveMask(
                    request.Grid,
                    request.Workspace,
                    staticChanged,
                    dynamicChanged,
                    ref dynamicDirty,
                    out FlowFieldCellRect effectiveMaskDirtyWithoutDynamic);
                return new FlowFieldSurfaceObstacleResult(
                    effectiveMaskChangedWithoutDynamic,
                    effectiveMaskDirtyWithoutDynamic);
            }

            {
                dynamicDirty = RebuildDynamicOverlay(
                    request.Grid,
                    request.Surface,
                    request.CheckHeight,
                    request.CenterOffset,
                    request.Clearance,
                    request.Workspace.ObstacleScratch,
                    request.DirtyRegion);
                dynamicChanged = CommitDynamicScratch(request.Workspace);
            }

            bool effectiveMaskChanged = CompareEffectiveMask(
                request.Grid,
                request.Workspace,
                staticChanged,
                dynamicChanged,
                ref dynamicDirty,
                out FlowFieldCellRect effectiveMaskDirty);
            return new FlowFieldSurfaceObstacleResult(
                effectiveMaskChanged,
                effectiveMaskDirty);
        }

        private static bool CommitDynamicScratch(FlowFieldSurfaceWorkspace workspace)
        {
            if (FlowFieldObstacleMaskBuilder.AreEqual(workspace.ObstacleScratch, workspace.DynamicBlocked))
                return false;
            System.Array.Copy(workspace.ObstacleScratch, workspace.DynamicBlocked, workspace.Capacity);
            return true;
        }

        private static bool CompareEffectiveMask(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceWorkspace workspace,
            bool staticChanged,
            bool dynamicChanged,
            ref FlowFieldCellRect dynamicDirty,
            out FlowFieldCellRect dirty)
        {
            dirty = FlowFieldCellRect.Invalid;
            if (!staticChanged && !dynamicChanged)
                return false;

            bool effectiveChanged = false;
            for (int index = 0; index < grid.CellCount; index++)
            {
                bool effectiveBlocked = workspace.StaticBlocked[index]
                    || workspace.DynamicBlocked[index];
                if (workspace.Blocked[index] == effectiveBlocked)
                    continue;
                effectiveChanged = true;
                if (!staticChanged && dynamicChanged)
                {
                    grid.FromFlatIndex(index, out int x, out int z);
                    dirty = FlowFieldCellRect.Union(
                        dirty,
                        new FlowFieldCellRect
                        {
                            MinX = x,
                            MaxX = x,
                            MinZ = z,
                            MaxZ = z,
                        });
                }
            }

            if (effectiveChanged && staticChanged)
                dirty = FlowFieldCellRect.Full(grid);
            else if (effectiveChanged && !dirty.IsValid)
                dirty = dynamicDirty;
            return effectiveChanged;
        }

        private static bool BuildStaticScratch(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldSurfaceWorkspace workspace,
            LayerMask obstacleLayer,
            float checkHeight,
            float centerOffset,
            float clearance,
            HashSet<Collider> registeredObstacles,
            ref Collider[] overlapBuffer,
            ref Collider[] targetOverlapBuffer)
        {
            if (!grid.IsValid)
                throw new ArgumentException("Static obstacle composition requires a valid grid.", nameof(grid));
            if (workspace == null)
                throw new ArgumentNullException(nameof(workspace));
            if (workspace.Capacity != grid.CellCount)
                throw new ArgumentException("Static obstacle workspace capacity must match the grid.", nameof(workspace));

            if (!FlowFieldObstacleMaskBuilder.BuildStatic(
                    grid,
                    surface,
                    obstacleLayer,
                    checkHeight,
                    centerOffset,
                    clearance,
                    workspace.ObstacleScratch,
                    registeredObstacles,
                    ref overlapBuffer,
                    ref targetOverlapBuffer,
                    syncTransformsBeforeQuery: true))
            {
                throw new InvalidOperationException("Static obstacle mask 생성에 실패했습니다.");
            }
            bool changed = !FlowFieldObstacleMaskBuilder.AreEqual(
                workspace.ObstacleScratch,
                workspace.StaticBlocked);
            if (changed)
                Array.Copy(workspace.ObstacleScratch, workspace.StaticBlocked, workspace.Capacity);
            return changed;
        }

        public bool BuildFullLayerScratch(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            LayerMask obstacleLayer,
            float checkHeight,
            float centerOffset,
            float clearance,
            bool[] destination,
            bool syncTransforms)
            => FlowFieldObstacleMaskBuilder.Build(
                grid,
                surface,
                obstacleLayer,
                checkHeight,
                centerOffset,
                clearance,
                destination,
                ref _overlapBuffer,
                syncTransforms);

        public FlowFieldCellRect RebuildDynamicOverlay(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            float checkHeight,
            float centerOffset,
            float clearance,
            bool[] dynamicBlocked,
            FlowFieldCellRect extraDirty)
        {
            if (!grid.IsValid)
                throw new ArgumentException("Dynamic obstacle rebuild requires a valid grid.", nameof(grid));
            if (!surface.IsValid)
                throw new ArgumentException("Dynamic obstacle rebuild requires a valid surface bake.", nameof(surface));
            if (dynamicBlocked == null)
                throw new ArgumentNullException(nameof(dynamicBlocked));
            if (dynamicBlocked.Length != grid.CellCount)
                throw new ArgumentException("Dynamic obstacle destination length must match the grid.", nameof(dynamicBlocked));
            System.Array.Clear(dynamicBlocked, 0, dynamicBlocked.Length);
            FlowFieldCellRect dirty = extraDirty;
            if (!FlowFieldGridSpace.IsFinite(checkHeight) || checkHeight <= 0f)
                throw new System.ArgumentOutOfRangeException(nameof(checkHeight));
            if (!FlowFieldGridSpace.IsFinite(centerOffset))
                throw new System.ArgumentOutOfRangeException(nameof(centerOffset));
            if (!FlowFieldGridSpace.IsFinite(clearance) || clearance < 0f)
                throw new System.ArgumentOutOfRangeException(nameof(clearance));
            float halfHeight = checkHeight * 0.5f;
            float halfXZ = FlowFieldOverlapUtility.CellHalfExtent(grid.CellSize, clearance);
            Vector3 cellHalfExtents = new Vector3(halfXZ, halfHeight, halfXZ);

            for (int i = _registry.Count - 1; i >= 0; i--)
            {
                Collider collider = _registry.GetCollider(i);
                if (collider == null)
                {
                    dirty = FlowFieldCellRect.Union(
                        dirty,
                        FlowFieldCellRect.FromBounds(grid, _registry.GetBounds(i)));
                    _registry.RemoveAt(i);
                    continue;
                }

                Bounds previous = _registry.GetBounds(i);
                Bounds current = collider.bounds;
                dirty = FlowFieldCellRect.Union(dirty, FlowFieldCellRect.FromBounds(grid, previous));
                dirty = FlowFieldCellRect.Union(dirty, FlowFieldCellRect.FromBounds(grid, current));
                _registry.SetBounds(i, current);

                Bounds expanded = current;
                expanded.Expand(new Vector3(halfXZ * 2f, 0f, halfXZ * 2f));
                if (!grid.TryGetOverlappingCells(
                        expanded,
                        out int minX,
                        out int maxX,
                        out int minZ,
                        out int maxZ))
                    continue;

                int layerMask = 1 << collider.gameObject.layer;
                for (int z = minZ; z <= maxZ; z++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        int index = grid.ToFlatIndex(x, z);
                        if (!surface.IsSurfaceValid(index) || dynamicBlocked[index])
                            continue;

                        Vector3 center = surface.GetCellCenter(grid, index);
                        center.y += centerOffset;
                        dynamicBlocked[index] = Physics.CheckBox(
                            center,
                            cellHalfExtents,
                            Quaternion.identity,
                            layerMask,
                            QueryTriggerInteraction.Ignore)
                            && FlowFieldOverlapUtility.OverlapsTarget(
                                center,
                                cellHalfExtents,
                                layerMask,
                                collider,
                                QueryTriggerInteraction.Ignore,
                                ref _overlapBuffer);
                    }
                }
            }

            return dirty;
        }

        public bool CommitCombinedAndBuildEscape(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldSurfaceWorkspace workspace,
            out bool hasWalkable)
        {
            if (!grid.IsValid)
                throw new ArgumentException("Obstacle commit requires a valid grid.", nameof(grid));
            if (!surface.IsValid)
                throw new ArgumentException("Obstacle commit requires a valid surface bake.", nameof(surface));
            if (workspace == null)
                throw new ArgumentNullException(nameof(workspace));
            if (workspace.Capacity != grid.CellCount)
                throw new ArgumentException("Obstacle workspace capacity must match the grid.", nameof(workspace));
            workspace.RebuildCombinedBlocked();
            hasWalkable = FlowFieldSurfaceSolver.BuildEscapeDirections(grid, surface, workspace);
            return true;
        }

        private static void ValidateRequest(in FlowFieldSurfaceObstacleRequest request)
        {
            if (!request.Grid.IsValid)
                throw new ArgumentException("Obstacle rebuild requires a valid grid.", nameof(request));
            if (request.Surface == null || !request.Surface.IsValid)
                throw new ArgumentException("Obstacle rebuild requires a valid surface bake.", nameof(request.Surface));
            if (request.Workspace == null)
                throw new ArgumentNullException(nameof(request.Workspace));
            if (request.Workspace.Capacity != request.Grid.CellCount)
                throw new ArgumentException("Obstacle workspace capacity must match the grid.", nameof(request.Workspace));
            if (!FlowFieldGridSpace.IsFinite(request.CheckHeight) || request.CheckHeight <= 0f)
                throw new ArgumentOutOfRangeException(nameof(request.CheckHeight));
            if (!FlowFieldGridSpace.IsFinite(request.CenterOffset))
                throw new ArgumentOutOfRangeException(nameof(request.CenterOffset));
            if (!FlowFieldGridSpace.IsFinite(request.Clearance) || request.Clearance < 0f)
                throw new ArgumentOutOfRangeException(nameof(request.Clearance));
        }
    }
}
