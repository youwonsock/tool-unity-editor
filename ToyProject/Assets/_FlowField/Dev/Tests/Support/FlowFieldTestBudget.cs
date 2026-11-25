namespace Common.FlowField.Tests
{
    /// <summary>Budgets for driving a session by hand.</summary>
    internal static class FlowFieldTestBudget
    {
        /// <summary>
        /// A budget that expires after the given number of checks, whatever
        /// the clock says, so a pump stops at the same point every run.
        /// </summary>
        internal static FlowFieldBuildBudget AfterChecks(int checks)
        {
            int seen = 0;
            return new FlowFieldBuildBudget(checks, () => seen++);
        }
    }
}
