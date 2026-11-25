using System.Collections;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// Shared steps of the Goal and GoalTarget tests: targets set up as the
    /// Inspector would, Inspector-style radius edits, Goal checks on the field
    /// info, and waits that let LateUpdate report before the build settles.
    /// </summary>
    internal static class FlowFieldGoalTestSupport
    {
        internal const string RadiusPath = FlowFieldManager.PropertyNames.Flow + "." + nameof(FlowFieldFlowSettings.GoalInfluenceRadius);

        private const string TargetManagerPath = "_manager";
        private const string TargetRadiusPath = "_influenceRadius";
        private const float GoalTolerance = 0.001f;
        private const float PlayerLoopTimeoutSeconds = 30f;

        internal static FlowFieldFieldInfo Info(FlowFieldManager manager)
        {
            Assert.That(manager.TryGetFieldInfo(out FlowFieldFieldInfo info), Is.True, "No field info: " + FlowFieldPlayScene.Describe());
            return info;
        }

        internal static Bounds WorldBounds(FlowFieldManager manager) => Info(manager).WorldBounds;

        /// <summary>A point at fractions of the bounds' X and Z, at the bounds' center height.</summary>
        internal static Vector3 Point(Bounds bounds, float x, float z) => Point(bounds, x, bounds.center.y, z);

        internal static Vector3 Point(Bounds bounds, float x, float y, float z)
            => new Vector3(
                Mathf.Lerp(bounds.min.x, bounds.max.x, x),
                y,
                Mathf.Lerp(bounds.min.z, bounds.max.z, z));

        internal static string DescribeGoal(FlowFieldManager manager)
        {
            bool hasInfo = manager.TryGetFieldInfo(out FlowFieldFieldInfo info);
            return "state=" + manager.State + " info=" + hasInfo + " hasGoal=" + info.HasRequestedGoal
                + " goal=" + info.RequestedGoalWorld.ToString("F2") + " radius=" + info.GoalInfluenceRadius
                + " err=" + manager.LastError;
        }

        /// <summary>The latest request has a Goal at this position.</summary>
        internal static void AssertGoal(FlowFieldManager manager, Vector3 expected, string what)
        {
            FlowFieldFieldInfo info = Info(manager);
            bool atExpected = info.HasRequestedGoal
                && Vector3.Distance(info.RequestedGoalWorld, expected) <= GoalTolerance;
            Assert.That(atExpected, Is.True,
                what + ": expected the Goal at " + expected.ToString("F2") + ", " + DescribeGoal(manager));
        }

        /// <summary>The latest request has no Goal.</summary>
        internal static void AssertNoGoal(FlowFieldManager manager, string what)
        {
            FlowFieldFieldInfo info = Info(manager);
            Assert.That(info.HasRequestedGoal, Is.False, what + ": expected no Goal, " + DescribeGoal(manager));
        }

        /// <summary>A new GoalTarget at the position, pointed at the Manager.</summary>
        internal static FlowFieldGoalTarget AddTarget(string name, Vector3 position, FlowFieldManager manager, float radius = 0f)
        {
            var gameObject = new GameObject(name);
            gameObject.transform.position = position;
            FlowFieldGoalTarget target = gameObject.AddComponent<FlowFieldGoalTarget>();
            var serialized = new SerializedObject(target);
            serialized.FindProperty(TargetManagerPath).objectReferenceValue = manager;
            serialized.FindProperty(TargetRadiusPath).floatValue = radius;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return target;
        }

        /// <summary>Changes the target's Manager field, or clears it with null.</summary>
        internal static void SetTargetManager(FlowFieldGoalTarget target, FlowFieldManager manager)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(TargetManagerPath).objectReferenceValue = manager;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>An Inspector edit of the target's Influence Radius.</summary>
        internal static void SetTargetRadius(FlowFieldGoalTarget target, float radius)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(TargetRadiusPath).floatValue = radius;
            serialized.ApplyModifiedProperties();
        }

        internal static float SerializedRadius(FlowFieldManager manager)
            => new SerializedObject(manager).FindProperty(RadiusPath).floatValue;

        /// <summary>An Inspector edit of the Manager's Goal Influence Radius; OnValidate runs at once.</summary>
        internal static void SetSerializedRadius(FlowFieldManager manager, float radius)
        {
            var serialized = new SerializedObject(manager);
            serialized.FindProperty(RadiusPath).floatValue = radius;
            serialized.ApplyModifiedProperties();
        }

        internal static Transform GoalTransform(FlowFieldManager manager)
            => new SerializedObject(manager).FindProperty(FlowFieldManager.PropertyNames.GoalTransform).objectReferenceValue as Transform;

        /// <summary>Waits until the player loop ran this many more frames, so every LateUpdate reported.</summary>
        internal static IEnumerator WaitPlayerFrames(int frames)
        {
            int target = Time.frameCount + frames;
            float deadline = Time.realtimeSinceStartup + PlayerLoopTimeoutSeconds;
            while (Time.frameCount < target)
            {
                if (Time.realtimeSinceStartup > deadline)
                    Assert.Fail("The player loop stopped: " + FlowFieldPlayScene.Describe());
                yield return null;
            }
        }

        /// <summary>The wait between two steps: the targets report, then the build is Ready.</summary>
        internal static IEnumerator Step()
        {
            yield return WaitPlayerFrames(3);
            yield return FlowFieldPlayScene.WaitUntilReady();
        }

        /// <summary>The error the Manager logs when its session faults with this reason.</summary>
        internal static void ExpectBuildFailed(FlowFieldTestLog log, string reason)
        {
            string pattern = "^" + Regex.Escape("[FlowField] Build failed: " + reason) + "$";
            LogAssert.Expect(LogType.Error, new Regex(pattern));
            log.Expect(pattern);
        }
    }
}
