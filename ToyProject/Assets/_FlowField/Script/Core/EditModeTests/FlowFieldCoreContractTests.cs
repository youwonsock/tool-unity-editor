using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField
{
    /// <summary>
    /// Core 계약의 fail-fast 입력과 정상적인 결과 없음 경계를 고정하는 편집 모드 테스트입니다.
    /// </summary>
    public sealed class FlowFieldCoreContractTests
    {
        [Test]
        public void GridRejectsInvalidDimensionsAndCellSize()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => FlowFieldGridSpace.FromCellGrid(Vector3.zero, 0, 4, 0.5f));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => FlowFieldGridSpace.FromCellGrid(Vector3.zero, 4, 4, float.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => FlowFieldGridSpace.FromCellGrid(Vector3.zero, 400, 400, 0.5f));
        }

        [Test]
        public void DefaultDirectionRejectsZeroAndNonFiniteValues()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => FlowFieldVectorUtility.NormalizeDefaultDirection(Vector3.zero));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => FlowFieldVectorUtility.NormalizeDefaultDirection(
                    new Vector3(float.PositiveInfinity, 0f, 1f)));
        }

        [Test]
        public void ModifierOutputRejectsNonNormalizedDirection()
        {
            FlowFieldVectorState candidate = new FlowFieldVectorState(
                new Vector3(2f, 0f, 0f),
                1f);

            Assert.Throws<ArgumentException>(
                () => FlowFieldVectorUtility.ValidateModifierOutput(candidate, Vector3.up));
        }

        [Test]
        public void MissingSurfaceIsAValidNoSampleResult()
        {
            FlowFieldGridSpace grid = FlowFieldGridSpace.FromCellGrid(Vector3.zero, 2, 2, 1f);
            FlowFieldWorkspace workspace = new FlowFieldWorkspace();
            workspace.Resize(grid.CellCount);

            bool sampled = FlowFieldCellSampler.TrySample(
                grid,
                null,
                workspace,
                new Vector3(0.5f, 0f, 0.5f),
                out FlowFieldSample sample);

            Assert.That(sampled, Is.False);
            Assert.That(sample.HasSurface, Is.False);
        }

        [Test]
        public void ExplicitClampKeepsWorldYAndClampsHorizontalCoordinates()
        {
            FlowFieldGridSpace grid = FlowFieldGridSpace.FromCellGrid(
                new Vector3(-1f, 7f, -1f),
                2,
                2,
                1f);
            Vector3 result = grid.ClampWorldXZ(new Vector3(-4f, 12f, 4f));

            Assert.That(result.y, Is.EqualTo(12f));
            Assert.That(result.x, Is.EqualTo(-1f).Within(0.0001f));
            Assert.That(result.z, Is.EqualTo(0.9999f).Within(0.001f));
        }

        [Test]
        public void GoalAlignmentScorePrefersTheClosestCandidateInGoalDirection()
        {
            int directCardinal = FlowFieldGraphTraversal.CalculateGoalAlignmentScore(
                currentX: 4,
                currentZ: 4,
                goalX: 8,
                goalZ: 4,
                stepX: 1,
                stepZ: 0);
            int unnecessaryDiagonal = FlowFieldGraphTraversal.CalculateGoalAlignmentScore(
                currentX: 4,
                currentZ: 4,
                goalX: 8,
                goalZ: 4,
                stepX: 1,
                stepZ: 1);
            int directDiagonal = FlowFieldGraphTraversal.CalculateGoalAlignmentScore(
                currentX: 4,
                currentZ: 4,
                goalX: 8,
                goalZ: 8,
                stepX: 1,
                stepZ: 1);

            Assert.That(directCardinal, Is.GreaterThan(unnecessaryDiagonal));
            Assert.That(directDiagonal, Is.GreaterThan(
                FlowFieldGraphTraversal.CalculateGoalAlignmentScore(4, 4, 8, 8, 1, 0)));
        }

        [Test]
        public void ManagedSolverUsesGoalAlignedPredecessorsAndStrictlyDecreasingCosts()
        {
            FlowFieldGridSpace grid = FlowFieldGridSpace.FromCellGrid(Vector3.zero, 9, 9, 1f);
            FlowFieldSurfaceBakeData surface = CreateFlatSurface(grid, allowDiagonals: true);
            var workspace = new FlowFieldWorkspace();
            workspace.Resize(grid.CellCount);

            try
            {
                Assert.That(
                    FlowFieldSolver.BuildGoal(grid, surface, workspace, 4, 4, 0f, out int goalIndex),
                    Is.True);
                Assert.That(goalIndex, Is.EqualTo(grid.ToFlatIndex(4, 4)));

                // On the goal row, an unnecessary diagonal is farther from the
                // Goal than the same-row cardinal predecessor.
                int sameRow = grid.ToFlatIndex(7, 4);
                Assert.That(workspace.NextCells[sameRow], Is.EqualTo(grid.ToFlatIndex(6, 4)));

                // Away from both axes, the diagonal predecessor is closest.
                int diagonal = grid.ToFlatIndex(7, 7);
                Assert.That(workspace.NextCells[diagonal], Is.EqualTo(grid.ToFlatIndex(6, 6)));

                // Every directed edge must move exactly one BFS wave closer.
                for (int index = 0; index < grid.CellCount; index++)
                {
                    int next = workspace.NextCells[index];
                    if (next < 0 || index == goalIndex)
                        continue;

                    Assert.That(workspace.Costs[next], Is.EqualTo(workspace.Costs[index] - 1));
                }
            }
            finally
            {
                workspace.Release();
                UnityEngine.Object.DestroyImmediate(surface);
            }
        }

        [Test]
        public void ManagedSolverUsesFlatIndexOnlyWhenGoalScoresTie()
        {
            FlowFieldGridSpace grid = FlowFieldGridSpace.FromCellGrid(Vector3.zero, 5, 5, 1f);
            FlowFieldSurfaceBakeData surface = CreateFlatSurface(grid, allowDiagonals: false);
            var workspace = new FlowFieldWorkspace();
            workspace.Resize(grid.CellCount);

            try
            {
                Assert.That(
                    FlowFieldGraphTraversal.CalculateGoalAlignmentScore(2, 2, 4, 4, 1, 0),
                    Is.EqualTo(FlowFieldGraphTraversal.CalculateGoalAlignmentScore(2, 2, 4, 4, 0, 1)));
                Assert.That(
                    FlowFieldSolver.BuildGoal(grid, surface, workspace, 4, 4, 0f, out _),
                    Is.True);

                int current = grid.ToFlatIndex(2, 2);
                int xCandidate = grid.ToFlatIndex(3, 2);
                int zCandidate = grid.ToFlatIndex(2, 3);
                Assert.That(xCandidate, Is.LessThan(zCandidate));
                Assert.That(workspace.NextCells[current], Is.EqualTo(xCandidate));
            }
            finally
            {
                workspace.Release();
                UnityEngine.Object.DestroyImmediate(surface);
            }
        }

        [Test]
        public void GoalAlignmentScoreRemainsWithinIntRangeAtMaximumGridCoordinates()
        {
            int towardGoal = FlowFieldGraphTraversal.CalculateGoalAlignmentScore(
                0,
                0,
                99999,
                99999,
                1,
                1);
            int awayFromGoal = FlowFieldGraphTraversal.CalculateGoalAlignmentScore(
                0,
                0,
                99999,
                99999,
                -1,
                -1);

            Assert.That(towardGoal, Is.EqualTo(399994));
            Assert.That(awayFromGoal, Is.EqualTo(-399998));
            Assert.That(towardGoal, Is.InRange(int.MinValue, int.MaxValue));
            Assert.That(awayFromGoal, Is.InRange(int.MinValue, int.MaxValue));
        }

        [Test]
        public void BlockedRequestedGoalUsesTheResolvedGoalForSelection()
        {
            FlowFieldGridSpace grid = FlowFieldGridSpace.FromCellGrid(Vector3.zero, 5, 5, 1f);
            FlowFieldSurfaceBakeData surface = CreateFlatSurface(grid, allowDiagonals: true);
            var workspace = new FlowFieldWorkspace();
            workspace.Resize(grid.CellCount);
            int requestedGoal = grid.ToFlatIndex(4, 4);
            workspace.Blocked[requestedGoal] = true;

            try
            {
                Assert.That(
                    FlowFieldSolver.BuildGoal(grid, surface, workspace, 4, 4, 0f, out int resolvedGoal),
                    Is.True);
                Assert.That(resolvedGoal, Is.Not.EqualTo(requestedGoal));
                Assert.That(workspace.NextCells[grid.ToFlatIndex(4, 3)], Is.EqualTo(resolvedGoal));
                Assert.That(workspace.NextCells[requestedGoal], Is.EqualTo(-2));
            }
            finally
            {
                workspace.Release();
                UnityEngine.Object.DestroyImmediate(surface);
            }
        }

        [Test]
        public void StaticBakeRejectsThePreviousDirectionFormat()
        {
            FlowFieldGridSpace grid = FlowFieldGridSpace.FromCellGrid(Vector3.zero, 3, 3, 1f);
            FlowFieldSurfaceBakeData surface = CreateFlatSurface(grid, allowDiagonals: true);
            var workspace = new FlowFieldWorkspace();
            workspace.Resize(grid.CellCount);
            var staticBake = ScriptableObject.CreateInstance<FlowFieldStaticBakeData>();
            staticBake.hideFlags = HideFlags.HideAndDontSave;

            try
            {
                Assert.That(
                    FlowFieldSolver.BuildGoal(grid, surface, workspace, 1, 1, 0f, out int resolvedGoal),
                    Is.True);
                Bounds bounds = new Bounds(
                    new Vector3(grid.WorldSizeX * 0.5f, 0f, grid.WorldSizeZ * 0.5f),
                    new Vector3(grid.WorldSizeX, 10f, grid.WorldSizeZ));
                FlowFieldSurfaceBakeSettings settings = new FlowFieldSurfaceBakeSettings(
                    grid,
                    bounds,
                    (LayerMask)1,
                    45f,
                    10f);
                staticBake.Apply(
                    settings,
                    surface,
                    (LayerMask)1,
                    2f,
                    1f,
                    0f,
                    true,
                    grid.LocalToWorldCenter(1, 1),
                    0f,
                    resolvedGoal,
                    workspace);
                Assert.That(staticBake.HasValidData, Is.True);

                typeof(FlowFieldStaticBakeData)
                    .GetField("_formatVersion", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .SetValue(staticBake, 2);
                Assert.That(staticBake.HasValidData, Is.False);
            }
            finally
            {
                workspace.Release();
                UnityEngine.Object.DestroyImmediate(staticBake);
                UnityEngine.Object.DestroyImmediate(surface);
            }
        }

        [UnityTest]
        public IEnumerator GpuAndManagedGoalSelectionRemainIdentical()
        {
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)
                Assert.Ignore("This graphics backend does not support the FlowField GPU test.");

            ComputeShader shader = Resources.Load<ComputeShader>("FlowFieldFrontier");
            if (shader == null)
                Assert.Fail("FlowFieldFrontier compute shader is missing.");

            FlowFieldGridSpace grid = FlowFieldGridSpace.FromCellGrid(Vector3.zero, 9, 9, 1f);
            FlowFieldSurfaceBakeData surface = CreateFlatSurface(grid, allowDiagonals: true);
            var gpuWorkspace = new FlowFieldWorkspace();
            var managedWorkspace = new FlowFieldWorkspace();
            gpuWorkspace.Resize(grid.CellCount);
            managedWorkspace.Resize(grid.CellCount);
            FlowFieldBuildPipeline pipeline = null;
            bool completed = false;
            Exception failure = null;

            try
            {
                Assert.That(
                    FlowFieldSolver.PrepareGoal(grid, surface, gpuWorkspace, 4, 4, 0f, out int gpuGoal),
                    Is.True);
                Assert.That(
                    FlowFieldSolver.BuildGoal(grid, surface, managedWorkspace, 4, 4, 0f, out int managedGoal),
                    Is.True);
                Assert.That(gpuGoal, Is.EqualTo(managedGoal));

                pipeline = new FlowFieldBuildPipeline(shader);
                FlowFieldBfsRequest request = new FlowFieldBfsRequest(
                    grid,
                    surface,
                    gpuWorkspace,
                    true,
                    4,
                    4,
                    0f,
                    gpuGoal,
                    grid.CellCount,
                    1);
                for (int iteration = 0; iteration < 10; iteration++)
                {
                    completed = false;
                    failure = null;
                    Assert.That(
                        pipeline.StartBfs(
                            request,
                            _ => completed = true,
                            (_, exception) => failure = exception),
                        Is.True);

                    for (int frame = 0; frame < 120 && !completed && failure == null; frame++)
                        yield return null;

                    if (failure != null)
                        Assert.Fail(failure.ToString());
                    Assert.That(completed, Is.True, "GPU BFS did not complete within the frame budget.");
                    Assert.That(pipeline.GpuDisabled, Is.False, "GPU test unexpectedly used Managed fallback.");

                    for (int index = 0; index < grid.CellCount; index++)
                    {
                        Assert.That(gpuWorkspace.NextCells[index], Is.EqualTo(managedWorkspace.NextCells[index]));
                        Assert.That(
                            Vector3.Distance(gpuWorkspace.GoalDirections[index], managedWorkspace.GoalDirections[index]),
                            Is.LessThanOrEqualTo(0.0001f));
                    }
                }
            }
            finally
            {
                pipeline?.Dispose();
                gpuWorkspace.Release();
                managedWorkspace.Release();
                UnityEngine.Object.DestroyImmediate(surface);
            }
        }

        private static FlowFieldSurfaceBakeData CreateFlatSurface(
            FlowFieldGridSpace grid,
            bool allowDiagonals)
        {
            var result = new FlowFieldSurfaceBakeResult(grid.CellCount);
            for (int z = 0; z < grid.Depth; z++)
            {
                for (int x = 0; x < grid.Width; x++)
                {
                    int index = grid.ToFlatIndex(x, z);
                    result.SetSurface(index, 0f, Vector3.up);
                    byte mask = 0;
                    for (int direction = 0; direction < FlowFieldNeighborUtility.Count; direction++)
                    {
                        if (!allowDiagonals && FlowFieldNeighborUtility.IsDiagonal(direction))
                            continue;

                        int nx = x + FlowFieldNeighborUtility.DeltaX[direction];
                        int nz = z + FlowFieldNeighborUtility.DeltaZ[direction];
                        if (grid.IsLocalInBounds(nx, nz))
                            mask |= (byte)(1 << direction);
                    }

                    result.NeighborMasks[index] = mask;
                }
            }

            Bounds bounds = new Bounds(
                new Vector3(
                    grid.Origin.x + grid.WorldSizeX * 0.5f,
                    grid.Origin.y,
                    grid.Origin.z + grid.WorldSizeZ * 0.5f),
                new Vector3(grid.WorldSizeX, 10f, grid.WorldSizeZ));
            var surface = ScriptableObject.CreateInstance<FlowFieldSurfaceBakeData>();
            surface.hideFlags = HideFlags.HideAndDontSave;
            surface.Apply(
                new FlowFieldSurfaceBakeSettings(grid, bounds, (LayerMask)1, 45f, 10f),
                result);
            return surface;
        }
    }
}
