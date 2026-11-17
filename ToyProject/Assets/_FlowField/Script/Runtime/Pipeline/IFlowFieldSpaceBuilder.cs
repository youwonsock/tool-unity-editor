using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>What changed in the Manager's configuration, so a builder knows what to rebuild.</summary>
    internal enum FlowFieldConfigChange
    {
        // Init, RequestRebuild or a restart: build everything from these settings.
        Rebuild,
        // A serialized setting was edited: rebuild what differs from the last request.
        Settings,
        // SetGoal changed the Goal.
        Goal,
        // NotifyCellsDirty: regenerate every cell.
        Cells,
    }

    /// <summary>How a mode's builds may begin within one pump.</summary>
    internal readonly struct FlowFieldBuildPolicy
    {
        // A build may begin after the frame budget ran out (Surface2D takes its
        // Physics snapshot in one frame whatever the budget says).
        internal readonly bool BeginsAfterBudget;
        internal readonly int MaxBuildsBegunPerPump;

        internal FlowFieldBuildPolicy(bool beginsAfterBudget, int maxBuildsBegunPerPump)
        {
            BeginsAfterBudget = beginsAfterBudget;
            MaxBuildsBegunPerPump = maxBuildsBegunPerPump;
        }
    }

    /// <summary>
    /// What one space mode does inside the common <see cref="FlowFieldSession"/>:
    /// how it builds a field, reads it, and takes obstacle and Modifier input.
    /// The session owns the lifecycle, the events and the build loop and calls
    /// these members; a builder reaches the session through the reference it
    /// was created with.
    /// </summary>
    internal interface IFlowFieldSpaceBuilder : IFlowFieldSampleSafety, IDisposable
    {
        FlowFieldSpaceMode SpaceMode { get; }
        bool HasPendingRequest { get; }
        bool IsWaitingForExternalCompletion { get; }

        // ----- Build steps the session's pipeline runs ------------------------
        bool HasActiveBuild { get; }
        bool TryBeginBuild(bool scheduled);
        FlowFieldBuildStep StepBuild(in FlowFieldBuildBudget budget, bool waitForWorker);
        void PublishBuild();
        FlowFieldBuildPolicy BuildPolicy { get; }
        // Input that arrived during a build is built next; the session stays
        // Building until nothing is owed.
        bool HasOwedInput { get; }

        // ----- Reads ---------------------------------------------------------
        // The field reads answer from, or null while none does (no build yet,
        // stopped, or a newer grid or source replaced it). The session reads,
        // samples and clamps through it; TryOverride adds the mode's path checks.
        FlowFieldField PublishedField { get; }
        // The grid ClampPositionToGrid uses, valid once a request exists.
        FlowFieldGridSpace LatestGrid { get; }
        bool TryGetFieldInfo(out FlowFieldFieldInfo info);

        // ----- Configuration -------------------------------------------------
        // The builder turns the Manager's settings into its own request; every
        // change arrives as a notification, never by polling the scene.
        void Initialize(FlowFieldBakeMode bakeMode);
        void SubmitConfig(in FlowFieldManagerConfig config, FlowFieldConfigChange change);

        // ----- Input ----------------------------------------------------------
        // The session keeps the Modifiers; the builder keeps what each touches
        // (Surface2D cell masks) and composes again after a change.
        IFlowFieldModifierArea CreateModifierArea();
        void ApplyModifierChange(in FlowFieldModifierUpdate update);
        // The session keeps the registered obstacles and marks the regions a
        // change touched as uncertain; the builder only rebuilds them.
        void ApplyObstacleChange(in FlowFieldObstacleChange change);

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
