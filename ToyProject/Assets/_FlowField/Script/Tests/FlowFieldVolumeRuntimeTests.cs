using NUnit.Framework;
using UnityEngine;

namespace Common.FlowField.Tests
{
    public sealed class FlowFieldVolumeRuntimeTests
    {
        private FlowFieldVolumeSession _session;
        private GameObject _obstacle;
        private FlowFieldVolumeBakeData _bakeData;

        [TearDown]
        public void TearDown()
        {
            _session?.Dispose();
            if (_obstacle != null)
                Object.DestroyImmediate(_obstacle);
            if (_bakeData != null)
                Object.DestroyImmediate(_bakeData);
        }

        [Test]
        public void VolumeGridUsesXThenZThenYFlatteningAndRoundTrips()
        {
            FlowFieldGridSpace grid = FlowFieldGridSpace.FromVolumeCellGrid(
                new Vector3(-2f, 4f, 8f),
                3,
                2,
                4,
                0.5f);

            Assert.That(grid.CellCount, Is.EqualTo(24));
            Assert.That(grid.ToFlatIndex(2, 1, 3), Is.EqualTo(2 + 3 * 3 + 4 * 3 * 1));
            grid.FromFlatIndex(grid.ToFlatIndex(2, 1, 3), out int x, out int y, out int z);
            Assert.That(x, Is.EqualTo(2));
            Assert.That(y, Is.EqualTo(1));
            Assert.That(z, Is.EqualTo(3));

            Assert.That(grid.TryWorldToLocal(new Vector3(-1.25f, 4.75f, 9.75f), out x, out y, out z), Is.True);
            Assert.That(x, Is.EqualTo(1));
            Assert.That(y, Is.EqualTo(1));
            Assert.That(z, Is.EqualTo(3));
            Assert.That(grid.ContainsWorldPosition(new Vector3(-1.25f, 6.1f, 9.75f)), Is.False);
        }

        [Test]
        public void VolumeSampleHasCellWithoutSurface()
        {
            BuildSession(
                new Vector3(1.5f, 1.5f, 1.5f),
                withCornerObstacle: false);

            Assert.That(_session.TrySample(new Vector3(0.5f, 0.5f, 0.5f), out FlowFieldSample sample), Is.True);
            Assert.That(sample.HasCell, Is.True);
            Assert.That(sample.HasSurface, Is.False);
            Assert.That(sample.SurfaceNormal, Is.EqualTo(Vector3.zero));
            Assert.That(sample.Direction.sqrMagnitude, Is.GreaterThan(0.9f));
        }

        [Test]
        public void VolumeDiagonalCannotCutThroughBlockedIntermediateCell()
        {
            BuildSession(
                new Vector3(1.5f, 1.5f, 1.5f),
                withCornerObstacle: true);

            Assert.That(_session.TrySample(new Vector3(0.5f, 0.5f, 0.5f), out FlowFieldSample sample), Is.True);
            Vector3 direct = new Vector3(1f, 1f, 1f).normalized;
            Assert.That(Vector3.Dot(sample.Direction, direct), Is.LessThan(0.99f));
            Assert.That(sample.Direction.sqrMagnitude, Is.GreaterThan(0.9f));
        }

        [Test]
        public void VolumeGoalIsTheOnlyAnchor()
        {
            BuildSession(
                new Vector3(1.5f, 1.5f, 1.5f),
                withCornerObstacle: false);

            Assert.That(_session.TryExport(
                out _,
                out _,
                out _,
                out FlowFieldGoalFlags[] goalFlags,
                out int[] next,
                out _,
                out _,
                out _,
                out int goalIndex), Is.True);
            Assert.That(goalIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(next[goalIndex], Is.EqualTo(goalIndex));
            Assert.That(goalFlags[goalIndex], Is.EqualTo(
                FlowFieldGoalFlags.Directed | FlowFieldGoalFlags.Anchor));

            int anchorCount = 0;
            for (int index = 0; index < next.Length; index++)
                if ((goalFlags[index] & FlowFieldGoalFlags.Anchor) != 0)
                    anchorCount++;
            Assert.That(anchorCount, Is.EqualTo(1));
        }

        [Test]
        public void VolumeBakeRejectsInvalidPayloadWithoutMutatingAsset()
        {
            _bakeData = ScriptableObject.CreateInstance<FlowFieldVolumeBakeData>();
            FlowFieldGridSpace grid = FlowFieldGridSpace.FromVolumeCellGrid(
                Vector3.zero,
                2,
                2,
                2,
                1f);
            Bounds bounds = new Bounds(Vector3.one, Vector3.one * 2f);
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
                bounds,
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
            int revision = _bakeData.Revision;
            Assert.That(_bakeData.TryGetView(grid, out _, out Vector3[] before, out _), Is.True);

            next[0] = 0;
            Assert.Throws<System.InvalidOperationException>(() => _bakeData.Apply(
                grid,
                bounds,
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
                escape));

            Assert.That(_bakeData.Revision, Is.EqualTo(revision));
            Assert.That(_bakeData.TryGetView(grid, out _, out Vector3[] after, out _), Is.True);
            Assert.That(after[0], Is.EqualTo(before[0]));
        }

        [Test]
        public void VolumeLayoutRejectsOversizedAndClampsTinyPositiveBoundsToOneCell()
        {
            Assert.That(
                FlowFieldBakeBoundsUtility.TryCreateVolumeWorldLayout(
                    Vector3.zero,
                    new Bounds(Vector3.zero, new Vector3(1000001f, 1f, 1f)),
                    1f,
                    out _,
                    out _),
                Is.False);

            Assert.That(
                FlowFieldBakeBoundsUtility.TryCreateVolumeWorldLayout(
                    Vector3.zero,
                    new Bounds(Vector3.zero, new Vector3(0.01f, 0.01f, 0.01f)),
                    1f,
                    out _,
                    out FlowFieldGridSpace grid),
                Is.True);
            Assert.That(grid.Width, Is.EqualTo(1));
            Assert.That(grid.Height, Is.EqualTo(1));
            Assert.That(grid.Depth, Is.EqualTo(1));
        }

        [Test]
        public void ExplicitSampleAndClampContractsExposeVolumeState()
        {
            FlowFieldSample sample = new FlowFieldSample(Vector3.forward, 1f, Vector3.up, true, true);
            Assert.That(sample.HasSurface, Is.True);
            Assert.That(sample.HasCell, Is.True);

            FlowFieldClampResult clamp = new FlowFieldClampResult(Vector3.one, true, true, false);
            Assert.That(clamp.ClampedY, Is.True);
        }

        [Test]
        public void XzOnlyGridApiIsExplicitlyRejectedForVolume()
        {
            FlowFieldGridSpace grid = FlowFieldGridSpace.FromVolumeCellGrid(Vector3.zero, 2, 2, 2, 1f);
            Assert.Throws<System.InvalidOperationException>(() => grid.ToFlatIndex(0, 0));
            Assert.Throws<System.InvalidOperationException>(() => grid.TryWorldToLocal(Vector3.one, out _, out _));
            Assert.Throws<System.InvalidOperationException>(() => grid.TryGetOverlappingCells(
                new Bounds(Vector3.one, Vector3.one), out _, out _, out _, out _));
        }

        [Test]
        public void StaticVolumeRequestRequiresBakeAsset()
        {
            FlowFieldGridSpace grid = FlowFieldGridSpace.FromVolumeCellGrid(
                Vector3.zero,
                2,
                2,
                2,
                1f);
            FlowFieldVolumeRequest request = new FlowFieldVolumeRequest(
                grid,
                new Bounds(Vector3.one, Vector3.one * 2f),
                FlowFieldBakeMode.StaticBaked,
                (LayerMask)1,
                0f,
                false,
                Vector3.zero,
                0f,
                Vector3.forward,
                1024,
                null,
                null);

            _session = new FlowFieldVolumeSession();
            _session.Initialize(FlowFieldBakeMode.StaticBaked, null);

            Assert.Throws<System.InvalidOperationException>(() => _session.Submit(request));
            Assert.That(_session.IsFaulted, Is.False);
            Assert.That(_session.TryGetLatestRequestedInput(out _), Is.False);
        }

        private void BuildSession(Vector3 goal, bool withCornerObstacle)
        {
            if (withCornerObstacle)
            {
                _obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
                _obstacle.layer = 0;
                _obstacle.transform.position = new Vector3(1.5f, 0.5f, 0.5f);
                _obstacle.transform.localScale = Vector3.one;
            }

            FlowFieldGridSpace grid = FlowFieldGridSpace.FromVolumeCellGrid(Vector3.zero, 2, 2, 2, 1f);
            FlowFieldVolumeRequest request = new FlowFieldVolumeRequest(
                grid,
                new Bounds(Vector3.one, Vector3.one * 2f),
                FlowFieldBakeMode.RuntimeDynamic,
                (LayerMask)1,
                0f,
                true,
                goal,
                0f,
                Vector3.forward,
                1024,
                null,
                null);

            _session = new FlowFieldVolumeSession();
            _session.Initialize(FlowFieldBakeMode.RuntimeDynamic, null);
            _session.Submit(request);
            for (int iteration = 0; iteration < 100 && _session.IsRebuilding; iteration++)
                _session.Pump(10d);

            Assert.That(_session.IsFaulted, Is.False, _session.LastError);
            Assert.That(_session.IsReady, Is.True, _session.LastError);
        }
    }
}
