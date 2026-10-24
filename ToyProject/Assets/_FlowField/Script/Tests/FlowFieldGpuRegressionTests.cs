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
