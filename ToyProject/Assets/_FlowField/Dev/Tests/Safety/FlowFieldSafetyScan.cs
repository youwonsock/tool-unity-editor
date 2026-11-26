using System.Collections;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// What the sample safety rule scenarios share: the waits and inputs of
    /// the earlier local kit, the planar heading tests its scans used, and a
    /// read-only view of a published Surface2D field's cells.
    /// </summary>
    internal static class FlowFieldSafetyScan
    {
        /// <summary>A mover's walk step in world units, as the kit walked.</summary>
        internal const float WalkStep = 0.06f;
        /// <summary>A near-edge sample sits this fraction of a cell from the cell center toward the side.</summary>
        internal const float NearEdgeOffset = 0.45f;

        // sin 22.5 degrees: a planar direction heads for the neighbour of its 45-degree sector.
        private const float SectorThreshold = 0.38268343f;
        private const int NoisePriority = 777;
        private const int NoiseSeed = 1;
        private const string NoiseName = "FlowFieldTestSafetyNoise";
        private const string DefaultDirectionPath =
            FlowFieldManager.PropertyNames.Flow + "." + nameof(FlowFieldFlowSettings.DefaultDirection);

        /// <summary>How one collide-and-slide walk step ended.</summary>
        internal enum SlideResult
        {
            Moved,
            // Every try ran into a blocked cell or a step up.
            Walled,
            OffGrid,
            // The step would cross into a drop or a cell with no ground.
            Drop,
        }

        /// <summary>One second, Ready, one second, Ready: the waits between the kit's steps.</summary>
        internal static IEnumerator Settle()
        {
            yield return FlowFieldPlayScene.WaitSeconds(1f);
            yield return FlowFieldPlayScene.WaitUntilReady();
            yield return FlowFieldPlayScene.WaitSeconds(1f);
            yield return FlowFieldPlayScene.WaitUntilReady();
        }

        /// <summary>The published field of a Ready Manager; samples read the same field until the next pump.</summary>
        internal static FlowFieldField PublishedField(FlowFieldManager manager)
        {
            Assert.That(manager.IsReady, Is.True, "The scan needs a Ready Manager: " + FlowFieldPlayScene.Describe());
            Assert.That(manager.TryGetPublishedField(out FlowFieldField field), Is.True,
                "No published field: " + FlowFieldPlayScene.Describe());
            return field;
        }

        internal static bool IsMoving(FlowFieldManager manager, Vector3 position, out FlowFieldSample sample)
            => manager.TrySample(position, out sample) && sample.SpeedMultiplier > 0f;

        internal static bool IsMoving(FlowFieldManager manager, Vector3 position)
            => IsMoving(manager, position, out _);

        internal static void ClearGoal(FlowFieldManager manager) => manager.SetGoal(FlowFieldGoalRequest.None);

        /// <summary>
        /// Sets Default Direction <paramref name="degrees"/> off +Z toward +X as an Inspector edit
        /// does; the caller then asks for the rebuild the kit asked for.
        /// </summary>
        internal static void SetDefaultDirection(FlowFieldManager manager, float degrees)
        {
            float radians = degrees * Mathf.Deg2Rad;
            var serialized = new SerializedObject(manager);
            serialized.FindProperty(DefaultDirectionPath).vector3Value =
                new Vector3(Mathf.Sin(radians), 0f, Mathf.Cos(radians));
            serialized.ApplyModifiedProperties();
        }

        /// <summary>
        /// A Noise Modifier whose trigger covers the field and 5 m around it, with the kit's
        /// Priority 777 and seed 1. It is made in Play Mode, so it goes when Play Mode ends.
        /// </summary>
        internal static FlowFieldNoiseModifier AddNoise(FlowFieldManager manager, float maxAngleDegrees)
        {
            Assert.That(manager.TryGetFieldInfo(out FlowFieldFieldInfo info), Is.True,
                "No field info: " + FlowFieldPlayScene.Describe());
            var host = new GameObject(NoiseName);
            host.SetActive(false);
            host.transform.position = info.WorldBounds.center;
            var box = host.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = info.WorldBounds.size + Vector3.one * 10f;
            var noise = host.AddComponent<FlowFieldNoiseModifier>();
            // Inactive, so the setters only store the values; Awake registers it.
            noise.SetFlowFieldManager(manager);
            noise.SetInfluenceCollider(box);
            noise.SetPriority(NoisePriority);
            noise.SetNoise(maxAngleDegrees, noise.SpatialFrequency, NoiseSeed);
            host.SetActive(true);
            return noise;
        }

        /// <summary>The neighbour step of the planar direction's 45-degree sector; false with no planar heading.</summary>
        internal static bool TrySectorStep(Vector3 direction, out int stepX, out int stepZ)
        {
            float threshold = SectorThreshold * new Vector2(direction.x, direction.z).magnitude;
            stepX = direction.x > threshold ? 1 : direction.x < -threshold ? -1 : 0;
            stepZ = direction.z > threshold ? 1 : direction.z < -threshold ? -1 : 0;
            return stepX != 0 || stepZ != 0;
        }

        /// <summary>
        /// The side step of a mover placed <see cref="NearEdgeOffset"/> of a cell toward the side
        /// of its minor axis, when it crosses that side face (0.05 cell away) before the forward
        /// one (0.5 cell away). False with no planar heading or a side component under 5% of it.
        /// </summary>
        internal static bool TryCrossesSidewaysNext(Vector3 direction, out int stepX, out int stepZ)
        {
            stepX = 0;
            stepZ = 0;
            float planar = new Vector2(direction.x, direction.z).magnitude;
            if (planar < 1e-4f)
                return false;
            bool sideIsX = Mathf.Abs(direction.x) < Mathf.Abs(direction.z);
            float side = sideIsX ? direction.x : direction.z;
            float forward = sideIsX ? direction.z : direction.x;
            if (Mathf.Abs(side) < 0.05f * planar)
                return false;
            if (0.05f / Mathf.Abs(side) >= 0.5f / Mathf.Max(1e-6f, Mathf.Abs(forward)))
                return false;
            int sign = side > 0f ? 1 : -1;
            stepX = sideIsX ? sign : 0;
            stepZ = sideIsX ? 0 : sign;
            return true;
        }

        /// <summary>Fails with the count and first description of every non-empty list.</summary>
        internal static void AssertNone(string scenario, params Offenders[] lists)
        {
            var failures = new StringBuilder();
            foreach (Offenders list in lists)
            {
                if (list.Count == 0)
                    continue;
                failures.Append("\n  ").Append(list.What).Append(": ").Append(list.Count)
                    .Append(", first ").Append(list.First);
            }
            if (failures.Length > 0)
                Assert.Fail(scenario + ":" + failures);
        }

        /// <summary>Samples a scan must not find: their count and the first one's description.</summary>
        internal sealed class Offenders
        {
            internal Offenders(string what) => What = what;

            internal string What { get; }
            internal int Count { get; private set; }
            internal string First { get; private set; }

            internal void Add(string description)
            {
                if (Count == 0)
                    First = description;
                Count++;
            }
        }

        /// <summary>
        /// The cells of a Ready Manager's published Surface2D field: blocked, walkable (has
        /// ground) and height, with the Max Step Height the safety rule reads.
        /// </summary>
        internal sealed class SurfaceCells
        {
            internal readonly FlowFieldField Field;
            internal readonly FlowFieldGridSpace Grid;
            internal readonly FlowFieldSurfaceData Surface;
            internal readonly bool[] Blocked;
            internal readonly bool[] Walkable;
            internal readonly float[] Heights;
            internal readonly float MaxStep;
            internal readonly int Width;
            internal readonly int Depth;

            internal SurfaceCells(FlowFieldManager manager)
            {
                Field = PublishedField(manager);
                Surface = Field.Scene?.Surface;
                Assert.That(Surface, Is.Not.Null, "The scan needs a Surface2D field: " + FlowFieldPlayScene.Describe());
                Grid = Field.Grid;
                Blocked = Field.Agent.Blocked;
                Heights = Surface.Heights;
                MaxStep = Surface.MaxStepHeight;
                Width = Grid.Width;
                Depth = Grid.Depth;
                Walkable = new bool[Grid.CellCount];
                for (int i = 0; i < Walkable.Length; i++)
                    Walkable[i] = Surface.IsSurfaceValid(i);
            }

            internal float CellSize => Grid.CellSize;

            internal int Index(int x, int z) => x + Width * z;

            internal bool Contains(int x, int z) => x >= 0 && z >= 0 && x < Width && z < Depth;

            internal Vector3 Center(int index) => Grid.CellCenter(index);

            internal bool IsOpen(int index) => !Blocked[index] && Walkable[index];

            /// <summary>Off the grid, blocked or with no ground.</summary>
            internal bool IsUnsafe(int x, int z) => !Contains(x, z) || !IsOpen(Index(x, z));

            /// <summary>A walkable cell more than Max Step Height above.</summary>
            internal bool IsStepUp(int from, int to) => Walkable[to] && Heights[to] > Heights[from] + MaxStep;

            /// <summary>What a colliding mover cannot walk into: a blocked cell or a step up.</summary>
            internal bool IsWall(int from, int to) => Blocked[to] || IsStepUp(from, to);

            /// <summary>An open cell with no ground or more than Max Step Height below.</summary>
            internal bool IsDrop(int from, int to)
                => !Blocked[to] && (!Walkable[to] || Heights[to] < Heights[from] - MaxStep);

            internal string Describe(int x, int z) => "cell (" + x + "," + z + ") at " + Center(Index(x, z)).ToString("F2");

            /// <summary>
            /// One collide-and-slide step: the whole step, else its X part, else its Z part, each
            /// skipped when it enters a wall. A drop ends the walk when <paramref name="dropsEnd"/>,
            /// otherwise the mover walks into it.
            /// </summary>
            internal SlideResult Slide(ref Vector3 position, ref int x, ref int z, Vector3 step, bool dropsEnd)
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    Vector3 move = attempt == 0 ? step
                        : attempt == 1 ? new Vector3(step.x, 0f, 0f)
                        : new Vector3(0f, 0f, step.z);
                    if (move.sqrMagnitude < 1e-10f)
                        continue;
                    Vector3 next = position + move;
                    if (!Grid.TryWorldToLocal(next, out int nextX, out int nextZ))
                        return SlideResult.OffGrid;
                    int from = Index(x, z);
                    int to = Index(nextX, nextZ);
                    if (to != from && IsWall(from, to))
                        continue;
                    if (dropsEnd && to != from && IsDrop(from, to))
                        return SlideResult.Drop;
                    position = next;
                    x = nextX;
                    z = nextZ;
                    return SlideResult.Moved;
                }
                return SlideResult.Walled;
            }
        }
    }
}
