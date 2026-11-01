using System;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// Content hashes of the two committed bake assets, taken through the
    /// arrays the runtime reads. They are independent of the serialized
    /// layout, so a storage format change must keep them unchanged.
    /// </summary>
    public sealed class FlowFieldBakeAssetContentTests
    {
        private const string SurfaceAssetPath = "Assets/_FlowField/Settings/FlowFieldManager_1569603542928883877_StaticBake.asset";
        private const string VolumeAssetPath = "Assets/_FlowField/Settings/4e0c5f04dff149c2a9b0f9c0e7af1a6d_1100003_FlowFieldManager_VolumeBake.asset";
        private const string SurfaceContentGolden = "5bb9a07cc0dd2bc6";
        private const string VolumeContentGolden = "33c02cc49edd6013";

        [Test]
        public void SurfaceBakeContentMatchesGolden()
        {
            var data = AssetDatabase.LoadAssetAtPath<FlowFieldStaticBakeData>(SurfaceAssetPath);
            Assert.That(data, Is.Not.Null);
            Assert.That(data.Validate(out string reason), Is.True, reason);
            var hash = new ContentHash();
            hash.Add(data.SurfaceHeights);
            hash.Add(data.SurfaceNormals);
            hash.Add(data.SurfaceFlags);
            hash.Add(data.SurfaceNeighborMasks);
            hash.Add(data.Blocked);
            hash.Add(data.EscapeDirections);
            hash.Add(data.TopologyMasks);
            hash.Add(Array.ConvertAll(data.GoalFlags, flag => (byte)flag));
            hash.Add(data.GoalDirections);
            hash.Add(data.NextCells);
            AssertGolden("Surface bake", hash.ToString(), SurfaceContentGolden);
        }

        [Test]
        public void VolumeBakeContentMatchesGolden()
        {
            var data = AssetDatabase.LoadAssetAtPath<FlowFieldVolumeBakeData>(VolumeAssetPath);
            Assert.That(data, Is.Not.Null);
            Assert.That(data.TryGetValidationView(out FlowFieldVolumeBakeValidationView view), Is.True);
            var hash = new ContentHash();
            hash.Add(view.Blocked);
            hash.Add(view.TopologyMasks);
            hash.Add(Array.ConvertAll(view.GoalFlags, flag => (byte)flag));
            hash.Add(view.NextCells);
            hash.Add(view.Directions);
            hash.Add(view.Speeds);
            hash.Add(view.EscapeDirections);
            AssertGolden("Volume bake", hash.ToString(), VolumeContentGolden);
        }

        private static void AssertGolden(string name, string actual, string expected)
        {
            TestContext.Progress.WriteLine($"[FlowField bake content] {name} = {actual}");
            Assert.That(actual, Is.EqualTo(expected), $"{name} content changed.");
        }

        /// <summary>FNV-1a over lengths and values; floats quantized to 1e-4.</summary>
        private sealed class ContentHash
        {
            private ulong _value = 14695981039346656037UL;

            internal void Add(float[] values)
            {
                Mix(values.Length);
                foreach (float value in values)
                    Mix(Mathf.RoundToInt(value * 10000f));
            }

            internal void Add(Vector3[] values)
            {
                Mix(values.Length);
                foreach (Vector3 value in values)
                {
                    Mix(Mathf.RoundToInt(value.x * 10000f));
                    Mix(Mathf.RoundToInt(value.y * 10000f));
                    Mix(Mathf.RoundToInt(value.z * 10000f));
                }
            }

            internal void Add(byte[] values)
            {
                Mix(values.Length);
                foreach (byte value in values)
                    Mix(value);
            }

            internal void Add(bool[] values)
            {
                Mix(values.Length);
                foreach (bool value in values)
                    Mix(value ? 1 : 0);
            }

            internal void Add(int[] values)
            {
                Mix(values.Length);
                foreach (int value in values)
                    Mix(value);
            }

            internal void Add(uint[] values)
            {
                Mix(values.Length);
                foreach (uint value in values)
                    Mix(unchecked((int)value));
            }

            private void Mix(int value)
            {
                unchecked
                {
                    for (int shift = 0; shift < 32; shift += 8)
                    {
                        _value ^= (byte)(value >> shift);
                        _value *= 1099511628211UL;
                    }
                }
            }

            public override string ToString() => _value.ToString("x16");
        }
    }
}
