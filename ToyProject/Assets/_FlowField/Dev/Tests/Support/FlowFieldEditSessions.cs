using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// Sessions a test creates and pumps itself in Edit Mode, from a Manager's
    /// captured settings: Init's order, the settings with another Goal, runs
    /// that finish every owed build, and the published field's hash.
    /// </summary>
    internal static class FlowFieldEditSessions
    {
        // Pumps that finish the owed builds; Surface2D begins one per pump.
        private const int MaxSettlePumps = 64;

        internal static FlowFieldSession Create(FlowFieldSpaceMode spaceMode)
            => spaceMode == FlowFieldSpaceMode.Volume3D ? FlowFieldSession.CreateVolume() : FlowFieldSession.CreateSurface();

        /// <summary>Init's order: the bake mode, then the whole settings as a rebuild.</summary>
        internal static void Start(FlowFieldSession session, FlowFieldBakeMode bakeMode, in FlowFieldManagerConfig config)
        {
            session.Initialize(bakeMode);
            session.SubmitConfig(in config, FlowFieldConfigChange.Rebuild);
        }

        /// <summary>The settings with the default Goal at the position, or with none.</summary>
        internal static FlowFieldManagerConfig WithGoal(FlowFieldManager manager, in FlowFieldManagerConfig settings, Vector3? goal)
            => new FlowFieldManagerConfig(
                settings.SpaceMode,
                settings.BakeMode,
                in settings.Grid,
                in settings.Surface,
                in settings.Obstacles,
                in settings.Flow,
                in settings.BakeAssets,
                manager.transform,
                goal.HasValue,
                goal ?? default);

        internal static bool IsIdle(FlowFieldSession session)
            => !session.HasActiveBuild && !session.HasOwedInput && !session.HasPendingRequest;

        /// <summary>Runs every owed build to its end, waiting for the worker.</summary>
        internal static void Settle(FlowFieldSession session, string where)
        {
            for (int pump = 0; pump < MaxSettlePumps && !IsIdle(session); pump++)
                session.Pump();
            Assert.That(session.IsFaulted, Is.False, where + " faulted: " + session.LastError);
            Assert.That(IsIdle(session), Is.True, where + " never settled.");
            Assert.That(session.IsReady, Is.True, where + " has no field.");
        }

        internal static FlowFieldField Published(FlowFieldSession session)
        {
            Assert.That(session.TryGetPublishedField(out FlowFieldField field), Is.True);
            return field;
        }

        internal static string Hash(FlowFieldField field)
            => FlowFieldFieldHash.Format(FlowFieldFieldHash.Compute(field));

        /// <summary>
        /// A settled session with the default Goal at the position, or none,
        /// and the given obstacles and Modifiers registered.
        /// </summary>
        internal static FlowFieldSession CreateSettled(
            FlowFieldManager manager,
            in FlowFieldManagerConfig settings,
            Vector3? goal,
            IReadOnlyList<Collider> obstacles = null,
            IReadOnlyList<IFlowFieldVectorModifier> modifiers = null)
        {
            FlowFieldSession session = Create(settings.SpaceMode);
            Start(session, settings.BakeMode, WithGoal(manager, settings, goal));
            for (int i = 0; obstacles != null && i < obstacles.Count; i++)
                Assert.That(session.RegisterObstacleCommand(obstacles[i]), Is.True);
            for (int i = 0; modifiers != null && i < modifiers.Count; i++)
                Assert.That(session.RegisterModifierCommand(modifiers[i]), Is.True);
            Settle(session, "a fresh session");
            return session;
        }
    }
}
