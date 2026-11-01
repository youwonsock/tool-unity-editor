namespace Common.FlowField
{
    /// <summary>
    /// The newest accepted request of a session and whether a build still owes it.
    /// A running build and the published field keep whatever they need themselves.
    /// </summary>
    internal sealed class FlowFieldRequestInbox<TRequest> where TRequest : struct
    {
        internal TRequest Latest;
        internal bool HasLatest;
        internal bool Pending;

        internal void Accept(in TRequest request)
        {
            Latest = request;
            HasLatest = true;
            Pending = true;
        }

        internal void Clear()
        {
            Latest = default;
            HasLatest = false;
            Pending = false;
        }
    }
}
