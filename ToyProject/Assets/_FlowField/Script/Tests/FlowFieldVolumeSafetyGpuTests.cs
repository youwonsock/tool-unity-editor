using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    public sealed class FlowFieldVolumeSafetyGpuTests
    {
        [UnityTest]
        public IEnumerator ReleaseDuringGpuReadbackCannotPublishIntoNewLifetime() => InterruptGpuBuild(0);

        [UnityTest]
        public IEnumerator SuspendDuringGpuReadbackCannotOverwriteResumedPublication() => InterruptGpuBuild(1);

        [UnityTest]
        public IEnumerator GridReplacementDuringGpuReadbackRejectsOldGridResult() => InterruptGpuBuild(2);

        private static IEnumerator InterruptGpuBuild(int interruption)
        {
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)
                Assert.Ignore("Compute shaders and async readback are required.");
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Assets/_FlowField/Script/Runtime/Core/Solvers/FlowFieldFrontier.compute");
            Assert.That(shader, Is.Not.Null);
            var session = new FlowFieldVolumeSession();
            try
            {
                int publications = 0;
                session.FieldCommitted += () => publications++;
                session.Initialize(FlowFieldBakeMode.RuntimeDynamic, shader);
                var grid = FlowFieldVolumeSafetyRegressionTests.Grid;
                session.Submit(FlowFieldVolumeSafetyRegressionTests.Request(grid, shader: shader));
                for (int i = 0; i < 300 && !session.IsWaitingForExternalCompletion && session.IsRebuilding; i++)
                    FlowFieldVolumeSafetyRegressionTests.PumpOneStep(session);
                Assert.That(session.IsWaitingForExternalCompletion, Is.True, "Expected a real in-flight GPU request.");
                Assert.That(publications, Is.Zero);

                var nextGrid = grid;
                if (interruption == 0)
                {
                    session.Release();
                    Assert.That(session.TrySample(Vector3.one * 0.5f, out _), Is.False);
                    session.Initialize(FlowFieldBakeMode.RuntimeDynamic);
                }
                else if (interruption == 1)
                {
                    session.Suspend();
                    Assert.That(session.TrySample(Vector3.one * 0.5f, out _), Is.False);
                    session.Resume();
                }
                else
                {
                    nextGrid = FlowFieldGridSpace.FromVolumeCellGrid(new Vector3(100f, 0f, 0f), 4, 4, 4, 1f);
                }

                var nextGoal = nextGrid.Origin + new Vector3(0.5f, 2.5f, 2.5f);
                session.Submit(FlowFieldVolumeSafetyRegressionTests.Request(nextGrid, goal: nextGoal));
                FlowFieldVolumeSafetyRegressionTests.Finish(session);
                int revision = session.Revision;
                Assert.That(publications, Is.EqualTo(1));

                // Drain the old control and output requests, with editor updates
                // between them, so the late continuation is actually exercised.
                for (int i = 0; i < 3; i++)
                {
                    AsyncGPUReadback.WaitAllRequests();
                    yield return null;
                }

                Assert.That(session.IsFaulted, Is.False, session.LastError);
                Assert.That(session.IsReady, Is.True);
                Assert.That(session.Revision, Is.EqualTo(revision));
                Assert.That(publications, Is.EqualTo(1));
                Assert.That(session.TryGetFieldInfo(out var info), Is.True);
                Assert.That(info.Grid.Origin, Is.EqualTo(nextGrid.Origin));
                Assert.That(info.ResolvedGoalWorld, Is.EqualTo(nextGoal));
                Assert.That(session.TrySample(nextGrid.Origin + Vector3.one * 0.5f, out var sample), Is.True);
                Assert.That(sample.SpeedMultiplier, Is.GreaterThan(0f));
            }
            finally { session.Dispose(); }
        }
    }
}
