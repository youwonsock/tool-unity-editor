using System;
using UnityEngine;

namespace Common.FlowField
{
    public partial class FlowFieldManager
    {
        private FlowFieldModifierRegistry _modifierRegistry;
        private FlowFieldModifierPipeline _modifierPipeline;

        public void RegisterVectorModifier(IFlowFieldVectorModifier modifier)
        {
            if (modifier == null)
                throw new ArgumentNullException(nameof(modifier));
            ThrowIfUnavailable();
            RequireServices();
            FlowFieldModifierRegistryResult result = _modifierRegistry.Register(modifier);
            ApplyModifierResult(result);
            InvalidateModifierPreview();
        }

        public void UnregisterVectorModifier(IFlowFieldVectorModifier modifier)
        {
            if (modifier == null)
                throw new ArgumentNullException(nameof(modifier));
            ThrowIfUnavailable();
            RequireServices();
            FlowFieldModifierRegistryResult result = _modifierRegistry.Unregister(modifier);
            ApplyModifierResult(result);
            InvalidateModifierPreview();
        }

        public void MarkVectorModifierDirty(IFlowFieldVectorModifier modifier)
        {
            if (modifier == null)
                throw new ArgumentNullException(nameof(modifier));
            ThrowIfUnavailable();
            RequireServices();
            FlowFieldModifierRegistryResult result = _modifierRegistry.MarkDirty(modifier);
            ApplyModifierResult(result);
            InvalidateModifierPreview();
        }

        public void MarkVectorModifierAreaDirty(IFlowFieldVectorModifier modifier)
        {
            if (modifier == null)
                throw new ArgumentNullException(nameof(modifier));
            ThrowIfUnavailable();
            RequireServices();
            ApplyModifierResult(_modifierRegistry.MarkAreaDirty(modifier));
            InvalidateModifierPreview();
        }

        private void DetectModifierChanges()
        {
            RequireServices();
            FlowFieldModifierRegistryResult result = _modifierRegistry.DetectChanges();
            ApplyModifierResult(result);
            if (result.AreaDirty || result.ValueDirty || result.FinalDirty)
                InvalidateModifierPreview();
        }

        private void FlushPendingModifierChanges()
        {
            RequireServices();

            FlowFieldModifierRegistryResult result = _modifierRegistry.FlushPendingChanges();
            ApplyModifierResult(result);
        }

        private void MarkAllModifierAreasDirty()
        {
            RequireServices();
            _modifierRegistry.MarkAllAreasDirty();
        }

        private bool RebuildModifierAreaData()
        {
            RequireServices();
            FlowFieldModifierBuildRequest request = CreateModifierBuildRequest(_context.Workspace);
            bool result = _modifierPipeline.RebuildAreaData(request, out bool changed) && changed;
            return result;
        }

        private bool RebuildFinalField()
        {
            RequireServices();
            FlowFieldCellRect dirty = _context.DirtyFinalRegion.IsValid
                ? _context.DirtyFinalRegion
                : FlowFieldCellRect.Full(_context.Grid);
            FlowFieldModifierBuildRequest request = CreateModifierBuildRequest(_context.Workspace);
            bool result = _modifierPipeline.RebuildFinalField(
                request,
                _context.ResolvedDefaultDirection,
                dirty);
            if (result)
                _context.DirtyFinalRegion = FlowFieldCellRect.Invalid;
            return result;
        }

        private FlowFieldModifierBuildRequest CreateModifierBuildRequest(FlowFieldWorkspace workspace)
            => new FlowFieldModifierBuildRequest(
                _context.Grid,
                ActiveSurfaceBakeData,
                workspace,
                _obstacleCheckHeight,
                _obstacleCheckCenterOffset);

        private void ApplyModifierResult(FlowFieldModifierRegistryResult result)
        {
            if (result.AreaDirty)
                _context.MarkDirty(FlowFieldDirtyFlags.ModifierArea);
            if (result.ValueDirty)
                _context.MarkDirty(FlowFieldDirtyFlags.ModifierValue);
            if (result.FinalDirty)
                _context.MarkDirty(FlowFieldDirtyFlags.FinalRegion);
        }

        private void RequireServices()
        {
            if (_modifierRegistry == null || _modifierPipeline == null)
                throw new InvalidOperationException("FlowField modifier services are not initialized.");
        }

        private void ClearModifierRuntimeState()
        {
            _modifierPipeline?.Clear();
            _modifierRegistry?.Clear();
        }

        private void InvalidateModifierPreview()
        {
#if UNITY_EDITOR
            InvalidateEditorPreview();
#endif
        }
    }
}
