using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Common.FlowField.Editor
{
    /// <summary>
    /// The world bake, in Edit Mode: every Manager of the world in the open
    /// scenes becomes an area with a session per agent type that builds from
    /// Physics, as RuntimeDynamic does. Each Bake Goal is searched across them
    /// the way a world Goal is in Play Mode, and the steps from every area's
    /// outer cells go into the world asset with the areas' lattice boxes. It
    /// runs to the end at once, under a progress bar, and changes no scene.
    /// </summary>
    internal static class FlowFieldWorldBaker
    {
        // Rounds of seeds and builds one Goal's search may take.
        private const int MaxRounds = 4096;
        // Pumps that finish a session's owed builds; Surface2D begins one per pump.
        private const int MaxPumps = 64;

        /// <summary>
        /// Bakes the world's Bake Goals across the areas of the open scenes
        /// into the world asset, with Undo, and saves it when it is an asset.
        /// </summary>
        /// <returns>False with the reason when the bake could not run; the asset keeps its bake.</returns>
        internal static bool TryBake(FlowFieldWorld world, out string message)
        {
            if (world == null)
                throw new ArgumentNullException(nameof(world));
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                message = "The world bake runs in Edit Mode only.";
                return false;
            }
            IReadOnlyList<Vector3> positions = world.BakeGoals;
            if (positions.Count == 0)
            {
                message = "Add a Bake Goal first.";
                return false;
            }

            Physics.SyncTransforms();
            FlowFieldWorldAreas areas = FlowFieldWorldAreas.CreateDetached(world);
            var members = new List<BakeArea>();
            try
            {
                if (!TryJoinAreas(world, areas, members, out message) || !TrySettle(members, out message))
                    return false;
                var baked = new List<FlowFieldWorldBakedGoal>();
                var cells = new HashSet<(int, int, int)>();
                for (int g = 0; g < positions.Count; g++)
                {
                    EditorUtility.DisplayProgressBar("FlowField World Bake", $"Bake Goal {g + 1} of {positions.Count}", (float)g / positions.Count);
                    Vector3 position = positions[g];
                    if (!FlowFieldGridSpace.IsFinite(position) || areas.Find(position) == null)
                    {
                        message = $"Bake Goal {g + 1} at {position} lies in no area of the open scenes.";
                        return false;
                    }
                    FlowFieldWorldAreas.LatticeCell(world, position, out int x, out int y, out int z);
                    if (!cells.Add((x, y, z)))
                        continue;
                    int id = areas.Goals.Add(FlowFieldGoalKind.Shared, FlowFieldGoalRequest.Position(position));
                    if (!TrySearch(areas, members, out message))
                    {
                        message = $"Bake Goal {g + 1}: {message}";
                        return false;
                    }
                    Collect(areas, id, x, y, z, baked);
                    areas.Goals.Remove(id);
                }

                var boxes = new List<FlowFieldLatticeBox>(areas.All.Count);
                for (int a = 0; a < areas.All.Count; a++)
                    boxes.Add(areas.All[a].Box);
                Undo.RecordObject(world, "Bake FlowField World");
                world.Bake = FlowFieldWorldBake.Create(world, boxes, baked.ToArray());
                Save(world);
                message = $"Baked {cells.Count} Goal cell(s) across {members.Count} area(s).";
                return true;
            }
            finally
            {
                for (int i = 0; i < members.Count; i++)
                    members[i].Dispose();
                EditorUtility.ClearProgressBar();
            }
        }

        /// <summary>Removes the world's bake, with Undo.</summary>
        internal static void Clear(FlowFieldWorld world)
        {
            Undo.RecordObject(world, "Clear FlowField World Bake");
            world.Bake = null;
            Save(world);
        }

        /// <summary>
        /// What the Inspector shows of the world's bake, with a warning when
        /// it is unused or an area of the open scenes is not in it as it is now.
        /// </summary>
        internal static string Describe(FlowFieldWorld world, out MessageType type)
        {
            FlowFieldWorldBake bake = world.Bake;
            type = MessageType.Info;
            if (bake == null || bake.GoalCount == 0)
                return "Not baked: a world Goal no loaded area holds leads by a straight-line estimate.";
            if (!bake.Matches(world))
            {
                type = MessageType.Warning;
                return "The bake ran with another Space Mode, Cell Size or Lattice Origin and is unused; bake again.";
            }
            FlowFieldManager[] managers = UnityEngine.Object.FindObjectsByType<FlowFieldManager>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);
            for (int i = 0; i < managers.Length; i++)
            {
                FlowFieldManager manager = managers[i];
                if (manager.World == world
                    && manager.TryGetLayout(out _, out FlowFieldGridSpace grid)
                    && FlowFieldWorldAreas.TryLatticeBox(world, grid, out FlowFieldLatticeBox box, out _)
                    && !bake.HasArea(in box))
                {
                    type = MessageType.Warning;
                    return $"The area of {manager.name} is not in the bake as it is now; bake again.";
                }
            }
            var cells = new HashSet<(int, int, int)>();
            var names = new List<string>();
            for (int i = 0; i < bake.GoalCount; i++)
            {
                FlowFieldWorldBakedGoal goal = bake.Goal(i);
                cells.Add((goal.GoalX, goal.GoalY, goal.GoalZ));
                if (!names.Contains(goal.AgentType))
                    names.Add(goal.AgentType);
            }
            return $"Baked {cells.Count} Goal cell(s) for {string.Join(", ", names)}.";
        }

        private static void Save(FlowFieldWorld world)
        {
            EditorUtility.SetDirty(world);
            if (AssetDatabase.Contains(world))
                AssetDatabase.SaveAssetIfDirty(world);
        }

        // Every Manager of the world in the open scenes, checked as Init
        // checks it, joined in lattice order.
        private static bool TryJoinAreas(
            FlowFieldWorld world,
            FlowFieldWorldAreas areas,
            List<BakeArea> members,
            out string message)
        {
            var found = new List<(FlowFieldManager Manager, FlowFieldGridSpace Grid, FlowFieldLatticeBox Box)>();
            FlowFieldManager[] managers = UnityEngine.Object.FindObjectsByType<FlowFieldManager>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);
            for (int i = 0; i < managers.Length; i++)
            {
                FlowFieldManager manager = managers[i];
                if (manager.World != world)
                    continue;
                if (!manager.TryValidateBakeSettings(out string reason)
                    || !manager.TryValidateAgentTypes(out reason)
                    || !manager.TryGetLayout(out _, out FlowFieldGridSpace grid)
                    || !FlowFieldWorldAreas.TryLatticeBox(world, grid, out FlowFieldLatticeBox box, out reason))
                {
                    message = $"{manager.name}: {reason ?? "the settings make no grid."}";
                    return false;
                }
                found.Add((manager, grid, box));
            }
            if (found.Count == 0)
            {
                message = "No Manager of this world is in the open scenes.";
                return false;
            }
            found.Sort((a, b) =>
            {
                int order = a.Box.MinX.CompareTo(b.Box.MinX);
                if (order == 0)
                    order = a.Box.MinY.CompareTo(b.Box.MinY);
                return order != 0 ? order : a.Box.MinZ.CompareTo(b.Box.MinZ);
            });
            for (int i = 0; i < found.Count; i++)
            {
                FlowFieldWorldArea other = areas.Overlapping(found[i].Box, null);
                if (other != null)
                {
                    message = $"{found[i].Manager.name} overlaps the area of {other.Member.Name}.";
                    return false;
                }
                var member = new BakeArea(found[i].Manager);
                members.Add(member);
                areas.Join(member, found[i].Grid, found[i].Box);
            }
            message = null;
            return true;
        }

        private static bool TrySettle(List<BakeArea> members, out string message)
        {
            for (int i = 0; i < members.Count; i++)
            {
                if (!members[i].TrySettle(out message))
                    return false;
            }
            message = null;
            return true;
        }

        // Seeds and builds in turn until the Goal's search has spread as far
        // as it reaches.
        private static bool TrySearch(FlowFieldWorldAreas areas, List<BakeArea> members, out string message)
        {
            for (int round = 0; round < MaxRounds; round++)
            {
                areas.Goals.Update();
                bool idle = true;
                for (int i = 0; i < members.Count; i++)
                    idle &= members[i].IsIdle;
                if (idle && areas.Goals.IsSettled)
                {
                    message = null;
                    return true;
                }
                if (!TrySettle(members, out message))
                    return false;
            }
            message = "the search did not settle.";
            return false;
        }

        // The steps of every reached outer cell, one baked Goal per agent type.
        private static void Collect(
            FlowFieldWorldAreas areas,
            int id,
            int goalX,
            int goalY,
            int goalZ,
            List<FlowFieldWorldBakedGoal> baked)
        {
            bool volume = areas.World.SpaceMode == FlowFieldSpaceMode.Volume3D;
            var names = new List<string>();
            var cells = new List<List<int>>();
            var steps = new List<List<int>>();
            var heights = new List<List<float>>();
            IReadOnlyList<FlowFieldWorldArea> all = areas.All;
            for (int a = 0; a < all.Count; a++)
            {
                FlowFieldWorldArea area = all[a];
                IFlowFieldWorldMember member = area.Member;
                for (int s = 0; s < member.WorldSessionCount; s++)
                {
                    // Every type gets its record, so a Goal its outer cells
                    // never reach is known unreachable rather than estimated.
                    string name = member.WorldSessionName(s);
                    int type = names.IndexOf(name);
                    if (type < 0)
                    {
                        type = names.Count;
                        names.Add(name);
                        cells.Add(new List<int>());
                        steps.Add(new List<int>());
                        heights.Add(new List<float>());
                    }
                    FlowFieldField field = member.WorldSession(s).GoalField(id);
                    int[] distances = field?.Goal.Distances;
                    if (distances == null)
                        continue;
                    for (int cell = 0; cell < distances.Length; cell++)
                    {
                        if (distances[cell] == FlowFieldGoalLayer.Unreachable || !area.IsOuterCell(cell))
                            continue;
                        area.Locate(cell, out int x, out int y, out int z);
                        cells[type].Add(x);
                        cells[type].Add(y);
                        cells[type].Add(z);
                        steps[type].Add(distances[cell]);
                        if (!volume)
                            heights[type].Add(field.Scene.Surface.Heights[cell]);
                    }
                }
            }
            for (int type = 0; type < names.Count; type++)
            {
                baked.Add(FlowFieldWorldBakedGoal.Create(
                    names[type],
                    goalX,
                    goalY,
                    goalZ,
                    cells[type].ToArray(),
                    steps[type].ToArray(),
                    heights[type].ToArray()));
            }
        }

        // A Manager's area in the bake: a session per agent type, built from
        // the Manager's settings as RuntimeDynamic, the Default type's first.
        private sealed class BakeArea : IFlowFieldWorldMember, IDisposable
        {
            private readonly FlowFieldExecutionBoundary _boundary = new FlowFieldExecutionBoundary();
            private readonly string _name;
            private readonly FlowFieldSession[] _sessions;
            private readonly string[] _names;

            internal BakeArea(FlowFieldManager manager)
            {
                _name = manager.name;
                int count = 1 + manager.ConfiguredAgentTypeCount;
                _sessions = new FlowFieldSession[count];
                _names = new string[count];
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        FlowFieldManagerConfig config = i == 0
                            ? manager.CaptureConfig(FlowFieldBakeMode.RuntimeDynamic)
                            : manager.CaptureAgentConfig(i - 1);
                        FlowFieldSession session = FlowFieldSpaceStrategy.For(config.SpaceMode).CreateSession(_boundary);
                        _sessions[i] = session;
                        _names[i] = i == 0 ? FlowFieldAgentType.DefaultName : manager.ConfiguredAgentTypeName(i - 1);
                        session.Initialize(FlowFieldBakeMode.RuntimeDynamic);
                        session.SubmitConfig(in config, FlowFieldConfigChange.Rebuild);
                    }
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            public string Name => _name;
            public int WorldSessionCount => _sessions.Length;
            public FlowFieldSession WorldSession(int index) => _sessions[index];
            public string WorldSessionName(int index) => _names[index];
            public FlowFieldExecutionBoundary.OperationScope EnterOperation() => _boundary.EnterOperation();
            public bool IsInUserCode => _boundary.IsInUserCode;

            internal bool IsIdle
            {
                get
                {
                    for (int i = 0; i < _sessions.Length; i++)
                    {
                        if (!IsSessionIdle(_sessions[i]))
                            return false;
                    }
                    return true;
                }
            }

            // Runs every owed build to its end, waiting for the worker.
            internal bool TrySettle(out string message)
            {
                for (int i = 0; i < _sessions.Length; i++)
                {
                    FlowFieldSession session = _sessions[i];
                    for (int pump = 0; pump < MaxPumps && !IsSessionIdle(session); pump++)
                        session.Pump();
                    string type = i == 0 ? "" : $" ({_names[i]})";
                    if (session.IsFaulted)
                    {
                        message = $"{_name}{type}: {session.LastError}";
                        return false;
                    }
                    if (!IsSessionIdle(session) || !session.IsReady)
                    {
                        message = $"{_name}{type} built no field.";
                        return false;
                    }
                }
                message = null;
                return true;
            }

            private static bool IsSessionIdle(FlowFieldSession session)
                => !session.HasActiveBuild && !session.HasOwedInput && !session.HasPendingRequest;

            public void Dispose()
            {
                for (int i = 0; i < _sessions.Length; i++)
                    _sessions[i]?.Dispose();
            }
        }
    }
}
