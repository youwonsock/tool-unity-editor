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
        // The cells whose obstacles changed; one that covers the grid rebuilds every mask.
        public FlowFieldCellRect DirtyRegion { get; }
        // BakedBase: the bake whose obstacle mask the static mask starts from. Null otherwise.
        public FlowFieldSurfaceBakeSnapshot BakedBase { get; }
        // BakedBase: every region obstacle changes reached since Initialize.
        public Bounds? Touched { get; }
        // The session sweeps for unregistered obstacles: the dynamic mask
        // holds every obstacle Collider of the layer, and without a bake the
        // static mask stays empty, so a static Collider that moved leaves nothing behind.
        public bool SweepsObstacles { get; }

        public FlowFieldSurfaceObstacleRequest(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldSurfaceWorkspace workspace,
            LayerMask obstacleLayer,
            float checkHeight,
            float centerOffset,
            float clearance,
            FlowFieldCellRect dirtyRegion,
            FlowFieldSurfaceBakeSnapshot bakedBase = null,
            Bounds? touched = null,
            bool sweepsObstacles = false)
        {
            Grid = grid;
            Surface = surface;
            Workspace = workspace;
            ObstacleLayer = obstacleLayer;
            CheckHeight = checkHeight;
            CenterOffset = centerOffset;
            Clearance = clearance;
            DirtyRegion = dirtyRegion;
            BakedBase = bakedBase;
            Touched = touched;
            SweepsObstacles = sweepsObstacles;
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
            // Every query below reads one synced pose, so an obstacle moved by
            // its Transform is found where it is now, whichever mask rebuilds.
            if (rebuildStatic || rebuildDynamic)
                Physics.SyncTransforms();
            bool staticChanged = false;
            bool dynamicChanged = false;
            if (rebuildStatic)
                staticChanged = BuildStaticScratch(in request);

            if (!rebuildDynamic)
            {
                bool effectiveMaskChangedWithoutDynamic = CompareEffectiveMask(
                    request.Grid,
                    request.Workspace,
                    staticChanged,
                    dynamicChanged,
                    out FlowFieldCellRect effectiveMaskDirtyWithoutDynamic);
                return new FlowFieldSurfaceObstacleResult(
                    effectiveMaskChangedWithoutDynamic,
                    effectiveMaskDirtyWithoutDynamic);
            }

            // Every registered Collider where it is now. The session owns the
            // registry: a destroyed Collider is skipped here and pruned there.
            FlowFieldObstacleMaskBuilder.BuildRegistered(
                request.Grid,
                request.Surface,
                request.CheckHeight,
                request.CenterOffset,
                request.Clearance,
                request.Workspace.ObstacleScratch,
                _registry,
                ref _overlapBuffer);
            AddSweptObstacles(in request);
            dynamicChanged = CommitDynamicScratch(request.Workspace);

            bool effectiveMaskChanged = CompareEffectiveMask(
                request.Grid,
                request.Workspace,
                staticChanged,
                dynamicChanged,
                out FlowFieldCellRect effectiveMaskDirty);
            return new FlowFieldSurfaceObstacleResult(
                effectiveMaskChanged,
                effectiveMaskDirty);
        }

        // The swept mask scans the dirty cells again with every obstacle
        // Collider of the layer, and joins the registered overlay.
        private void AddSweptObstacles(in FlowFieldSurfaceObstacleRequest request)
        {
            FlowFieldSurfaceWorkspace workspace = request.Workspace;
            if (!request.SweepsObstacles)
            {
                Array.Clear(workspace.SweptBlocked, 0, workspace.Capacity);
                return;
            }
            FlowFieldObstacleMaskBuilder.Build(
                request.Grid,
                request.Surface,
                request.ObstacleLayer,
                request.CheckHeight,
                request.CenterOffset,
                request.Clearance,
                workspace.SweptBlocked,
                request.DirtyRegion.IsValid ? request.DirtyRegion : FlowFieldCellRect.Full(request.Grid),
                ref _overlapBuffer);
            bool[] overlay = workspace.ObstacleScratch;
            bool[] swept = workspace.SweptBlocked;
            for (int i = 0; i < workspace.Capacity; i++)
                overlay[i] |= swept[i];
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
            return effectiveChanged;
        }

        /// <summary>
        /// The world box whose cells an obstacle with these bounds can block:
        /// a cell's query box reaches half a cell and the clearance beyond it.
        /// </summary>
        internal static Bounds Reach(Bounds bounds, float cellSize, float clearance)
        {
            bounds.Expand(2f * (cellSize + clearance));
            return bounds;
        }

        // A dirty region rescans only its cells on top of the current mask.
        // Otherwise the mask starts again: from a full scan, or in BakedBase
        // from the bake, with every region a change reached scanned again.
        private bool BuildStaticScratch(in FlowFieldSurfaceObstacleRequest request)
        {
            FlowFieldGridSpace grid = request.Grid;
            FlowFieldSurfaceWorkspace workspace = request.Workspace;
            bool[] scratch = workspace.ObstacleScratch;
            FlowFieldCellRect region = request.DirtyRegion;
            bool partial = region.IsValid && !region.Covers(grid);
            if (request.SweepsObstacles && request.BakedBase == null)
            {
                Array.Clear(scratch, 0, workspace.Capacity);
                region = FlowFieldCellRect.Invalid;
            }
            else if (partial)
            {
                Array.Copy(workspace.StaticBlocked, scratch, workspace.Capacity);
            }
            else if (request.BakedBase != null)
            {
                request.BakedBase.CopyBlockedTo(scratch);
                region = ReloadRegion(in request);
            }
            else
            {
                region = FlowFieldCellRect.Full(grid);
            }

            if (region.IsValid)
                FlowFieldObstacleMaskBuilder.BuildStatic(
                    grid,
                    request.Surface,
                    request.ObstacleLayer,
                    request.CheckHeight,
                    request.CenterOffset,
                    request.Clearance,
                    scratch,
                    region,
                    _registry.Set,
                    ref _overlapBuffer,
                    ref _targetOverlapBuffer);
            bool changed = !FlowFieldObstacleMaskBuilder.AreEqual(scratch, workspace.StaticBlocked);
            if (changed)
                Array.Copy(scratch, workspace.StaticBlocked, workspace.Capacity);
            return changed;
        }

        // A reloaded bake holds every obstacle where it was baked. The regions
        // changes reached and the registered obstacles, which the static mask
        // leaves to the dynamic mask, are scanned again.
        private FlowFieldCellRect ReloadRegion(in FlowFieldSurfaceObstacleRequest request)
        {
            FlowFieldGridSpace grid = request.Grid;
            FlowFieldCellRect region = request.Touched.HasValue
                ? FlowFieldCellRect.FromBounds(grid, request.Touched.Value)
                : FlowFieldCellRect.Invalid;
            for (int i = 0; i < _registry.Count; i++)
            {
                region = FlowFieldCellRect.Union(region, FlowFieldCellRect.FromBounds(
                    grid, Reach(_registry.GetBounds(i), grid.CellSize, request.Clearance)));
                Collider collider = _registry.GetCollider(i);
                if (collider != null)
                    region = FlowFieldCellRect.Union(region, FlowFieldCellRect.FromBounds(
                        grid, Reach(collider.bounds, grid.CellSize, request.Clearance)));
            }
            return region;
        }

        public void CommitCombinedAndBuildEscape(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldSurfaceWorkspace workspace)
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
            FlowFieldSurfaceNavigationPreparation.BuildEscapeDirections(grid, surface, workspace);
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
