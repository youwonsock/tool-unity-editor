using System;

namespace Common.FlowField
{
    /// <summary>
    /// The exceptions of the FlowField contract, the same in both space modes.
    /// The Manager and the session build every failure a caller can see here.
    /// </summary>
    internal static class FlowFieldFailures
    {
        internal static InvalidOperationException NotInitialized()
            => new InvalidOperationException($"{nameof(FlowFieldManager)} is not initialized.");

        internal static InvalidOperationException Faulted(Exception fault)
            => new InvalidOperationException(
                $"{nameof(FlowFieldManager)} is faulted. Call RequestRebuild() to retry.",
                fault);

        /// <summary>No published result answers yet: the first build, after a stop or a new grid.</summary>
        internal static InvalidOperationException NotReady()
            => new InvalidOperationException("FlowField session is not ready.");

        internal static ArgumentOutOfRangeException NotFinite(string paramName)
            => new ArgumentOutOfRangeException(paramName, "Position must be finite.");

        internal static ArgumentOutOfRangeException OutsideGrid(string paramName)
            => new ArgumentOutOfRangeException(paramName, "Position is outside the FlowField grid.");

        /// <summary><paramref name="change"/> names what StaticBaked mode does not accept.</summary>
        internal static InvalidOperationException NotInStaticBaked(string change)
            => new InvalidOperationException($"{change} is not allowed in StaticBaked mode.");

        /// <summary>The exception Sample throws for a read that produced no sample.</summary>
        internal static Exception ForSample(FlowFieldSampleStatus status, string paramName)
            => status switch
            {
                FlowFieldSampleStatus.NotReady => NotReady(),
                FlowFieldSampleStatus.NonFinite => NotFinite(paramName),
                FlowFieldSampleStatus.OutOfGrid => OutsideGrid(paramName),
                _ => new InvalidOperationException("FlowField sampling data is inconsistent."),
            };

        /// <summary>The exception ClampPositionToGrid throws for a clamp that produced no position.</summary>
        internal static Exception ForClamp(FlowFieldClampStatus status, string paramName)
            => status switch
            {
                FlowFieldClampStatus.NotInitialized => NotInitialized(),
                FlowFieldClampStatus.NonFinite => NotFinite(paramName),
                _ => NotReady(),
            };
    }
}
