using System;
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using static Common.FlowField.Tests.FlowFieldGoalTestSupport;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// Additional Agent Types on a running Manager. A type with the Manager's
    /// own settings reads the same field; a wider type with a lower step
    /// blocks at least the default type's cells and keeps at most its links;
    /// obstacles, Goals, Modifiers and enabling reach every type, also an
    /// input a handler makes during another, and the default Goal and a cell
    /// regeneration reach them before any handler; a type's invalid value or
    /// a changed number of types faults the Manager, a retry brings back what
    /// a type missed, a Modifier included, a disabled Manager's retry only
    /// leaves the faults, a type found for older sessions names none, and
    /// StaticBaked refuses them.
    /// </summary>
    [TestFixture, Category("Agents")]
    internal sealed class AgentTypeTests : FlowFieldPlayTestBase
    {
        private const float ReadyTimeoutSeconds = 60f;
        private const int Samples = 32;

        [UnityTest, Timeout(300000)]
        public IEnumerator SampleRuntimeDynamicSameSettings() => SameSettings(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);

        [UnityTest, Timeout(300000)]
        public IEnumerator SampleBakedBaseSameSettings() => SameSettings(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.BakedBase);

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeRuntimeDynamicSameSettings() => SameSettings(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.RuntimeDynamic);

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeBakedBaseSameSettings() => SameSettings(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.BakedBase);

        [UnityTest, Timeout(300000)]
        public IEnumerator SampleWiderType() => WiderType(FlowFieldPlayScene.SampleScene);

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeWiderType() => WiderType(FlowFieldPlayScene.VolumeScene);

        [UnityTest, Timeout(300000)]
        public IEnumerator SampleInputsReachEveryType() => InputsReachEveryType(FlowFieldPlayScene.SampleScene);

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeInputsReachEveryType() => InputsReachEveryType(FlowFieldPlayScene.VolumeScene);

        [UnityTest, Timeout(300000)]
        public IEnumerator TypeFaultFaultsTheManager()
        {
            yield return FlowFieldPlayScene.Enter(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);
            FlowFieldManager manager = Manager;
            FlowFieldManagerConfig config = manager.CaptureConfig();
            Restart(manager, new AgentType("Small", config.Obstacles.Clearance, config.Surface.MaxSlope, config.Surface.MaxStepHeight));
            yield return WaitUntilAllReady(manager);
            Assert.That(manager.TryGetAgentType("Small", out FlowFieldAgentType small), Is.True);

            const string invalid = "Agent type \"Small\": Obstacle Clearance must be finite and not negative.";
            ExpectBuildFailed(Log, invalid);
            SetTypeClearance(manager, 0, -1f);
            Assert.That(manager.IsFaulted, Is.True, "A type's invalid value must fault the Manager at once.");
            Assert.That(manager.LastError, Is.EqualTo(invalid));
            Assert.That(manager.IsAgentTypeReady(small), Is.False, "A faulted Manager stops its agent types.");

            SetTypeClearance(manager, 0, config.Obstacles.Clearance);
            manager.RequestRebuild();
            yield return WaitUntilAllReady(manager);
            Assert.That(manager.IsAgentTypeReady(small), Is.True);

            const string locked = "The number and names of Additional Agent Types can change only after Release.";
            ExpectBuildFailed(Log, locked);
            SetTypes(manager,
                new AgentType("Small", config.Obstacles.Clearance, config.Surface.MaxSlope, config.Surface.MaxStepHeight),
                new AgentType("Large", config.Obstacles.Clearance + config.Grid.CellSize, config.Surface.MaxSlope, config.Surface.MaxStepHeight));
            Assert.That(manager.IsFaulted, Is.True);
            Assert.That(manager.LastError, Is.EqualTo(locked));

            manager.Release();
            Assert.That(manager.TryGetAgentType("Small", out _), Is.False, "Only a session's types are found.");
            manager.Init();
            yield return WaitUntilAllReady(manager);
            Assert.That(manager.AgentTypeCount, Is.EqualTo(3));
            Assert.That(manager.TryGetAgentType("Large", out FlowFieldAgentType large), Is.True);
            Assert.That(manager.IsAgentTypeReady(large), Is.True);
            Assert.That(manager.IsAgentTypeReady(small), Is.False, "A type found for the older sessions names none.");
            Assert.That(manager.TryGetAgentType("Small", out FlowFieldAgentType again), Is.True);
            Assert.That(again, Is.Not.EqualTo(small));
        }

        // A handler that runs while the Manager takes an input and makes its
        // own input finds every type with the first, so every type ends with
        // the handler's.
        [UnityTest, Timeout(300000)]
        public IEnumerator HandlerInputReachesEveryType()
        {
            yield return FlowFieldPlayScene.Enter(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);
            FlowFieldManager manager = Manager;
            FlowFieldManagerConfig config = manager.CaptureConfig();
            Restart(manager, new AgentType("Same", config.Obstacles.Clearance, config.Surface.MaxSlope, config.Surface.MaxStepHeight));
            yield return WaitUntilAllReady(manager);
            Bounds bounds = WorldBounds(manager);
            FlowFieldGoalHandle goal = manager.AddGoal(FlowFieldGoalKind.Shared, FlowFieldGoalRequest.Position(Point(bounds, 0.2f, 0.2f)));
            yield return WaitUntilAllReady(manager);

            Vector3 first = Point(bounds, 0.4f, 0.6f);
            Vector3 second = Point(bounds, 0.7f, 0.3f);
            bool moved = false;
            void MoveOnBuilding(FlowFieldRuntimeState state)
            {
                if (moved || state != FlowFieldRuntimeState.Building)
                    return;
                moved = true;
                manager.SetGoal(goal, FlowFieldGoalRequest.Position(second));
            }
            manager.StateChanged += MoveOnBuilding;
            try
            {
                Assert.That(manager.SetGoal(goal, FlowFieldGoalRequest.Position(first)), Is.True);
            }
            finally
            {
                manager.StateChanged -= MoveOnBuilding;
            }
            Assert.That(moved, Is.True, "The input never raised Building.");
            int id = goal.Id;
            Assert.That(manager.Session.Goals.Find(id).Request.WorldPosition, Is.EqualTo(second));
            Assert.That(manager.AgentSession(0).Goals.Find(id).Request.WorldPosition, Is.EqualTo(second),
                "The type kept the first input after the handler's.");
            yield return WaitUntilAllReady(manager);
            Assert.That(manager.TryGetAgentType("Same", out FlowFieldAgentType same), Is.True);
            AssertSamplesMatch(manager, same, bounds, goal);
        }

        // A type that misses a registration takes it again at the retry.
        [UnityTest, Timeout(300000)]
        public IEnumerator RetryBringsBackWhatATypeMissed()
        {
            yield return FlowFieldPlayScene.Enter(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);
            FlowFieldManager manager = Manager;
            FlowFieldManagerConfig config = manager.CaptureConfig();
            Restart(manager, new AgentType("Small", config.Obstacles.Clearance, config.Surface.MaxSlope, config.Surface.MaxStepHeight));
            yield return WaitUntilAllReady(manager);
            Bounds bounds = WorldBounds(manager);
            GameObject cube = FlowFieldObstacleProbe.CreateRegisteredCube(manager, "Missed Cube", Point(bounds, 0.5f, 0.5f), config.Grid.CellSize);
            Collider collider = cube.GetComponent<Collider>();
            yield return WaitUntilAllReady(manager);
            // The type loses the registration the Manager still holds.
            Assert.That(manager.AgentSession(0).UnregisterObstacleCommand(collider), Is.True);

            const string invalid = "Agent type \"Small\": Obstacle Clearance must be finite and not negative.";
            ExpectBuildFailed(Log, invalid);
            SetTypeClearance(manager, 0, -1f);
            Assert.That(manager.IsFaulted, Is.True);
            SetTypeClearance(manager, 0, config.Obstacles.Clearance);
            manager.RequestRebuild();
            yield return WaitUntilAllReady(manager);
            Assert.That(manager.AgentSession(0).Obstacles.Contains(collider), Is.True, "The retry did not bring the obstacle back.");
            Assert.That(Hash(AgentField(manager, 0)), Is.EqualTo(FlowFieldPlayScene.Hash(manager)));
        }

        // A type that missed a Modifier takes it again at the retry, checked
        // against the Priorities its other Modifiers have now: both changed
        // while the Manager was faulted, so no notice reached the type.
        [UnityTest, Timeout(300000)]
        public IEnumerator RetryTakesAMissedModifierWithNewPriorities()
        {
            yield return FlowFieldPlayScene.Enter(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);
            FlowFieldManager manager = Manager;
            FlowFieldManagerConfig config = manager.CaptureConfig();
            Restart(manager, new AgentType("Small", config.Obstacles.Clearance, config.Surface.MaxSlope, config.Surface.MaxStepHeight));
            yield return WaitUntilAllReady(manager);
            Bounds bounds = WorldBounds(manager);
            Vector3 size = new Vector3(bounds.size.x * 0.3f, bounds.size.y, bounds.size.z * 0.3f);
            FlowFieldProbeModifier kept = FlowFieldModifierScenarios.CreateProbe("Kept", Point(bounds, 0.3f, 0.3f), size);
            kept.Priority = 4101;
            FlowFieldProbeModifier missed = FlowFieldModifierScenarios.CreateProbe("Missed", Point(bounds, 0.7f, 0.7f), size);
            missed.Priority = 4102;
            Assert.That(manager.RegisterVectorModifier(kept), Is.True);
            Assert.That(manager.RegisterVectorModifier(missed), Is.True);
            yield return WaitUntilAllReady(manager);
            // The type loses a registration the Manager still holds.
            Assert.That(manager.AgentSession(0).UnregisterModifierCommand(missed), Is.True);

            const string invalid = "Agent type \"Small\": Obstacle Clearance must be finite and not negative.";
            ExpectBuildFailed(Log, invalid);
            SetTypeClearance(manager, 0, -1f);
            Assert.That(manager.IsFaulted, Is.True);
            kept.Priority = 4103;
            missed.Priority = 4101;
            SetTypeClearance(manager, 0, config.Obstacles.Clearance);
            manager.RequestRebuild();
            Assert.That(manager.IsFaulted, Is.False, manager.LastError);
            yield return WaitUntilAllReady(manager);
            Assert.That(manager.AgentSession(0).Modifiers.Contains(missed), Is.True, "The retry did not bring the Modifier back.");
            Assert.That(Hash(AgentField(manager, 0)), Is.EqualTo(FlowFieldPlayScene.Hash(manager)));
        }

        // A disabled Manager's retry only leaves the faults, every type's too;
        // enabling builds every type and brings back what one missed.
        [UnityTest, Timeout(300000)]
        public IEnumerator DisabledRetryLeavesTheFaults()
        {
            yield return FlowFieldPlayScene.Enter(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);
            FlowFieldManager manager = Manager;
            FlowFieldManagerConfig config = manager.CaptureConfig();
            Restart(manager, new AgentType("Small", config.Obstacles.Clearance, config.Surface.MaxSlope, config.Surface.MaxStepHeight));
            yield return WaitUntilAllReady(manager);
            Bounds bounds = WorldBounds(manager);
            GameObject cube = FlowFieldObstacleProbe.CreateRegisteredCube(manager, "Missed Cube", Point(bounds, 0.5f, 0.5f), config.Grid.CellSize);
            Collider collider = cube.GetComponent<Collider>();
            yield return WaitUntilAllReady(manager);
            Assert.That(manager.AgentSession(0).UnregisterObstacleCommand(collider), Is.True);

            ExpectBuildFailed(Log, "Agent type \"Small\": Obstacle Clearance must be finite and not negative.");
            SetTypeClearance(manager, 0, -1f);
            Assert.That(manager.IsFaulted, Is.True);
            SetTypeClearance(manager, 0, config.Obstacles.Clearance);
            manager.enabled = false;
            manager.RequestRebuild();
            Assert.That(manager.IsFaulted, Is.False, manager.LastError);
            Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Suspended));
            Assert.That(manager.AgentSession(0).State, Is.EqualTo(FlowFieldRuntimeState.Suspended));
            yield return null;
            Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Suspended), "A disabled Manager built.");

            manager.enabled = true;
            yield return WaitUntilAllReady(manager);
            Assert.That(manager.AgentSession(0).Obstacles.Contains(collider), Is.True, "Enabling did not bring the obstacle back.");
            Assert.That(Hash(AgentField(manager, 0)), Is.EqualTo(FlowFieldPlayScene.Hash(manager)));
        }

        // The default Goal and a cell regeneration reach every type before a
        // handler runs, as the other inputs do.
        [UnityTest, Timeout(300000)]
        public IEnumerator DefaultInputsReachEveryTypeBeforeHandlers()
        {
            yield return FlowFieldPlayScene.Enter(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);
            FlowFieldManager manager = Manager;
            FlowFieldManagerConfig config = manager.CaptureConfig();
            Restart(manager, new AgentType("Same", config.Obstacles.Clearance, config.Surface.MaxSlope, config.Surface.MaxStepHeight));
            yield return WaitUntilAllReady(manager);
            Bounds bounds = WorldBounds(manager);

            FlowFieldRuntimeState? typeState = null;
            void OnBuilding(FlowFieldRuntimeState state)
            {
                if (typeState == null && state == FlowFieldRuntimeState.Building)
                    typeState = manager.AgentSession(0).State;
            }
            manager.StateChanged += OnBuilding;
            try
            {
                manager.SetGoal(FlowFieldGoalRequest.Position(Point(bounds, 0.6f, 0.4f)));
                Assert.That(typeState, Is.EqualTo(FlowFieldRuntimeState.Building), "A handler ran before the type took the Goal.");
                yield return WaitUntilAllReady(manager);
                typeState = null;
                manager.NotifyCellsDirty();
                Assert.That(typeState, Is.EqualTo(FlowFieldRuntimeState.Building), "A handler ran before the type took the cells.");
            }
            finally
            {
                manager.StateChanged -= OnBuilding;
            }
            yield return WaitUntilAllReady(manager);
            Assert.That(manager.TryGetAgentType("Same", out FlowFieldAgentType same), Is.True);
            AssertSamplesMatch(manager, same, bounds, null);
        }

        [UnityTest, Timeout(300000)]
        public IEnumerator StaticBakedRefusesAgentTypes()
        {
            yield return FlowFieldPlayScene.Enter(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.StaticBaked);
            FlowFieldManager manager = Manager;
            manager.Release();
            SetTypes(manager, new AgentType("Small", 0f, 45f, 0.5f));
            var refused = Assert.Throws<InvalidOperationException>(() => manager.Init());
            Assert.That(refused.Message, Is.EqualTo("StaticBaked loads the Default agent type only; remove the Additional Agent Types."));
            Assert.That(manager.IsInitialized, Is.False);
        }

        // ----- Scenarios ------------------------------------------------------

        private IEnumerator SameSettings(string scene, FlowFieldBakeMode bakeMode)
        {
            yield return FlowFieldPlayScene.Enter(scene, bakeMode);
            FlowFieldManager manager = Manager;
            FlowFieldManagerConfig config = manager.CaptureConfig();
            Restart(manager, new AgentType("Same", config.Obstacles.Clearance, config.Surface.MaxSlope, config.Surface.MaxStepHeight));
            yield return WaitUntilAllReady(manager);

            Assert.That(manager.AgentTypeCount, Is.EqualTo(2));
            Assert.That(manager.TryGetAgentType("Same", out FlowFieldAgentType same), Is.True);
            Assert.That(same.IsDefault, Is.False);
            Assert.That(manager.TryGetAgentType(FlowFieldAgentType.DefaultName, out FlowFieldAgentType byName), Is.True);
            Assert.That(byName, Is.EqualTo(FlowFieldAgentType.Default));
            Assert.That(manager.TryGetAgentType("Missing", out _), Is.False);
            Assert.That(Hash(AgentField(manager, 0)), Is.EqualTo(FlowFieldPlayScene.Hash(manager)),
                "A type with the Manager's own settings must build the same field.");
            Bounds bounds = WorldBounds(manager);
            AssertSamplesMatch(manager, same, bounds, null);

            FlowFieldGoalHandle goal = manager.AddGoal(FlowFieldGoalKind.Shared, FlowFieldGoalRequest.Position(Point(bounds, 0.3f, 0.7f)));
            yield return WaitUntilAllReady(manager);
            Assert.That(manager.IsGoalReady(goal, same), Is.True);
            AssertSamplesMatch(manager, same, bounds, goal);
        }

        private IEnumerator WiderType(string scene)
        {
            yield return FlowFieldPlayScene.Enter(scene, FlowFieldBakeMode.RuntimeDynamic);
            FlowFieldManager manager = Manager;
            FlowFieldManagerConfig config = manager.CaptureConfig();
            float wider = config.Obstacles.Clearance + config.Grid.CellSize * 2f;
            Restart(manager, new AgentType("Wide", wider, config.Surface.MaxSlope, config.Surface.MaxStepHeight * 0.5f));
            yield return WaitUntilAllReady(manager);

            Assert.That(manager.TryGetPublishedField(out FlowFieldField normal), Is.True);
            FlowFieldField wide = AgentField(manager, 0);
            int normalBlocked = 0;
            int wideBlocked = 0;
            for (int index = 0; index < normal.Grid.CellCount; index++)
            {
                if (normal.Agent.Blocked[index])
                {
                    normalBlocked++;
                    Assert.That(wide.Agent.Blocked[index], Is.True, "The wider type left a cell the default type blocks open.");
                }
                if (wide.Agent.Blocked[index])
                    wideBlocked++;
                Assert.That(wide.Agent.GroundTopology[index] & ~normal.Agent.GroundTopology[index], Is.EqualTo(0u),
                    "The wider, lower-stepping type links cells the default type does not.");
            }
            Assert.That(wideBlocked, Is.GreaterThan(normalBlocked), "A wider clearance must block more cells.");
        }

        private IEnumerator InputsReachEveryType(string scene)
        {
            yield return FlowFieldPlayScene.Enter(scene, FlowFieldBakeMode.RuntimeDynamic);
            FlowFieldManager manager = Manager;
            FlowFieldManagerConfig config = manager.CaptureConfig();
            float cellSize = config.Grid.CellSize;
            Restart(manager, new AgentType("Wide", config.Obstacles.Clearance + cellSize, config.Surface.MaxSlope, config.Surface.MaxStepHeight));
            yield return WaitUntilAllReady(manager);
            Assert.That(manager.TryGetAgentType("Wide", out FlowFieldAgentType wide), Is.True);
            Bounds bounds = WorldBounds(manager);

            // A registered obstacle blocks its cell for both types, and a
            // type's new field is a new Revision.
            int cell = OpenCell(manager, bounds, 0.5f, 0.5f);
            Vector3 cubePoint = FlowFieldObstacleProbe.Position(manager, AgentField(manager, 0).Grid, cell);
            GameObject cube = FlowFieldObstacleProbe.CreateRegisteredCube(manager, "Agent Cube", cubePoint, cellSize);
            int revision = manager.Revision;
            yield return WaitUntilAllReady(manager);
            Assert.That(manager.TryGetPublishedField(out FlowFieldField normal), Is.True);
            Assert.That(normal.Agent.Blocked[cell], Is.True, "The default type misses the obstacle.");
            Assert.That(AgentField(manager, 0).Agent.Blocked[cell], Is.True, "The wider type misses the obstacle.");
            Assert.That(manager.Revision, Is.GreaterThan(revision));
            Assert.That(manager.UnregisterDynamicObstacle(cube.GetComponent<Collider>()), Is.True);
            UnityEngine.Object.DestroyImmediate(cube);
            Physics.SyncTransforms();
            yield return WaitUntilAllReady(manager);
            Assert.That(AgentField(manager, 0).Agent.Blocked[cell], Is.False, "The wider type kept the removed obstacle.");

            // A Goal builds for every type and goes from every type.
            FlowFieldGoalHandle goal = manager.AddGoal(FlowFieldGoalKind.Group, FlowFieldGoalRequest.Position(Point(bounds, 0.3f, 0.3f)));
            yield return WaitUntilAllReady(manager);
            Assert.That(manager.IsGoalReady(goal), Is.True);
            Assert.That(manager.IsGoalReady(goal, wide), Is.True);
            Assert.That(manager.RemoveGoal(goal), Is.True);
            Assert.That(manager.IsGoalReady(goal, wide), Is.False);

            // A Modifier applies to every type.
            Vector3 size = new Vector3(cellSize * 4f, bounds.size.y, cellSize * 4f);
            int probeCell = OpenCell(manager, bounds, 0.7f, 0.7f);
            Vector3 probePoint = FlowFieldObstacleProbe.Position(manager, AgentField(manager, 0).Grid, probeCell);
            Assert.That(manager.TryGetPublishedField(out normal), Is.True);
            float normalSpeed = normal.Goal.FinalSpeeds[probeCell];
            float wideSpeed = AgentField(manager, 0).Goal.FinalSpeeds[probeCell];
            Assert.That(normalSpeed, Is.GreaterThan(0f), "The probe's cell must move for the default type.");
            FlowFieldProbeModifier probe = FlowFieldModifierScenarios.CreateProbe("Agent Probe", probePoint, size);
            Assert.That(manager.RegisterVectorModifier(probe), Is.True);
            yield return WaitUntilAllReady(manager);
            Assert.That(manager.TryGetPublishedField(out normal), Is.True);
            Assert.That(normal.Goal.FinalSpeeds[probeCell], Is.EqualTo(normalSpeed * 0.5f).Within(0.0001f),
                "The Modifier misses the default type.");
            Assert.That(AgentField(manager, 0).Goal.FinalSpeeds[probeCell], Is.EqualTo(wideSpeed * 0.5f).Within(0.0001f),
                "The Modifier misses the wider type.");
            Assert.That(manager.UnregisterVectorModifier(probe), Is.True);

            // Disabling stops every type, and enabling builds them again.
            manager.enabled = false;
            Assert.That(manager.IsAgentTypeReady(wide), Is.False, "A disabled Manager stops its agent types.");
            manager.enabled = true;
            yield return WaitUntilAllReady(manager);
            Assert.That(manager.IsAgentTypeReady(wide), Is.True);
        }

        // ----- Steps ----------------------------------------------------------

        private readonly struct AgentType
        {
            internal readonly string Name;
            internal readonly float Clearance;
            internal readonly float MaxSlope;
            internal readonly float MaxStepHeight;

            internal AgentType(string name, float clearance, float maxSlope, float maxStepHeight)
            {
                Name = name;
                Clearance = clearance;
                MaxSlope = maxSlope;
                MaxStepHeight = maxStepHeight;
            }
        }

        // Release, the types as the Inspector would set them, and Init.
        private static void Restart(FlowFieldManager manager, params AgentType[] types)
        {
            manager.Release();
            SetTypes(manager, types);
            manager.Init();
        }

        private static void SetTypes(FlowFieldManager manager, params AgentType[] types)
        {
            var serialized = new SerializedObject(manager);
            SerializedProperty list = serialized.FindProperty(FlowFieldManager.PropertyNames.AgentTypes);
            list.arraySize = types.Length;
            for (int i = 0; i < types.Length; i++)
            {
                SerializedProperty element = list.GetArrayElementAtIndex(i);
                element.FindPropertyRelative(nameof(FlowFieldAgentTypeSettings.Name)).stringValue = types[i].Name;
                element.FindPropertyRelative(nameof(FlowFieldAgentTypeSettings.Clearance)).floatValue = types[i].Clearance;
                element.FindPropertyRelative(nameof(FlowFieldAgentTypeSettings.MaxSlope)).floatValue = types[i].MaxSlope;
                element.FindPropertyRelative(nameof(FlowFieldAgentTypeSettings.MaxStepHeight)).floatValue = types[i].MaxStepHeight;
            }
            serialized.ApplyModifiedProperties();
        }

        private static void SetTypeClearance(FlowFieldManager manager, int index, float clearance)
        {
            var serialized = new SerializedObject(manager);
            serialized.FindProperty(FlowFieldManager.PropertyNames.AgentTypes)
                .GetArrayElementAtIndex(index)
                .FindPropertyRelative(nameof(FlowFieldAgentTypeSettings.Clearance))
                .floatValue = clearance;
            serialized.ApplyModifiedProperties();
        }

        // Every session of the Manager is Ready, then the same a second later.
        private static IEnumerator WaitUntilAllReady(FlowFieldManager manager)
        {
            yield return WaitAllReady(manager);
            yield return FlowFieldPlayScene.WaitSeconds(1f);
            yield return WaitAllReady(manager);
        }

        private static IEnumerator WaitAllReady(FlowFieldManager manager)
        {
            float deadline = Time.realtimeSinceStartup + ReadyTimeoutSeconds;
            yield return null;
            while (!AllReady(manager))
            {
                if (Time.realtimeSinceStartup > deadline)
                    Assert.Fail("Timed out waiting for every agent type: " + FlowFieldPlayScene.Describe());
                yield return null;
            }
        }

        private static bool AllReady(FlowFieldManager manager)
        {
            if (manager.State != FlowFieldRuntimeState.Ready)
                return false;
            for (int i = 0; i < manager.AgentTypeCount - 1; i++)
            {
                if (manager.AgentSession(i).State != FlowFieldRuntimeState.Ready)
                    return false;
            }
            return true;
        }

        private static FlowFieldField AgentField(FlowFieldManager manager, int index)
        {
            Assert.That(manager.AgentSession(index).TryGetPublishedField(out FlowFieldField field), Is.True,
                "Agent type " + (index + 1) + " has no field: " + FlowFieldPlayScene.Describe());
            return field;
        }

        // An open cell of the wider type at full speed, from a point of the
        // bounds on in cell order; the default type blocks no more.
        private static int OpenCell(FlowFieldManager manager, Bounds bounds, float x, float z)
        {
            FlowFieldField wide = AgentField(manager, 0);
            Assert.That(FlowFieldSampler.TryLocate(wide, Point(bounds, x, z), out int start), Is.True);
            return FlowFieldObstacleProbe.PickOpenCell(wide, start, fullSpeed: true);
        }

        private static string Hash(FlowFieldField field) => FlowFieldFieldHash.Format(FlowFieldFieldHash.Compute(field));

        private static void AssertSamplesMatch(FlowFieldManager manager, FlowFieldAgentType type, Bounds bounds, FlowFieldGoalHandle? goal)
        {
            var random = new System.Random(11);
            for (int i = 0; i < Samples; i++)
            {
                Vector3 position = StormTests.Pick(bounds, random);
                FlowFieldSample expected;
                FlowFieldSample sample;
                bool sampled = goal.HasValue
                    ? manager.TrySample(position, goal.Value, out expected)
                    : manager.TrySample(position, out expected);
                bool typed = goal.HasValue
                    ? manager.TrySample(position, goal.Value, type, out sample)
                    : manager.TrySample(position, type, out sample);
                Assert.That(typed, Is.EqualTo(sampled));
                Assert.That(sample, Is.EqualTo(expected), "at " + position.ToString("F2"));
            }
        }
    }
}
