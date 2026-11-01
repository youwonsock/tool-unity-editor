using System;
using System.Diagnostics;
using System.Threading;
using UnityEngine;
using BuildContext = Common.FlowField.FlowFieldVolumeBuildContext;
using VolumeFieldData = Common.FlowField.FlowFieldVolumeFieldData;
using BuildPhase = Common.FlowField.FlowFieldVolumeBuildPhase;
using static Common.FlowField.FlowFieldVolumeNavigation;

namespace Common.FlowField
{
    /// <summary>The estimate of tracked memory that bounds a Volume3D build.</summary>
    internal sealed partial class FlowFieldVolumeSession
    {
        private bool CanReserveForBuild(
            in FlowFieldVolumeRequest request,
            long buildReservation)
            => EstimateRequiredBytes(request, buildReservation) <= MemoryBudgetBytes;

        private long EstimateRequiredBytes(
            in FlowFieldVolumeRequest request,
            long replacementBuildReservation)
        {
            long current = TrackedMemoryBytes;
            if (_buildReservationBytes > 0)
                current -= _buildReservationBytes;

            return checked(
                current
                + replacementBuildReservation);
        }

        private static long EstimateBuildBytes(int count, int modifierCount)
        {
            if (count <= 0)
                return 0;
            // The build owns twelve per-cell arrays: topology/state/output,
            // BFS costs/queue and final composition buffers. Reserve the
            // measured logical element footprint rounded up to 72 bytes per
            // cell, plus array headers and the captured modifier references.
            // The small safety margin prevents a managed-array type change
            // from turning the budget into an underestimate.
            return checked(
                72L * count
                + 32L * 13L
                + 32L
                + 8L * Math.Max(0, modifierCount));
        }

        private static long EstimateCommittedBytes(int count)
        {
            if (count <= 0)
                return 0;
            // VolumeFieldData owns nine per-cell arrays after commit. Reserve
            // the logical element footprint rounded up to 60 bytes per cell.
            return checked(60L * count + 32L * 9L + 32L);
        }

        private long EstimateAuxiliaryBytes()
        {
            long bytes = 64L;
            bytes = checked(bytes + _obstacles.SweepBufferBytes);
            bytes = checked(bytes + _cellQuery.BufferBytes);
            bytes = checked(bytes
                + 4L * (_spareCosts?.Length ?? 0)
                + 4L * (_spareQueue?.Length ?? 0)
                + (_spareInfluence?.Length ?? 0));
            if (_registeredObstacleOverlapBuffer != null)
                bytes = checked(bytes + 8L * _registeredObstacleOverlapBuffer.Length);
            bytes = checked(bytes + 8L * _modifierRegistry.Count);
            bytes = checked(bytes + 32L * _safety.UncertainRegionCount);
            return bytes;
        }
    }
}
