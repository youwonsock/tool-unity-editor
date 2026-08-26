using System;

namespace Supercent.Common.FlowField
{
    internal enum FlowFieldModifierDiagnosticKind
    {
        InvalidConfiguration,
        AccessException,
        RuntimeException,
        EditorException,
        DuplicatePriority,
    }

    internal readonly struct FlowFieldModifierDiagnostic
    {
        public FlowFieldModifierDiagnosticKind Kind { get; }
        public IFlowFieldVectorModifier Modifier { get; }
        public string Message { get; }
        public Exception Exception { get; }
        public int Priority { get; }

        public FlowFieldModifierDiagnostic(
            FlowFieldModifierDiagnosticKind kind,
            IFlowFieldVectorModifier modifier,
            string message = null,
            Exception exception = null,
            int priority = 0)
        {
            Kind = kind;
            Modifier = modifier;
            Message = message;
            Exception = exception;
            Priority = priority;
        }
    }

    internal readonly struct FlowFieldModifierRegistryResult
    {
        public bool AreaDirty { get; }
        public bool ValueDirty { get; }
        public bool FinalDirty { get; }

        public FlowFieldModifierRegistryResult(bool areaDirty, bool valueDirty, bool finalDirty)
        {
            AreaDirty = areaDirty;
            ValueDirty = valueDirty;
            FinalDirty = finalDirty;
        }
    }
}
