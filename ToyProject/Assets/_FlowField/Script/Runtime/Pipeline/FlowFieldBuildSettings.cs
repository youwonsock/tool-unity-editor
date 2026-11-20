using System;

namespace Common.FlowField
{
    /// <summary>
    /// How long FlowField's main-thread work may run each frame, shared by every
    /// Manager in the project. It does not limit solves on worker threads, and a
    /// stretch that cannot be divided, such as Physics reads, can exceed it.
    /// </summary>
    public static class FlowFieldBuildSettings
    {
        private const double DefaultBudgetMilliseconds = 2d;
        private static double _budgetMilliseconds = DefaultBudgetMilliseconds;

        // A budget set during one Play session must not leak into the next
        // when domain reload is disabled.
        internal static void ResetToDefault() => _budgetMilliseconds = DefaultBudgetMilliseconds;

        public static double BudgetMilliseconds
        {
            get => _budgetMilliseconds;
            set
            {
                if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0d)
                    throw new ArgumentOutOfRangeException(nameof(value));
                _budgetMilliseconds = value;
            }
        }
    }
}
