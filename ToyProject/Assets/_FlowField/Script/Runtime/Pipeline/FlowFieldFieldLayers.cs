using System;
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
    }

    /// <summary>
    /// What an agent can enter: which cells are blocked and, in Volume3D, the
    /// way out of a blocked cell. It follows from the ground and the
    /// obstacles only, whatever the Goal.
    /// </summary>
    internal sealed class FlowFieldAgentLayer
    {
        internal readonly bool[] Blocked;
        // Volume3D only; Surface2D reconstructs escape vectors when asked.
        internal readonly Vector3[] EscapeDirections;

        internal FlowFieldAgentLayer(bool[] blocked, Vector3[] escapeDirections)
        {
            Blocked = blocked ?? throw new ArgumentNullException(nameof(blocked));
            EscapeDirections = escapeDirections;
        }
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
    }
}
