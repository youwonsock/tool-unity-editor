using System.Collections;
using NUnit.Framework;
using Common.FlowField.Editor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// The Volume3D display in Play Mode shows the runtime field, hides its
    /// vectors after an unreported Manager move, and shows them again once
    /// RequestRebuild has built the field at the new place.
    /// </summary>
    [TestFixture, Category("EditorBake")]
    internal sealed class VolumeDisplayStatusTests : FlowFieldPlayTestBase
    {
        private const string ShowingRuntimeField = "Showing the runtime final field.";
        private const string FieldNoLongerMatches =
            "The published field no longer matches the Manager's place or Bake Asset (an unreported move or asset change), so its vectors are hidden.";
        private const int Pumps = 20;
        private const double PumpMilliseconds = 50d;

        [UnityTest, Timeout(300000)]
        public IEnumerator UnreportedMoveHidesTheVectorsUntilRebuilt()
        {
            yield return FlowFieldPlayScene.Enter(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.RuntimeDynamic);
            FlowFieldManager manager = Manager;
            AssertStatus(manager, FlowFieldVolumeVisualizationStatusCode.Valid, ShowingRuntimeField, "built");

            manager.transform.position += new Vector3(3f, 0f, 0f);
            AssertStatus(manager, FlowFieldVolumeVisualizationStatusCode.Unavailable, FieldNoLongerMatches, "moved without a report");

            manager.RequestRebuild();
            yield return FlowFieldPlayScene.WaitUntilSettled(2f);
            AssertStatus(manager, FlowFieldVolumeVisualizationStatusCode.Valid, ShowingRuntimeField, "rebuilt");
        }

        // Registers the Manager with the display, pumps it as the Editor
        // update would, then reads its status.
        private static void AssertStatus(
            FlowFieldManager manager,
            FlowFieldVolumeVisualizationStatusCode code,
            string message,
            string what)
        {
            FlowFieldVolumeVisualizationEditor.TryGetStatus(manager, out _);
            for (int i = 0; i < Pumps; i++)
                FlowFieldVolumeVisualizationEditor.PumpAll(PumpMilliseconds);
            Assert.That(FlowFieldVolumeVisualizationEditor.TryGetStatus(manager, out FlowFieldVolumeVisualizationStatus status), Is.True, what);
            Assert.That(status.Code, Is.EqualTo(code), what + ": " + status.Message + " | " + FlowFieldPlayScene.Describe());
            Assert.That(status.Message, Is.EqualTo(message), what);
        }
    }
}
