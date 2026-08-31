using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
    internal readonly struct FlowFieldModifierBuildRequest
    {
        public FlowFieldGridSpace Grid { get; }
        public FlowFieldSurfaceBakeData Surface { get; }
        public FlowFieldWorkspace Workspace { get; }
        public float ObstacleCheckHeight { get; }
        public float ObstacleCheckCenterOffset { get; }

        public FlowFieldModifierBuildRequest(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
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
        private Collider[] _overlapBuffer;

#if UNITY_EDITOR
        private readonly List<FlowFieldModifierLayer> _editorLayers = new List<FlowFieldModifierLayer>(16);
        private Collider[] _editorOverlapBuffer;
#endif

        internal FlowFieldModifierPipeline(FlowFieldModifierRegistry registry)
        {
            if (registry == null)
                throw new ArgumentNullException(nameof(registry));
            _registry = registry;
        }

        internal bool RebuildAreaData(in FlowFieldModifierBuildRequest request, out bool changed)
        {
            changed = false;
            if (!request.Grid.IsValid)
                throw new ArgumentException("Modifier rebuild requires a valid grid.", nameof(request));
            if (request.Surface == null)
                throw new ArgumentNullException(nameof(request.Surface));
            if (!request.Surface.HasValidData)
                throw new ArgumentException("Modifier rebuild requires a valid surface bake.", nameof(request.Surface));
            if (request.Workspace == null)
                throw new ArgumentNullException(nameof(request.Workspace));
            if (request.Workspace.Capacity != request.Grid.CellCount)
                throw new ArgumentException("Modifier workspace capacity must match the grid.", nameof(request.Workspace));
            if (!FlowFieldGridSpace.IsFinite(request.ObstacleCheckHeight) || request.ObstacleCheckHeight <= 0f)
                throw new ArgumentOutOfRangeException(nameof(request.ObstacleCheckHeight));
            if (!FlowFieldGridSpace.IsFinite(request.ObstacleCheckCenterOffset))
                throw new ArgumentOutOfRangeException(nameof(request.ObstacleCheckCenterOffset));
            if (_registry.Entries.Count == 0)
                return false;

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
                return false;

            Physics.SyncTransforms();
            for (int i = 0; i < _registry.Entries.Count; i++)
            {
                FlowFieldModifierRegistry.Entry entry = _registry.Entries[i];
                if (!entry.AreaDirty)
                    continue;

                ResizeRuntimeMask(entry, request.Grid.CellCount);
                Collider collider = entry.Modifier.InfluenceCollider;

                FlowFieldModifierMaskBuilder.Build(
                    request.Grid,
                    request.Surface,
                    collider,
                    request.ObstacleCheckHeight,
                    request.ObstacleCheckCenterOffset,
                    entry.InfluenceScratch,
                    ref _overlapBuffer);

                if (!FlowFieldObstacleMaskBuilder.AreEqual(entry.InfluenceMask, entry.InfluenceScratch))
                {
                    Array.Copy(entry.InfluenceScratch, entry.InfluenceMask, entry.InfluenceMask.Length);
                    changed = true;
                }

                entry.InfluenceCollider = collider;
                _registry.UpdateColliderSnapshot(entry, collider);
                entry.AreaDirty = false;
            }

            return true;
        }

        internal bool RebuildFinalField(
            in FlowFieldModifierBuildRequest request,
            Vector3 defaultDirection,
            FlowFieldCellRect dirty)
        {
            if (!request.Grid.IsValid)
                throw new ArgumentException("Modifier compose requires a valid grid.", nameof(request));
            if (request.Surface == null)
                throw new ArgumentNullException(nameof(request.Surface));
            if (!request.Surface.HasValidData)
                throw new ArgumentException("Modifier compose requires a valid surface bake.", nameof(request.Surface));
            if (request.Workspace == null)
                throw new ArgumentNullException(nameof(request.Workspace));
            if (request.Workspace.Capacity != request.Grid.CellCount)
                throw new ArgumentException("Modifier workspace capacity must match the grid.", nameof(request.Workspace));
            if (!FlowFieldGridSpace.IsFinite(request.ObstacleCheckHeight) || request.ObstacleCheckHeight <= 0f)
                throw new ArgumentOutOfRangeException(nameof(request.ObstacleCheckHeight));
            if (!FlowFieldGridSpace.IsFinite(request.ObstacleCheckCenterOffset))
                throw new ArgumentOutOfRangeException(nameof(request.ObstacleCheckCenterOffset));

            _registry.BeginComposition();
            try
            {
                if (!dirty.IsValid)
                    dirty = FlowFieldCellRect.Full(request.Grid);

                BuildRuntimeLayers(request.Grid.CellCount);
                FlowFieldJobRunner.RunBaseComposeJob(
                    request.Grid,
                    request.Surface,
                    request.Workspace,
                    defaultDirection);
                FlowFieldFinalFieldComposer.ApplyModifiers(
                    request.Grid,
                    request.Surface,
                    request.Workspace,
                    _runtimeLayers);
                request.Workspace.BumpGeneration(request.Grid, dirty);
                return true;
            }
            finally
            {
                _registry.EndComposition();
            }
        }

#if UNITY_EDITOR
        internal void BuildEditorFinalField(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
            FlowFieldWorkspace workspace,
            Vector3 defaultDirection,
            float obstacleCheckHeight,
            float obstacleCheckCenterOffset)
        {
            if (!grid.IsValid)
                throw new ArgumentException("Editor modifier compose requires a valid grid.", nameof(grid));
            if (surface == null)
                throw new ArgumentNullException(nameof(surface));
            if (!surface.HasValidData)
                throw new ArgumentException("Editor modifier compose requires a valid surface bake.", nameof(surface));
            if (workspace == null)
                throw new ArgumentNullException(nameof(workspace));
            if (workspace.Capacity != grid.CellCount)
                throw new ArgumentException("Editor modifier workspace capacity must match the grid.", nameof(workspace));
            if (!FlowFieldGridSpace.IsFinite(obstacleCheckHeight) || obstacleCheckHeight <= 0f)
                throw new ArgumentOutOfRangeException(nameof(obstacleCheckHeight));
            if (!FlowFieldGridSpace.IsFinite(obstacleCheckCenterOffset))
                throw new ArgumentOutOfRangeException(nameof(obstacleCheckCenterOffset));

            _registry.BeginComposition();
            try
            {
                Physics.SyncTransforms();
                _editorLayers.Clear();
                for (int i = 0; i < _registry.Entries.Count; i++)
                {
                    FlowFieldModifierRegistry.Entry entry = _registry.Entries[i];
                    IFlowFieldVectorModifier modifier = entry.Modifier;
                    if (IsMissingModifier(modifier))
                        throw new InvalidOperationException("Registered modifier data is inconsistent.");

                    ResizeEditorMask(entry, grid.CellCount);
                    Collider collider = modifier.InfluenceCollider;

                    FlowFieldModifierMaskBuilder.Build(
                        grid,
                        surface,
                        collider,
                        obstacleCheckHeight,
                        obstacleCheckCenterOffset,
                        entry.EditorInfluenceScratch,
                        ref _editorOverlapBuffer);
                    Array.Copy(
                        entry.EditorInfluenceScratch,
                        entry.EditorInfluenceMask,
                        entry.EditorInfluenceMask.Length);
                    _editorLayers.Add(new FlowFieldModifierLayer(modifier, entry.EditorInfluenceMask));
                }

                FlowFieldFinalFieldComposer.Compose(
                    grid,
                    surface,
                    workspace,
                    defaultDirection,
                    _editorLayers);
            }
            finally
            {
                _registry.EndComposition();
            }
        }
#endif

        internal void Clear()
        {
            _runtimeLayers.Clear();
            _overlapBuffer = null;
#if UNITY_EDITOR
            _editorLayers.Clear();
            _editorOverlapBuffer = null;
#endif
        }

        private void BuildRuntimeLayers(int cellCount)
        {
            _runtimeLayers.Clear();
            for (int i = 0; i < _registry.Entries.Count; i++)
            {
                FlowFieldModifierRegistry.Entry entry = _registry.Entries[i];
                if (IsMissingModifier(entry.Modifier)
                    || entry.InfluenceMask == null
                    || entry.InfluenceMask.Length != cellCount)
                    throw new InvalidOperationException("Registered modifier data is inconsistent.");

                entry.InfluenceIndices.Clear();
                for (int index = 0; index < entry.InfluenceMask.Length; index++)
                {
                    if (entry.InfluenceMask[index])
                        entry.InfluenceIndices.Add(index);
                }

                _runtimeLayers.Add(new FlowFieldModifierLayer(
                    entry.Modifier,
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
            entry.InfluenceIndices.Clear();
        }

#if UNITY_EDITOR
        private static void ResizeEditorMask(
            FlowFieldModifierRegistry.Entry entry,
            int cellCount)
        {
            if (entry.EditorInfluenceMask != null && entry.EditorInfluenceMask.Length == cellCount)
                return;

            entry.EditorInfluenceMask = new bool[cellCount];
            entry.EditorInfluenceScratch = new bool[cellCount];
        }
#endif

        private static bool IsMissingModifier(IFlowFieldVectorModifier modifier)
            => modifier == null || modifier is UnityEngine.Object unityObject && unityObject == null;
    }
}
