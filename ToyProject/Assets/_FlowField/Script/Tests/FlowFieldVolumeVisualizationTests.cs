using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Common.FlowField.Tests
{
    public sealed class FlowFieldVolumeVisualizationTests
    {
        private FlowFieldVolumeBakeData _bakeData;

        [TearDown]
        public void TearDownBakeData()
        {
            if (_bakeData != null)
                Object.DestroyImmediate(_bakeData);
        }

        [Test]
        public void FullVolumeSelectsEveryCellAcrossAllYLayers()
        {
            FlowFieldGridSpace grid = FlowFieldGridSpace.FromVolumeCellGrid(
                Vector3.zero,
                20,
                12,
                20,
                1f);

            Assert.That(
                FlowFieldVolumeDisplaySelection.TryCreate(
                    grid,
                    FlowFieldVolumeGizmoMode.FullVolume,
                    FlowFieldVolumeSliceAxis.Y,
                    -1,
                    FlowFieldVolumeDisplaySelection.DefaultFullVolumeLimit,
                    out FlowFieldVolumeDisplaySelection selection),
                Is.True);
            Assert.That(selection.DisplayCount, Is.EqualTo(4800));

            var indices = new HashSet<int>();
            var layers = new HashSet<int>();
            for (int ordinal = 0; ordinal < selection.DisplayCount; ordinal++)
            {
                Assert.That(
                    selection.TryGetCoordinate(
                        grid,
                        ordinal,
                        out int x,
                        out int y,
                        out int z,
                        out int index),
                    Is.True);
                Assert.That(indices.Add(index), Is.True, $"Duplicate display index {index}.");
                Assert.That(grid.ToFlatIndex(x, y, z), Is.EqualTo(index));
                layers.Add(y);
            }

            Assert.That(indices.Count, Is.EqualTo(4800));
            Assert.That(layers.Count, Is.EqualTo(12));
            Assert.That(layers.Contains(0), Is.True);
            Assert.That(layers.Contains(11), Is.True);
        }

        [Test]
        public void SliceSelectsOnlyTheNormalizedYLayer()
        {
            FlowFieldGridSpace grid = FlowFieldGridSpace.FromVolumeCellGrid(
                Vector3.zero,
                20,
                12,
                20,
                1f);

            Assert.That(
                FlowFieldVolumeDisplaySelection.TryCreate(
                    grid,
                    FlowFieldVolumeGizmoMode.Slice,
                    FlowFieldVolumeSliceAxis.Y,
                    -1,
                    FlowFieldVolumeDisplaySelection.DefaultSliceLimit,
                    out FlowFieldVolumeDisplaySelection selection),
                Is.True);
            Assert.That(selection.DisplayCount, Is.EqualTo(400));
            Assert.That(selection.Slice, Is.EqualTo(6));
            for (int ordinal = 0; ordinal < selection.DisplayCount; ordinal++)
            {
                Assert.That(
                    selection.TryGetCoordinate(
                        grid,
                        ordinal,
                        out _,
                        out int y,
                        out _,
                        out _),
                    Is.True);
                Assert.That(y, Is.EqualTo(6));
            }
        }

        [Test]
        public void LargeDimensionsUseLongIntermediateMathAndKeepEndpoints()
        {
            AssertLargeSelection(1, 1, 1000000);
            AssertLargeSelection(2, 250000, 2);
        }

        [Test]
        public void SampleCoordinateMappingAlwaysIncludesAxisEndpoints()
        {
            Assert.That(
                FlowFieldVolumeDisplaySelection.MapSampleCoordinate(0, 2, 1000000),
                Is.EqualTo(0));
            Assert.That(
                FlowFieldVolumeDisplaySelection.MapSampleCoordinate(1, 2, 1000000),
                Is.EqualTo(999999));
            Assert.That(
                FlowFieldVolumeDisplaySelection.ComputeSampleCount(1000000, 500000),
                Is.EqualTo(2));
        }

        [Test]
        public void ResumableValidatorAcceptsValidPayloadAndRejectsNonGoalAnchor()
        {
            FlowFieldGridSpace grid = FlowFieldGridSpace.FromVolumeCellGrid(
                Vector3.zero,
                2,
                2,
                2,
                1f);
            _bakeData = ScriptableObject.CreateInstance<FlowFieldVolumeBakeData>();
            bool[] blocked = new bool[grid.CellCount];
            uint[] topology = new uint[grid.CellCount];
            FlowFieldGoalFlags[] flags = new FlowFieldGoalFlags[grid.CellCount];
            int[] next = new int[grid.CellCount];
            Vector3[] directions = new Vector3[grid.CellCount];
            float[] speeds = new float[grid.CellCount];
            Vector3[] escape = new Vector3[grid.CellCount];
            for (int index = 0; index < grid.CellCount; index++)
            {
                next[index] = -1;
                directions[index] = Vector3.forward;
                speeds[index] = 1f;
            }
            _bakeData.Apply(
                grid,
                new Bounds(Vector3.one, Vector3.one * 2f),
                (LayerMask)1,
                0f,
                false,
                Vector3.zero,
                0f,
                -1,
                blocked,
                topology,
                flags,
                next,
                directions,
                speeds,
                escape);

            var valid = new FlowFieldVolumeBakeValidator(_bakeData, grid);
            while (!valid.IsComplete)
                valid.Step(1, 1);
            Assert.That(valid.IsValid, Is.True, valid.Error);
            valid.Dispose();

            Assert.That(_bakeData.TryGetValidationView(out FlowFieldVolumeBakeValidationView view), Is.True);
            view.NextCells[0] = 0;
            var invalid = new FlowFieldVolumeBakeValidator(_bakeData, grid);
            invalid.Step(4096, 64);
            Assert.That(invalid.Status, Is.EqualTo(FlowFieldVolumeBakeValidationStatus.Invalid));
            StringAssert.Contains("(0, 0, 0)", invalid.Error);
            invalid.Dispose();
            view.NextCells[0] = -1;
        }

        [Test]
        public void ResumableValidatorRejectsBlockedVolumeDiagonalCorner()
        {
            FlowFieldGridSpace grid = FlowFieldGridSpace.FromVolumeCellGrid(
                Vector3.zero,
                2,
                2,
                2,
                1f);
            int count = grid.CellCount;
            bool[] blocked = new bool[count];
            uint[] topology = new uint[count];
            FlowFieldGoalFlags[] flags = new FlowFieldGoalFlags[count];
            int[] next = new int[count];
            Vector3[] directions = new Vector3[count];
            float[] speeds = new float[count];
            Vector3[] escape = new Vector3[count];
            for (int index = 0; index < count; index++)
            {
                next[index] = -1;
                directions[index] = Vector3.forward;
                speeds[index] = 1f;
            }

            int corner = grid.ToFlatIndex(0, 0, 0);
            int goal = grid.ToFlatIndex(1, 1, 1);
            int blockedIntermediate = grid.ToFlatIndex(1, 0, 0);
            blocked[blockedIntermediate] = true;
            next[blockedIntermediate] = -2;
            directions[blockedIntermediate] = Vector3.zero;
            speeds[blockedIntermediate] = 0f;
            int directionIndex = FlowFieldNeighborUtility.FindDirectionIndex(1, 1, 1);
            topology[corner] = 1u << directionIndex;
            next[corner] = goal;
            flags[corner] = FlowFieldGoalFlags.Directed;
            directions[corner] = new Vector3(1f, 1f, 1f).normalized;
            next[goal] = goal;
            flags[goal] = FlowFieldGoalFlags.Directed | FlowFieldGoalFlags.Anchor;
            directions[goal] = Vector3.zero;
            speeds[goal] = 0f;

            FlowFieldVolumeBakeValidationView view = new FlowFieldVolumeBakeValidationView(
                FlowFieldVolumeBakeData.CURRENT_FORMAT_VERSION,
                1,
                grid.Width,
                grid.Height,
                grid.Depth,
                grid.CellSize,
                grid.Origin,
                Vector3.one,
                Vector3.one * 2f,
                1,
                0f,
                true,
                grid.LocalToWorldCenter(1, 1, 1),
                0f,
                goal,
                blocked,
                topology,
                flags,
                next,
                directions,
                speeds,
                escape);

            using (var validator = new FlowFieldVolumeBakeValidator(view, grid))
            {
                while (!validator.IsComplete)
                    validator.Step(4096, 64);
                Assert.That(validator.IsValid, Is.False);
                StringAssert.Contains("blocked diagonal corner", validator.Error);
                StringAssert.Contains("(0, 0, 0)", validator.Error);
            }
        }

        private static void AssertLargeSelection(int width, int height, int depth)
        {
            FlowFieldGridSpace grid = FlowFieldGridSpace.FromVolumeCellGrid(
                Vector3.zero,
                width,
                height,
                depth,
                1f);
            Assert.That(
                FlowFieldVolumeDisplaySelection.TryCreate(
                    grid,
                    FlowFieldVolumeGizmoMode.FullVolume,
                    FlowFieldVolumeSliceAxis.Y,
                    -1,
                    FlowFieldVolumeDisplaySelection.DefaultFullVolumeLimit,
                    out FlowFieldVolumeDisplaySelection selection),
                Is.True);
            Assert.That(selection.DisplayCount, Is.LessThanOrEqualTo(8192));
            Assert.That(selection.DisplayCount, Is.GreaterThan(2));

            var indices = new HashSet<int>();
            int min = int.MaxValue;
            int max = int.MinValue;
            for (int ordinal = 0; ordinal < selection.DisplayCount; ordinal++)
            {
                Assert.That(
                    selection.TryGetCoordinate(
                        grid,
                        ordinal,
                        out _,
                        out _,
                        out _,
                        out int index),
                    Is.True);
                Assert.That(index, Is.GreaterThanOrEqualTo(0));
                Assert.That(index, Is.LessThan(grid.CellCount));
                Assert.That(indices.Add(index), Is.True);
                min = Mathf.Min(min, index);
                max = Mathf.Max(max, index);
            }
            Assert.That(min, Is.EqualTo(0));
            Assert.That(max, Is.EqualTo(grid.CellCount - 1));
        }
    }
}
