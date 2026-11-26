using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// The obstacles and Modifiers registered with a world, which may reach
    /// any of its areas. Every area that takes them holds every one: an area
    /// keeps a registration its grid does not reach without building for it,
    /// so the one that moves into an area later is already there. Once a
    /// frame, before the builds, an area that joined, restarted after a fault
    /// or was released and initialized again takes what it misses.
    /// </summary>
    internal sealed class FlowFieldWorldRegistrations
    {
        private readonly FlowFieldWorldAreas _areas;
        private readonly List<Collider> _obstacles = new List<Collider>(8);
        private readonly List<IFlowFieldVectorModifier> _modifiers = new List<IFlowFieldVectorModifier>(4);

        internal FlowFieldWorldRegistrations(FlowFieldWorldAreas areas)
        {
            _areas = areas;
        }

        internal bool RegisterObstacle(Collider collider)
        {
            if (_obstacles.Contains(collider))
                return false;
            _obstacles.Add(collider);
            Update();
            return true;
        }

        internal bool UnregisterObstacle(Collider collider)
        {
            if (!_obstacles.Remove(collider))
                return false;
            IReadOnlyList<FlowFieldWorldArea> areas = _areas.All;
            for (int i = 0; i < areas.Count; i++)
            {
                FlowFieldManager manager = areas[i].Manager;
                if (Takes(manager, removal: true))
                    Run(manager, () => manager.UnregisterDynamicObstacle(collider));
            }
            return true;
        }

        // Called as an obstacle moves, so it runs no closure.
        internal bool NotifyObstacleMoved(Collider collider)
        {
            if (!_obstacles.Contains(collider))
                return false;
            IReadOnlyList<FlowFieldWorldArea> areas = _areas.All;
            for (int i = 0; i < areas.Count; i++)
            {
                FlowFieldManager manager = areas[i].Manager;
                if (!Takes(manager, removal: false))
                    continue;
                try
                {
                    manager.NotifyDynamicObstacleMoved(collider);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, manager);
                }
            }
            return true;
        }

        internal void NotifyRegionDirty(Bounds worldBounds)
        {
            IReadOnlyList<FlowFieldWorldArea> areas = _areas.All;
            for (int i = 0; i < areas.Count; i++)
            {
                FlowFieldManager manager = areas[i].Manager;
                if (!Takes(manager, removal: false))
                    continue;
                try
                {
                    manager.NotifyObstacleRegionDirty(worldBounds);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, manager);
                }
            }
        }

        internal bool RegisterModifier(IFlowFieldVectorModifier modifier)
        {
            if (_modifiers.Contains(modifier))
                return false;
            _modifiers.Add(modifier);
            Update();
            return true;
        }

        internal bool UnregisterModifier(IFlowFieldVectorModifier modifier)
        {
            if (!_modifiers.Remove(modifier))
                return false;
            IReadOnlyList<FlowFieldWorldArea> areas = _areas.All;
            for (int i = 0; i < areas.Count; i++)
            {
                FlowFieldManager manager = areas[i].Manager;
                if (Takes(manager, removal: true))
                    Run(manager, () => manager.UnregisterVectorModifier(modifier));
            }
            return true;
        }

        internal bool NotifyModifierChanged(IFlowFieldVectorModifier modifier, FlowFieldModifierChange change)
        {
            if (!_modifiers.Contains(modifier))
                return false;
            IReadOnlyList<FlowFieldWorldArea> areas = _areas.All;
            for (int i = 0; i < areas.Count; i++)
            {
                FlowFieldManager manager = areas[i].Manager;
                if (!Takes(manager, removal: false) || !manager.Session.Modifiers.Contains(modifier))
                    continue;
                try
                {
                    manager.NotifyModifierChanged(modifier, change);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, manager);
                }
            }
            return true;
        }

        /// <summary>Every area that takes registrations holds every one; destroyed ones leave.</summary>
        internal void Update()
        {
            _obstacles.RemoveAll(collider => collider == null);
            _modifiers.RemoveAll(FlowFieldVectorUtility.IsMissingModifier);
            if (_obstacles.Count == 0 && _modifiers.Count == 0)
                return;
            IReadOnlyList<FlowFieldWorldArea> areas = _areas.All;
            for (int a = 0; a < areas.Count; a++)
            {
                FlowFieldManager manager = areas[a].Manager;
                if (!Takes(manager, removal: false))
                    continue;
                try
                {
                    for (int i = 0; i < _obstacles.Count; i++)
                    {
                        if (!manager.IsDynamicObstacleRegistered(_obstacles[i]))
                            manager.RegisterDynamicObstacle(_obstacles[i]);
                    }
                    for (int i = 0; i < _modifiers.Count; i++)
                    {
                        if (!manager.Session.Modifiers.Contains(_modifiers[i]))
                            manager.RegisterVectorModifier(_modifiers[i]);
                    }
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, manager);
                }
            }
        }

        // Whether the area's Manager takes this input now. StaticBaked keeps
        // its baked obstacles; a faulted Manager takes only removals.
        private static bool Takes(FlowFieldManager manager, bool removal)
            => manager != null
                && manager.BakeMode != FlowFieldBakeMode.StaticBaked
                && (manager.IsInitialized && !manager.IsFaulted || removal && manager.IsFaulted);

        // One area's failure is logged and never keeps the input from the others.
        private static void Run(FlowFieldManager manager, Func<bool> action)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, manager);
            }
        }
    }
}
