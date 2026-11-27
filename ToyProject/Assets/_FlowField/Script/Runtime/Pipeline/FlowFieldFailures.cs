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

        internal static InvalidOperationException InitOutsidePlayMode()
            => new InvalidOperationException(
                $"{nameof(FlowFieldManager)}.Init runs only in Play Mode. In Edit Mode, use Bake and the Scene View preview.");

        internal static InvalidOperationException Faulted(Exception fault)
            => new InvalidOperationException(
                $"{nameof(FlowFieldManager)} is faulted. Call RequestRebuild() to retry.",
                fault);

        /// <summary>No published result answers yet: the first build, after a stop or a new grid.</summary>
        internal static InvalidOperationException NotReady()
            => new InvalidOperationException("FlowField session is not ready.");

        internal static ArgumentOutOfRangeException NotFinite(string paramName)
            => new ArgumentOutOfRangeException(paramName, "Position must be finite.");

        internal static ArgumentOutOfRangeException BoundsNotFinite(string paramName)
            => new ArgumentOutOfRangeException(paramName, "Bounds must be finite.");

        /// <summary>A running or faulted session keeps its Space Mode until Release.</summary>
        internal static InvalidOperationException SpaceModeLocked()
            => new InvalidOperationException("FlowField Space Mode can only change after Release and before Init.");

        internal static InvalidOperationException DestroyedCollider()
            => new InvalidOperationException("Cannot register a destroyed Collider.");

        /// <summary>A world Goal is the world's: its areas keep it, so a Manager cannot move or remove it.</summary>
        internal static InvalidOperationException WorldGoalChange()
            => new InvalidOperationException(
                "A world Goal changes only through its FlowFieldWorld: call FlowFieldWorld.SetGoal or RemoveGoal.");

        internal static InvalidOperationException ModifierNotRegistered()
            => new InvalidOperationException("Modifier is not registered.");

        internal static ArgumentOutOfRangeException OutsideGrid(string paramName)
            => new ArgumentOutOfRangeException(paramName, "Position is outside the FlowField grid.");

        /// <summary><paramref name="change"/> names what the Bake Mode <paramref name="mode"/> does not accept.</summary>
        internal static InvalidOperationException NotAllowedIn(string change, FlowFieldBakeMode mode)
            => new InvalidOperationException($"{change} is not allowed in {mode} mode.");

        /// <summary>The exception Sample throws for a read that produced no sample.</summary>
        internal static Exception ForSample(FlowFieldSampleStatus status, string paramName)
            => status switch
            {
                FlowFieldSampleStatus.NotReady => NotReady(),
                FlowFieldSampleStatus.NonFinite => NotFinite(paramName),
                FlowFieldSampleStatus.OutOfGrid => OutsideGrid(paramName),
                _ => new ArgumentOutOfRangeException(nameof(status)),
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
