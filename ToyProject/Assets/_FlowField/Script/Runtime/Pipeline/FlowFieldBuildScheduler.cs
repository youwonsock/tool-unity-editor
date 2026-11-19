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
        private static readonly List<FlowFieldSession> Sessions
            = new List<FlowFieldSession>(8);
        private static int _cursor;
        private static bool _pumping;
        // PumpAll is non-reentrant, so one timer and one cached, non-capturing
        // delegate serve every frame without allocating.
        private static readonly Stopwatch Timer = new Stopwatch();
        private static readonly Func<double> ElapsedMilliseconds = () => Timer.Elapsed.TotalMilliseconds;

        // The longest main-thread pass since the last read, for the Player probe.
        internal static double LongestPumpMilliseconds { get; private set; }

        internal static void ResetLongestPump() => LongestPumpMilliseconds = 0d;

        /// <summary>
        /// Clears the round-robin position and the probe reading at the start of
        /// a Play session. With domain reload disabled, static fields otherwise
        /// carry over from the previous one. Registered operations stay: each
        /// session tracks its own registration and unregisters when it stops.
        /// </summary>
        internal static void ResetForNewPlaySession()
        {
            _cursor = 0;
            _pumping = false;
            LongestPumpMilliseconds = 0d;
        }

        internal static void Register(FlowFieldSession session)
        {
            if (session == null || Sessions.Contains(session))
                return;
            Sessions.Add(session);
            if (_cursor >= Sessions.Count)
                _cursor = 0;
        }

        internal static void Unregister(FlowFieldSession session)
        {
            if (session == null)
                return;
            int index = Sessions.IndexOf(session);
            if (index < 0)
                return;
            Sessions.RemoveAt(index);
            if (Sessions.Count == 0)
            {
                _cursor = 0;
                return;
            }
            if (index < _cursor)
                _cursor--;
            _cursor %= Sessions.Count;
        }

        internal static void PumpAll(double budgetMilliseconds)
        {
            if (_pumping || Sessions.Count == 0 || budgetMilliseconds <= 0d)
                return;

            _pumping = true;
            Timer.Restart();
            try
            {
                int visitedWithoutWork = 0;
                while (Sessions.Count > 0
                    && Timer.Elapsed.TotalMilliseconds < budgetMilliseconds)
                {
                    if (_cursor >= Sessions.Count)
                        _cursor = 0;
                    FlowFieldSession session = Sessions[_cursor];
                    _cursor = (_cursor + 1) % Math.Max(1, Sessions.Count);

                    if (session == null)
                    {
                        visitedWithoutWork++;
                        if (visitedWithoutWork >= Math.Max(1, Sessions.Count))
                            break;
                        continue;
                    }

                    FlowFieldBuildBudget budget = new FlowFieldBuildBudget(
                        budgetMilliseconds,
                        ElapsedMilliseconds);
                    // A waiting session only observes its inputs, which is
                    // no reason to give it another turn within this budget.
                    bool wasWaiting = session.IsWaitingForExternalCompletion;
                    bool wasBuilding = session.IsBuilding;
                    session.PumpInternal(in budget);
                    bool isBuilding = session.IsBuilding;
                    if (wasWaiting || !wasBuilding || !isBuilding)
                        visitedWithoutWork++;
                    else
                        visitedWithoutWork = 0;

                    if (visitedWithoutWork >= Math.Max(1, Sessions.Count))
                        break;
                }
            }
            finally
            {
                _pumping = false;
                LongestPumpMilliseconds = Math.Max(LongestPumpMilliseconds, Timer.Elapsed.TotalMilliseconds);
            }
        }

    }
}
