using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
    internal readonly struct FlowFieldObstacleRequest
    {
        public FlowFieldGridSpace Grid { get; }
        public FlowFieldSurfaceData Surface { get; }
        public FlowFieldWorkspace Workspace { get; }
        public LayerMask ObstacleLayer { get; }
        public float CheckHeight { get; }
        public float CenterOffset { get; }
        public float Clearance { get; }
        public bool UseUnregisteredSweep { get; }
        public FlowFieldCellRect DirtyRegion { get; }

        public FlowFieldObstacleRequest(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldWorkspace workspace,
            LayerMask obstacleLayer,
            float checkHeight,
            float centerOffset,
            float clearance,
            bool useUnregisteredSweep,
            FlowFieldCellRect dirtyRegion)
        {
            Grid = grid;
            Surface = surface;
            Workspace = workspace;
            ObstacleLayer = obstacleLayer;
            CheckHeight = checkHeight;
            CenterOffset = centerOffset;
            Clearance = clearance;
            UseUnregisteredSweep = useUnregisteredSweep;
            DirtyRegion = dirtyRegion;
        }
    }

    internal readonly struct FlowFieldObstacleResult
    {
        public bool MaskChanged { get; }
        public FlowFieldCellRect DirtyRegion { get; }
        public int ExcludedColliderCount { get; }

        public FlowFieldObstacleResult(
            bool maskChanged,
            FlowFieldCellRect dirtyRegion,
            int excludedColliderCount = 0)
        {
            MaskChanged = maskChanged;
            DirtyRegion = dirtyRegion;
            ExcludedColliderCount = excludedColliderCount;
        }
    }

    internal sealed class FlowFieldObstaclePipeline
    {
        private Collider[] _overlapBuffer;
        private Collider[] _targetOverlapBuffer;
        private readonly List<Collider> _dynamicObstacles = new List<Collider>(32);
        private readonly List<Bounds> _dynamicBounds = new List<Bounds>(32);
        // Last observed bounds are kept separate from the last-built bounds.
        // This prevents LateUpdate from versioning the same transform change
        // on every poll while a GPU solve is still in flight, while the
        // rebuild still receives the complete previous→latest dirty range.
        private readonly List<Bounds> _observedDynamicBounds = new List<Bounds>(32);
        private readonly HashSet<Collider> _dynamicSet = new HashSet<Collider>();
        // A transform/sweep probe leaves its candidate in the reusable
        // scratch mask.  The next base build consumes that candidate instead
        // of issuing the same Physics queries a second time.  Keeping the
        // flag separate from the mask itself lets sampling stop immediately
        // for a newly blocked cell while the committed graph is still old.
        private bool _hasStagedDynamicProbe;
        private FlowFieldCellRect _stagedDynamicDirtyRegion = FlowFieldCellRect.Invalid;

        public IReadOnlyList<Collider> DynamicObstacles => _dynamicObstacles;
        public bool AllBlockedWarningIssued;
        internal bool HasStagedDynamicProbe => _hasStagedDynamicProbe;

        public bool RegisterDynamicObstacle(Collider collider)
        {
            if (collider == null || !_dynamicSet.Add(collider))
                return false;

            DiscardStagedDynamicProbe();
            _dynamicObstacles.Add(collider);
            _dynamicBounds.Add(collider.bounds);
            _observedDynamicBounds.Add(collider.bounds);
            return true;
        }

        public bool UnregisterDynamicObstacle(Collider collider)
        {
            if (collider == null || !_dynamicSet.Remove(collider))
                return false;

            DiscardStagedDynamicProbe();
            int index = _dynamicObstacles.IndexOf(collider);
            if (index >= 0)
            {
                _dynamicObstacles.RemoveAt(index);
                _dynamicBounds.RemoveAt(index);
                _observedDynamicBounds.RemoveAt(index);
            }

            return true;
        }

        public void ClearDynamicObstacles()
        {
            DiscardStagedDynamicProbe();
            _dynamicObstacles.Clear();
            _dynamicBounds.Clear();
            _observedDynamicBounds.Clear();
            _dynamicSet.Clear();
        }

        internal FlowFieldObstacleResult RebuildMasks(
            in FlowFieldObstacleRequest request,
            bool rebuildStatic,
            bool rebuildDynamic)
        {
            ValidateRequest(request);
            bool consumeStagedDynamicProbe = rebuildDynamic && _hasStagedDynamicProbe;
            bool staticChanged = false;
            bool dynamicChanged = false;
            FlowFieldCellRect dynamicDirty = FlowFieldCellRect.Invalid;
            int excludedColliderCount = 0;
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
                    ref _overlapBuffer,
                    ref _targetOverlapBuffer,
                    out excludedColliderCount);
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
                return new FlowFieldObstacleResult(
                    effectiveMaskChangedWithoutDynamic,
                    effectiveMaskDirtyWithoutDynamic,
                    excludedColliderCount);
            }

            if (consumeStagedDynamicProbe)
            {
                dynamicDirty = _stagedDynamicDirtyRegion;
                dynamicChanged = !FlowFieldObstacleMaskBuilder.AreEqual(
                    request.Workspace.ObstacleScratch,
                    request.Workspace.DynamicBlocked);
                if (dynamicChanged)
                {
                    System.Array.Copy(
                        request.Workspace.ObstacleScratch,
                        request.Workspace.DynamicBlocked,
                        request.Workspace.Capacity);
                }
                DiscardStagedDynamicProbe();
            }
            else if (request.UseUnregisteredSweep)
            {
                if (BuildFullLayerScratch(
                        request.Grid,
                        request.Surface,
                        request.ObstacleLayer,
                        request.CheckHeight,
                        request.CenterOffset,
                        request.Clearance,
                        request.Workspace.ObstacleScratch,
                        syncTransforms: false))
                {
                    dynamicChanged = !FlowFieldObstacleMaskBuilder.AreEqual(
                        request.Workspace.ObstacleScratch,
                        request.Workspace.DynamicBlocked);
                    if (dynamicChanged)
                    {
                        System.Array.Copy(
                            request.Workspace.ObstacleScratch,
                            request.Workspace.DynamicBlocked,
                            request.Workspace.Capacity);
                        dynamicDirty = FlowFieldCellRect.Full(request.Grid);
                    }
                }
            }
            else
            {
                dynamicDirty = RebuildDynamicOverlay(
                    request.Grid,
                    request.Surface,
                    request.CheckHeight,
                    request.CenterOffset,
                    request.Clearance,
                    request.Workspace.ObstacleScratch,
                    request.DirtyRegion);
                dynamicChanged = !FlowFieldObstacleMaskBuilder.AreEqual(
                    request.Workspace.ObstacleScratch,
                    request.Workspace.DynamicBlocked);
                if (dynamicChanged)
                {
                    System.Array.Copy(
                        request.Workspace.ObstacleScratch,
                        request.Workspace.DynamicBlocked,
                        request.Workspace.Capacity);
                }
            }

            bool effectiveMaskChanged = CompareEffectiveMask(
                request.Grid,
                request.Workspace,
                staticChanged,
                dynamicChanged,
                ref dynamicDirty,
                out FlowFieldCellRect effectiveMaskDirty);
            return new FlowFieldObstacleResult(
                effectiveMaskChanged,
                effectiveMaskDirty,
                excludedColliderCount);
        }

        private static bool CompareEffectiveMask(
            FlowFieldGridSpace grid,
            FlowFieldWorkspace workspace,
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

        /// <summary>
        /// Non-versioning obstacle probe. It writes only the reusable scratch
        /// mask and compares it with the last committed dynamic overlay. The
        /// caller promotes a changed probe to a rebuild; an identical probe
        /// has no observable version, BFS or Revision side effect.
        /// </summary>
        internal bool ProbeDynamicMask(
            in FlowFieldObstacleRequest request,
            out FlowFieldCellRect dirtyRegion)
        {
            ValidateRequest(request);
            dirtyRegion = FlowFieldCellRect.Invalid;
            bool built;
            if (request.UseUnregisteredSweep)
            {
                built = BuildFullLayerScratch(
                    request.Grid,
                    request.Surface,
                    request.ObstacleLayer,
                    request.CheckHeight,
                    request.CenterOffset,
                    request.Clearance,
                    request.Workspace.ObstacleScratch,
                    syncTransforms: false);
                if (!built)
                {
                    DiscardStagedDynamicProbe();
                    return false;
                }
                bool changed = !FlowFieldObstacleMaskBuilder.AreEqual(
                    request.Workspace.ObstacleScratch,
                    request.Workspace.DynamicBlocked);
                if (changed)
                {
                    _stagedDynamicDirtyRegion = FlowFieldCellRect.Full(request.Grid);
                    _hasStagedDynamicProbe = true;
                }
                else
                {
                    DiscardStagedDynamicProbe();
                }
                return changed;
            }

            dirtyRegion = RebuildDynamicOverlay(
                request.Grid,
                request.Surface,
                request.CheckHeight,
                request.CenterOffset,
                request.Clearance,
                request.Workspace.ObstacleScratch,
                request.DirtyRegion);
            bool overlayChanged = !FlowFieldObstacleMaskBuilder.AreEqual(
                request.Workspace.ObstacleScratch,
                request.Workspace.DynamicBlocked);
            if (overlayChanged)
            {
                _stagedDynamicDirtyRegion = dirtyRegion;
                _hasStagedDynamicProbe = true;
            }
            else
            {
                DiscardStagedDynamicProbe();
            }
            return overlayChanged;
        }

        internal void DiscardStagedDynamicProbe()
        {
            _hasStagedDynamicProbe = false;
            _stagedDynamicDirtyRegion = FlowFieldCellRect.Invalid;
        }

        private static bool BuildStaticScratch(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldWorkspace workspace,
            LayerMask obstacleLayer,
            float checkHeight,
            float centerOffset,
            float clearance,
            ref Collider[] overlapBuffer,
            ref Collider[] targetOverlapBuffer,
            out int excludedColliderCount)
        {
            excludedColliderCount = 0;
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
                    ref overlapBuffer,
                    ref targetOverlapBuffer,
                    out excludedColliderCount,
                    syncTransformsBeforeQuery: true))
                throw new InvalidOperationException("Static obstacle mask 생성에 실패했습니다.");
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
            float halfXZ = grid.CellSize * 0.5f + clearance;
            Vector3 cellHalfExtents = new Vector3(halfXZ, halfHeight, halfXZ);

            for (int i = _dynamicObstacles.Count - 1; i >= 0; i--)
            {
                Collider collider = _dynamicObstacles[i];
                if (collider == null)
                {
                    dirty = FlowFieldCellRect.Union(
                        dirty,
                        FlowFieldCellRect.FromBounds(grid, _dynamicBounds[i]));
                    _dynamicSet.Remove(collider);
                    _dynamicObstacles.RemoveAt(i);
                    _dynamicBounds.RemoveAt(i);
                    _observedDynamicBounds.RemoveAt(i);
                    continue;
                }

                Bounds previous = _dynamicBounds[i];
                Bounds current = collider.bounds;
                dirty = FlowFieldCellRect.Union(dirty, FlowFieldCellRect.FromBounds(grid, previous));
                dirty = FlowFieldCellRect.Union(dirty, FlowFieldCellRect.FromBounds(grid, current));
                _dynamicBounds[i] = current;
                _observedDynamicBounds[i] = current;

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

        public bool DetectDynamicTransformsChanged()
            => DetectDynamicTransformsChanged(default, out _);

        internal bool DetectDynamicTransformsChanged(
            FlowFieldGridSpace grid,
            out FlowFieldCellRect dirtyRegion)
        {
            dirtyRegion = FlowFieldCellRect.Invalid;
            for (int i = 0; i < _dynamicObstacles.Count; i++)
            {
                Collider collider = _dynamicObstacles[i];
                if (collider == null)
                {
                    if (grid.IsValid)
                        dirtyRegion = FlowFieldCellRect.Union(
                            dirtyRegion,
                            FlowFieldCellRect.FromBounds(grid, _dynamicBounds[i]));
                    _dynamicSet.Remove(collider);
                    _dynamicObstacles.RemoveAt(i);
                    _dynamicBounds.RemoveAt(i);
                    _observedDynamicBounds.RemoveAt(i);
                    return true;
                }

                Bounds current = collider.bounds;
                Bounds previous = _observedDynamicBounds[i];
                if ((current.center - previous.center).sqrMagnitude > 0.0001f
                    || (current.size - previous.size).sqrMagnitude > 0.0001f)
                {
                    _observedDynamicBounds[i] = current;
                    // The exact grid conversion is performed by the caller;
                    // return an invalid rect here and let RebuildDynamicOverlay
                    // union the cached world bounds at probe time.
                    return true;
                }
            }

            return false;
        }

        public bool CommitCombinedAndBuildEscape(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldWorkspace workspace,
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
            hasWalkable = FlowFieldSolver.BuildEscapeDirections(grid, surface, workspace);
            return true;
        }

        internal void CommitPreviewAndBuildEscape(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldWorkspace workspace)
        {
            if (!grid.IsValid)
                throw new ArgumentException("Obstacle preview commit requires a valid grid.", nameof(grid));
            if (!surface.IsValid)
                throw new ArgumentException("Obstacle preview commit requires a valid surface bake.", nameof(surface));
            if (workspace == null)
                throw new ArgumentNullException(nameof(workspace));
            if (workspace.Capacity != grid.CellCount)
                throw new ArgumentException("Obstacle preview workspace capacity must match the grid.", nameof(workspace));
            workspace.CommitObstacleScratch();
            FlowFieldSolver.BuildEscapeDirections(grid, surface, workspace);
        }

        private static void ValidateRequest(in FlowFieldObstacleRequest request)
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
