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
    /// isolated bake session of each mode. Everything reads the Manager through
    /// <see cref="FlowFieldManager.CaptureConfig()"/> and writes it through
    /// its serialized properties.
    /// </summary>
    internal static class FlowFieldBakeService
    {
        // ----- Both modes ----------------------------------------------------

        /// <summary>The grid the settings lay out, as the space mode's strategy computes it.</summary>
        internal static bool TryGetLayout(this FlowFieldManager manager, out Bounds worldBounds, out FlowFieldGridSpace grid)
        {
            FlowFieldManagerConfig config = manager.CaptureConfig();
            return FlowFieldSpaceStrategy.For(manager.SpaceMode).TryCreateLayout(in config, out worldBounds, out grid);
        }

        /// <summary>
        /// The check Init makes, with its reason, for the Bake Mode the session
        /// accepted. Without a session that is the serialized Bake Mode.
        /// </summary>
        internal static bool TryValidateConfig(this FlowFieldManager manager, out string reason, bool includeStaticGoal = true)
        {
            FlowFieldManagerConfig config = manager.CaptureConfig(manager.BakeMode);
            return FlowFieldSpaceStrategy.For(manager.SpaceMode)
                .TryValidate(in config, bakeModeLocked: false, includeStaticGoal, out reason);
        }

        /// <summary>
        /// The checks Init makes on the settings a bake reads, leaving out the
        /// Bake Asset the bake is about to write. A bake that passes them saves
        /// an asset Init can load.
        /// </summary>
        internal static bool TryValidateBakeSettings(this FlowFieldManager manager, out string reason)
        {
            FlowFieldManagerConfig config = manager.CaptureConfig(FlowFieldBakeMode.RuntimeDynamic);
            return FlowFieldSpaceStrategy.For(manager.SpaceMode)
                .TryValidate(in config, bakeModeLocked: false, validateStaticGoal: false, out reason);
        }

        /// <summary>
        /// The Volume3D checks Init makes before the bake payload, with its
        /// reasons. The Volume display validates the payload in steps, then
        /// runs <see cref="TryValidateConfig"/> on the validated payload.
        /// </summary>
        internal static bool TryValidateVolumeConfigBeforePayload(this FlowFieldManager manager, out string reason)
        {
            FlowFieldManagerConfig config = manager.CaptureConfig(manager.BakeMode);
            return FlowFieldVolumeBuilder.TryValidateBeforePayload(
                in config, bakeModeLocked: false, out reason);
        }

        // ----- Surface2D -----------------------------------------------------

        internal static FlowFieldSurfaceSignature CreateSurfaceSignature(this FlowFieldManager manager)
        {
            FlowFieldManagerConfig config = manager.CaptureConfig();
            return FlowFieldSurfaceBuilder.CreateSignature(in config);
        }

        internal static FlowFieldSurfaceGoalResolution ResolveConfiguredGoal(this FlowFieldManager manager, FlowFieldGridSpace grid)
        {
            FlowFieldManagerConfig config = manager.CaptureConfig();
            return FlowFieldSurfaceBuilder.ResolveGoal(in config, grid);
        }

        /// <summary>
        /// Creates an isolated Surface2D bake session on a finished ground scan
        /// made with <paramref name="signature"/>, and submits the whole grid.
        /// The caller owns and must dispose it, and pumps it from the Editor
        /// update loop.
        /// </summary>
        internal static FlowFieldSession CreateSurfaceBakeSession(
            FlowFieldManager manager,
            in FlowFieldSurfaceSignature signature,
            FlowFieldSurfaceBakeResult ground)
        {
            FlowFieldManagerConfig config = manager.CaptureConfig();
            if (config.SpaceMode != FlowFieldSpaceMode.Surface2D)
                throw new InvalidOperationException("A Surface2D Bake session requires Surface2D mode.");
            FlowFieldSurfaceData surface = FlowFieldSurfaceData.FromRuntime(signature.Surface, ground, 1);
            FlowFieldGridSpace grid = signature.Surface.Grid;
            FlowFieldSurfaceRequest request = FlowFieldSurfaceBuilder.CreateRequest(
                in config,
                FlowFieldBakeMode.RuntimeDynamic,
                in signature,
                FlowFieldSurfaceDirtyFlags.All,
                FlowFieldCellRect.Full(grid),
                FlowFieldCellRect.Full(grid));

            var session = FlowFieldSession.CreateSurface(surfaceSource: new FlowFieldFixedSurfaceSource(surface));
            try
            {
                session.Initialize(FlowFieldBakeMode.RuntimeDynamic);
                session.Surface.Submit(request);
                return session;
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        // ----- Edits -----------------------------------------------------------

        /// <summary>
        /// A Bake Bounds handle edit, snapped to whole cells, with Undo. A
        /// running session hears of it through OnValidate. False when the
        /// snapped bounds are the ones it already has.
        /// </summary>
        internal static bool SetBakeBoundsLocal(this FlowFieldManager manager, Bounds localBounds, string undoName)
        {
            FlowFieldManagerConfig config = manager.CaptureConfig();
            Bounds snapped = FlowFieldSpaceStrategy.For(config.SpaceMode).SnapBounds(localBounds, config.Grid.CellSize);
            if (FlowFieldLayoutUtility.Approximately(config.Grid.BakeBoundsLocal, snapped))
                return false;
            // One Undo step under undoName; ApplyModifiedProperties would add a
            // second, separately named one.
            Undo.RecordObject(manager, undoName);
            var serialized = new SerializedObject(manager);
            serialized.FindProperty(FlowFieldManager.PropertyNames.BakeBoundsLocal).boundsValue = snapped;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return true;
        }

        // A bake assigns or clears its asset at the property path without an Undo step.
        internal static void AssignBakeAsset(this FlowFieldManager manager, string propertyPath, UnityEngine.Object asset)
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
        internal static FlowFieldSession CreateVolumeBakeSession(FlowFieldManager manager)
        {
            FlowFieldManagerConfig config = manager.CaptureConfig();
            if (config.SpaceMode != FlowFieldSpaceMode.Volume3D)
                throw new InvalidOperationException("Volume bake requires Volume3D mode.");
            if (!FlowFieldVolumeBuilder.TryCreateLayout(in config, out Bounds worldBounds, out FlowFieldGridSpace grid))
                throw new InvalidOperationException(FlowFieldManagerConfig.InvalidGridMessage);

            FlowFieldVolumeRequest request = FlowFieldVolumeBuilder.CreateRequest(
                in config,
                grid,
                worldBounds,
                FlowFieldBakeMode.RuntimeDynamic,
                null);

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
