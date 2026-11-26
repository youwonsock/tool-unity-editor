using System;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// What the ground is: Surface2D's heights, normals, step links and which
    /// cells have a surface. Every agent and Goal of a field shares it; a
    /// Volume3D field has none.
    /// </summary>
    internal sealed class FlowFieldSceneLayer
    {
        internal readonly FlowFieldSurfaceData Surface;

        internal FlowFieldSceneLayer(FlowFieldSurfaceData surface)
        {
            Surface = surface ?? throw new ArgumentNullException(nameof(surface));
        }

        // The same ground, or no ground on either side.
        internal static bool SameContent(FlowFieldSceneLayer left, FlowFieldSceneLayer right)
        {
            FlowFieldSurfaceData a = left?.Surface;
            FlowFieldSurfaceData b = right?.Surface;
            return ReferenceEquals(a, b) || a != null && b != null && a.ContentEquals(b);
        }
    }

    /// <summary>
    /// What an agent can enter: which cells are blocked, the links between
    /// open cells and, in Volume3D, the way out of a blocked cell. It follows
    /// from the ground and the obstacles only, whatever the Goal.
    /// </summary>
    internal sealed class FlowFieldAgentLayer
    {
        internal readonly bool[] Blocked;
        // The links the ground and the obstacles allow, whatever the Goal:
        // the Goal layer's Topology without the Goal's influence. Volume3D
        // links every open cell there already, so it is the same array.
        internal readonly uint[] GroundTopology;
        // Volume3D only; Surface2D reconstructs escape vectors when asked.
        internal readonly Vector3[] EscapeDirections;

        internal FlowFieldAgentLayer(bool[] blocked, uint[] groundTopology, Vector3[] escapeDirections)
        {
            Blocked = blocked ?? throw new ArgumentNullException(nameof(blocked));
            GroundTopology = groundTopology ?? throw new ArgumentNullException(nameof(groundTopology));
            EscapeDirections = escapeDirections;
        }

        // GroundTopology follows from Blocked and the scene, so it is not compared.
        internal static bool SameContent(FlowFieldAgentLayer left, FlowFieldAgentLayer right)
            => ReferenceEquals(left, right)
                || FlowFieldLayerBytes.Same(left.Blocked, right.Blocked)
                && FlowFieldLayerBytes.Same(left.EscapeDirections, right.EscapeDirections);
    }

    /// <summary>
    /// The navigation toward one Goal: the links a path may take, the path,
    /// the base and final vectors, and the cells a Modifier touched.
    /// </summary>
    internal sealed class FlowFieldGoalLayer
    {
        // Surface2D uses 8 bits and links only cells inside the Goal's
        // influence; Volume3D uses 26 and links every open cell.
        internal readonly uint[] Topology;
        internal readonly FlowFieldGoalFlags[] Flags;
        internal readonly int[] NextCells;
        internal readonly Vector3[] BaseDirections;
        internal readonly float[] BaseSpeeds;
        internal readonly Vector3[] FinalDirections;
        internal readonly float[] FinalSpeeds;
        // Surface2D only: the cells a Modifier touched.
        internal readonly bool[] ModifierInfluence;
        internal readonly bool IsActive;
        // The resolved Goal cell, or -1.
        internal readonly int CellIndex;

        internal FlowFieldGoalLayer(
            uint[] topology,
            FlowFieldGoalFlags[] flags,
            int[] nextCells,
            Vector3[] baseDirections,
            float[] baseSpeeds,
            Vector3[] finalDirections,
            float[] finalSpeeds,
            bool[] modifierInfluence,
            bool isActive,
            int cellIndex)
        {
            Topology = topology;
            Flags = flags;
            NextCells = nextCells;
            BaseDirections = baseDirections;
            BaseSpeeds = baseSpeeds;
            FinalDirections = finalDirections;
            FinalSpeeds = finalSpeeds;
            ModifierInfluence = modifierInfluence;
            IsActive = isActive;
            CellIndex = cellIndex;
        }

        // The base vectors are not compared: reads take only the final ones.
        internal static bool SameContent(FlowFieldGoalLayer left, FlowFieldGoalLayer right)
            => ReferenceEquals(left, right)
                || left.IsActive == right.IsActive
                && left.CellIndex == right.CellIndex
                && FlowFieldLayerBytes.Same(left.Topology, right.Topology)
                && FlowFieldLayerBytes.Same(left.Flags, right.Flags)
                && FlowFieldLayerBytes.Same(left.NextCells, right.NextCells)
                && FlowFieldLayerBytes.Same(left.FinalDirections, right.FinalDirections)
                && FlowFieldLayerBytes.Same(left.FinalSpeeds, right.FinalSpeeds)
                && FlowFieldLayerBytes.Same(left.ModifierInfluence, right.ModifierInfluence);
    }

    /// <summary>The bitwise array comparison the layers share: memcmp over each array.</summary>
    internal static class FlowFieldLayerBytes
    {
        internal static unsafe bool Same<T>(T[] left, T[] right) where T : unmanaged
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null || left.Length != right.Length)
                return false;
            if (left.Length == 0)
                return true;
            fixed (T* a = left)
            fixed (T* b = right)
                return UnsafeUtility.MemCmp(a, b, (long)left.Length * sizeof(T)) == 0;
        }
    }
}
