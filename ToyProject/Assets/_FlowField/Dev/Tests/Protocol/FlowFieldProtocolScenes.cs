using System.Collections;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// What the build protocol tests share: Inspector-style settings edits,
    /// their Goals, direct pumps that stop a build at a fixed point, and the
    /// fresh build a field is compared with. A direct pump runs inside one
    /// frame, so the runtime scheduler cannot move the build in between.
    /// </summary>
    internal static class FlowFieldProtocolScenes
    {
        internal const string DefaultDirectionPath
            = FlowFieldManager.PropertyNames.Flow + "." + nameof(FlowFieldFlowSettings.DefaultDirection);
        internal const string GoalRadiusPath
            = FlowFieldManager.PropertyNames.Flow + "." + nameof(FlowFieldFlowSettings.GoalInfluenceRadius);
        internal const string CellSizePath = FlowFieldManager.PropertyNames.CellSize;
        internal const string MaxStepHeightPath
            = FlowFieldManager.PropertyNames.Surface + "." + nameof(FlowFieldSurfaceSettings.MaxStepHeight);
        internal const string MaxSlopePath
            = FlowFieldManager.PropertyNames.Surface + "." + nameof(FlowFieldSurfaceSettings.MaxSlope);

        internal const string FaultMessage = "Protocol test fault.";
        // The Manager logs this when its session faults.
        private const string FaultLogPattern = @"^\[FlowField\] Build failed: ";

        internal static readonly Vector3 PlusZ = new Vector3(0f, 0f, 1f);
        internal static readonly Vector3 PlusX = new Vector3(1f, 0f, 0f);
        internal static readonly Vector3 MinusX = new Vector3(-1f, 0f, 0f);

        // The Default Direction scenarios, recorded by the earlier local kit on
        // both RuntimeDynamic and BakedBase: D1 = +Z everywhere outside the
        // Goal's reach, and D2 = +X where the Goal's path happens to run.
        internal const string ControlDirectionCounts = "base D1=8383 D2=439 final D2=439";
        internal const string KeptDirectionCounts = "base D1=346 D2=8604 final D2=8604";

        // ----- Settings edits ------------------------------------------------
        // ApplyModifiedProperties runs OnValidate, which in Play Mode submits
        // the edit as the Inspector would.

        internal static void SetFloat(FlowFieldManager manager, string path, float value)
        {
            var serialized = new SerializedObject(manager);
            SerializedProperty property = serialized.FindProperty(path);
            Assert.That(property, Is.Not.Null, "No serialized property " + path);
            property.floatValue = value;
            serialized.ApplyModifiedProperties();
        }

        internal static void SetVector(FlowFieldManager manager, string path, Vector3 value)
        {
            var serialized = new SerializedObject(manager);
            SerializedProperty property = serialized.FindProperty(path);
            Assert.That(property, Is.Not.Null, "No serialized property " + path);
            property.vector3Value = value;
            serialized.ApplyModifiedProperties();
        }

        internal static Vector3 GetVector(FlowFieldManager manager, string path)
        {
            SerializedProperty property = new SerializedObject(manager).FindProperty(path);
            Assert.That(property, Is.Not.Null, "No serialized property " + path);
            return property.vector3Value;
        }

        // ----- Goals ---------------------------------------------------------

        /// <summary>
        /// The Goal the kit's Default Direction scenarios solved for: open
        /// ground of the Surface2D sample scene, away from the sample's own Goal.
        /// </summary>
        internal static Vector3 SurfaceSolveGoal(FlowFieldManager manager)
        {
            Bounds bounds = WorldBounds(manager);
            return new Vector3(bounds.center.x + 7f, bounds.center.y, bounds.center.z - 9f);
        }

        /// <summary>
        /// A Volume3D Goal far from the requested one, so SetGoal never counts
        /// as a move within the Goal's cell.
        /// </summary>
        internal static Vector3 VolumeGoal(FlowFieldManager manager)
        {
            Bounds bounds = WorldBounds(manager);
            Vector3 first = Lerp(bounds, 0.3f, 0.7f);
            Vector3 second = Lerp(bounds, 0.7f, 0.3f);
            Assert.That(manager.TryGetFieldInfo(out FlowFieldFieldInfo info), Is.True, FlowFieldPlayScene.Describe());
            if (!info.HasRequestedGoal)
                return first;
            return Vector3.Distance(first, info.RequestedGoalWorld) >= Vector3.Distance(second, info.RequestedGoalWorld)
                ? first
                : second;
        }

        /// <summary>A region notice around a point: it changes no Collider, so the field stays the same.</summary>
        internal static Bounds RegionAround(Vector3 center) => new Bounds(center, new Vector3(4f, 4f, 4f));

        private static Bounds WorldBounds(FlowFieldManager manager)
        {
            Assert.That(manager.TryGetFieldInfo(out FlowFieldFieldInfo info), Is.True, FlowFieldPlayScene.Describe());
            return info.WorldBounds;
        }

        private static Vector3 Lerp(Bounds bounds, float x, float z)
            => new Vector3(
                Mathf.Lerp(bounds.min.x, bounds.max.x, x),
                bounds.center.y,
                Mathf.Lerp(bounds.min.z, bounds.max.z, z));

        // ----- Fixed pump points ---------------------------------------------

        /// <summary>
        /// One direct pump of a session with a request: a Surface2D build
        /// begins whatever the budget says and, with an active Goal, starts its
        /// worker-thread solve and returns without waiting for it.
        /// </summary>
        internal static void BeginSurfaceSolve(FlowFieldSession session)
        {
            session.Pump(FlowFieldTestBudget.AfterChecks(0), waitForWorker: false);
            Assert.That(session.IsWaitingForExternalCompletion, Is.True,
                "The Surface2D build is not waiting for its solve: " + Describe(session));
        }

        /// <summary>
        /// Waits for the running solve and composes one piece of the field:
        /// the build is composing, nothing is published yet.
        /// </summary>
        internal static void FinishSolveAndComposeOnePiece(FlowFieldSession session)
        {
            session.Pump(FlowFieldTestBudget.AfterChecks(0), waitForWorker: true);
            Assert.That(IsComposing(session), Is.True,
                "The Surface2D build is not composing after its solve: " + Describe(session));
        }

        /// <summary>
        /// Between pumps a Surface2D build is either waiting for its solve or
        /// composing: begin, publish and reject all finish inside one pump.
        /// </summary>
        internal static bool IsComposing(FlowFieldSession session)
            => session.HasActiveBuild && !session.IsWaitingForExternalCompletion;

        /// <summary>
        /// One direct pump that begins a Volume3D build and takes exactly one
        /// step of it: the first check lets it begin, the second its first step.
        /// </summary>
        internal static void BeginVolumeBuild(FlowFieldSession session)
        {
            Assert.That(session.HasPendingRequest, Is.True, "Nothing to build: " + Describe(session));
            session.Pump(FlowFieldTestBudget.AfterChecks(2), waitForWorker: false);
            Assert.That(session.HasActiveBuild, Is.True, "The Volume3D build did not begin: " + Describe(session));
        }

        // ----- Comparison ----------------------------------------------------

        /// <summary>Release, Init and the given Goal, then the build settles: the field to compare with.</summary>
        internal static IEnumerator SettleFresh(FlowFieldManager manager, FlowFieldGoalRequest goal)
        {
            // Release forgets the obstacles a script registered itself (the
            // sample registers its gate), so the fresh build registers them
            // again, as that script would after an Init.
            var registered = new System.Collections.Generic.List<Collider>();
            FlowFieldObstacleRegistry obstacles = manager.Session.Obstacles;
            for (int i = 0; i < obstacles.Count; i++)
            {
                Collider collider = obstacles.GetCollider(i);
                if (collider != null)
                    registered.Add(collider);
            }
            manager.Release();
            manager.Init();
            foreach (Collider collider in registered)
                manager.RegisterDynamicObstacle(collider);
            manager.SetGoal(goal);
            yield return FlowFieldPlayScene.WaitUntilSettled(2f);
        }

        /// <summary>
        /// How many published base and final directions are D1 (+Z) or D2 (+X),
        /// as the kit's Default Direction scenarios counted them.
        /// </summary>
        internal static string DirectionCounts(FlowFieldManager manager)
        {
            Assert.That(manager.TryGetPublishedField(out FlowFieldField field), Is.True, FlowFieldPlayScene.Describe());
            int baseD1 = 0, baseD2 = 0, finalD2 = 0;
            foreach (Vector3 direction in field.Goal.BaseDirections)
            {
                if (Vector3.Distance(direction, PlusZ) < 1e-3f)
                    baseD1++;
                if (Vector3.Distance(direction, PlusX) < 1e-3f)
                    baseD2++;
            }
            foreach (Vector3 direction in field.Goal.FinalDirections)
            {
                if (Vector3.Distance(direction, PlusX) < 1e-3f)
                    finalD2++;
            }
            return "base D1=" + baseD1 + " D2=" + baseD2 + " final D2=" + finalD2;
        }

        /// <summary>The error and the FlowField line a deliberate fault logs.</summary>
        internal static void ExpectFault(FlowFieldTestLog log)
        {
            log.Expect(FaultLogPattern);
            LogAssert.Expect(LogType.Error, new Regex(FaultLogPattern));
        }

        internal static string Describe(FlowFieldSession session)
            => "state=" + session.State
                + " rev=" + session.Revision
                + " active=" + session.HasActiveBuild
                + " waiting=" + session.IsWaitingForExternalCompletion
                + " pending=" + session.HasPendingRequest
                + " owed=" + session.HasOwedInput
                + " uncertain=" + session.Safety.UncertainRegionCount
                + " err=" + session.LastError;
    }
}
