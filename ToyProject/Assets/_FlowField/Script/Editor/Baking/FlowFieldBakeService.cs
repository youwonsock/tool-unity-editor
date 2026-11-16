using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField.Editor
{
    /// <summary>
    /// Bake support that only the Editor needs: telling whether a Manager's bake
    /// inputs changed during a bake, and the isolated Volume3D bake session.
    /// </summary>
    internal static class FlowFieldBakeService
    {
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
            int hash = manager.ComputeBakeInputHash();
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
        internal static FlowFieldVolumeSession CreateVolumeBakeSession(
            FlowFieldManager manager,
            out FlowFieldVolumeRequest request)
        {
            request = default;
            if (manager.ConfiguredSpaceMode != FlowFieldSpaceMode.Volume3D)
                throw new InvalidOperationException("Volume bake requires Volume3D mode.");
            if (!manager.TryGetVolumeLayout(out Bounds worldBounds, out FlowFieldGridSpace grid))
                throw new InvalidOperationException("Volume Bounds and Cell Size do not produce a valid grid.");

            request = manager.CreateVolumeRequest(
                grid,
                worldBounds,
                FlowFieldBakeMode.RuntimeDynamic,
                null,
                CaptureInputGeneration(manager));

            var session = new FlowFieldVolumeSession();
            try
            {
                session.Initialize(FlowFieldBakeMode.RuntimeDynamic);
                session.Submit(request);
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
