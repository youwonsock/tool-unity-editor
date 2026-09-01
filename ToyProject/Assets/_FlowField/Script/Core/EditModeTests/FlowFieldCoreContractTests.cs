using System;
using NUnit.Framework;
using UnityEngine;

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
    }
}
