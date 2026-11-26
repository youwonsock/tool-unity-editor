using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// The objects, inputs and readings the Modifier scenarios share. Objects
    /// made here in Play Mode go when Play Mode ends.
    /// </summary>
    internal static class FlowFieldModifierScenarios
    {
        // The scenarios acted about a second after Ready, once the sample
        // controllers had started.
        internal const float StartSeconds = 1f;
        // How long a scenario looks for a build to start after an input.
        internal const int BuildStartFrames = 900;
        // A budget this small spreads a build over many frames.
        internal const double TinyBudgetMilliseconds = 0.01d;
        internal const double DefaultBudgetMilliseconds = 2d;

        internal static Bounds WorldBounds(FlowFieldManager manager)
        {
            Assert.That(manager.TryGetFieldInfo(out FlowFieldFieldInfo info), Is.True,
                "No field info: " + FlowFieldPlayScene.Describe());
            return info.WorldBounds;
        }

        /// <summary>A point at the bounds' centre height, the given fractions across X and Z.</summary>
        internal static Vector3 At(Bounds bounds, float x, float z)
            => new Vector3(
                Mathf.Lerp(bounds.min.x, bounds.max.x, x),
                bounds.center.y,
                Mathf.Lerp(bounds.min.z, bounds.max.z, z));

        internal static void SetGoal(FlowFieldManager manager, Vector3 position)
            => manager.SetGoal(FlowFieldGoalRequest.Position(position, 0f));

        /// <summary>
        /// A custom Modifier with a trigger box, not registered yet. It never
        /// unregisters itself, so destroying it leaves it registered.
        /// </summary>
        internal static FlowFieldProbeModifier CreateProbe(string name, Vector3 position, Vector3 size)
        {
            var probe = new GameObject(name);
            probe.transform.position = position;
            var box = probe.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = size;
            var modifier = probe.AddComponent<FlowFieldProbeModifier>();
            modifier.Influence = box;
            Physics.SyncTransforms();
            return modifier;
        }

        /// <summary>
        /// A Speed Modifier Volume that registers itself with the Manager when
        /// it is activated, as one made in the Inspector does.
        /// </summary>
        internal static FlowFieldSpeedModifier CreateSpeedModifier(
            FlowFieldManager manager,
            string name,
            Vector3 position,
            Vector3 size,
            int? priority = null,
            float? speedMultiplier = null)
        {
            var probe = new GameObject(name);
            probe.SetActive(false);
            probe.transform.position = position;
            var box = probe.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = size;
            var modifier = probe.AddComponent<FlowFieldSpeedModifier>();
            var serialized = new SerializedObject(modifier);
            serialized.FindProperty("_flowFieldManager").objectReferenceValue = manager;
            serialized.FindProperty("_influenceCollider").objectReferenceValue = box;
            if (priority.HasValue)
                serialized.FindProperty("_priority").intValue = priority.Value;
            if (speedMultiplier.HasValue)
                serialized.FindProperty("_speedMultiplier").floatValue = speedMultiplier.Value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            probe.SetActive(true);
            return modifier;
        }

        /// <summary>Cells a Speed Modifier made faster: final speed above 1.</summary>
        internal static int FasterCells(FlowFieldManager manager)
        {
            Assert.That(manager.TryGetPublishedField(out FlowFieldField field), Is.True,
                "No published field: " + FlowFieldPlayScene.Describe());
            int count = 0;
            for (int i = 0; i < field.Goal.FinalSpeeds.Length; i++)
            {
                if (field.Goal.FinalSpeeds[i] > 1.01f)
                    count++;
            }
            return count;
        }

        /// <summary>Cells the probe Modifier slowed: final speed well below the base speed.</summary>
        internal static int SlowedCells(FlowFieldManager manager)
        {
            Assert.That(manager.TryGetPublishedField(out FlowFieldField field), Is.True,
                "No published field: " + FlowFieldPlayScene.Describe());
            int count = 0;
            for (int i = 0; i < field.Goal.FinalSpeeds.Length; i++)
            {
                if (field.Goal.BaseSpeeds[i] > 0f && field.Goal.FinalSpeeds[i] < field.Goal.BaseSpeeds[i] * 0.75f)
                    count++;
            }
            return count;
        }

        /// <summary>Surface2D cells a Modifier touched.</summary>
        internal static int InfluencedCells(FlowFieldManager manager)
        {
            Assert.That(manager.TryGetPublishedField(out FlowFieldField field), Is.True,
                "No published field: " + FlowFieldPlayScene.Describe());
            Assert.That(field.Goal.ModifierInfluence, Is.Not.Null, "Only Surface2D keeps Modifier influence.");
            int count = 0;
            for (int i = 0; i < field.Goal.ModifierInfluence.Length; i++)
            {
                if (field.Goal.ModifierInfluence[i])
                    count++;
            }
            return count;
        }

        internal static int RegisteredCount(FlowFieldManager manager) => manager.Session.Modifiers.Count;

        /// <summary>
        /// The registered Modifiers whose object name starts with
        /// <paramref name="prefix"/>, in composition order, each as the rest of
        /// its name and its Priority ("A:3001").
        /// </summary>
        internal static List<string> Entries(FlowFieldManager manager, string prefix)
        {
            var entries = new List<string>();
            foreach (FlowFieldModifierEntry entry in manager.Session.Modifiers.Entries)
            {
                if (entry.Modifier is Component component && component != null && component.name.StartsWith(prefix))
                    entries.Add(component.name.Substring(prefix.Length) + ":" + entry.Priority);
            }
            return entries;
        }

        /// <summary>A build of the session is running, in a frame after <paramref name="inputFrame"/>.</summary>
        internal static bool BuildRunsAfter(FlowFieldManager manager, int inputFrame)
            => Time.frameCount > inputFrame && manager.Session.HasActiveBuild;

        internal static void FailIfPast(int deadlineFrame, string what)
        {
            if (Time.frameCount > deadlineFrame)
                Assert.Fail(what + ": " + FlowFieldPlayScene.Describe());
        }

        /// <summary>
        /// The error the Manager logs when its session faults, for both the
        /// test log and the Test Framework. Call it before the action that faults.
        /// </summary>
        internal static void ExpectBuildFailed(FlowFieldTestLog log, string lastError)
        {
            string message = "[FlowField] Build failed: " + lastError;
            log.Expect("^" + Regex.Escape(message) + "$");
            LogAssert.Expect(LogType.Error, message);
        }

        internal static string State(FlowFieldManager manager)
            => "state=" + manager.State + " faulted=" + manager.IsFaulted + " err=" + manager.LastError;
    }
}
