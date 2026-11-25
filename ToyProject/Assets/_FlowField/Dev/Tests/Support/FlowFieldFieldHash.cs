using System;
using UnityEngine;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// FNV-1a over every published field array, byte for byte the same as the
    /// hash the earlier local kit recorded, so its baselines stay valid.
    /// Goal flags count four bytes each and a missing array one zero byte.
    /// </summary>
    internal static class FlowFieldFieldHash
    {
        private const ulong OffsetBasis = 1469598103934665603UL;
        private const ulong Prime = 1099511628211UL;

        internal static ulong Compute(FlowFieldField field)
        {
            if (field == null)
                throw new ArgumentNullException(nameof(field));
            ulong hash = OffsetBasis;
            Mix(ref hash, field.Blocked);
            Mix(ref hash, field.Topology);
            Mix(ref hash, field.GoalFlags);
            Mix(ref hash, field.NextCells);
            Mix(ref hash, field.BaseDirections);
            Mix(ref hash, field.BaseSpeeds);
            Mix(ref hash, field.FinalDirections);
            Mix(ref hash, field.FinalSpeeds);
            Mix(ref hash, field.EscapeDirections);
            Mix(ref hash, field.ModifierInfluence);
            return hash;
        }

        internal static string Format(ulong hash) => hash.ToString("X16");

        private static void MixByte(ref ulong hash, byte value)
        {
            hash ^= value;
            hash *= Prime;
        }

        private static void MixInt(ref ulong hash, int value)
        {
            MixByte(ref hash, (byte)value);
            MixByte(ref hash, (byte)(value >> 8));
            MixByte(ref hash, (byte)(value >> 16));
            MixByte(ref hash, (byte)(value >> 24));
        }

        private static void MixFloat(ref ulong hash, float value)
            => MixInt(ref hash, BitConverter.SingleToInt32Bits(value));

        private static void Mix(ref ulong hash, bool[] values)
        {
            if (values == null) { MixByte(ref hash, 0); return; }
            for (int i = 0; i < values.Length; i++)
                MixByte(ref hash, values[i] ? (byte)1 : (byte)0);
        }

        private static void Mix(ref ulong hash, uint[] values)
        {
            if (values == null) { MixByte(ref hash, 0); return; }
            for (int i = 0; i < values.Length; i++)
                MixInt(ref hash, unchecked((int)values[i]));
        }

        private static void Mix(ref ulong hash, int[] values)
        {
            if (values == null) { MixByte(ref hash, 0); return; }
            for (int i = 0; i < values.Length; i++)
                MixInt(ref hash, values[i]);
        }

        private static void Mix(ref ulong hash, FlowFieldGoalFlags[] values)
        {
            if (values == null) { MixByte(ref hash, 0); return; }
            for (int i = 0; i < values.Length; i++)
                MixInt(ref hash, (int)values[i]);
        }

        private static void Mix(ref ulong hash, float[] values)
        {
            if (values == null) { MixByte(ref hash, 0); return; }
            for (int i = 0; i < values.Length; i++)
                MixFloat(ref hash, values[i]);
        }

        private static void Mix(ref ulong hash, Vector3[] values)
        {
            if (values == null) { MixByte(ref hash, 0); return; }
            for (int i = 0; i < values.Length; i++)
            {
                MixFloat(ref hash, values[i].x);
                MixFloat(ref hash, values[i].y);
                MixFloat(ref hash, values[i].z);
            }
        }
    }
}
