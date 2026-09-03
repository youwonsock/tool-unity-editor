using System;

namespace Common.FlowField
{
    public partial class FlowFieldManager
    {
        public void RegisterVectorModifier(IFlowFieldVectorModifier modifier)
        {
            if (modifier == null)
                throw new ArgumentNullException(nameof(modifier));
            ThrowIfInputAllowedForMode();
            _session.RegisterModifier(modifier);
            InvalidateModifierPreview();
        }

        public void UnregisterVectorModifier(IFlowFieldVectorModifier modifier)
        {
            if (modifier == null)
                throw new ArgumentNullException(nameof(modifier));
            ThrowIfInputAllowedForMode();
            _session.UnregisterModifier(modifier);
            InvalidateModifierPreview();
        }

        public void MarkVectorModifierDirty(IFlowFieldVectorModifier modifier)
        {
            if (modifier == null)
                throw new ArgumentNullException(nameof(modifier));
            ThrowIfInputAllowedForMode();
            _session.MarkModifierDirty(modifier);
            InvalidateModifierPreview();
        }

        public void MarkVectorModifierAreaDirty(IFlowFieldVectorModifier modifier)
        {
            if (modifier == null)
                throw new ArgumentNullException(nameof(modifier));
            ThrowIfInputAllowedForMode();
            _session.MarkModifierAreaDirty(modifier);
            InvalidateModifierPreview();
        }

        private void InvalidateModifierPreview()
        {
#if UNITY_EDITOR
            InvalidateEditorPreview();
#endif
        }
    }
}
