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
        // A registration an area refused: tried again only once that area's
        // session starts a new lifetime (a restart, or Release and Init).
        private readonly HashSet<(FlowFieldSession, int, object)> _refused = new HashSet<(FlowFieldSession, int, object)>();
        private static readonly Predicate<Collider> IsDestroyed = collider => collider == null;
        private static readonly Predicate<IFlowFieldVectorModifier> IsMissing = FlowFieldVectorUtility.IsMissingModifier;
        // A refusal of a lifetime that has ended, or of a registration that
        // is gone, is never asked about again.
        private static readonly Predicate<(FlowFieldSession, int, object)> IsStaleRefusal = key =>
            key.Item1.Disposed
            || key.Item1.LifetimeGeneration != key.Item2
            || key.Item3 is Collider collider && collider == null
            || key.Item3 is IFlowFieldVectorModifier modifier && FlowFieldVectorUtility.IsMissingModifier(modifier);

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
            ForgetRefusals(collider);
            _areas.EnterOperations();
            try
            {
                IReadOnlyList<FlowFieldWorldArea> areas = _areas.All;
                for (int i = 0; i < areas.Count; i++)
                {
                    FlowFieldManager manager = areas[i].Manager;
                    if (Takes(manager, removal: true))
                        Run(manager, () => manager.UnregisterDynamicObstacle(collider));
                }
            }
            finally
            {
                _areas.ExitOperations();
            }
            return true;
        }

        // Called as an obstacle moves, so it runs no closure.
        internal bool NotifyObstacleMoved(Collider collider)
        {
            if (!_obstacles.Contains(collider))
                return false;
            _areas.EnterOperations();
            try
            {
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
            }
            finally
            {
                _areas.ExitOperations();
            }
            return true;
        }

        internal void NotifyRegionDirty(Bounds worldBounds)
        {
            _areas.EnterOperations();
            try
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
            finally
            {
                _areas.ExitOperations();
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
            ForgetRefusals(modifier);
            _areas.EnterOperations();
            try
            {
                IReadOnlyList<FlowFieldWorldArea> areas = _areas.All;
                for (int i = 0; i < areas.Count; i++)
                {
                    FlowFieldManager manager = areas[i].Manager;
                    if (Takes(manager, removal: true))
                        Run(manager, () => manager.UnregisterVectorModifier(modifier));
                }
            }
            finally
            {
                _areas.ExitOperations();
            }
            return true;
        }

        internal bool NotifyModifierChanged(IFlowFieldVectorModifier modifier, FlowFieldModifierChange change)
        {
            if (!_modifiers.Contains(modifier))
                return false;
            _areas.EnterOperations();
            try
            {
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
            }
            finally
            {
                _areas.ExitOperations();
            }
            return true;
        }

        /// <summary>Every area that takes registrations holds every one; destroyed ones leave.</summary>
        internal void Update()
        {
            _obstacles.RemoveAll(IsDestroyed);
            _modifiers.RemoveAll(IsMissing);
            if (_refused.Count > 0)
                _refused.RemoveWhere(IsStaleRefusal);
            if (_obstacles.Count == 0 && _modifiers.Count == 0)
                return;
            _areas.EnterOperations();
            try
            {
                IReadOnlyList<FlowFieldWorldArea> areas = _areas.All;
                for (int a = 0; a < areas.Count; a++)
                {
                    FlowFieldManager manager = areas[a].Manager;
                    if (!Takes(manager, removal: false))
                        continue;
                    FlowFieldSession session = manager.Session;
                    for (int i = 0; i < _obstacles.Count; i++)
                    {
                        if (!manager.IsDynamicObstacleRegistered(_obstacles[i]))
                            TryRegister(manager, session, _obstacles[i]);
                    }
                    for (int i = 0; i < _modifiers.Count; i++)
                    {
                        if (!session.Modifiers.Contains(_modifiers[i]))
                            TryRegister(manager, session, _modifiers[i]);
                    }
                }
            }
            finally
            {
                _areas.ExitOperations();
            }
        }

        /// <summary>
        /// A disabled Manager gives back every registration of the world, so
        /// none stays in it while it is out; it takes them again at the first
        /// update after it joins.
        /// </summary>
        internal void RemoveFrom(FlowFieldManager manager)
        {
            if (!Takes(manager, removal: true))
                return;
            for (int i = 0; i < _obstacles.Count; i++)
            {
                Collider collider = _obstacles[i];
                // A destroyed one leaves the Manager by itself.
                if (collider != null && manager.IsDynamicObstacleRegistered(collider))
                    Run(manager, () => manager.UnregisterDynamicObstacle(collider));
            }
            for (int i = 0; i < _modifiers.Count; i++)
            {
                IFlowFieldVectorModifier modifier = _modifiers[i];
                if (manager.Session.Modifiers.Contains(modifier))
                    Run(manager, () => manager.UnregisterVectorModifier(modifier));
            }
        }

        // A registration the world no longer holds: registered again, it is
        // tried again in every area.
        private void ForgetRefusals(object registration)
        {
            if (_refused.Count > 0)
                _refused.RemoveWhere(key => ReferenceEquals(key.Item3, registration));
        }

        // Logged once: a refused registration waits for the area's next lifetime.
        private void TryRegister(FlowFieldManager manager, FlowFieldSession session, object registration)
        {
            var key = (session, session.LifetimeGeneration, registration);
            if (_refused.Contains(key))
                return;
            try
            {
                if (registration is Collider collider)
                    manager.RegisterDynamicObstacle(collider);
                else
                    manager.RegisterVectorModifier((IFlowFieldVectorModifier)registration);
            }
            catch (Exception exception)
            {
                _refused.Add(key);
                Debug.LogException(exception, manager);
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
