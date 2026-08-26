using System.Collections.Generic;
using UnityEngine;

namespace Supercent.Common.FlowField
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

        public FlowFieldObstacleResult(bool maskChanged, FlowFieldCellRect dirtyRegion)
        {
            MaskChanged = maskChanged;
            DirtyRegion = dirtyRegion;
        }
    }

    internal sealed class FlowFieldObstaclePipeline
    {
        private Collider[] _overlapBuffer;
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
            bool changed = false;
            FlowFieldCellRect dirty = FlowFieldCellRect.Invalid;
            if (rebuildStatic)
            {
                ApplyStaticMask(request.Grid, request.Workspace, request.StaticBake);
                changed = true;
            }

            if (!rebuildDynamic)
                return new FlowFieldObstacleResult(changed, dirty);

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

            return new FlowFieldObstacleResult(changed, dirty);
        }

        private static bool ApplyStaticMask(
            FlowFieldGridSpace grid,
            FlowFieldWorkspace workspace,
            FlowFieldStaticObstacleBakeData staticBake)
        {
            if (!grid.IsValid || workspace == null || workspace.Capacity != grid.CellCount)
                return false;

            if (staticBake != null && staticBake.HasValidData)
            {
                staticBake.CopyTo(workspace.StaticBlocked);
                return true;
            }

            System.Array.Clear(workspace.StaticBlocked, 0, workspace.Capacity);
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
            System.Array.Clear(dynamicBlocked, 0, dynamicBlocked.Length);
            FlowFieldCellRect dirty = extraDirty;
            float halfHeight = Mathf.Max(0.005f, checkHeight * 0.5f);
            float halfXZ = grid.CellSize * 0.5f + Mathf.Max(0f, clearance);
            Vector3 cellHalfExtents = new Vector3(halfXZ, halfHeight, halfXZ);

            for (int i = _dynamicObstacles.Count - 1; i >= 0; i--)
            {
                Collider collider = _dynamicObstacles[i];
                if (collider == null)
                {
                    Bounds stale = _dynamicBounds[i];
                    dirty = FlowFieldCellRect.Union(dirty, FlowFieldCellRect.FromBounds(grid, stale));
                    _dynamicObstacles.RemoveAt(i);
                    _dynamicBounds.RemoveAt(i);
                    continue;
                }

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
                    return true;

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
            workspace.RebuildCombinedBlocked();
            hasWalkable = FlowFieldSolver.BuildEscapeDirections(grid, surface, workspace);
            return true;
        }

        internal void CommitPreviewAndBuildEscape(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
            FlowFieldWorkspace workspace)
        {
            workspace.CommitObstacleScratch();
            FlowFieldSolver.BuildEscapeDirections(grid, surface, workspace);
        }
    }
}
