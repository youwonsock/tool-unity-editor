using System;
using System.Collections.Generic;
using UnityEngine;

namespace Supercent.Common.FlowField
{
    public partial class FlowFieldManager
    {
        private FlowFieldModifierRegistry _modifierRegistry;
        private FlowFieldModifierPipeline _modifierPipeline;

        public void RegisterVectorModifier(IFlowFieldVectorModifier modifier)
        {
            EnsureServices();
            FlowFieldModifierRegistryResult result = _modifierRegistry.Register(modifier);
            ApplyModifierResult(result);
            ReportModifierDiagnostics();
            InvalidateModifierPreview();
        }

        public void UnregisterVectorModifier(IFlowFieldVectorModifier modifier)
        {
            EnsureServices();
            FlowFieldModifierRegistryResult result = _modifierRegistry.Unregister(modifier);
            ApplyModifierResult(result);
            ReportModifierDiagnostics();
            InvalidateModifierPreview();
        }

        public void MarkVectorModifierDirty(IFlowFieldVectorModifier modifier)
        {
            EnsureServices();
            FlowFieldModifierRegistryResult result = _modifierRegistry.MarkDirty(modifier);
            ApplyModifierResult(result);
            ReportModifierDiagnostics();
            InvalidateModifierPreview();
        }

        public void MarkVectorModifierAreaDirty(IFlowFieldVectorModifier modifier)
        {
            EnsureServices();
            ApplyModifierResult(_modifierRegistry.MarkAreaDirty(modifier));
            ReportModifierDiagnostics();
            InvalidateModifierPreview();
        }

        public bool HasDuplicateVectorModifierPriority(IFlowFieldVectorModifier modifier)
        {
            EnsureServices();
            return _modifierRegistry.HasDuplicate(modifier);
        }

        private void DetectModifierChanges()
        {
            EnsureServices();
            FlowFieldModifierRegistryResult result = _modifierRegistry.DetectChanges();
            ApplyModifierResult(result);
            ReportModifierDiagnostics();
            if (result.AreaDirty || result.ValueDirty || result.FinalDirty)
                InvalidateModifierPreview();
        }

        private void FlushPendingModifierChanges()
        {
            if (_modifierRegistry == null)
                return;

            ReportModifierDiagnostics();
            FlowFieldModifierRegistryResult result = _modifierRegistry.FlushPendingChanges();
            ApplyModifierResult(result);
            ReportModifierDiagnostics();
        }

        private void MarkAllModifierAreasDirty()
        {
            EnsureServices();
            _modifierRegistry.MarkAllAreasDirty();
        }

        private bool RebuildModifierAreaData()
        {
            EnsureServices();
            FlowFieldModifierBuildRequest request = CreateModifierBuildRequest(_context.Workspace);
            bool result = _modifierPipeline.RebuildAreaData(request, out bool changed) && changed;
            ReportModifierDiagnostics();
            return result;
        }

        private bool RebuildFinalField()
        {
            EnsureServices();
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
            ReportModifierDiagnostics();
            return result;
        }

        private FlowFieldModifierBuildRequest CreateModifierBuildRequest(FlowFieldWorkspace workspace)
            => new FlowFieldModifierBuildRequest(
                _context.Grid,
                _surfaceBakeData,
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

        private void ReportModifierDiagnostics()
        {
            if (_modifierRegistry == null)
                return;

            IReadOnlyList<FlowFieldModifierDiagnostic> diagnostics = _modifierRegistry.Diagnostics;
            for (int i = 0; i < diagnostics.Count; i++)
            {
                FlowFieldModifierDiagnostic diagnostic = diagnostics[i];
                switch (diagnostic.Kind)
                {
                    case FlowFieldModifierDiagnosticKind.InvalidConfiguration:
                        Debug.LogError(
                            $"[{nameof(FlowFieldManager)}] Vector Modifier 구성 오류: "
                            + $"{diagnostic.Message} ({diagnostic.Modifier})",
                            this);
                        break;
                    case FlowFieldModifierDiagnosticKind.AccessException:
                        Debug.LogWarning(
                            $"[{nameof(FlowFieldManager)}] Vector modifier 접근 중 예외가 발생해 "
                            + $"재등록 전까지 제외합니다: {diagnostic.Modifier}\n{diagnostic.Exception}",
                            this);
                        break;
                    case FlowFieldModifierDiagnosticKind.RuntimeException:
                        Debug.LogWarning(
                            $"[{nameof(FlowFieldManager)}] Vector modifier가 예외를 발생시켜 "
                            + $"재등록 전까지 제외합니다: {diagnostic.Modifier}\n{diagnostic.Exception}",
                            this);
                        break;
                    case FlowFieldModifierDiagnosticKind.EditorException:
                        Debug.LogWarning(
                            $"[{nameof(FlowFieldManager)}] Edit Mode Vector modifier 미리보기 중 "
                            + $"예외가 발생했습니다: {diagnostic.Modifier}\n{diagnostic.Exception}",
                            this);
                        break;
                    case FlowFieldModifierDiagnosticKind.DuplicatePriority:
                        Debug.LogWarning(
                            $"[{nameof(FlowFieldManager)}] Priority {diagnostic.Priority}인 Vector Modifier가 "
                            + "중복되었습니다. 동률은 등록 순서로 적용되므로 결정적 결과가 필요하면 "
                            + "고유 Priority를 사용하세요.",
                            this);
                        break;
                }
            }

            _modifierRegistry.ClearDiagnostics();
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
