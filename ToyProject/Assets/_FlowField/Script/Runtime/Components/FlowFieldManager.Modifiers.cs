using System;

namespace Common.FlowField
{
    public partial class FlowFieldManager
    {
        public bool RegisterVectorModifier(IFlowFieldVectorModifier modifier)
        {
            if (modifier == null)
                throw new ArgumentNullException(nameof(modifier));
            ThrowIfInputAllowedForMode();
            return ActiveSession.RegisterModifierCommand(modifier);
        }

        public bool UnregisterVectorModifier(IFlowFieldVectorModifier modifier)
        {
            if (modifier == null)
                return false;
            ThrowIfInputAllowedForMode();
            return ActiveSession.UnregisterModifierCommand(modifier);
        }

        public void NotifyModifierChanged(
            IFlowFieldVectorModifier modifier,
            FlowFieldModifierChange change)
        {
            if (modifier == null)
                throw new ArgumentNullException(nameof(modifier));
            if ((change & ~(FlowFieldModifierChange.Value
                | FlowFieldModifierChange.Area
                | FlowFieldModifierChange.Priority)) != 0)
                throw new ArgumentOutOfRangeException(nameof(change));
            if (change == FlowFieldModifierChange.None)
                return;
            ThrowIfInputAllowedForMode();
            ActiveSession.NotifyModifierChangedCommand(modifier, change);
        }
    }
}
