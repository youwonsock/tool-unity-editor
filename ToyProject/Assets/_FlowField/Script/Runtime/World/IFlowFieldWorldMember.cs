namespace Common.FlowField
{
    /// <summary>
    /// What a world needs of an area: its sessions, one per agent type with
    /// the Default type's first, and the boundary its commands run in. A
    /// Manager is one in Play Mode; a world bake joins Edit Mode sessions.
    /// </summary>
    internal interface IFlowFieldWorldMember
    {
        string Name { get; }
        int WorldSessionCount { get; }
        FlowFieldSession WorldSession(int index);
        string WorldSessionName(int index);
        FlowFieldExecutionBoundary.OperationScope EnterOperation();
        bool IsInUserCode { get; }
    }
}
