using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// The areas of a world: a grid fits the world only with its Space Mode,
    /// Cell Size and lattice; joined areas never overlap; touching areas know
    /// every neighbor step between their cells, and leaving forgets them.
    /// </summary>
    [TestFixture, Category("World")]
    internal sealed class WorldAreaTests
    {
        private FlowFieldWorld _world;

        [SetUp]
        public void CreateWorld() => _world = CreateWorld(FlowFieldSpaceMode.Surface2D, 1f, new Vector3(0.5f, 0f, 0.5f));

        [TearDown]
        public void Clean()
        {
            Object.DestroyImmediate(_world);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [Test]
        public void GridFitsOnlyTheWorldsLattice()
        {
            Assert.That(FlowFieldWorldAreas.TryLatticeBox(_world, Surface(2.5f, 3.5f, 4, 3), out FlowFieldLatticeBox box, out _), Is.True);
            Assert.That(box.MinX, Is.EqualTo(2));
            Assert.That(box.MinZ, Is.EqualTo(3));
            Assert.That(box.SizeX, Is.EqualTo(4));
            Assert.That(box.SizeY, Is.EqualTo(1));
            Assert.That(box.SizeZ, Is.EqualTo(3));

            Assert.That(FlowFieldWorldAreas.TryLatticeBox(_world, Surface(2.7f, 3.5f, 4, 3), out _, out string offLattice), Is.False);
            StringAssert.Contains("does not start on the lattice", offLattice);
            Assert.That(FlowFieldWorldAreas.TryLatticeBox(
                _world, FlowFieldGridSpace.FromCellGrid(new Vector3(2.5f, 0f, 3.5f), 4, 3, 0.5f), out _, out string cellSize), Is.False);
            StringAssert.Contains("Cell Size", cellSize);
            Assert.That(FlowFieldWorldAreas.TryLatticeBox(_world, Volume(0.5f, 0f, 0.5f, 2, 2, 2), out _, out string mode), Is.False);
            StringAssert.Contains("Volume3D", mode);
        }

        [Test]
        public void TouchingAreasPairEveryStepBetweenThem()
        {
            FlowFieldWorldAreas areas = _world.Areas;
            FlowFieldWorldArea a = Join(areas, "A", Surface(0.5f, 0.5f, 4, 3));
            FlowFieldWorldArea b = Join(areas, "B", Surface(4.5f, 0.5f, 2, 3));
            // The three cells along the shared edge: one straight step each and
            // a diagonal up and down where the row exists on the other side.
            Assert.That(a.BorderWith(b).Pairs.Length, Is.EqualTo(7));
            Assert.That(b.BorderWith(a).Pairs.Length, Is.EqualTo(7));
            foreach (FlowFieldBorderPair pair in a.BorderWith(b).Pairs)
            {
                a.Locate(pair.Cell, out int x, out _, out int z);
                b.Locate(pair.Other, out int ox, out _, out int oz);
                Assert.That(ox - x, Is.EqualTo(FlowFieldNeighborUtility.DeltaX[pair.Direction]));
                Assert.That(oz - z, Is.EqualTo(FlowFieldNeighborUtility.DeltaZ[pair.Direction]));
            }

            // A corner meets diagonally once.
            FlowFieldWorldArea c = Join(areas, "C", Surface(6.5f, 3.5f, 2, 2));
            Assert.That(b.BorderWith(c).Pairs.Length, Is.EqualTo(1));
            Assert.That(a.BorderWith(c), Is.Null, "A and C are two cells apart.");

            Assert.That(areas.Overlapping(new FlowFieldLatticeBox(3, 0, 1, 2, 1, 1), null), Is.SameAs(a));
            Assert.That(areas.Overlapping(new FlowFieldLatticeBox(3, 0, 1, 2, 1, 1), a.Manager), Is.SameAs(b));
            Assert.That(areas.Find(new Vector3(5f, 0f, 1f)), Is.SameAs(b));

            areas.Leave(b.Manager);
            Assert.That(a.BorderWith(b), Is.Null, "Leaving forgets the border on both sides.");
            Assert.That(c.Borders, Is.Empty);
            Assert.That(areas.Find(b.Manager), Is.Null);
            Assert.That(areas.Managers, Is.EquivalentTo(new[] { a.Manager, c.Manager }));
            areas.Leave(a.Manager);
            areas.Leave(c.Manager);
        }

        [Test]
        public void VolumeAreasPairAcrossAFace()
        {
            FlowFieldWorld world = CreateWorld(FlowFieldSpaceMode.Volume3D, 1f, Vector3.zero);
            try
            {
                FlowFieldWorldAreas areas = world.Areas;
                FlowFieldWorldArea low = Join(areas, "Low", Volume(0f, 0f, 0f, 2, 2, 2));
                FlowFieldWorldArea high = Join(areas, "High", Volume(0f, 2f, 0f, 2, 2, 2));
                // Each of the 4 top cells steps up straight and up diagonally to
                // each top cell of the other area within one cell: 4 × 4 = 16.
                Assert.That(low.BorderWith(high).Pairs.Length, Is.EqualTo(16));
                areas.Leave(low.Manager);
                areas.Leave(high.Manager);
            }
            finally
            {
                Object.DestroyImmediate(world);
            }
        }

        private static FlowFieldWorld CreateWorld(FlowFieldSpaceMode mode, float cellSize, Vector3 origin)
        {
            var world = ScriptableObject.CreateInstance<FlowFieldWorld>();
            var serialized = new SerializedObject(world);
            serialized.FindProperty("_spaceMode").enumValueIndex = (int)mode;
            serialized.FindProperty("_cellSize").floatValue = cellSize;
            serialized.FindProperty("_latticeOrigin").vector3Value = origin;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return world;
        }

        private FlowFieldWorldArea Join(FlowFieldWorldAreas areas, string name, FlowFieldGridSpace grid)
        {
            FlowFieldManager manager = new GameObject(name).AddComponent<FlowFieldManager>();
            Assert.That(FlowFieldWorldAreas.TryLatticeBox(areas.World, grid, out FlowFieldLatticeBox box, out string reason), Is.True, reason);
            Assert.That(areas.Overlapping(in box, manager), Is.Null);
            return areas.Join(manager, grid, in box);
        }

        private static FlowFieldGridSpace Surface(float x, float z, int width, int depth)
            => FlowFieldGridSpace.FromCellGrid(new Vector3(x, 0f, z), width, depth, 1f);

        private static FlowFieldGridSpace Volume(float x, float y, float z, int width, int height, int depth)
            => FlowFieldGridSpace.FromVolumeCellGrid(new Vector3(x, y, z), width, height, depth, 1f);
    }
}
