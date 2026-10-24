using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Common.FlowField
{
    /// <summary>
    /// Unity-independent cooperative scheduler. It does not create or find
    /// GameObjects; the runtime driver invokes PumpAll at its update boundary.
    /// Isolated Editor sessions are pumped directly by their owner.
    /// </summary>
    internal static class FlowFieldBuildScheduler
    {
        private static readonly List<IFlowFieldBuildOperation> Operations
            = new List<IFlowFieldBuildOperation>(8);
        private static int _cursor;
        private static bool _pumping;

        internal static int Count => Operations.Count;

        internal static void Register(IFlowFieldBuildOperation operation)
        {
            if (operation == null || Operations.Contains(operation))
                return;
            Operations.Add(operation);
            if (_cursor >= Operations.Count)
                _cursor = 0;
        }

        internal static void Unregister(IFlowFieldBuildOperation operation)
        {
            if (operation == null)
                return;
            int index = Operations.IndexOf(operation);
            if (index < 0)
                return;
            Operations.RemoveAt(index);
            if (Operations.Count == 0)
            {
                _cursor = 0;
                return;
            }
            if (index < _cursor)
                _cursor--;
            _cursor %= Operations.Count;
        }

        internal static void PumpAll(double budgetMilliseconds)
        {
            if (_pumping || Operations.Count == 0 || budgetMilliseconds <= 0d)
                return;

            _pumping = true;
            Stopwatch timer = Stopwatch.StartNew();
            try
            {
                int visitedWithoutWork = 0;
                while (Operations.Count > 0
                    && timer.Elapsed.TotalMilliseconds < budgetMilliseconds)
                {
                    if (_cursor >= Operations.Count)
                        _cursor = 0;
                    IFlowFieldBuildOperation operation = Operations[_cursor];
                    _cursor = (_cursor + 1) % Math.Max(1, Operations.Count);

                    if (operation == null
                        || operation.IsWaitingForExternalCompletion)
                    {
                        visitedWithoutWork++;
                        if (visitedWithoutWork >= Math.Max(1, Operations.Count))
                            break;
                        continue;
                    }

                    FlowFieldBuildBudget budget = new FlowFieldBuildBudget(
                        budgetMilliseconds,
                        () => timer.Elapsed.TotalMilliseconds);
                    bool wasBuilding = operation.IsBuilding;
                    operation.PumpInternal(in budget);
                    bool isBuilding = operation.IsBuilding;
                    if (!wasBuilding || !isBuilding)
                        visitedWithoutWork++;
                    else
                        visitedWithoutWork = 0;

                    if (visitedWithoutWork >= Math.Max(1, Operations.Count))
                        break;
                }
            }
            finally
            {
                _pumping = false;
            }
        }

    }
}
