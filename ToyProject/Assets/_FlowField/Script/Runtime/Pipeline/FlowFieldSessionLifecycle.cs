namespace Common.FlowField
{
    internal enum FlowFieldSessionLifecycle
    {
        Uninitialized,
        Active,
        Building,
        Suspended,
        Faulted,
        Released,
    }
}
