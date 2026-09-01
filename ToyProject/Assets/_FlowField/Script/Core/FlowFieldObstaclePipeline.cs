using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
    internal readonly struct FlowFieldObstacleRequest
    {
        public FlowFieldGridSpace Grid { get; }
        public FlowFieldSurfaceBakeData Surface { get; }
        public FlowFieldStaticObstacleBakeData StaticBake { get; }
        public FlowFieldWorkspace Workspace { get; }
        public LayerMask ObstacleLayer { get; }
        public float CheckHeight { get; }
        public float CenterOffset { get; }
        public float Clearance { get; }
        public bool UseUnregisteredSweep { get; }
        public FlowFieldCellRect DirtyRegion { get; }

        public FlowFieldObstacleRequest(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
            FlowFieldStaticObstacleBakeData staticBake,
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
            StaticBake = staticBake;
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
        private readonly HashSet<Collider> _dynamicSet = new HashSet<Collider>();

        public IReadOnlyList<Collider> DynamicObstacles => _dynamicObstacles;
        public bool AllBlockedWarningIssued;

        public bool RegisterDynamicObstacle(Collider collider)
        {
            if (collider == null || !_dynamicSet.Add(collider))
                return false;

            _dynamicObstacles.Add(collider);
            _dynamicBounds.Add(collider.bounds);
            return true;
        }

        public bool UnregisterDynamicObstacle(Collider collider)
        {
            if (collider == null || !_dynamicSet.Remove(collider))
                return false;

            int index = _dynamicObstacles.IndexOf(collider);
            if (index >= 0)
            {
                _dynamicObstacles.RemoveAt(index);
                _dynamicBounds.RemoveAt(index);
            }

            return true;
        }

        public void ClearDynamicObstacles()
        {
            _dynamicObstacles.Clear();
            _dynamicBounds.Clear();
            _dynamicSet.Clear();
        }

        internal FlowFieldObstacleResult RebuildMasks(
            in FlowFieldObstacleRequest request,
            bool rebuildStatic,
            bool rebuildDynamic)
        {
            ValidateRequest(request);
            bool changed = false;
            FlowFieldCellRect dirty = FlowFieldCellRect.Invalid;
            int excludedColliderCount = 0;
            if (rebuildStatic)
            {
                ApplyStaticMask(
                    request.Grid,
                    request.Surface,
                    request.Workspace,
                    request.StaticBake,
                    request.ObstacleLayer,
                    request.CheckHeight,
                    request.CenterOffset,
                    request.Clearance,
                    ref _overlapBuffer,
                    ref _targetOverlapBuffer,
                    out excludedColliderCount);
                changed = true;
            }

            if (!rebuildDynamic)
                return new FlowFieldObstacleResult(changed, dirty, excludedColliderCount);

            if (request.UseUnregisteredSweep)
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
                    bool dynamicChanged = !FlowFieldObstacleMaskBuilder.AreEqual(
                        request.Workspace.ObstacleScratch,
                        request.Workspace.DynamicBlocked);
                    changed |= dynamicChanged;
                    if (dynamicChanged)
                    {
                        System.Array.Copy(
                            request.Workspace.ObstacleScratch,
                            request.Workspace.DynamicBlocked,
                            request.Workspace.Capacity);
                        dirty = FlowFieldCellRect.Full(request.Grid);
                    }
                }
            }
            else
            {
                FlowFieldCellRect dynamicDirty = RebuildDynamicOverlay(
                    request.Grid,
                    request.Surface,
                    request.CheckHeight,
                    request.CenterOffset,
                    request.Clearance,
                    request.Workspace.ObstacleScratch,
                    request.DirtyRegion);
                bool dynamicChanged = !FlowFieldObstacleMaskBuilder.AreEqual(
                    request.Workspace.ObstacleScratch,
                    request.Workspace.DynamicBlocked);
                changed |= dynamicChanged;
                if (dynamicChanged)
                {
                    System.Array.Copy(
                        request.Workspace.ObstacleScratch,
                        request.Workspace.DynamicBlocked,
                        request.Workspace.Capacity);
                    dirty = dynamicDirty;
                }
            }

            return new FlowFieldObstacleResult(changed, dirty, excludedColliderCount);
        }

        private static bool ApplyStaticMask(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
            FlowFieldWorkspace workspace,
            FlowFieldStaticObstacleBakeData staticBake,
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

            if (staticBake != null)
            {
                if (!staticBake.HasValidData)
                    throw new ArgumentException("Static obstacle bake data is invalid.", nameof(staticBake));
                staticBake.CopyTo(workspace.StaticBlocked);
                return true;
            }

            if (!FlowFieldObstacleMaskBuilder.BuildStatic(
                    grid,
                    surface,
                    obstacleLayer,
                    checkHeight,
                    centerOffset,
                    clearance,
                    workspace.StaticBlocked,
                    ref overlapBuffer,
                    ref targetOverlapBuffer,
                    out excludedColliderCount,
                    syncTransformsBeforeQuery: true))
                throw new InvalidOperationException("Static obstacle mask 생성에 실패했습니다.");
            return true;
        }

        public bool BuildFullLayerScratch(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
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
            FlowFieldSurfaceBakeData surface,
            float checkHeight,
            float centerOffset,
            float clearance,
            bool[] dynamicBlocked,
            FlowFieldCellRect extraDirty)
        {
            if (!grid.IsValid)
                throw new ArgumentException("Dynamic obstacle rebuild requires a valid grid.", nameof(grid));
            if (surface == null)
                throw new ArgumentNullException(nameof(surface));
            if (!surface.HasValidData)
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
                    throw new InvalidOperationException("A registered dynamic obstacle was destroyed without being unregistered.");

                Bounds previous = _dynamicBounds[i];
                Bounds current = collider.bounds;
                dirty = FlowFieldCellRect.Union(dirty, FlowFieldCellRect.FromBounds(grid, previous));
                dirty = FlowFieldCellRect.Union(dirty, FlowFieldCellRect.FromBounds(grid, current));
                _dynamicBounds[i] = current;

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
        {
            for (int i = 0; i < _dynamicObstacles.Count; i++)
            {
                Collider collider = _dynamicObstacles[i];
                if (collider == null)
                    throw new InvalidOperationException("A registered dynamic obstacle was destroyed without being unregistered.");

                Bounds current = collider.bounds;
                Bounds previous = _dynamicBounds[i];
                if ((current.center - previous.center).sqrMagnitude > 0.0001f
                    || (current.size - previous.size).sqrMagnitude > 0.0001f)
                    return true;
            }

            return false;
        }

        public bool CommitCombinedAndBuildEscape(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
            FlowFieldWorkspace workspace,
            out bool hasWalkable)
        {
            if (!grid.IsValid)
                throw new ArgumentException("Obstacle commit requires a valid grid.", nameof(grid));
            if (surface == null)
                throw new ArgumentNullException(nameof(surface));
            if (!surface.HasValidData)
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
            FlowFieldSurfaceBakeData surface,
            FlowFieldWorkspace workspace)
        {
            if (!grid.IsValid)
                throw new ArgumentException("Obstacle preview commit requires a valid grid.", nameof(grid));
            if (surface == null)
                throw new ArgumentNullException(nameof(surface));
            if (!surface.HasValidData)
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
            if (request.Surface == null)
                throw new ArgumentNullException(nameof(request.Surface));
            if (!request.Surface.HasValidData)
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
