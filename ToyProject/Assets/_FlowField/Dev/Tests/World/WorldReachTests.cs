using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using static Common.FlowField.Tests.FlowFieldWorldTestScene;
using Object = UnityEngine.Object;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// Which areas a world Goal reaches and which areas a world holds. An area
    /// no chain of touching areas links to the Goal's area drops its field,
    /// and one walled off at its border has none, until a way opens again (in
    /// both space modes for the cut). A disabled Manager leaves the world as
    /// an unloaded scene's area does, giving back the world's Goals and
    /// obstacles, and joins again when enabled; one initialized while
    /// disabled joins only once enabled, so a Manager never active leaves no
    /// area. A move within the Goal's cell starts no epoch, the lattice of a
    /// world with joined areas stays as it is, and a refusal of an ended
    /// lifetime is forgotten.
    /// </summary>
    [TestFixture, Category("World")]
    internal sealed class WorldReachTests : FlowFieldPlayTestBase
    {
        [UnityTest, Timeout(300000)]
        public IEnumerator SurfaceCutOffAreaDropsItsField() => CutOffAreaDropsItsField(FlowFieldSpaceMode.Surface2D);

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeCutOffAreaDropsItsField() => CutOffAreaDropsItsField(FlowFieldSpaceMode.Volume3D);

        // The areas join against the way the Goal spreads (C, B, A), and an
        // area that touches none of them leaves, so a new epoch spreads from
        // A: C, which the epoch reaches last, keeps its field on every frame.
        [UnityTest, Timeout(300000)]
        public IEnumerator ReachableAreaKeepsItsFieldWhenAnotherLeaves()
        {
            yield return EnterEmptyPlayMode();
            FlowFieldWorld world = CreateWorld(FlowFieldSpaceMode.Surface2D);
            CreateGround();
            Vector3 size = new Vector3(10f, 4f, 10f);
            FlowFieldManager c = CreateManager("C", FlowFieldSpaceMode.Surface2D, new Vector3(20f, 0f, 0f), size, world);
            FlowFieldManager b = CreateManager("B", FlowFieldSpaceMode.Surface2D, new Vector3(10f, 0f, 0f), size, world);
            FlowFieldManager a = CreateManager("A", FlowFieldSpaceMode.Surface2D, Vector3.zero, size, world);
            FlowFieldManager far = CreateManager("Far", FlowFieldSpaceMode.Surface2D, new Vector3(0f, 0f, 12f), new Vector3(4f, 4f, 4f), world);
            yield return WaitQuiet(a, b, c, far);
            FlowFieldGoalHandle goal = world.AddGoal(FlowFieldGoalKind.Shared, FlowFieldGoalRequest.Position(new Vector3(1.5f, 0f, 5.5f)));
            yield return WaitFor(() => c.IsGoalReady(goal), "C builds the world Goal");
            yield return WaitQuiet(a, b, c, far);
            Assert.That(far.IsGoalReady(goal), Is.False, "An area that touches none has no field.");

            Object.Destroy(far.gameObject);
            for (int frame = 0; frame < 60; frame++)
            {
                yield return null;
                Assert.That(c.IsGoalReady(goal), Is.True, "C lost its field at frame " + frame + " while the epoch spread.");
            }
            Assert.That(world.IsGoalReady(goal), Is.True);
        }

        // A walled off at B's border: B has no field; the wall goes and B
        // reaches the Goal.
        [UnityTest, Timeout(300000)]
        public IEnumerator WalledAreaHasNoField()
        {
            yield return EnterEmptyPlayMode();
            FlowFieldWorld world = CreateWorld(FlowFieldSpaceMode.Surface2D);
            CreateGround();
            GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = "Border Wall";
            wall.layer = ObstacleLayer;
            wall.transform.position = new Vector3(10.5f, 1f, 5f);
            wall.transform.localScale = new Vector3(1f, 2f, 12f);
            Physics.SyncTransforms();
            FlowFieldManager a = CreateManager("A", FlowFieldSpaceMode.Surface2D, Vector3.zero, new Vector3(10f, 4f, 10f), world);
            FlowFieldManager b = CreateManager("B", FlowFieldSpaceMode.Surface2D, new Vector3(10f, 0f, 0f), new Vector3(10f, 4f, 10f), world);
            yield return WaitQuiet(a, b);
            FlowFieldGoalHandle goal = world.AddGoal(FlowFieldGoalKind.Shared, FlowFieldGoalRequest.Position(new Vector3(1.5f, 0f, 5.5f)));

            // The epoch spreads without reaching B, which then has seeds with no cell.
            yield return WaitFor(() => a.IsGoalReady(goal) && b.Session.Goals.Find(goal.Id)?.Seeds != null, "the epoch has spread");
            Assert.That(b.Session.Goals.Find(goal.Id).Seeds.Count, Is.EqualTo(0));
            Assert.That(b.IsGoalReady(goal), Is.False, "A walled off area has no field.");
            Assert.That(b.Session.GoalField(goal.Id), Is.Null);
            Assert.That(world.IsGoalReady(goal), Is.False);

            Bounds bounds = wall.GetComponent<Collider>().bounds;
            Object.DestroyImmediate(wall);
            Physics.SyncTransforms();
            a.NotifyObstacleRegionDirty(bounds);
            b.NotifyObstacleRegionDirty(bounds);
            yield return WaitFor(() => world.IsGoalReady(goal), "B reaches the Goal once the wall is gone");
            FlowFieldField field = b.Session.GoalField(goal.Id);
            Assert.That(field.Goal.Distances[field.Grid.ToFlatIndex(9, 5)], Is.Not.EqualTo(FlowFieldGoalLayer.Unreachable));
        }

        // B is disabled: it leaves as an unloaded scene's area does and gives
        // back the world's Goals and obstacles; what the world removes
        // meanwhile stays gone when B is enabled and joins again.
        [UnityTest, Timeout(300000)]
        public IEnumerator DisabledAreaLeavesTheWorld()
        {
            yield return EnterEmptyPlayMode();
            FlowFieldWorld world = CreateWorld(FlowFieldSpaceMode.Surface2D);
            CreateGround();
            FlowFieldManager a = CreateManager("A", FlowFieldSpaceMode.Surface2D, Vector3.zero, new Vector3(10f, 4f, 10f), world);
            FlowFieldManager b = CreateManager("B", FlowFieldSpaceMode.Surface2D, new Vector3(10f, 0f, 0f), new Vector3(10f, 4f, 10f), world);
            yield return WaitQuiet(a, b);
            FlowFieldGoalHandle goal = world.AddGoal(FlowFieldGoalKind.Shared, FlowFieldGoalRequest.Position(new Vector3(15.5f, 0f, 5.5f)));
            FlowFieldGoalHandle other = world.AddGoal(FlowFieldGoalKind.Group, FlowFieldGoalRequest.Position(new Vector3(12.5f, 0f, 2.5f)));
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.layer = ObstacleLayer;
            cube.transform.position = new Vector3(17.5f, 1f, 8.5f);
            cube.transform.localScale = new Vector3(0.6f, 2f, 0.6f);
            Physics.SyncTransforms();
            Collider collider = cube.GetComponent<Collider>();
            Assert.That(world.RegisterDynamicObstacle(collider), Is.True);
            Collider stays = CreateObstacle(new Vector3(12.5f, 1f, 8.5f));
            Assert.That(world.RegisterDynamicObstacle(stays), Is.True);
            // B's own input stays with it.
            Collider own = CreateObstacle(new Vector3(18.5f, 1f, 2.5f));
            Assert.That(b.RegisterDynamicObstacle(own), Is.True);
            FlowFieldGoalHandle ownGoal = b.AddGoal(FlowFieldGoalKind.Shared, FlowFieldGoalRequest.Position(new Vector3(16.5f, 0f, 7.5f)));
            yield return WaitFor(() => world.IsGoalReady(goal) && world.IsGoalReady(other) && b.IsGoalReady(ownGoal), "every Goal builds");
            yield return WaitQuiet(a, b);

            b.enabled = false;
            Assert.That(world.Managers, Is.EqualTo(new[] { a }), "A disabled area leaves the world.");
            Assert.That(world.TryGetArea(new Vector3(15.5f, 0f, 5.5f), out _), Is.False);
            Assert.That(b.Session.Goals.Find(goal.Id), Is.Null, "A disabled area gives back the world Goals.");
            Assert.That(b.IsDynamicObstacleRegistered(collider) || b.IsDynamicObstacleRegistered(stays), Is.False,
                "A disabled area gives back the world obstacles.");
            Assert.That(b.IsDynamicObstacleRegistered(own), Is.True, "A disabled area keeps its own obstacle.");
            Assert.That(b.Session.Goals.Find(ownGoal.Id), Is.Not.Null, "A disabled area keeps its own Goal.");
            // A leads out of its own edge toward the Goal, as without B's scene.
            yield return WaitFor(() =>
            {
                FlowFieldField field = a.Session.GoalField(goal.Id);
                int edge = field?.Grid.ToFlatIndex(9, 5) ?? -1;
                return field != null && FlowFieldNextCell.IsPortal(field.Goal.NextCells[edge]) && field.Goal.Distances[edge] == 6;
            }, "A leads to its border by the straight-line estimate");

            Assert.That(world.RemoveGoal(other), Is.True);
            Assert.That(world.UnregisterDynamicObstacle(collider), Is.True);
            b.enabled = true;
            Assert.That(world.Managers, Is.EquivalentTo(new[] { a, b }), "Enabling joins the world again.");
            yield return WaitFor(() => world.IsGoalReady(goal) && b.IsGoalReady(ownGoal), "B builds the Goals again");
            Assert.That(b.Session.Goals.Find(other.Id), Is.Null, "The Goal the world removed meanwhile stays gone.");
            Assert.That(b.IsDynamicObstacleRegistered(collider), Is.False, "The obstacle the world removed meanwhile stays gone.");
            Assert.That(b.IsDynamicObstacleRegistered(stays), Is.True, "B takes the world obstacle back.");
            Assert.That(b.IsDynamicObstacleRegistered(own), Is.True);
            Assert.That(world.TrySample(new Vector3(18.5f, 0f, 1.5f), goal, out FlowFieldSample sample), Is.True);
            Assert.That(sample.SpeedMultiplier, Is.GreaterThan(0f));
        }

        // A Manager initialized while disabled joins once enabled; one whose
        // GameObject is never active joins never and leaves nothing behind.
        [UnityTest, Timeout(300000)]
        public IEnumerator InitWhileDisabledJoinsWhenEnabled()
        {
            yield return EnterEmptyPlayMode();
            FlowFieldWorld world = CreateWorld(FlowFieldSpaceMode.Surface2D);
            CreateGround();
            FlowFieldManager a = CreateManager("A", FlowFieldSpaceMode.Surface2D, Vector3.zero, new Vector3(10f, 4f, 10f), world);
            FlowFieldManager b = CreateManager("B", FlowFieldSpaceMode.Surface2D, new Vector3(10f, 0f, 0f), new Vector3(10f, 4f, 10f), world);
            yield return WaitQuiet(a, b);

            b.Release();
            b.enabled = false;
            b.Init();
            Assert.That(world.Managers, Is.EqualTo(new[] { a }), "A Manager initialized while disabled joins only when enabled.");
            b.enabled = true;
            Assert.That(world.Managers, Is.EquivalentTo(new[] { a, b }));
            yield return WaitQuiet(a, b);

            var inactive = new GameObject("Never Active");
            inactive.SetActive(false);
            FlowFieldManager never = AddManager(inactive, new Vector3(0f, 0f, 10f), new Vector3(10f, 4f, 10f), world);
            never.Init();
            Assert.That(world.Managers, Is.EquivalentTo(new[] { a, b }), "A Manager that is never active joins no world.");
            // Unity never calls OnDestroy here, so the test releases it itself.
            never.Release();
            Object.DestroyImmediate(inactive);
            yield return null;
            Assert.That(world.Managers, Is.EquivalentTo(new[] { a, b }));
        }

        // A move within the Goal's lattice cell starts the same searches, so
        // no epoch; a move to another cell starts one.
        [UnityTest, Timeout(300000)]
        public IEnumerator MoveWithinTheGoalCellStartsNoEpoch()
        {
            yield return EnterEmptyPlayMode();
            FlowFieldWorld world = CreateWorld(FlowFieldSpaceMode.Surface2D);
            CreateGround();
            FlowFieldManager a = CreateManager("A", FlowFieldSpaceMode.Surface2D, Vector3.zero, new Vector3(10f, 4f, 10f), world);
            FlowFieldManager b = CreateManager("B", FlowFieldSpaceMode.Surface2D, new Vector3(10f, 0f, 0f), new Vector3(10f, 4f, 10f), world);
            yield return WaitQuiet(a, b);
            FlowFieldGoalHandle goal = world.AddGoal(FlowFieldGoalKind.Shared, FlowFieldGoalRequest.Position(new Vector3(1.5f, 0f, 5.5f)));
            yield return WaitFor(() => world.IsGoalReady(goal), "the world Goal builds");
            yield return WaitQuiet(a, b);
            int epoch = SeedEpoch(b, goal);
            yield return Frames(40);
            Assert.That(SeedEpoch(b, goal), Is.EqualTo(epoch), "The world never settled.");
            int revision = a.Revision + b.Revision;

            Assert.That(world.SetGoal(goal, FlowFieldGoalRequest.Position(new Vector3(1.9f, 0f, 5.1f))), Is.True);
            yield return Frames(40);
            Assert.That(SeedEpoch(b, goal), Is.EqualTo(epoch), "A move within the Goal's cell started an epoch.");
            Assert.That(a.Revision + b.Revision, Is.EqualTo(revision), "A move within the Goal's cell built a field.");

            Assert.That(world.SetGoal(goal, FlowFieldGoalRequest.Position(new Vector3(2.5f, 0f, 5.5f))), Is.True);
            yield return WaitFor(() => SeedEpoch(b, goal) > epoch, "a move to another cell starts an epoch");
        }

        // An edit of the lattice under a joined area is undone with a
        // warning; with no area joined it applies.
        [UnityTest, Timeout(300000)]
        public IEnumerator LatticeEditWaitsUntilNoAreaIsJoined()
        {
            yield return EnterEmptyPlayMode();
            FlowFieldWorld world = CreateWorld(FlowFieldSpaceMode.Surface2D);
            world.name = "Lattice World";
            CreateGround();
            FlowFieldManager a = CreateManager("A", FlowFieldSpaceMode.Surface2D, Vector3.zero, new Vector3(10f, 4f, 10f), world);
            yield return WaitQuiet(a);

            string pattern = "^" + Regex.Escape(
                "[FlowField] The Space Mode, Cell Size and Lattice Origin of the FlowField World Lattice World can change only while no area is joined.") + "$";
            Log.Expect(pattern);
            LogAssert.Expect(LogType.Warning, new Regex(pattern));
            var serialized = new SerializedObject(world);
            serialized.FindProperty("_cellSize").floatValue = 0.5f;
            serialized.ApplyModifiedProperties();
            Assert.That(world.CellSize, Is.EqualTo(1f), "The edit moved the lattice under a joined area.");

            Log.Expect(pattern);
            LogAssert.Expect(LogType.Warning, new Regex(pattern));
            serialized.Update();
            serialized.FindProperty("_latticeOrigin").vector3Value = new Vector3(0.5f, 0f, 0.5f);
            serialized.ApplyModifiedProperties();
            Assert.That(world.LatticeOrigin, Is.EqualTo(Vector3.zero), "The edit moved the lattice under a joined area.");

            a.Release();
            serialized.Update();
            serialized.FindProperty("_cellSize").floatValue = 0.5f;
            serialized.ApplyModifiedProperties();
            Assert.That(world.CellSize, Is.EqualTo(0.5f), "With no area joined the edit applies.");
        }

        // A refuses the world's Modifier (its own one has the Priority); the
        // refusal goes with A's session.
        [UnityTest, Timeout(300000)]
        public IEnumerator RefusalOfAnEndedLifetimeIsForgotten()
        {
            yield return EnterEmptyPlayMode();
            FlowFieldWorld world = CreateWorld(FlowFieldSpaceMode.Surface2D);
            CreateGround();
            FlowFieldManager a = CreateManager("A", FlowFieldSpaceMode.Surface2D, Vector3.zero, new Vector3(10f, 4f, 10f), world);
            FlowFieldManager b = CreateManager("B", FlowFieldSpaceMode.Surface2D, new Vector3(10f, 0f, 0f), new Vector3(10f, 4f, 10f), world);
            yield return WaitQuiet(a, b);
            Vector3 size = new Vector3(4f, 4f, 4f);
            FlowFieldProbeModifier own = FlowFieldModifierScenarios.CreateProbe("Own", new Vector3(5f, 0f, 5f), size);
            own.Priority = 4301;
            Assert.That(a.RegisterVectorModifier(own), Is.True);
            FlowFieldProbeModifier shared = FlowFieldModifierScenarios.CreateProbe("Shared", new Vector3(10f, 0f, 5f), size);
            shared.Priority = 4301;
            LogAssert.Expect(LogType.Exception, new Regex("duplicated"));
            Assert.That(world.RegisterVectorModifier(shared), Is.True);
            Assert.That(RefusalCount(world), Is.EqualTo(1));

            // Unregistered and registered again in the same lifetime: A is
            // asked again and refuses again, with a new log.
            Assert.That(world.UnregisterVectorModifier(shared), Is.True);
            Assert.That(RefusalCount(world), Is.EqualTo(0), "A refusal of a registration the world gave up stayed.");
            LogAssert.Expect(LogType.Exception, new Regex("duplicated"));
            Assert.That(world.RegisterVectorModifier(shared), Is.True);
            Assert.That(RefusalCount(world), Is.EqualTo(1));

            Object.Destroy(a.gameObject);
            yield return Frames(3);
            Assert.That(RefusalCount(world), Is.EqualTo(0), "A refusal of a disposed session stayed.");
        }

        // A–B–C with the Goal in A: B's scene unloads, so C is cut off and
        // drops its field; B comes back and C reaches the Goal again.
        private IEnumerator CutOffAreaDropsItsField(FlowFieldSpaceMode mode)
        {
            yield return EnterEmptyPlayMode();
            bool volume = mode == FlowFieldSpaceMode.Volume3D;
            FlowFieldWorld world = CreateWorld(mode);
            if (!volume)
                CreateGround();
            Vector3 size = volume ? new Vector3(8f, 6f, 8f) : new Vector3(10f, 4f, 10f);
            float width = size.x;
            FlowFieldManager a = CreateManager("A", mode, Vector3.zero, size, world);
            FlowFieldManager b = CreateManager("B", mode, new Vector3(width, 0f, 0f), size, world);
            FlowFieldManager c = CreateManager("C", mode, new Vector3(width * 2f, 0f, 0f), size, world);
            yield return WaitQuiet(a, b, c);
            float y = volume ? 1.5f : 0f;
            FlowFieldGoalHandle goal = world.AddGoal(FlowFieldGoalKind.Shared, FlowFieldGoalRequest.Position(new Vector3(1.5f, y, 4.5f)));
            yield return WaitFor(() => world.IsGoalReady(goal), "every area builds the world Goal");
            yield return WaitQuiet(a, b, c);

            Object.Destroy(b.gameObject);
            yield return WaitFor(() => !c.IsGoalReady(goal), "C drops the field no way reaches");
            Assert.That(c.Session.GoalField(goal.Id), Is.Null);
            Assert.That(c.TrySample(new Vector3(width * 2f + 4.5f, y, 4.5f), goal, out _), Is.False);
            Assert.That(a.IsGoalReady(goal), Is.True, "The area holding the Goal keeps its field.");
            Assert.That(world.IsGoalReady(goal), Is.False);

            b = CreateManager("B", mode, new Vector3(width, 0f, 0f), size, world);
            yield return WaitFor(() => world.IsGoalReady(goal), "C reaches the Goal through B again");
            FlowFieldField field = c.Session.GoalField(goal.Id);
            int far = volume
                ? field.Grid.ToFlatIndex(field.Grid.Width - 1, 1, 4)
                : field.Grid.ToFlatIndex(field.Grid.Width - 1, 4);
            Assert.That(field.Goal.Distances[far], Is.Not.EqualTo(FlowFieldGoalLayer.Unreachable));
        }

        // ----- Steps ----------------------------------------------------------

        private static Collider CreateObstacle(Vector3 position)
        {
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.layer = ObstacleLayer;
            cube.transform.position = position;
            cube.transform.localScale = new Vector3(0.6f, 2f, 0.6f);
            Physics.SyncTransforms();
            return cube.GetComponent<Collider>();
        }

        private static int SeedEpoch(FlowFieldManager manager, FlowFieldGoalHandle goal)
            => manager.Session.Goals.Find(goal.Id).Seeds.Epoch;

        private static int RefusalCount(FlowFieldWorld world)
        {
            FieldInfo refused = typeof(FlowFieldWorldRegistrations).GetField("_refused", BindingFlags.NonPublic | BindingFlags.Instance);
            return ((ICollection<(FlowFieldSession, int, object)>)refused.GetValue(world.Areas.Registrations)).Count;
        }

        // CreateManager's settings on a GameObject the caller keeps inactive.
        private static FlowFieldManager AddManager(GameObject gameObject, Vector3 position, Vector3 size, FlowFieldWorld world)
        {
            gameObject.transform.position = position;
            FlowFieldManager manager = gameObject.AddComponent<FlowFieldManager>();
            var serialized = new SerializedObject(manager);
            serialized.FindProperty(FlowFieldManager.PropertyNames.SpaceMode).enumValueIndex = (int)FlowFieldSpaceMode.Surface2D;
            serialized.FindProperty(FlowFieldManager.PropertyNames.BakeMode).enumValueIndex = (int)FlowFieldBakeMode.RuntimeDynamic;
            serialized.FindProperty(FlowFieldManager.PropertyNames.BakeBoundsLocal).boundsValue
                = new Bounds(new Vector3(size.x * 0.5f, -2f + size.y * 0.5f, size.z * 0.5f), size);
            serialized.FindProperty(FlowFieldManager.PropertyNames.CellSize).floatValue = CellSize;
            serialized.FindProperty(FlowFieldManager.PropertyNames.GroundLayer).intValue = 1 << GroundLayer;
            serialized.FindProperty(FlowFieldManager.PropertyNames.ObstacleLayer).intValue = 1 << ObstacleLayer;
            serialized.FindProperty(FlowFieldManager.PropertyNames.World).objectReferenceValue = world;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return manager;
        }

        private static IEnumerator Frames(int count)
        {
            for (int i = 0; i < count; i++)
                yield return null;
        }

        private static IEnumerator WaitFor(Func<bool> condition, string what)
        {
            float deadline = Time.realtimeSinceStartup + SettleTimeoutSeconds;
            while (!condition())
            {
                if (Time.realtimeSinceStartup > deadline)
                    Assert.Fail("Timed out waiting until " + what + ": " + FlowFieldPlayScene.Describe());
                yield return null;
            }
        }

        // Every Manager Ready, then no Revision change for a second.
        private static IEnumerator WaitQuiet(params FlowFieldManager[] managers)
        {
            float deadline = Time.realtimeSinceStartup + SettleTimeoutSeconds;
            long revision = -1;
            float quietSince = 0f;
            while (true)
            {
                if (Time.realtimeSinceStartup > deadline)
                    Assert.Fail("Timed out settling: " + FlowFieldPlayScene.Describe());
                bool ready = true;
                long now = 0;
                for (int i = 0; i < managers.Length; i++)
                {
                    ready &= managers[i].State == FlowFieldRuntimeState.Ready;
                    now = now * 7919 + managers[i].Revision;
                }
                if (!ready || now != revision)
                {
                    revision = now;
                    quietSince = Time.realtimeSinceStartup;
                }
                else if (Time.realtimeSinceStartup - quietSince > 1f)
                {
                    yield break;
                }
                yield return null;
            }
        }
    }
}
