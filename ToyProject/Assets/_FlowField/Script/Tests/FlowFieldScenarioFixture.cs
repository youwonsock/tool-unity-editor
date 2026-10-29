using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// Deterministic multi-cell scenes shared by the golden field hashes and
    /// the explicit performance probes. Both sessions use the Managed solver:
    /// the GPU validator accepts any equal-cost successor, so GPU tie-breaks
    /// are not part of the golden contract.
    /// </summary>
    internal static class FlowFieldScenarioFixture
    {
        internal const int Layer = FlowFieldRegressionFixture.Layer;

        internal sealed class SceneObjects : IDisposable
        {
            private readonly List<GameObject> _objects = new List<GameObject>();

            internal BoxCollider Box(string name, Vector3 center, Vector3 size, bool isTrigger = false, bool isStatic = false)
            {
                var gameObject = new GameObject(name) { layer = Layer };
                gameObject.isStatic = isStatic;
                var box = gameObject.AddComponent<BoxCollider>();
                box.isTrigger = isTrigger;
                gameObject.transform.position = center;
                box.size = size;
                _objects.Add(gameObject);
                return box;
            }

            public void Dispose()
            {
                foreach (GameObject gameObject in _objects)
                {
                    if (gameObject != null)
                        Object.DestroyImmediate(gameObject);
                }
                _objects.Clear();
                Physics.SyncTransforms();
            }
        }

        internal sealed class RotateModifier : IFlowFieldVectorModifier
        {
            public Collider InfluenceCollider { get; set; }
            public int Priority { get; set; }
            public int Revision { get; set; }
            internal float Angle = 30f;
            internal float Speed = 0.5f;

            public IFlowFieldModifierSnapshot CaptureSnapshot() => new Snapshot(Angle, Speed);

            private sealed class Snapshot : IFlowFieldModifierSnapshot
            {
                private readonly float _angle;
                private readonly float _speed;

                internal Snapshot(float angle, float speed)
                {
                    _angle = angle;
                    _speed = speed;
                }

                public FlowFieldVectorState Modify(in FlowFieldVectorState current, in FlowFieldVectorModifierContext context)
                {
                    float speed = current.SpeedMultiplier * _speed;
                    if (current.Direction.sqrMagnitude <= 0.00000001f)
                        return new FlowFieldVectorState(current.Direction, speed);
                    Vector3 axis = context.SurfaceNormal.sqrMagnitude > 0.00000001f ? context.SurfaceNormal : Vector3.up;
                    Vector3 rotated = (Quaternion.AngleAxis(_angle, axis) * current.Direction).normalized;
                    return new FlowFieldVectorState(rotated, speed);
                }
            }
        }

        /// <summary>
        /// A Surface2D ramp rising along +X with static walls, a registered
        /// dynamic obstacle and two prioritized modifiers.
        /// </summary>
        internal sealed class SurfaceScenario : IDisposable
        {
            internal const float Rise = 0.1f;
            internal readonly FlowFieldGridSpace Grid;
            internal readonly FlowFieldSurfaceBakeSettings Settings;
            internal readonly FlowFieldSurfaceData Surface;
            internal readonly FlowFieldSession Session;
            internal readonly SceneObjects Objects = new SceneObjects();
            internal readonly List<Collider> DynamicObstacles = new List<Collider>();
            internal readonly List<RotateModifier> Modifiers = new List<RotateModifier>();
            internal Vector3 Goal;
            internal float GoalInfluenceRadius;
            internal Vector3 DefaultDirection = Vector3.forward;
            private readonly FlowFieldRegressionFixture.EventFailures _failures;

            internal SurfaceScenario(int width, int depth)
            {
                Grid = FlowFieldGridSpace.FromCellGrid(Vector3.zero, width, depth, 1f);
                Settings = new FlowFieldSurfaceBakeSettings(
                    Grid,
                    new Bounds(new Vector3(width * 0.5f, 0f, depth * 0.5f), new Vector3(width, width * Rise * 2f + 4f, depth)),
                    1 << Layer,
                    45f,
                    0.5f);
                var result = new FlowFieldSurfaceBakeResult(Grid.CellCount);
                Vector3 normal = new Vector3(-Rise, 1f, 0f).normalized;
                for (int index = 0; index < Grid.CellCount; index++)
                {
                    Grid.FromFlatIndex(index, out int x, out _);
                    result.SetSurface(index, x * Rise, normal);
                }
                for (int index = 0; index < Grid.CellCount; index++)
                {
                    Grid.FromFlatIndex(index, out int x, out int z);
                    FlowFieldSurfaceBaker.BuildNeighborMask(Settings, result, index, x, z);
                }
                Surface = FlowFieldSurfaceData.FromRuntime(Settings, result, 1);
                Session = new FlowFieldSession(new FlowFieldFixedSurfaceSource(Surface));
                _failures = new FlowFieldRegressionFixture.EventFailures(Session);
                Session.Initialize(
                    FlowFieldBakeMode.RuntimeDynamic,
                    FlowFieldSessionSourceKind.SceneBuild,
                    FlowFieldBfsBackendPolicy.ManagedOnly,
                    null);
            }

            internal float HeightAt(float x) => Mathf.Floor(x) * Rise;

            internal BoxCollider StaticWall(float minX, float maxX, float minZ, float maxZ)
                => Objects.Box(
                    "Golden static wall",
                    new Vector3((minX + maxX) * 0.5f, HeightAt(maxX) + 0.5f, (minZ + maxZ) * 0.5f),
                    new Vector3(maxX - minX, Grid.Width * Rise + 4f, maxZ - minZ),
                    isStatic: true);

            internal BoxCollider DynamicObstacle(Vector3 cellCenter, Vector3 size)
            {
                BoxCollider box = Objects.Box("Golden dynamic obstacle", cellCenter, size);
                DynamicObstacles.Add(box);
                Assert.That(Session.RegisterObstacleCommand(box), Is.True);
                return box;
            }

            internal RotateModifier Modifier(Vector3 center, Vector3 size, int priority, float angle, float speed)
            {
                var modifier = new RotateModifier
                {
                    InfluenceCollider = Objects.Box("Golden modifier", center, size, isTrigger: true),
                    Priority = priority,
                    Angle = angle,
                    Speed = speed,
                };
                Modifiers.Add(modifier);
                Session.RegisterModifierCommand(modifier);
                return modifier;
            }

            internal FlowFieldSessionRequest Request()
                => FlowFieldSessionRequest.ForSceneBuild(
                    Settings,
                    1 << Layer,
                    1f,
                    0.5f,
                    0f,
                    false,
                    FlowFieldGoalPipeline.Resolve(Grid, null, true, Goal, GoalInfluenceRadius),
                    DefaultDirection,
                    FlowFieldDirtyFlags.None,
                    FlowFieldCellRect.Invalid,
                    FlowFieldCellRect.Invalid,
                    1024);

            internal void Build()
            {
                Physics.SyncTransforms();
                Session.Submit(Request());
                Session.Pump();
                Assert.That(Session.IsFaulted, Is.False, Session.LastError);
                Assert.That(Session.IsRebuilding, Is.False, "Managed Surface builds complete inside Pump.");
                Assert.That(Session.IsReady, Is.True, Session.LastError);
            }

            internal FieldHash Hash()
            {
                var hash = new FieldHash();
                for (int index = 0; index < Grid.CellCount; index++)
                {
                    bool sampled = Session.TrySample(Surface.GetCellCenter(Grid, index), out FlowFieldSample sample);
                    hash.Add(sampled, sample);
                }
                return hash;
            }

            public void Dispose()
            {
                Session.Dispose();
                Objects.Dispose();
                _failures.AssertNone();
            }
        }

        /// <summary>
        /// A Volume3D box with layer obstacles, optional registered obstacles
        /// and modifiers. Managed only (no compute shader).
        /// </summary>
        internal sealed class VolumeScenario : IDisposable
        {
            internal readonly FlowFieldGridSpace Grid;
            internal readonly Bounds WorldBounds;
            internal readonly FlowFieldVolumeSession Session = new FlowFieldVolumeSession();
            internal readonly SceneObjects Objects = new SceneObjects();
            internal readonly List<Collider> RegisteredObstacles = new List<Collider>();
            internal Vector3 Goal;
            internal float GoalInfluenceRadius;
            internal float ObstacleClearance = 0.1f;
            internal Vector3 DefaultDirection = Vector3.forward;
            private readonly FlowFieldRegressionFixture.EventFailures _failures;

            internal VolumeScenario(int width, int height, int depth)
            {
                Grid = FlowFieldGridSpace.FromVolumeCellGrid(Vector3.zero, width, height, depth, 1f);
                WorldBounds = new Bounds(new Vector3(width, height, depth) * 0.5f, new Vector3(width, height, depth));
                _failures = new FlowFieldRegressionFixture.EventFailures(Session);
                Session.Initialize(FlowFieldBakeMode.RuntimeDynamic);
            }

            internal BoxCollider LayerObstacle(Vector3 center, Vector3 size)
                => Objects.Box("Golden volume obstacle", center, size);

            internal BoxCollider RegisteredObstacle(Vector3 center, Vector3 size)
            {
                BoxCollider box = Objects.Box("Golden registered obstacle", center, size);
                RegisteredObstacles.Add(box);
                Assert.That(Session.RegisterObstacleCommand(box), Is.True);
                return box;
            }

            internal RotateModifier Modifier(Vector3 center, Vector3 size, int priority, float angle, float speed)
            {
                var modifier = new RotateModifier
                {
                    InfluenceCollider = Objects.Box("Golden volume modifier", center, size, isTrigger: true),
                    Priority = priority,
                    Angle = angle,
                    Speed = speed,
                };
                Session.RegisterModifierCommand(modifier);
                return modifier;
            }

            internal FlowFieldVolumeRequest Request()
                => new FlowFieldVolumeRequest(
                    Grid,
                    WorldBounds,
                    FlowFieldBakeMode.RuntimeDynamic,
                    1 << Layer,
                    ObstacleClearance,
                    true,
                    Goal,
                    GoalInfluenceRadius,
                    DefaultDirection,
                    1024,
                    null,
                    null);

            /// <summary>Submits and pumps to completion; returns the number of Pump calls.</summary>
            internal int Build(double budgetMilliseconds = 1000d, int maxPumps = 1000000)
            {
                Physics.SyncTransforms();
                Session.Submit(Request());
                return Finish(budgetMilliseconds, maxPumps);
            }

            internal int Finish(double budgetMilliseconds = 1000d, int maxPumps = 1000000)
            {
                int pumps = 0;
                while (Session.IsRebuilding && pumps < maxPumps)
                {
                    Session.Pump(budgetMilliseconds);
                    pumps++;
                }
                Assert.That(Session.IsFaulted, Is.False, Session.LastError);
                Assert.That(Session.IsRebuilding, Is.False, $"Volume build did not finish within {maxPumps} pumps.");
                Assert.That(Session.IsReady, Is.True, Session.LastError);
                return pumps;
            }

            internal FieldHash Hash()
            {
                var hash = new FieldHash();
                for (int index = 0; index < Grid.CellCount; index++)
                {
                    Grid.FromFlatIndex(index, out int x, out int y, out int z);
                    bool sampled = Session.TrySample(Grid.LocalToWorldCenter(x, y, z), out FlowFieldSample sample);
                    hash.Add(sampled, sample);
                }
                return hash;
            }

            public void Dispose()
            {
                Session.Dispose();
                Objects.Dispose();
                _failures.AssertNone();
            }
        }

        /// <summary>
        /// FNV-1a over quantized public samples. Quantization (1e-4) keeps the
        /// hash independent of last-bit float differences between platforms
        /// while still detecting any real direction or speed change.
        /// </summary>
        internal sealed class FieldHash
        {
            private const ulong Offset = 14695981039346656037UL;
            private const ulong Prime = 1099511628211UL;
            private ulong _value = Offset;
            internal int Count { get; private set; }
            internal int Unsampled { get; private set; }
            internal int Stopped { get; private set; }
            internal int Modified { get; private set; }

            internal void Add(bool sampled, in FlowFieldSample sample)
            {
                Count++;
                Mix(sampled ? 1 : 0);
                if (!sampled)
                {
                    Unsampled++;
                    return;
                }
                if (sample.SpeedMultiplier <= 0f)
                    Stopped++;
                else if (Mathf.Abs(sample.SpeedMultiplier - 1f) > 0.0001f)
                    Modified++;
                Mix(sample.HasCell ? 1 : 0);
                Mix(sample.HasSurface ? 1 : 0);
                Mix(Quantize(sample.Direction.x));
                Mix(Quantize(sample.Direction.y));
                Mix(Quantize(sample.Direction.z));
                Mix(Quantize(sample.SpeedMultiplier));
            }

            private static int Quantize(float value)
                => float.IsNaN(value) ? int.MinValue : Mathf.RoundToInt(value * 10000f);

            private void Mix(int value)
            {
                unchecked
                {
                    for (int shift = 0; shift < 32; shift += 8)
                    {
                        _value ^= (byte)(value >> shift);
                        _value *= Prime;
                    }
                }
            }

            public override string ToString() => $"{Count}:{_value:x16}";

            internal string Coverage => $"cells={Count} unsampled={Unsampled} stopped={Stopped} modified={Modified}";
        }
    }
}
