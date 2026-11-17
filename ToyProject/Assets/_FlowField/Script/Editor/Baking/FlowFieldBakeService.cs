using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Common.FlowField.Editor
{
    /// <summary>
    /// Bake support that only the Editor needs: the layouts, checks and
    /// requests a bake builds from a Manager's settings, the edits it makes
    /// to them, telling whether its inputs changed during a bake, and the
    /// isolated Volume3D bake session. Everything reads the Manager through
    /// <see cref="FlowFieldManager.CaptureConfig()"/> and writes it through
    /// its serialized properties.
    /// </summary>
    internal static class FlowFieldBakeService
    {
        // ----- Surface2D -----------------------------------------------------

        internal static bool TryGetBakeLayout(this FlowFieldManager manager, out Bounds worldBounds, out FlowFieldGridSpace grid)
        {
            FlowFieldManagerConfig config = manager.CaptureConfig();
            return FlowFieldSurfaceBuilder.TryCreateLayout(in config, out worldBounds, out grid);
        }

        internal static FlowFieldSurfaceBakeSettings CreateSurfaceBakeSettings(this FlowFieldManager manager)
        {
            FlowFieldManagerConfig config = manager.CaptureConfig();
            return FlowFieldSurfaceBuilder.CreateBakeSettings(in config);
        }

        internal static FlowFieldSurfaceGoalResolution ResolveConfiguredGoal(this FlowFieldManager manager, FlowFieldGridSpace grid)
        {
            FlowFieldManagerConfig config = manager.CaptureConfig();
            return FlowFieldSurfaceBuilder.ResolveGoal(in config, grid);
        }

        internal static FlowFieldSurfaceRequest CreateSurfaceRequest(
            this FlowFieldManager manager,
            FlowFieldSurfaceBakeSettings settings,
            FlowFieldSurfaceDirtyFlags dirtyFlags,
            FlowFieldCellRect dirtyFinalRegion,
            FlowFieldCellRect dirtyObstacleRegion)
        {
            FlowFieldManagerConfig config = manager.CaptureConfig();
            return FlowFieldSurfaceBuilder.CreateRequest(
                in config,
                manager.BakeMode,
                in settings,
                dirtyFlags,
                dirtyFinalRegion,
                dirtyObstacleRegion);
        }

        internal static bool TryValidateSurfaceBake(this FlowFieldManager manager, out string reason)
            => manager.TryValidateSurfaceBake(out reason, includeStaticGoal: true);

        // A Volume3D Manager answers with its Volume bake check. Surface2D
        // checks with the Bake Mode its session accepted.
        internal static bool TryValidateSurfaceBake(this FlowFieldManager manager, out string reason, bool includeStaticGoal)
        {
            FlowFieldManagerConfig config = manager.CaptureConfig(manager.BakeMode);
            if (config.SpaceMode == FlowFieldSpaceMode.Volume3D)
                return FlowFieldVolumeBuilder.TryValidateBake(manager.CaptureConfig(), out reason);
            return FlowFieldSurfaceBuilder.TryValidateBake(in config, includeStaticGoal, out reason);
        }

        // ----- Volume3D ------------------------------------------------------

        internal static bool TryGetVolumeLayout(this FlowFieldManager manager, out Bounds worldBounds, out FlowFieldGridSpace grid)
        {
            FlowFieldManagerConfig config = manager.CaptureConfig();
            return FlowFieldVolumeBuilder.TryCreateLayout(in config, out worldBounds, out grid);
        }

        internal static FlowFieldVolumeRequest CreateVolumeRequest(
            this FlowFieldManager manager,
            FlowFieldGridSpace grid,
            Bounds worldBounds,
            FlowFieldBakeMode bakeMode,
            FlowFieldVolumeBakeData bakeData,
            int bakeInputGeneration = 0)
        {
            FlowFieldManagerConfig config = manager.CaptureConfig();
            return FlowFieldVolumeBuilder.CreateRequest(
                in config,
                grid,
                worldBounds,
                bakeMode,
                bakeData,
                bakeInputGeneration);
        }

        // ----- Edits -----------------------------------------------------------

        /// <summary>
        /// A Bake Bounds handle edit, snapped to whole cells, with Undo. A
        /// running session hears of it through OnValidate.
        /// </summary>
        internal static void SetBakeBoundsLocal(this FlowFieldManager manager, Bounds localBounds, string undoName)
        {
            FlowFieldManagerConfig config = manager.CaptureConfig();
            Bounds snapped = FlowFieldSpaceStrategy.For(config.SpaceMode).SnapBounds(localBounds, config.Grid.CellSize);
            if (FlowFieldBakeBoundsUtility.Approximately(config.Grid.BakeBoundsLocal, snapped))
                return;
            var serialized = new SerializedObject(manager);
            serialized.FindProperty(FlowFieldManager.PropertyNames.BakeBoundsLocal).boundsValue = snapped;
            serialized.ApplyModifiedProperties();
            Undo.SetCurrentGroupName(undoName);
        }

        // A bake assigns or clears its asset without an Undo step.
        internal static void AssignStaticBakeData(this FlowFieldManager manager, FlowFieldStaticBakeData bakeData)
            => AssignWithoutUndo(manager, FlowFieldManager.PropertyNames.SurfaceBakeData, bakeData);

        internal static void AssignVolumeBakeData(this FlowFieldManager manager, FlowFieldVolumeBakeData bakeData)
            => AssignWithoutUndo(manager, FlowFieldManager.PropertyNames.VolumeBakeData, bakeData);

        private static void AssignWithoutUndo(FlowFieldManager manager, string propertyPath, UnityEngine.Object asset)
        {
            var serialized = new SerializedObject(manager);
            SerializedProperty property = serialized.FindProperty(propertyPath);
            if (property.objectReferenceValue == asset)
                return;
            property.objectReferenceValue = asset;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        // ----- Input changes ---------------------------------------------------

        /// <summary>A signature of every setting a bake depends on; the bake assets are not one.</summary>
        private static int ComputeBakeInputHash(FlowFieldManager manager)
        {
            FlowFieldManagerConfig config = manager.CaptureConfig();
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + (int)config.SpaceMode;
                hash = hash * 31 + (int)config.BakeMode;
                hash = hash * 31 + config.Grid.BakeBoundsLocal.center.GetHashCode();
                hash = hash * 31 + config.Grid.BakeBoundsLocal.size.GetHashCode();
                hash = hash * 31 + config.Grid.CellSize.GetHashCode();
                hash = hash * 31 + config.Surface.GroundLayer.value;
                hash = hash * 31 + config.Surface.MaxSlope.GetHashCode();
                hash = hash * 31 + config.Surface.MaxStepHeight.GetHashCode();
                hash = hash * 31 + config.Obstacles.Layer.value;
                hash = hash * 31 + config.Surface.ObstacleCheckHeight.GetHashCode();
                hash = hash * 31 + config.Surface.ObstacleCheckCenterOffset.GetHashCode();
                hash = hash * 31 + config.Obstacles.Clearance.GetHashCode();
                hash = hash * 31 + config.Flow.DefaultDirection.GetHashCode();
                hash = hash * 31 + (config.HasGoal ? 1 : 0);
                hash = hash * 31 + config.GoalWorld.GetHashCode();
                hash = hash * 31 + config.Flow.GoalInfluenceRadius.GetHashCode();
                hash = hash * 31 + config.Position.GetHashCode();
                hash = hash * 31 + config.Rotation.GetHashCode();
                hash = hash * 31 + config.LossyScale.GetHashCode();
                return hash;
            }
        }

        private struct InputState
        {
            internal int Hash;
            internal int Generation;
        }

        private static readonly Dictionary<int, InputState> Inputs = new Dictionary<int, InputState>();

        /// <summary>
        /// A number that changes whenever the Manager's bake inputs changed since
        /// the previous call. A bake compares it before and after its work.
        /// </summary>
        internal static int CaptureInputGeneration(FlowFieldManager manager)
        {
            int id = manager.GetInstanceID();
            int hash = ComputeBakeInputHash(manager);
            if (!Inputs.TryGetValue(id, out InputState state) || state.Hash != hash)
            {
                unchecked { state.Generation++; }
                state.Hash = hash;
                Inputs[id] = state;
            }
            return state.Generation;
        }

        /// <summary>
        /// Creates an isolated Volume3D bake session. The caller owns and must
        /// dispose it, and pumps it from the Editor update loop.
        /// </summary>
        internal static FlowFieldSession CreateVolumeBakeSession(
            FlowFieldManager manager,
            out FlowFieldVolumeRequest request)
        {
            request = default;
            if (manager.CaptureConfig().SpaceMode != FlowFieldSpaceMode.Volume3D)
                throw new InvalidOperationException("Volume bake requires Volume3D mode.");
            if (!manager.TryGetVolumeLayout(out Bounds worldBounds, out FlowFieldGridSpace grid))
                throw new InvalidOperationException("Volume Bounds and Cell Size do not produce a valid grid.");

            request = manager.CreateVolumeRequest(
                grid,
                worldBounds,
                FlowFieldBakeMode.RuntimeDynamic,
                null,
                CaptureInputGeneration(manager));

            var session = FlowFieldSession.CreateVolume();
            try
            {
                session.Initialize(FlowFieldBakeMode.RuntimeDynamic);
                session.Volume.Submit(request);
                return session;
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }
    }
}
