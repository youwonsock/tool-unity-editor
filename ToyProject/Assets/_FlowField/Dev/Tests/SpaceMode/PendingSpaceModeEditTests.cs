using System;
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// While a Space Mode edit waits for Release, the session keeps its mode
    /// and takes every other input in it. SMDINS: an invalid Inspector edit
    /// faults at once instead of being dropped, SetGoal then refuses as for
    /// any Fault, and the retry refuses the waiting edit until both edits are
    /// undone. B3 (e9rr): NotifyCellsDirty, SetGoal and RequestRebuild are all
    /// accepted in the session's mode.
    /// </summary>
    [TestFixture, Category("SpaceMode")]
    internal sealed class PendingSpaceModeEditTests : FlowFieldPlayTestBase
    {
        private const string CellSizeInvalid = "Cell Size must be finite and positive.";
        private const string FaultedRefusal = "FlowFieldManager is faulted. Call RequestRebuild() to retry.";

        [UnityTest, Timeout(300000)]
        public IEnumerator InvalidInspectorEditFaults()
        {
            yield return FlowFieldSpaceModeScenarios.EnterAndSettle(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);
            FlowFieldManager manager = Manager;
            Bounds bounds = FlowFieldSpaceModeScenarios.WorldBounds(manager);
            int oldSpace = FlowFieldSpaceModeScenarios.SerializedInt(manager, FlowFieldManager.PropertyNames.SpaceMode);
            float oldCellSize = new SerializedObject(manager).FindProperty(FlowFieldManager.PropertyNames.CellSize).floatValue;

            FlowFieldSpaceModeScenarios.EditInt(manager, FlowFieldManager.PropertyNames.SpaceMode, FlowFieldSpaceModeScenarios.OtherSpaceMode(oldSpace));
            Assert.That(Report(manager), Is.EqualTo("state=Ready faulted=False space=Surface2D err="));
            int revision = manager.Revision;
            yield return FlowFieldPlayScene.WaitSeconds(0.5f);

            const string faulted = "state=Faulted faulted=True space=Surface2D err=" + CellSizeInvalid;
            FlowFieldSpaceModeScenarios.ExpectBuildFailed(Log, CellSizeInvalid);
            var serialized = new SerializedObject(manager);
            serialized.FindProperty(FlowFieldManager.PropertyNames.CellSize).floatValue = 0f;
            serialized.ApplyModifiedProperties();
            Assert.That(Report(manager), Is.EqualTo(faulted), "The Cell Size edit was not checked while the Space Mode edit waits.");
            yield return FlowFieldPlayScene.WaitSeconds(1.5f);
            Assert.That(Report(manager), Is.EqualTo(faulted));

            var goal = FlowFieldGoalRequest.Position(FlowFieldSpaceModeScenarios.At(bounds, 0.3f, 0.6f), 0f);
            var setGoal = Assert.Throws<InvalidOperationException>(() => manager.SetGoal(goal));
            Assert.That(setGoal.Message, Is.EqualTo(FaultedRefusal));
            Assert.That(Report(manager), Is.EqualTo(faulted));
            yield return FlowFieldPlayScene.WaitSeconds(1.5f);
            Assert.That(Report(manager), Is.EqualTo(faulted));

            var retry = Assert.Throws<InvalidOperationException>(() => manager.RequestRebuild());
            Assert.That(retry.Message, Is.EqualTo(FlowFieldSpaceModeScenarios.SpaceModeLocked));
            Assert.That(Report(manager), Is.EqualTo(faulted));

            serialized = new SerializedObject(manager);
            serialized.FindProperty(FlowFieldManager.PropertyNames.SpaceMode).intValue = oldSpace;
            serialized.FindProperty(FlowFieldManager.PropertyNames.CellSize).floatValue = oldCellSize;
            serialized.ApplyModifiedProperties();
            Assert.DoesNotThrow(() => manager.RequestRebuild());
            Assert.That(Report(manager), Is.EqualTo("state=Building faulted=False space=Surface2D err="));
            yield return FlowFieldPlayScene.WaitUntilSettled(1f);
            Assert.That(Report(manager), Is.EqualTo("state=Ready faulted=False space=Surface2D err="));
            Assert.That(manager.Revision, Is.GreaterThan(revision));
        }

        [UnityTest, Timeout(300000)]
        public IEnumerator OtherInputsAccepted()
        {
            yield return FlowFieldSpaceModeScenarios.EnterAndSettle(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);
            FlowFieldManager manager = Manager;
            Bounds bounds = FlowFieldSpaceModeScenarios.WorldBounds(manager);
            int oldSpace = FlowFieldSpaceModeScenarios.SerializedInt(manager, FlowFieldManager.PropertyNames.SpaceMode);
            int editedSpace = FlowFieldSpaceModeScenarios.OtherSpaceMode(oldSpace);

            FlowFieldSpaceModeScenarios.EditInt(manager, FlowFieldManager.PropertyNames.SpaceMode, editedSpace);
            Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Ready), "The waiting edit started a build.");
            AssertSessionMode(manager, editedSpace);
            yield return Settle();

            Assert.DoesNotThrow(() => manager.NotifyCellsDirty());
            Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Building));
            AssertSessionMode(manager, editedSpace);
            yield return Settle();

            var goal = FlowFieldGoalRequest.Position(FlowFieldSpaceModeScenarios.At(bounds, 0.3f, 0.6f), 0f);
            Assert.DoesNotThrow(() => manager.SetGoal(goal));
            Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Building));
            AssertSessionMode(manager, editedSpace);
            yield return Settle();

            Assert.DoesNotThrow(() => manager.RequestRebuild());
            Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Building));
            AssertSessionMode(manager, editedSpace);
            yield return Settle();

            FlowFieldSpaceModeScenarios.EditInt(manager, FlowFieldManager.PropertyNames.SpaceMode, oldSpace);
            Assert.DoesNotThrow(() => manager.RequestRebuild());
            Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Building));
            AssertSessionMode(manager, oldSpace);
            yield return Settle();
            Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Ready));
        }

        private static string Report(FlowFieldManager manager)
            => "state=" + manager.State + " faulted=" + manager.IsFaulted + " space=" + manager.SpaceMode + " err=" + manager.LastError;

        // The session keeps Surface2D; only the serialized value changed.
        private static void AssertSessionMode(FlowFieldManager manager, int serializedSpace)
        {
            Assert.That(manager.SpaceMode, Is.EqualTo(FlowFieldSpaceMode.Surface2D));
            Assert.That(FlowFieldSpaceModeScenarios.SerializedInt(manager, FlowFieldManager.PropertyNames.SpaceMode), Is.EqualTo(serializedSpace));
            Assert.That(manager.IsFaulted, Is.False, FlowFieldSpaceModeScenarios.Report(manager));
        }

        // A second, then Ready, as the kit waited between its inputs.
        private static IEnumerator Settle()
        {
            yield return FlowFieldPlayScene.WaitSeconds(1f);
            yield return FlowFieldPlayScene.WaitUntilReady();
        }
    }
}
