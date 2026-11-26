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

    internal sealed class FlowFieldSurfaceObstacleMasks
    {
        private Collider[] _overlapBuffer;
        private Collider[] _targetOverlapBuffer;
        // The session's registered obstacles; the dynamic mask is built from them.
        private readonly IFlowFieldObstacleView _registry;

        // The registered overlay, kept between rebuilds with what each Collider
        // was drawn as, so a rebuild redraws only the cells a change can reach:
        // the dirty region and the old and new footprints of every Collider
        // that moved, appeared, left or changed. Cells outside see the same
        // Colliders as before, so they keep the values a full redraw would give.
        private bool[] _overlay;
        private FlowFieldGridSpace _overlayGrid;
        private FlowFieldSurfaceData _overlaySurface;
        private float _overlayCheckHeight;
        private float _overlayCenterOffset;
        private float _overlayClearance;
        private readonly Dictionary<Collider, DrawnObstacle> _drawn = new Dictionary<Collider, DrawnObstacle>();
        private readonly List<KeyValuePair<Collider, DrawnObstacle>> _current = new List<KeyValuePair<Collider, DrawnObstacle>>();

        // What a registered Collider looked like when the overlay drew it. A
        // disabled Collider, or one on an inactive GameObject, has empty bounds.
        private readonly struct DrawnObstacle
        {
            internal readonly Bounds Bounds;
            internal readonly bool IsTrigger;

            internal DrawnObstacle(Collider collider)
            {
                Bounds = collider.bounds;
                IsTrigger = collider.isTrigger;
            }

            // Exact, as the cell queries are.
            internal bool Matches(in DrawnObstacle other)
                => IsTrigger == other.IsTrigger && Bounds.Equals(other.Bounds);
        }

        internal FlowFieldSurfaceObstacleMasks(IFlowFieldObstacleView registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        public bool AllBlockedWarningIssued;

        /// <summary>True when the effective mask, static | dynamic, differs from Blocked.</summary>
        internal bool RebuildMasks(
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
                return EffectiveMaskChanged(request.Grid, request.Workspace, staticChanged, dynamicChanged);

            // Every registered Collider where it is now. The session owns the
            // registry: a destroyed Collider is skipped here and pruned there.
            RedrawRegisteredOverlay(in request);
            Array.Copy(_overlay, request.Workspace.ObstacleScratch, request.Workspace.Capacity);
            AddSweptObstacles(in request);
            dynamicChanged = CommitDynamicScratch(request.Workspace);

            return EffectiveMaskChanged(request.Grid, request.Workspace, staticChanged, dynamicChanged);
        }

        private void RedrawRegisteredOverlay(in FlowFieldSurfaceObstacleRequest request)
        {
            FlowFieldGridSpace grid = request.Grid;
            FlowFieldCellRect region = request.DirtyRegion;
            bool keep = _overlay != null
                && _overlay.Length == grid.CellCount
                && SameGrid(_overlayGrid, grid)
                && ReferenceEquals(_overlaySurface, request.Surface)
                && _overlayCheckHeight == request.CheckHeight
                && _overlayCenterOffset == request.CenterOffset
                && _overlayClearance == request.Clearance
                && region.IsValid
                && !region.Covers(grid);

            _current.Clear();
            int stillDrawn = 0;
            for (int i = 0; i < _registry.Count; i++)
            {
                Collider collider = _registry.GetCollider(i);
                bool alive = collider != null;
                DrawnObstacle now = alive ? new DrawnObstacle(collider) : default;
                if (alive)
                    _current.Add(new KeyValuePair<Collider, DrawnObstacle>(collider, now));
                if (!keep)
                    continue;
                if (_drawn.TryGetValue(collider, out DrawnObstacle drawn))
                {
                    stillDrawn++;
                    if (alive && now.Matches(in drawn))
                        continue;
                    region = FlowFieldCellRect.Union(region, Footprint(in request, drawn.Bounds));
                }
                if (alive)
                    region = FlowFieldCellRect.Union(region, Footprint(in request, now.Bounds));
            }
            // A Collider that left the registry leaves its old cells.
            if (keep && stillDrawn < _drawn.Count)
            {
                foreach (KeyValuePair<Collider, DrawnObstacle> entry in _drawn)
                {
                    if (!_registry.Contains(entry.Key))
                        region = FlowFieldCellRect.Union(region, Footprint(in request, entry.Value.Bounds));
                }
            }

            if (!keep)
            {
                if (_overlay == null || _overlay.Length != grid.CellCount)
                    _overlay = new bool[grid.CellCount];
                region = FlowFieldCellRect.Full(grid);
                _overlayGrid = grid;
                _overlaySurface = request.Surface;
                _overlayCheckHeight = request.CheckHeight;
                _overlayCenterOffset = request.CenterOffset;
                _overlayClearance = request.Clearance;
            }
            FlowFieldSurfaceObstacleMaskBuilder.BuildRegistered(
                grid,
                request.Surface,
                request.CheckHeight,
                request.CenterOffset,
                request.Clearance,
                _overlay,
                region,
                _registry,
                ref _overlapBuffer);

            _drawn.Clear();
            foreach (KeyValuePair<Collider, DrawnObstacle> entry in _current)
                _drawn[entry.Key] = entry.Value;
            _current.Clear();
        }

        private static FlowFieldCellRect Footprint(in FlowFieldSurfaceObstacleRequest request, Bounds bounds)
            => FlowFieldSurfaceObstacleMaskBuilder.Footprint(request.Grid, bounds, request.CheckHeight, request.Clearance);

        // Exact, unlike MatchesBounds: every cell centre follows from these.
        private static bool SameGrid(FlowFieldGridSpace left, FlowFieldGridSpace right)
            => left.IsValid
                && left.SpaceMode == right.SpaceMode
                && left.Width == right.Width
                && left.Height == right.Height
                && left.Depth == right.Depth
                && left.CellSize == right.CellSize
                && left.Origin.Equals(right.Origin);

        /// <summary>Drops the kept overlay; the next dynamic rebuild draws every cell.</summary>
        internal void Clear()
        {
            _overlay = null;
            _overlaySurface = null;
            _overlayGrid = default;
            _drawn.Clear();
            _current.Clear();
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
            FlowFieldSurfaceObstacleMaskBuilder.Build(
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
            if (FlowFieldSurfaceObstacleMaskBuilder.AreEqual(workspace.ObstacleScratch, workspace.DynamicBlocked))
                return false;
            System.Array.Copy(workspace.ObstacleScratch, workspace.DynamicBlocked, workspace.Capacity);
            return true;
        }

        // Any cell whose static | dynamic mask differs from Blocked; the base
        // stage then rebuilds the whole field, so no region is kept.
        private static bool EffectiveMaskChanged(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceWorkspace workspace,
            bool staticChanged,
            bool dynamicChanged)
        {
            if (!staticChanged && !dynamicChanged)
                return false;
            for (int index = 0; index < grid.CellCount; index++)
            {
                if (workspace.Blocked[index] != (workspace.StaticBlocked[index] || workspace.DynamicBlocked[index]))
                    return true;
            }
            return false;
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
                FlowFieldSurfaceObstacleMaskBuilder.BuildStatic(
                    grid,
                    request.Surface,
                    request.ObstacleLayer,
                    request.CheckHeight,
                    request.CenterOffset,
                    request.Clearance,
                    scratch,
                    region,
                    _registry,
                    ref _overlapBuffer,
                    ref _targetOverlapBuffer);
            bool changed = !FlowFieldSurfaceObstacleMaskBuilder.AreEqual(scratch, workspace.StaticBlocked);
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
                    grid, FlowFieldObstacleReach.Expand(_registry.GetBounds(i), grid.CellSize, request.Clearance)));
                Collider collider = _registry.GetCollider(i);
                if (collider != null)
                    region = FlowFieldCellRect.Union(region, FlowFieldCellRect.FromBounds(
                        grid, FlowFieldObstacleReach.Expand(collider.bounds, grid.CellSize, request.Clearance)));
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
                throw new ArgumentException("Obstacle commit requires valid Surface data.", nameof(surface));
            if (workspace == null)
                throw new ArgumentNullException(nameof(workspace));
            if (workspace.Capacity != grid.CellCount)
                throw new ArgumentException("Obstacle workspace capacity must match the grid.", nameof(workspace));
            FlowFieldSurfaceNavigationPreparation.CombineBlockedAndBuildEscapeDirections(grid, surface, workspace);
        }

        private static void ValidateRequest(in FlowFieldSurfaceObstacleRequest request)
        {
            if (!request.Grid.IsValid)
                throw new ArgumentException("Obstacle rebuild requires a valid grid.", nameof(request));
            if (request.Surface == null || !request.Surface.IsValid)
                throw new ArgumentException("Obstacle rebuild requires valid Surface data.", nameof(request.Surface));
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
