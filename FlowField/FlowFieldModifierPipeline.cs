using System;
using System.Collections.Generic;
using UnityEngine;

namespace Supercent.Common.FlowField
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
            _registry = registry;
        }

        internal bool RebuildAreaData(in FlowFieldModifierBuildRequest request, out bool changed)
        {
            changed = false;
            if (!request.Grid.IsValid
                || request.Surface == null
                || !request.Surface.HasValidData
                || _registry.Entries.Count == 0)
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

                EnsureRuntimeMaskCapacity(entry, request.Grid.CellCount);
                Collider collider = null;
                if (!IsMissingModifier(entry.Modifier))
                {
                    try
                    {
                        collider = entry.Modifier.InfluenceCollider;
                    }
                    catch (Exception exception)
                    {
                        _registry.ReportAccessException(entry.Modifier, exception);
                    }
                }

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
            if (!request.Grid.IsValid || request.Workspace.Capacity != request.Grid.CellCount)
                return false;

            _registry.BeginComposition();
            try
            {
                if (!dirty.IsValid)
                    dirty = FlowFieldCellRect.Full(request.Grid);

                while (true)
                {
                    BuildRuntimeLayers(request.Grid.CellCount);
                    FlowFieldJobRunner.RunBaseComposeJob(
                        request.Grid,
                        request.Surface,
                        request.Workspace,
                        defaultDirection);
                    if (FlowFieldFinalFieldComposer.TryApplyModifiers(
                            request.Grid,
                            request.Surface,
                            request.Workspace,
                            _runtimeLayers,
                            out IFlowFieldVectorModifier faultedModifier,
                            out Exception exception))
                        break;

                    if (faultedModifier == null
                        || !_registry.MarkRuntimeFaulted(faultedModifier, exception))
                        break;
                }

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
            Physics.SyncTransforms();
            _editorLayers.Clear();
            for (int i = 0; i < _registry.Entries.Count; i++)
            {
                FlowFieldModifierRegistry.Entry entry = _registry.Entries[i];
                IFlowFieldVectorModifier modifier = entry.Modifier;
                if (IsMissingModifier(modifier) || _registry.IsEditorFaulted(modifier))
                    continue;

                EnsureEditorMaskCapacity(entry, grid.CellCount);
                Collider collider;
                try
                {
                    collider = modifier.InfluenceCollider;
                }
                catch (Exception exception)
                {
                    _registry.ReportEditorAccessException(modifier, exception);
                    continue;
                }

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

            while (true)
            {
                if (FlowFieldFinalFieldComposer.TryCompose(
                        grid,
                        surface,
                        workspace,
                        defaultDirection,
                        _editorLayers,
                        out IFlowFieldVectorModifier faultedModifier,
                        out Exception exception))
                    return;

                if (faultedModifier == null || !_registry.MarkEditorFaulted(faultedModifier, exception))
                    return;

                for (int i = _editorLayers.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(_editorLayers[i].Modifier, faultedModifier))
                        _editorLayers.RemoveAt(i);
                }
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
                    || _registry.IsFaulted(entry.Modifier)
                    || entry.InfluenceMask == null
                    || entry.InfluenceMask.Length != cellCount)
                    continue;

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

        private static void EnsureRuntimeMaskCapacity(
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
        private static void EnsureEditorMaskCapacity(
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
