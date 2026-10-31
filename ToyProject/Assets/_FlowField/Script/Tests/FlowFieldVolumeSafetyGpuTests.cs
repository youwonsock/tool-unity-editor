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

        [UnityTest]
        public IEnumerator SuspendDuringGpuReadbackKeepsTheGpuForLaterBuilds() => GpuReturnsAfterInterruption(true);

        [UnityTest]
        public IEnumerator GridReplacementDuringGpuReadbackKeepsTheGpuForLaterBuilds() => GpuReturnsAfterInterruption(false);

        private static IEnumerator GpuReturnsAfterInterruption(bool suspend)
        {
            ComputeShader shader = LoadShader();
            var session = new FlowFieldVolumeSession();
            try
            {
                session.Initialize(FlowFieldBakeMode.RuntimeDynamic, shader);
                var grid = FlowFieldVolumeSafetyRegressionTests.Grid;
                session.Submit(FlowFieldVolumeSafetyRegressionTests.Request(grid, shader: shader));
                PumpUntilGpuWaits(session);
                Assert.That(session.IsWaitingForExternalCompletion, Is.True, "Expected a real in-flight GPU request.");

                var nextGrid = grid;
                if (suspend)
                {
                    session.Suspend();
                    session.Resume();
                }
                else
                {
                    nextGrid = FlowFieldGridSpace.FromVolumeCellGrid(new Vector3(100f, 0f, 0f), 4, 4, 4, 1f);
                }
                var nextGoal = nextGrid.Origin + new Vector3(0.5f, 2.5f, 2.5f);
                var nextRequest = FlowFieldVolumeSafetyRegressionTests.Request(nextGrid, goal: nextGoal, shader: shader);
                session.Submit(nextRequest);
                FlowFieldVolumeSafetyRegressionTests.Finish(session);
                Assert.That(session.GpuFallbackReason, Is.Null);

                for (int i = 0; i < 3; i++)
                {
                    AsyncGPUReadback.WaitAllRequests();
                    yield return null;
                }

                session.Submit(nextRequest);
                PumpUntilGpuWaits(session);
                Assert.That(session.IsWaitingForExternalCompletion, Is.True, "The later build must use the GPU again.");
            }
            finally { session.Dispose(); }
        }

        [UnityTest]
        public IEnumerator WaveOverflowKeepsManagedAndReportsTheReason()
        {
            ComputeShader shader = LoadShader();
            var session = new FlowFieldVolumeSession();
            try
            {
                session.Initialize(FlowFieldBakeMode.RuntimeDynamic, shader);
                var grid = FlowFieldGridSpace.FromVolumeCellGrid(Vector3.zero, 200, 1, 1, 1f);
                var request = new FlowFieldVolumeRequest(
                    grid,
                    new Bounds(new Vector3(100f, 0.5f, 0.5f), new Vector3(200f, 1f, 1f)),
                    FlowFieldBakeMode.RuntimeDynamic,
                    1 << FlowFieldRegressionFixture.Layer,
                    0f,
                    true,
                    new Vector3(199.5f, 0.5f, 0.5f),
                    0f,
                    Vector3.forward,
                    64,
                    shader,
                    null);
                session.Submit(request);
                double deadline = EditorApplication.timeSinceStartup + 30d;
                while (session.IsRebuilding && EditorApplication.timeSinceStartup < deadline)
                {
                    session.Pump(10d);
                    yield return null;
                }

                Assert.That(session.IsRebuilding, Is.False, "GPU fallback timed out.");
                Assert.That(session.IsFaulted, Is.False, session.LastError);
                Assert.That(session.GpuFallbackReason, Does.Contain("Overflow"));
                Assert.That(session.Sample(new Vector3(0.5f, 0.5f, 0.5f)).Direction, Is.EqualTo(Vector3.right));

                session.Submit(request);
                PumpUntilGpuWaits(session);
                Assert.That(session.IsWaitingForExternalCompletion, Is.False, "Overflow keeps the session on Managed.");
            }
            finally { session.Dispose(); }
        }

        [UnityTest]
        public IEnumerator SchedulerObservesARegisteredObstacleDuringTheGpuWait()
        {
            ComputeShader shader = LoadShader();
            var obstacle = new GameObject("Scheduler GPU wait obstacle") { layer = FlowFieldRegressionFixture.Layer };
            obstacle.transform.position = new Vector3(-50f, 0f, 0f);
            obstacle.AddComponent<BoxCollider>();
            Physics.SyncTransforms();
            var session = new FlowFieldVolumeSession();
            try
            {
                var grid = FlowFieldVolumeSafetyRegressionTests.Grid;
                var cell = new Vector3(3.5f, 1.5f, 3.5f);
                session.Initialize(FlowFieldBakeMode.RuntimeDynamic, shader);
                session.RegisterDynamicObstacle(obstacle.GetComponent<Collider>());
                session.Submit(FlowFieldVolumeSafetyRegressionTests.Request(grid, shader: shader));
                double deadline = EditorApplication.timeSinceStartup + 30d;
                while (session.IsRebuilding && EditorApplication.timeSinceStartup < deadline)
                {
                    session.Pump(10d);
                    yield return null;
                }
                Assert.That(session.IsReady, Is.True, session.LastError);

                session.Submit(FlowFieldVolumeSafetyRegressionTests.Request(
                    grid, goal: new Vector3(0.5f, 3.5f, 7.5f), shader: shader));
                PumpUntilGpuWaits(session);
                Assert.That(session.IsWaitingForExternalCompletion, Is.True, "Expected a real in-flight GPU request.");
                session.AttachRuntimeScheduler();
                Assert.That(session.Sample(cell).SpeedMultiplier, Is.GreaterThan(0f));

                obstacle.transform.position = cell;
                Physics.SyncTransforms();
                FlowFieldBuildScheduler.PumpAll(10d);

                Assert.That(session.IsWaitingForExternalCompletion, Is.True);
                Assert.That(session.Sample(cell).SpeedMultiplier, Is.Zero);
            }
            finally
            {
                session.Dispose();
                Object.DestroyImmediate(obstacle);
            }
        }

        [UnityTest]
        public IEnumerator GpuWaitDoesNotSpendTheCallersBudget()
        {
            ComputeShader shader = LoadShader();
            var session = new FlowFieldVolumeSession();
            try
            {
                session.Initialize(FlowFieldBakeMode.RuntimeDynamic, shader);
                session.Submit(FlowFieldVolumeSafetyRegressionTests.Request(
                    FlowFieldVolumeSafetyRegressionTests.Grid, shader: shader));
                PumpUntilGpuWaits(session);
                Assert.That(session.IsWaitingForExternalCompletion, Is.True, "Expected a real in-flight GPU request.");

                var timer = System.Diagnostics.Stopwatch.StartNew();
                session.Pump(500d);
                Assert.That(timer.Elapsed.TotalMilliseconds, Is.LessThan(100d));
                yield return null;
            }
            finally { session.Dispose(); }
        }

        private static ComputeShader LoadShader()
        {
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)
                Assert.Ignore("Compute shaders and async readback are required.");
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Assets/_FlowField/Script/Runtime/Core/Solvers/FlowFieldFrontier.compute");
            Assert.That(shader, Is.Not.Null);
            return shader;
        }

        private static void PumpUntilGpuWaits(FlowFieldVolumeSession session)
        {
            for (int i = 0; i < 2000 && !session.IsWaitingForExternalCompletion && session.IsRebuilding; i++)
                FlowFieldVolumeSafetyRegressionTests.PumpOneStep(session);
        }

        private static IEnumerator InterruptGpuBuild(int interruption)
        {
            ComputeShader shader = LoadShader();
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
