namespace Common.FlowField
{
    /// <summary>
    /// The published Volume3D field and the spare arrays builds take. A
    /// publication keeps the arrays the old field no longer shares as spares.
    /// </summary>
    internal sealed class FlowFieldVolumeFieldStore
    {
        internal readonly FlowFieldSpareArrays Spares = new FlowFieldSpareArrays();

        // The published field, or null.
        internal FlowFieldField<FlowFieldVolumeRequest> Committed { get; private set; }

        // True when the field now reads differently. An agent layer an
        // additional Goal still reads keeps its arrays.
        internal bool Publish(FlowFieldField<FlowFieldVolumeRequest> next, FlowFieldGoalSet goals)
        {
            bool changed = !FlowFieldField.HasSameContent(Committed, next);
            Spares.Retire(Committed, next, goals.HoldsAgent(Committed?.Agent));
            Committed = next;
            return changed;
        }

        // Nothing is published; the spares stay for the next build.
        internal void Drop() => Committed = null;

        internal void Release()
        {
            Committed = null;
            Spares.Clear();
        }
    }
}
