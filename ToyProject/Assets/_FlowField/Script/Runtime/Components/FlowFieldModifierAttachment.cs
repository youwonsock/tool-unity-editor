using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Connects the scene's Modifier Volumes to a Manager's session: they join
    /// when a session starts or retries and leave when it is released.
    /// </summary>
    internal sealed class FlowFieldModifierAttachment
    {
        private readonly FlowFieldManager _manager;
        private List<FlowFieldVectorModifierVolume> _buffer;

        internal FlowFieldModifierAttachment(FlowFieldManager manager)
        {
            _manager = manager;
        }

        /// <summary>
        /// True while a fault retry reattaches. Registration is then allowed
        /// before the configuration is checked again.
        /// </summary>
        internal bool IsReattachingForRetry { get; private set; }

        // A retried Surface session stays stale until it commits.
        internal void ReattachForRetry()
        {
            IsReattachingForRetry = true;
            try { Reattach(); }
            finally { IsReattachingForRetry = false; }
        }

        internal void Reattach()
        {
            List<FlowFieldVectorModifierVolume> volumes = RentBuffer();
            try
            {
                for (int i = 0; i < volumes.Count; i++)
                {
                    FlowFieldVectorModifierVolume volume = volumes[i];
                    if (volume == null
                        || volume.FlowFieldManager != _manager
                        || !volume.gameObject.scene.IsValid())
                        continue;

                    try
                    {
                        volume.ReattachToConfiguredManager();
                    }
                    catch (Exception exception)
                    {
                        Debug.LogException(exception, volume);
                    }
                }
            }
            finally
            {
                ReturnBuffer(volumes);
            }
        }

        internal void Detach()
        {
            List<FlowFieldVectorModifierVolume> volumes = RentBuffer();
            try
            {
                for (int i = 0; i < volumes.Count; i++)
                {
                    FlowFieldVectorModifierVolume volume = volumes[i];
                    if (volume == null || volume.FlowFieldManager != _manager)
                        continue;

                    volume.DetachFromFlowFieldSession(_manager);
                }
            }
            finally
            {
                ReturnBuffer(volumes);
            }
        }

        // A reattach can run a Manager command that walks the volumes again,
        // so a nested walk gets its own list instead of clearing this one.
        private List<FlowFieldVectorModifierVolume> RentBuffer()
        {
            List<FlowFieldVectorModifierVolume> buffer = _buffer ?? new List<FlowFieldVectorModifierVolume>(8);
            _buffer = null;
            FlowFieldVectorModifierVolume.CopyLive(buffer);
            return buffer;
        }

        private void ReturnBuffer(List<FlowFieldVectorModifierVolume> buffer)
        {
            buffer.Clear();
            _buffer ??= buffer;
        }
    }
}
