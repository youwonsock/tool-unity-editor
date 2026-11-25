using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// Pins where the two space modes' builders differ in what the session
    /// reads, so a rewrite of the build protocol changes them on purpose:
    /// Volume3D counts a running build in HasPendingRequest and Surface2D
    /// does not, and a fault clears a pending Volume3D request while Surface2D
    /// keeps an accepted request through it.
    /// </summary>
    /// <remarks>
    /// Surface2D's HasPendingRequest is its request inbox alone. The build
    /// takes the request when it begins, so a running build with no newer
    /// input shows false, before and after a fault; input that arrives during
    /// the build sets it again, and a fault keeps it (CancelWork leaves the
    /// inbox alone). Volume3D's also counts the running build and a Modifier
    /// recompose, and its CancelWork clears all three.
    /// </remarks>
    [TestFixture, Category("Protocol")]
    internal sealed class BuildProtocolAsymmetryTests : FlowFieldPlayTestBase
    {
        [UnityTest, Timeout(300000)]
        public IEnumerator SurfaceFaultDuringSolve() => Surface(newerInput: false);

        [UnityTest, Timeout(300000)]
        public IEnumerator SurfaceFaultWithNewerInput() => Surface(newerInput: true);

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeFaultDuringBuild() => Volume(newerInput: false);

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeFaultWithNewerInput() => Volume(newerInput: true);

        private IEnumerator Surface(bool newerInput)
        {
            yield return FlowFieldPlayScene.Enter(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);
            yield return FlowFieldPlayScene.WaitUntilSettled();
            FlowFieldManager manager = Manager;
            FlowFieldSession session = manager.Session;
            Vector3 goal = FlowFieldProtocolScenes.SurfaceSolveGoal(manager);
            manager.SetGoal(FlowFieldGoalRequest.Position(goal, 0f));
            FlowFieldProtocolScenes.BeginSurfaceSolve(session);

            Assert.That(session.HasActiveBuild, Is.True, FlowFieldProtocolScenes.Describe(session));
            Assert.That(session.HasPendingRequest, Is.False,
                "Surface2D counted its running build as a pending request: " + FlowFieldProtocolScenes.Describe(session));
            Assert.That(session.IsBuilding, Is.True, "A running Surface2D build is still Building through its lifecycle.");
            if (newerInput)
            {
                manager.NotifyObstacleRegionDirty(FlowFieldProtocolScenes.RegionAround(goal));
                Assert.That(session.HasPendingRequest, Is.True,
                    "Input during the Surface2D solve is not pending: " + FlowFieldProtocolScenes.Describe(session));
            }

            int revision = Fault(session);
            Assert.That(session.HasPendingRequest, Is.EqualTo(newerInput),
                "Surface2D keeps an accepted request through a fault, and has none otherwise: " + FlowFieldProtocolScenes.Describe(session));
            Assert.That(session.HasOwedInput, Is.EqualTo(newerInput), FlowFieldProtocolScenes.Describe(session));
            yield return AssertStaysFaulted(session, revision);
        }

        private IEnumerator Volume(bool newerInput)
        {
            yield return FlowFieldPlayScene.Enter(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.RuntimeDynamic);
            yield return FlowFieldPlayScene.WaitUntilSettled();
            FlowFieldManager manager = Manager;
            FlowFieldSession session = manager.Session;
            Vector3 goal = FlowFieldProtocolScenes.VolumeGoal(manager);
            manager.SetGoal(FlowFieldGoalRequest.Position(goal, 0f));
            FlowFieldProtocolScenes.BeginVolumeBuild(session);

            Assert.That(session.HasPendingRequest, Is.True,
                "Volume3D did not count its running build as a pending request: " + FlowFieldProtocolScenes.Describe(session));
            Assert.That(session.HasOwedInput, Is.False, FlowFieldProtocolScenes.Describe(session));
            if (newerInput)
            {
                manager.NotifyObstacleRegionDirty(FlowFieldProtocolScenes.RegionAround(goal));
                Assert.That(session.HasOwedInput, Is.True,
                    "Input during the Volume3D build is not owed: " + FlowFieldProtocolScenes.Describe(session));
            }

            int revision = Fault(session);
            Assert.That(session.HasPendingRequest, Is.False,
                "A Volume3D fault kept a pending request: " + FlowFieldProtocolScenes.Describe(session));
            Assert.That(session.HasOwedInput, Is.False, FlowFieldProtocolScenes.Describe(session));
            yield return AssertStaysFaulted(session, revision);
        }

        // Faults the session in the middle of its build; the Manager logs it.
        private int Fault(FlowFieldSession session)
        {
            int revision = session.Revision;
            FlowFieldProtocolScenes.ExpectFault(Log);
            session.ReportFault(new InvalidOperationException(FlowFieldProtocolScenes.FaultMessage));
            Assert.That(session.State, Is.EqualTo(FlowFieldRuntimeState.Faulted), FlowFieldProtocolScenes.Describe(session));
            Assert.That(session.LastError, Is.EqualTo(FlowFieldProtocolScenes.FaultMessage));
            Assert.That(session.HasActiveBuild, Is.False, "The fault kept the build: " + FlowFieldProtocolScenes.Describe(session));
            Assert.That(session.IsWaitingForExternalCompletion, Is.False, FlowFieldProtocolScenes.Describe(session));
            Assert.That(session.IsBuilding, Is.False, "A faulted session reports Building work.");
            // A faulted session stays in the scheduler, idle, until its retry or Release.
            Assert.That(FlowFieldBuildScheduler.Contains(session), Is.True);
            return revision;
        }

        // Whatever HasPendingRequest says, a faulted session builds nothing.
        private static IEnumerator AssertStaysFaulted(FlowFieldSession session, int revision)
        {
            yield return FlowFieldPlayScene.WaitFrames(5);
            Assert.That(session.State, Is.EqualTo(FlowFieldRuntimeState.Faulted), FlowFieldProtocolScenes.Describe(session));
            Assert.That(session.Revision, Is.EqualTo(revision), "The faulted session published.");
        }
    }
}
