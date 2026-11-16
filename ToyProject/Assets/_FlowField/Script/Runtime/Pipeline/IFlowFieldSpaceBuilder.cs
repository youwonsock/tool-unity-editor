using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// What one space mode does inside the common <see cref="FlowFieldSession"/>:
    /// how it builds a field, reads it, and takes obstacle and Modifier input.
    /// The session owns the lifecycle, the events and the build loop and calls
    /// these members; a builder reaches the session through the reference it
    /// was created with.
    /// </summary>
    internal interface IFlowFieldSpaceBuilder : IDisposable
    {
        FlowFieldSpaceMode SpaceMode { get; }
        bool IsReady { get; }
        bool HasPendingRequest { get; }
        bool IsWaitingForExternalCompletion { get; }

        // ----- Build steps the session's pipeline runs ------------------------
        bool HasActiveBuild { get; }
        bool TryBeginBuild(bool scheduled);
        FlowFieldBuildStep StepBuild(in FlowFieldBuildBudget budget, bool waitForWorker);
        void PublishBuild();
        void BeforePump();
        bool BeginsAfterBudget { get; }
        int MaxBuildsBegunPerPump { get; }
        bool StaysBuildingWhileOwed { get; }
        bool HasOwedInput { get; }

        // ----- Reads ---------------------------------------------------------
        // Never throws; the session turns a failed status into the contract's exception.
        FlowFieldSampleStatus TrySample(Vector3 worldPosition, out FlowFieldSample sample);
        FlowFieldClampResult ClampPositionToGrid(Vector3 worldPosition);
        bool TryGetFieldInfo(out FlowFieldFieldInfo info);
        bool TryGetPublishedField(out FlowFieldField field);

        // ----- Input ----------------------------------------------------------
        bool RegisterModifierNow(IFlowFieldVectorModifier modifier);
        bool UnregisterModifierNow(IFlowFieldVectorModifier modifier);
        void NotifyModifierChangedNow(IFlowFieldVectorModifier modifier, FlowFieldModifierChange change);
        bool RegisterObstacleNow(Collider collider);
        bool UnregisterObstacleNow(Collider collider);
        void NotifyObstacleRegionDirtyCommand(Bounds worldBounds);

        // ----- Lifecycle -----------------------------------------------------
        // Stop: a running session drops its work and its field and waits.
        // Restart: a stopped or faulted session starts again from nothing,
        // keeping its registrations. Release: everything is cleared.
        void Stop();
        void Restart();
        void Release();
        void AbandonWorkForFault();
        void ApplyFaultedState();
    }
}
