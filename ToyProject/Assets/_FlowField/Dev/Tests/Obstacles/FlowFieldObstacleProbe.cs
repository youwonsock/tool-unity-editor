using System;
using System.Collections;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// The steps the obstacle tests share, as the earlier local kit made them:
    /// probe cells picked on the published field, probe cubes on the Obstacle
    /// Layer, the sweep and grid settings edited as the Inspector does, a test
    /// fault, and the pauses the kit left between its calls.
    /// </summary>
    internal static class FlowFieldObstacleProbe
    {
        internal const string FaultMessage = "Test fault.";
        private const string FaultLogPattern = @"^\[FlowField\] Build failed: Test fault\.$";

        internal const string SweepProperty =
            FlowFieldManager.PropertyNames.Obstacles + "." + nameof(FlowFieldObstacleSettings.SweepUnregistered);
        internal const string SweepIntervalProperty =
            FlowFieldManager.PropertyNames.Obstacles + "." + nameof(FlowFieldObstacleSettings.SweepInterval);
        internal const string ClearanceProperty =
            FlowFieldManager.PropertyNames.Obstacles + "." + nameof(FlowFieldObstacleSettings.Clearance);

        /// <summary>Two probe cells of the published field and where a probe cube stands on each.</summary>
        internal readonly struct Cells
        {
            internal readonly int P;
            internal readonly int Q;
            internal readonly Vector3 PPosition;
            internal readonly Vector3 QPosition;
            internal readonly float CellSize;

            internal Cells(int p, int q, Vector3 pPosition, Vector3 qPosition, float cellSize)
            {
                P = p;
                Q = q;
                PPosition = pPosition;
                QPosition = qPosition;
                CellSize = cellSize;
            }
        }

        // ----- Play Mode and waits ---------------------------------------------

        /// <summary>Enters Play Mode and lets the startup builds settle, so the probe cells come from the final field.</summary>
        internal static IEnumerator Enter(string scene, FlowFieldBakeMode bakeMode)
        {
            yield return FlowFieldPlayScene.Enter(scene, bakeMode);
            yield return FlowFieldPlayScene.WaitUntilSettled(1f);
        }

        /// <summary>A pause the kit made between two calls, then Ready.</summary>
        internal static IEnumerator PauseThenReady(float seconds)
        {
            yield return FlowFieldPlayScene.WaitSeconds(seconds);
            yield return FlowFieldPlayScene.WaitUntilReady();
        }

        // ----- Reads -------------------------------------------------------------

        internal static FlowFieldField Published(FlowFieldManager manager)
        {
            Assert.That(manager.TryGetPublishedField(out FlowFieldField field), Is.True,
                "No published field: " + FlowFieldPlayScene.Describe());
            return field;
        }

        internal static Bounds WorldBounds(FlowFieldManager manager)
        {
            Assert.That(manager.TryGetFieldInfo(out FlowFieldFieldInfo info), Is.True,
                "No field info: " + FlowFieldPlayScene.Describe());
            return info.WorldBounds;
        }

        internal static int UncertainRegions(FlowFieldManager manager) => manager.Session.Safety.UncertainRegionCount;

        /// <summary>P and Q blocked, the uncertain regions and the state, without run-to-run noise.</summary>
        internal static string Report(FlowFieldManager manager, Cells cells)
        {
            string blocked = manager.TryGetPublishedField(out FlowFieldField field)
                ? "P=" + field.Agent.Blocked[cells.P] + " Q=" + field.Agent.Blocked[cells.Q]
                : "no field";
            return blocked + " uncertain=" + UncertainRegions(manager) + " state=" + manager.State;
        }

        // ----- Probe cells -------------------------------------------------------

        /// <summary>
        /// The first row, from a quarter of the depth (and height) on, whose
        /// columns <paramref name="xP"/>, <paramref name="xQ"/> and
        /// <paramref name="xBetween"/> are open, with P and Q at full speed.
        /// </summary>
        internal static Cells PickRow(FlowFieldManager manager, int xP, int xQ, int xBetween)
        {
            FlowFieldField field = Published(manager);
            FlowFieldGridSpace grid = field.Grid;
            bool volume = IsVolume(grid);
            for (int z = grid.Depth / 4; z < grid.Depth * 3 / 4; z++)
            {
                for (int y = volume ? grid.Height / 4 : 0; y < (volume ? grid.Height * 3 / 4 : 1); y++)
                {
                    int p = Index(grid, xP, y, z);
                    int q = Index(grid, xQ, y, z);
                    int between = Index(grid, xBetween, y, z);
                    if (!field.Agent.Blocked[p] && !field.Agent.Blocked[q] && !field.Agent.Blocked[between]
                        && field.Goal.FinalSpeeds[p] > 0.5f && field.Goal.FinalSpeeds[q] > 0.5f)
                        return CellsOf(manager, grid, p, q);
                }
            }
            Assert.Fail("No open pair of probe cells: " + FlowFieldPlayScene.Describe());
            return default;
        }

        /// <summary>The first row, from a quarter of the depth (and height) on, open in columns 2 and Width - 3.</summary>
        internal static Cells PickAcross(FlowFieldManager manager)
        {
            FlowFieldField field = Published(manager);
            FlowFieldGridSpace grid = field.Grid;
            bool volume = IsVolume(grid);
            for (int z = grid.Depth / 4; z < grid.Depth * 3 / 4; z++)
            {
                for (int y = volume ? grid.Height / 4 : 0; y < (volume ? grid.Height * 3 / 4 : 1); y++)
                {
                    int p = Index(grid, 2, y, z);
                    int q = Index(grid, grid.Width - 3, y, z);
                    if (!field.Agent.Blocked[p] && !field.Agent.Blocked[q])
                        return CellsOf(manager, grid, p, q);
                }
            }
            Assert.Fail("No open pair of probe cells: " + FlowFieldPlayScene.Describe());
            return default;
        }

        /// <summary>
        /// The first row, from three quarters of the depth (and height) down,
        /// in which the column three quarters across, the one after it and the
        /// middle column are open, with the first and the middle at full speed.
        /// P lies far from the origin, Q halfway.
        /// </summary>
        internal static Cells PickFarRow(FlowFieldManager manager)
        {
            FlowFieldField field = Published(manager);
            FlowFieldGridSpace grid = field.Grid;
            bool volume = IsVolume(grid);
            int xP = grid.Width * 3 / 4;
            int xQ = grid.Width / 2;
            for (int z = grid.Depth * 3 / 4; z > grid.Depth / 4; z--)
            {
                for (int y = volume ? grid.Height * 3 / 4 : 0; y > (volume ? grid.Height / 4 : -1); y--)
                {
                    int p = Index(grid, xP, y, z);
                    int next = Index(grid, xP + 1, y, z);
                    int q = Index(grid, xQ, y, z);
                    if (!field.Agent.Blocked[p] && !field.Agent.Blocked[q] && !field.Agent.Blocked[next]
                        && field.Goal.FinalSpeeds[p] > 0.5f && field.Goal.FinalSpeeds[q] > 0.5f)
                        return CellsOf(manager, grid, p, q);
                }
            }
            Assert.Fail("No open pair of probe cells: " + FlowFieldPlayScene.Describe());
            return default;
        }

        /// <summary>The first open cell from <paramref name="start"/> on, at full speed when asked.</summary>
        internal static int PickOpenCell(FlowFieldField field, int start, bool fullSpeed)
        {
            for (int cell = start; cell < field.Agent.Blocked.Length; cell++)
                if (!field.Agent.Blocked[cell] && (!fullSpeed || field.Goal.FinalSpeeds[cell] > 0.5f))
                    return cell;
            Assert.Fail("No open probe cell: " + FlowFieldPlayScene.Describe());
            return -1;
        }

        /// <summary>Where a probe cube stands on a cell: its center, at the middle of the field's height in Surface2D.</summary>
        internal static Vector3 Position(FlowFieldManager manager, FlowFieldGridSpace grid, int cell)
        {
            Vector3 center = grid.CellCenter(cell);
            if (!IsVolume(grid))
                center.y = WorldBounds(manager).center.y;
            return center;
        }

        private static Cells CellsOf(FlowFieldManager manager, FlowFieldGridSpace grid, int p, int q)
            => new Cells(p, q, Position(manager, grid, p), Position(manager, grid, q), grid.CellSize);

        private static bool IsVolume(FlowFieldGridSpace grid) => grid.SpaceMode == FlowFieldSpaceMode.Volume3D;

        private static int Index(FlowFieldGridSpace grid, int x, int y, int z)
            => IsVolume(grid) ? grid.ToFlatIndex(x, y, z) : grid.ToFlatIndex(x, z);

        // ----- Probe cubes -------------------------------------------------------

        /// <summary>
        /// A cube on the Obstacle Layer, 0.6 cells wide (and as tall as the
        /// field in Surface2D), with the physics scene synced. It is not
        /// registered. Made in Play Mode, it goes when Play Mode ends.
        /// </summary>
        internal static GameObject CreateCube(FlowFieldManager manager, string name, Vector3 position, float cellSize)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = name;
            cube.layer = ObstacleLayer(manager);
            cube.transform.position = position;
            cube.transform.localScale = CubeScale(manager, cellSize);
            Physics.SyncTransforms();
            return cube;
        }

        /// <summary>A probe cube registered as a dynamic obstacle.</summary>
        internal static GameObject CreateRegisteredCube(FlowFieldManager manager, string name, Vector3 position, float cellSize)
        {
            GameObject cube = CreateCube(manager, name, position, cellSize);
            Assert.That(manager.RegisterDynamicObstacle(cube.GetComponent<Collider>()), Is.True,
                "The probe cube was already registered: " + FlowFieldPlayScene.Describe());
            return cube;
        }

        /// <summary>
        /// A cube on the Obstacle Layer with a FlowFieldDynamicObstacle, which
        /// registers it in its next LateUpdate. Without <paramref name="scale"/>
        /// it keeps the unit size.
        /// </summary>
        internal static GameObject CreateDynamicObstacle(FlowFieldManager manager, string name, Vector3 position, Vector3? scale)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.SetActive(false);
            cube.name = name;
            cube.layer = ObstacleLayer(manager);
            cube.transform.position = position;
            if (scale.HasValue)
                cube.transform.localScale = scale.Value;
            var obstacle = cube.AddComponent<FlowFieldDynamicObstacle>();
            var serialized = new SerializedObject(obstacle);
            serialized.FindProperty("_manager").objectReferenceValue = manager;
            serialized.FindProperty("_collider").objectReferenceValue = cube.GetComponent<Collider>();
            serialized.ApplyModifiedPropertiesWithoutUndo();
            cube.SetActive(true);
            return cube;
        }

        /// <summary>0.6 cells across, and in Surface2D as tall as the field.</summary>
        internal static Vector3 CubeScale(FlowFieldManager manager, float cellSize)
        {
            float width = cellSize * 0.6f;
            float height = manager.SpaceMode == FlowFieldSpaceMode.Volume3D ? width : WorldBounds(manager).size.y;
            return new Vector3(width, height, width);
        }

        /// <summary>The lowest layer of the Obstacle Layer mask.</summary>
        internal static int ObstacleLayer(FlowFieldManager manager)
        {
            int mask = manager.CaptureConfig().Obstacles.Layer.value;
            int layer = 0;
            while (layer < 31 && (mask & (1 << layer)) == 0)
                layer++;
            return layer;
        }

        // ----- Settings ----------------------------------------------------------

        /// <summary>Turns the unregistered obstacle sweep on or off, as an Inspector edit in Play Mode does.</summary>
        internal static void SetSweep(FlowFieldManager manager, bool on, float? interval = null)
        {
            var serialized = new SerializedObject(manager);
            serialized.FindProperty(SweepProperty).boolValue = on;
            if (interval.HasValue)
                serialized.FindProperty(SweepIntervalProperty).floatValue = interval.Value;
            serialized.ApplyModifiedProperties();
        }

        internal static Bounds BakeBounds(FlowFieldManager manager) => manager.CaptureConfig().Grid.BakeBoundsLocal;

        /// <summary>Edits the Bake Bounds, as an Inspector edit in Play Mode does.</summary>
        internal static void SetBakeBounds(FlowFieldManager manager, Bounds bounds)
        {
            var serialized = new SerializedObject(manager);
            serialized.FindProperty(FlowFieldManager.PropertyNames.BakeBoundsLocal).boundsValue = bounds;
            serialized.ApplyModifiedProperties();
        }

        // ----- Faults ------------------------------------------------------------

        /// <summary>
        /// Faults the session as a failed build does. The Manager logs the
        /// fault as an error, which the test then expects.
        /// </summary>
        internal static void Fault(FlowFieldManager manager, FlowFieldTestLog log)
        {
            log.Expect(FaultLogPattern);
            LogAssert.Expect(LogType.Error, new Regex(FaultLogPattern));
            manager.Session.ReportFault(new InvalidOperationException(FaultMessage));
            Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Faulted), FlowFieldPlayScene.Describe());
        }

        // ----- Assertions --------------------------------------------------------

        /// <summary>The published field blocks P and Q as given.</summary>
        internal static void AssertCells(FlowFieldManager manager, Cells cells, bool pBlocked, bool qBlocked, string what)
        {
            FlowFieldField field = Published(manager);
            string observed = Report(manager, cells) + " | " + FlowFieldPlayScene.Describe();
            Assert.That(field.Agent.Blocked[cells.P], Is.EqualTo(pBlocked), what + ": P blocked. " + observed);
            Assert.That(field.Agent.Blocked[cells.Q], Is.EqualTo(qBlocked), what + ": Q blocked. " + observed);
        }

        /// <summary>Ready with no uncertain region left, and P and Q blocked as given.</summary>
        internal static void AssertSettled(FlowFieldManager manager, Cells cells, bool pBlocked, bool qBlocked, string what)
        {
            AssertCells(manager, cells, pBlocked, qBlocked, what);
            Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Ready), what + ": " + Report(manager, cells));
            Assert.That(UncertainRegions(manager), Is.Zero, what + ": " + Report(manager, cells));
        }

        /// <summary>The published field blocks one cell as given.</summary>
        internal static void AssertBlocked(FlowFieldManager manager, int cell, bool blocked, string what)
        {
            FlowFieldField field = Published(manager);
            Assert.That(field.Agent.Blocked[cell], Is.EqualTo(blocked),
                what + ": cell " + cell + " blocked. uncertain=" + UncertainRegions(manager) + " | " + FlowFieldPlayScene.Describe());
        }

        /// <summary>
        /// The cells the session treats as uncertain are exactly those that
        /// <paramref name="region"/> reaches, compared as the session compares
        /// them (XZ only in Surface2D).
        /// </summary>
        internal static void AssertUncertainCellsAre(FlowFieldManager manager, FlowFieldGridSpace grid, Bounds region, string what)
        {
            FlowFieldSafety safety = manager.Session.Safety;
            bool planar = !IsVolume(grid);
            int uncertain = 0;
            int expected = 0;
            int differing = 0;
            int firstDiffering = -1;
            for (int cell = 0; cell < grid.CellCount; cell++)
            {
                Vector3 center = grid.CellCenter(cell);
                bool isUncertain = safety.IsCellUncertain(grid, center);
                bool inRegion = Overlaps(new Bounds(center, Vector3.one * grid.CellSize), region, planar);
                if (isUncertain)
                    uncertain++;
                if (inRegion)
                    expected++;
                if (isUncertain == inRegion)
                    continue;
                differing++;
                if (firstDiffering < 0)
                    firstDiffering = cell;
            }
            Assert.That(expected, Is.GreaterThan(0), what + ": the expected region reaches no cell.");
            Assert.That(differing, Is.Zero,
                what + ": " + uncertain + " uncertain cells, " + expected + " in the region, " + differing
                + " differ (first " + firstDiffering + "). " + FlowFieldPlayScene.Describe());
        }

        private static bool Overlaps(Bounds cell, Bounds region, bool planar)
            => planar
                ? cell.min.x <= region.max.x && cell.max.x >= region.min.x
                    && cell.min.z <= region.max.z && cell.max.z >= region.min.z
                : region.Intersects(cell);
    }
}
