using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Common.FlowField.Tests
{
    public sealed class FlowFieldReadViewRegressionTests
    {
        [Test]
        public void SurfaceReadViewPreservesCommittedCellDataAndRejectsVolumeAccess()
        {
            using var session = new FlowFieldSession(FlowFieldRegressionFixture.Surface());
            session.Initialize(FlowFieldBakeMode.RuntimeDynamic, FlowFieldSessionSourceKind.SceneBuild,
                FlowFieldSolvePolicy.Inline);
            session.Submit(FlowFieldRegressionFixture.Request());
            session.Pump();
            var view = session.CommittedView;
            Assert.That(view.IsValid, Is.True);
            for (int i = 0; i < view.CellCount; i++)
            {
                Assert.That(view.TryGetCell(i, out bool blocked, out FlowFieldGoalFlags flags,
                    out Vector3 direction, out float speed, out bool influenced), Is.True);
                Assert.That(blocked || influenced, Is.False);
                Assert.That(flags, Is.EqualTo(FlowFieldGoalFlags.None));
                Assert.That(direction, Is.EqualTo(Vector3.forward));
                Assert.That(speed, Is.EqualTo(1f));
            }
            Assert.That(view.TryGetCell(0, out _, out Vector3 _, out float _), Is.False);
            Assert.That(view.TryGetCell(view.CellCount, out _, out FlowFieldGoalFlags _, out _, out _, out _), Is.False);
        }

        [Test]
        public void VolumeViewKeepsPublishedIdentityUntilReplacementCommits()
        {
            var grid = FlowFieldGridSpace.FromVolumeCellGrid(Vector3.zero, 3, 3, 3, 1f);
            FlowFieldVolumeRequest Request(Vector3 direction) => new FlowFieldVolumeRequest(
                grid, new Bounds(Vector3.one * 1.5f, Vector3.one * 3f), FlowFieldBakeMode.RuntimeDynamic,
                1 << FlowFieldRegressionFixture.Layer, 0f, false, Vector3.zero, 0f, direction, null);
            using var session = new FlowFieldVolumeSession();
            var errors = new FlowFieldRegressionFixture.EventFailures(session);
            session.Initialize(FlowFieldBakeMode.RuntimeDynamic);
            void Finish()
            {
                for (int i = 0; i < 100 && session.IsRebuilding; i++) session.Pump(10d);
                Assert.That(session.IsReady, Is.True, session.LastError);
                Assert.That(session.IsRebuilding, Is.False);
            }
            session.Submit(Request(Vector3.forward));
            Finish();
            Assert.That(session.TryGetVisualizationView(out var before), Is.True);
            session.Submit(Request(Vector3.right));
            Assert.That(session.TryGetVisualizationView(out var pending), Is.True);
            Assert.That(pending.ResultId, Is.EqualTo(before.ResultId));
            Assert.That(pending.Source.HasSameDisplaySource(before.Source), Is.True);
            Assert.That(pending.TryGetCell(0, out bool blocked, out Vector3 direction, out float speed), Is.True);
            Assert.That(blocked, Is.False);
            Assert.That(direction, Is.EqualTo(Vector3.forward));
            Assert.That(speed, Is.EqualTo(1f));
            Finish();
            Assert.That(session.TryGetVisualizationView(out var after), Is.True);
            Assert.That(after.ResultId, Is.Not.EqualTo(before.ResultId));
            Assert.That(after.Source.HasSameDisplaySource(before.Source), Is.True);
            Assert.That(after.TryGetCell(0, out _, out direction, out speed), Is.True);
            Assert.That(direction, Is.EqualTo(Vector3.right));
            Assert.That(after.TryGetCell(0, out _, out FlowFieldGoalFlags _, out _, out _, out _), Is.False);
            session.Suspend();
            Assert.That(session.TryGetVisualizationView(out _), Is.False);
            errors.AssertNone();
        }

        [TestCase(FlowFieldVolumeGizmoMode.FullVolume, FlowFieldVolumeSliceAxis.X)]
        [TestCase(FlowFieldVolumeGizmoMode.Slice, FlowFieldVolumeSliceAxis.X)]
        [TestCase(FlowFieldVolumeGizmoMode.Slice, FlowFieldVolumeSliceAxis.Y)]
        [TestCase(FlowFieldVolumeGizmoMode.Slice, FlowFieldVolumeSliceAxis.Z)]
        public void DisplaySelectionPreservesBoundariesAndClampedSlices(
            FlowFieldVolumeGizmoMode mode, FlowFieldVolumeSliceAxis axis)
        {
            var grid = FlowFieldGridSpace.FromVolumeCellGrid(Vector3.zero, 5, 7, 9, 1f);
            bool slice = mode == FlowFieldVolumeGizmoMode.Slice;
            int limit = slice ? 4 : 8;
            Assert.That(FlowFieldVolumeDisplaySelection.TryCreate(grid, mode, axis, 999, limit, out var selection), Is.True);
            Assert.That(selection.DisplayCount, Is.EqualTo(limit));
            var visited = new HashSet<int>();
            for (int i = 0; i < selection.DisplayCount; i++)
            {
                Assert.That(selection.TryGetCoordinate(grid, i, out int x, out int y, out int z, out int flat), Is.True);
                Assert.That(visited.Add(flat), Is.True);
                Assert.That(x == 0 || x == 4, Is.True);
                Assert.That(y == 0 || y == 6, Is.True);
                Assert.That(z == 0 || z == 8, Is.True);
                if (slice) Assert.That(axis == FlowFieldVolumeSliceAxis.X ? x : axis == FlowFieldVolumeSliceAxis.Y ? y : z,
                    Is.EqualTo(axis == FlowFieldVolumeSliceAxis.X ? 4 : axis == FlowFieldVolumeSliceAxis.Y ? 6 : 8));
            }
            Assert.That(selection.TryGetCoordinate(grid, selection.DisplayCount, out _, out _, out _, out _), Is.False);
        }
    }
}
