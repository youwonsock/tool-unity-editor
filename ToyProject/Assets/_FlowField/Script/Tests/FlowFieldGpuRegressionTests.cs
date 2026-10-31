using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    public sealed class FlowFieldGpuRegressionTests
    {
        private static ComputeShader Shader => AssetDatabase.LoadAssetAtPath<ComputeShader>(
            "Assets/_FlowField/Script/Runtime/Core/Solvers/FlowFieldFrontier.compute");

        [UnityTest]
        public IEnumerator GpuWaveLimitFallsBackUsingTheSameNavigationInput()
        {
            using (var backend = new FlowFieldSurfaceBuildPipeline(Shader))
            {
                if (!backend.SupportsGpu) Assert.Ignore("Compute shaders and async readback are required.");
                var grid = FlowFieldGridSpace.FromCellGrid(Vector3.zero, 80, 1, 1f);
                var settings = new FlowFieldSurfaceBakeSettings(grid,
                    new Bounds(new Vector3(40f, 0f, 0.5f), new Vector3(80f, 4f, 1f)),
                    1 << FlowFieldRegressionFixture.Layer, 45f, 0.5f);
                var baked = new FlowFieldSurfaceBakeResult(grid.CellCount);
                for (int i = 0; i < grid.CellCount; i++) baked.SetSurface(i, 0f, Vector3.up);
                for (int i = 0; i < grid.CellCount; i++) FlowFieldSurfaceBaker.BuildNeighborMask(settings, baked, i, i, 0);
                var surface = FlowFieldSurfaceData.FromRuntime(settings, baked, 1);
                var workspace = new FlowFieldWorkspace();
                workspace.Resize(grid.CellCount);
                try
                {
                    Assert.That(FlowFieldSolver.PrepareGoal(grid, surface, workspace, 79, 0, 0f, out int goal), Is.True);
                    var request = new FlowFieldBfsRequest(grid, surface, workspace, true, 79, 0, 0f, goal, 64);
                    bool completed = false;
                    string failure = null;
                    Assert.That(backend.StartBfs(request, _ => completed = true,
                        (_, exception) => { failure = exception.ToString(); completed = true; }), Is.True);
                    double deadline = EditorApplication.timeSinceStartup + 30d;
                    while (!completed && EditorApplication.timeSinceStartup < deadline) yield return null;
                    Assert.That(completed, Is.True, "GPU fallback timed out.");
                    Assert.That(failure, Is.Null);
                    Assert.That(backend.GpuDisabled, Is.True);
                    Assert.That(backend.GpuFallbackReason, Does.Contain("Overflow"));
                    for (int i = 0; i < 79; i++)
                    {
                        Assert.That(workspace.NextCells[i], Is.EqualTo(i + 1));
                        Assert.That(workspace.GoalDirections[i], Is.EqualTo(Vector3.right));
                    }
                    Assert.That(workspace.NextCells[79], Is.EqualTo(79));
                }
                finally { workspace.Release(); }
            }
        }

        [UnityTest]
        public IEnumerator SurfaceGpuMatchesManagedWithBlockedCorner()
        {
            using (var solver = new FlowFieldComputeSolver(Shader))
            {
                if (!solver.IsSupported) Assert.Ignore("Compute shaders and async readback are required.");
                var grid = FlowFieldRegressionFixture.Grid;
                var surface = FlowFieldRegressionFixture.Surface().Build(FlowFieldRegressionFixture.Settings);
                var workspace = new FlowFieldWorkspace();
                workspace.Resize(grid.CellCount);
                try
                {
                    workspace.Blocked[4] = true;
                    Assert.That(FlowFieldSolver.BuildGoal(grid, surface, workspace, 2, 2, 0f, out int goal), Is.True);
                    yield return Compare(solver, new FlowFieldComputeRequest(grid, surface, workspace, goal, 1024),
                        workspace.NextCells, workspace.GoalDirections, workspace.Blocked);
                }
                finally { workspace.Release(); }
            }
        }

        [UnityTest]
        public IEnumerator VolumeGpuMatchesManagedWithBlockedIntermediateCell()
        {
            using (var solver = new FlowFieldComputeSolver(Shader))
            {
                if (!solver.IsSupported) Assert.Ignore("Compute shaders and async readback are required.");
                var obstacle = new GameObject("GPU volume corner");
                obstacle.layer = FlowFieldRegressionFixture.Layer;
                obstacle.transform.position = new Vector3(1.5f, 0.5f, 0.5f);
                obstacle.AddComponent<BoxCollider>();
                var session = new FlowFieldVolumeSession();
                try
                {
                    var grid = FlowFieldGridSpace.FromVolumeCellGrid(Vector3.zero, 3, 3, 3, 1f);
                    var request = new FlowFieldVolumeRequest(grid, new Bounds(Vector3.one * 1.5f, Vector3.one * 3f),
                        FlowFieldBakeMode.RuntimeDynamic, 1 << FlowFieldRegressionFixture.Layer, 0f, true,
                        Vector3.one * 2.5f, 0f, Vector3.forward, 1024, null, null);
                    session.Initialize(FlowFieldBakeMode.RuntimeDynamic);
                    session.Submit(request);
                    for (int i = 0; i < 100 && session.IsRebuilding; i++) session.Pump(10d);
                    Assert.That(session.TryExport(out _, out var blocked, out var topology, out _, out var next,
                        out var directions, out _, out _, out int goal), Is.True, session.LastError);
                    var influence = new bool[grid.CellCount];
                    for (int i = 0; i < influence.Length; i++) influence[i] = !blocked[i];
                    yield return Compare(solver, new FlowFieldComputeRequest(grid, blocked, influence, topology, goal, 1024),
                        next, directions, blocked);
                }
                finally
                {
                    session.Dispose();
                    Object.DestroyImmediate(obstacle);
                }
            }
        }

        // GPU may choose a different tied neighbor, so large scenes compare the
        // blocked cells and every cell's path length rather than each next cell.
        [UnityTest]
        public IEnumerator SurfaceGpuPathLengthsMatchManagedInALargeScene()
        {
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)
                Assert.Ignore("Compute shaders and async readback are required.");
            using var scenario = new FlowFieldScenarioFixture.SurfaceScenario(64, 48)
            {
                Goal = new Vector3(60.5f, 0f, 3.5f),
                GoalInfluenceRadius = 200f,
            };
            scenario.StaticWall(20f, 23f, 0f, 36f);
            scenario.StaticWall(40f, 43f, 12f, 48f);
            scenario.StaticWall(4f, 16f, 30f, 33f);
            scenario.Build();
            FlowFieldWorkspace managed = scenario.Session.CommittedWorkspace;

            var session = new FlowFieldSession(new FlowFieldFixedSurfaceSource(scenario.Surface));
            try
            {
                session.Initialize(
                    FlowFieldBakeMode.RuntimeDynamic,
                    FlowFieldSessionSourceKind.SceneBuild,
                    FlowFieldBfsBackendPolicy.PreferGpu,
                    Shader);
                session.Submit(scenario.Request());
                session.Pump();
                double deadline = EditorApplication.timeSinceStartup + 30d;
                while (session.IsRebuilding && EditorApplication.timeSinceStartup < deadline)
                    yield return null;
                Assert.That(session.IsReady, Is.True, session.LastError);
                Assert.That(session.GpuFallbackReason, Is.Null);

                FlowFieldWorkspace gpu = session.CommittedWorkspace;
                Assert.That(gpu.ResolvedGoalIndex, Is.EqualTo(managed.ResolvedGoalIndex));
                Assert.That(gpu.Blocked, Is.EqualTo(managed.Blocked));
                AssertSamePathLengths(gpu.NextCells, managed.NextCells);
            }
            finally { session.Dispose(); }
        }

        [UnityTest]
        public IEnumerator VolumeGpuPathLengthsMatchManagedInALargeScene()
        {
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)
                Assert.Ignore("Compute shaders and async readback are required.");
            using var scenario = new FlowFieldScenarioFixture.VolumeScenario(32, 12, 32)
            {
                Goal = new Vector3(29.5f, 1.5f, 29.5f),
                GoalInfluenceRadius = 200f,
            };
            scenario.LayerObstacle(new Vector3(10f, 4f, 12f), new Vector3(1f, 8f, 24f));
            scenario.LayerObstacle(new Vector3(21f, 8f, 20f), new Vector3(1f, 8f, 24f));
            scenario.RegisteredObstacle(new Vector3(26.5f, 2.5f, 10.5f), new Vector3(3f, 3f, 1f));
            scenario.Build();
            Assert.That(scenario.Session.TryExport(out FlowFieldVolumeRequest managedRequest, out bool[] managedBlocked, out _, out _,
                out int[] managedNext, out _, out _, out _, out int managedGoal), Is.True);

            var session = new FlowFieldVolumeSession();
            try
            {
                session.Initialize(FlowFieldBakeMode.RuntimeDynamic, Shader);
                session.RegisterDynamicObstacle(scenario.RegisteredObstacles[0]);
                session.Submit(new FlowFieldVolumeRequest(
                    managedRequest.Grid, managedRequest.WorldBounds, managedRequest.BakeMode, managedRequest.ObstacleLayer,
                    managedRequest.ObstacleClearance, managedRequest.HasGoal, managedRequest.GoalWorld, managedRequest.GoalInfluenceRadius,
                    managedRequest.DefaultDirection, 4096, Shader, null));
                double deadline = EditorApplication.timeSinceStartup + 30d;
                while (session.IsRebuilding && EditorApplication.timeSinceStartup < deadline)
                {
                    session.Pump(1000d);
                    yield return null;
                }
                Assert.That(session.IsReady, Is.True, session.LastError);
                Assert.That(session.GpuFallbackReason, Is.Null);

                Assert.That(session.TryExport(out _, out bool[] gpuBlocked, out _, out _,
                    out int[] gpuNext, out _, out _, out _, out int gpuGoal), Is.True);
                Assert.That(gpuGoal, Is.EqualTo(managedGoal));
                Assert.That(gpuBlocked, Is.EqualTo(managedBlocked));
                AssertSamePathLengths(gpuNext, managedNext);
            }
            finally { session.Dispose(); }
        }

        private static void AssertSamePathLengths(int[] actualNext, int[] expectedNext)
        {
            int reachable = 0;
            for (int cell = 0; cell < expectedNext.Length; cell++)
            {
                int expected = PathLength(expectedNext, cell);
                Assert.That(PathLength(actualNext, cell), Is.EqualTo(expected), "Cell " + cell);
                if (expected > 0) reachable++;
            }
            Assert.That(reachable, Is.GreaterThan(expectedNext.Length / 2), "The scene must exercise most cells.");
        }

        // A Goal points at itself and other negative values mean no path.
        private static int PathLength(int[] next, int cell)
        {
            int length = 0;
            while (next[cell] >= 0 && next[cell] != cell)
            {
                cell = next[cell];
                length++;
                Assert.That(length, Is.LessThanOrEqualTo(next.Length), "Next cells form a cycle.");
            }
            return next[cell] == cell ? length : -1;
        }

        private static IEnumerator Compare(FlowFieldComputeSolver solver, FlowFieldComputeRequest request,
            int[] expectedNext, Vector3[] expectedDirection, bool[] blocked)
        {
            bool completed = false;
            string failure = null;
            int[] next = null;
            Vector3[] directions = null;
            Assert.That(solver.Start(request, (input, result) =>
            {
                next = new int[result.Length];
                directions = new Vector3[result.Length];
                for (int i = 0; i < result.Length; i++)
                {
                    next[i] = result[i].NextCell;
                    directions[i] = result[i].Direction;
                }
                completed = true;
            }, (input, kind, exception) => { failure = kind + ": " + exception; completed = true; }), Is.True);
            double deadline = EditorApplication.timeSinceStartup + 30d;
            while (!completed && EditorApplication.timeSinceStartup < deadline) yield return null;
            Assert.That(completed, Is.True, "GPU readback timed out.");
            Assert.That(failure, Is.Null);
            Assert.That(next, Is.EqualTo(expectedNext));
            for (int i = 0; i < directions.Length; i++)
                if (!blocked[i]) Assert.That(Vector3.Distance(directions[i], expectedDirection[i]), Is.LessThan(0.0001f), "Cell " + i);
        }
    }
}
