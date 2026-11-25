using System.Collections;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// Serialized edits, fault expectations, waits and reports the Space Mode
    /// and fault scenarios share.
    /// </summary>
    internal static class FlowFieldSpaceModeScenarios
    {
        // The scenarios acted about a second after Ready, once the sample
        // controllers had started.
        internal const float StartSeconds = 1f;
        internal const string TestFault = "Test fault.";
        internal const string SpaceModeLocked = "FlowField Space Mode can only change after Release and before Init.";
        internal const string SampleGateName = "West Ramp Gate";

        internal static IEnumerator EnterAndSettle(string scene, FlowFieldBakeMode bakeMode)
        {
            yield return FlowFieldPlayScene.Enter(scene, bakeMode);
            yield return FlowFieldPlayScene.WaitUntilSettled(StartSeconds);
        }

        internal static string Report(FlowFieldManager manager)
            => "state=" + manager.State + " init=" + manager.IsInitialized + " faulted=" + manager.IsFaulted
                + " space=" + manager.SpaceMode + " err=" + manager.LastError;

        internal static int SerializedInt(Object target, string path)
            => new SerializedObject(target).FindProperty(path).intValue;

        /// <summary>An edit as the Inspector makes it: OnValidate runs in Play Mode.</summary>
        internal static void EditInt(Object target, string path, int value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(path).intValue = value;
            serialized.ApplyModifiedProperties();
        }

        internal static int OtherSpaceMode(int spaceMode)
            => spaceMode == (int)FlowFieldSpaceMode.Surface2D ? (int)FlowFieldSpaceMode.Volume3D : (int)FlowFieldSpaceMode.Surface2D;

        internal static FlowFieldSpaceMode OtherSpaceMode(FlowFieldSpaceMode spaceMode)
            => spaceMode == FlowFieldSpaceMode.Surface2D ? FlowFieldSpaceMode.Volume3D : FlowFieldSpaceMode.Surface2D;

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

        /// <summary>Waits until the Manager faults; Ready or a timeout fails.</summary>
        internal static IEnumerator WaitUntilFaulted(FlowFieldManager manager, float timeoutSeconds = 60f)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (!manager.IsFaulted)
            {
                if (manager.State == FlowFieldRuntimeState.Ready)
                    Assert.Fail("Ready where a Fault was expected: " + FlowFieldPlayScene.Describe());
                if (Time.realtimeSinceStartup > deadline)
                    Assert.Fail("Timed out waiting for a Fault: " + FlowFieldPlayScene.Describe());
                yield return null;
            }
        }

        /// <summary>Release, the other Space Mode, Init: the way a script changes the mode.</summary>
        internal static void SwitchSpaceMode(FlowFieldManager manager)
        {
            manager.Release();
            manager.SetSpaceMode(OtherSpaceMode(manager.SpaceMode));
            manager.Init();
        }

        /// <summary>
        /// Release and Init with the same Goal and, when it was registered, the
        /// sample's gate, which Release drops.
        /// </summary>
        internal static void ReleaseAndInit(FlowFieldManager manager, Vector3 goal)
        {
            GameObject gateObject = GameObject.Find(SampleGateName);
            Collider gate = gateObject == null ? null : gateObject.GetComponent<Collider>();
            bool gateRegistered = gate != null && manager.IsDynamicObstacleRegistered(gate);
            manager.Release();
            manager.Init();
            if (gateRegistered)
                manager.RegisterDynamicObstacle(gate);
            manager.SetGoal(FlowFieldGoalRequest.Position(goal, 0f));
        }

        internal static Vector3 At(Bounds bounds, float x, float z)
            => new Vector3(
                Mathf.Lerp(bounds.min.x, bounds.max.x, x),
                bounds.center.y,
                Mathf.Lerp(bounds.min.z, bounds.max.z, z));

        internal static Bounds WorldBounds(FlowFieldManager manager)
        {
            Assert.That(manager.TryGetFieldInfo(out FlowFieldFieldInfo info), Is.True,
                "No field info: " + FlowFieldPlayScene.Describe());
            return info.WorldBounds;
        }
    }
}
