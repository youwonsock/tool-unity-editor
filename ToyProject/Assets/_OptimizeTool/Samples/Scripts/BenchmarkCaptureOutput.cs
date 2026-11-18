using System;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

namespace Common.OptimizeTool.Samples
{
    /// <summary>Claims one output directory and publishes completion only after both payloads are durable.</summary>
    internal sealed class BenchmarkCaptureOutput
    {
        [Serializable]
        private sealed class Manifest
        {
            public int schemaVersion = 2;
            public string captureId, status, framesSha256, metadataSha256;
            public int samples;
        }

        public string DirectoryPath { get; }
        public string CaptureId { get; } = Guid.NewGuid().ToString("N");
        private bool _complete;

        public BenchmarkCaptureOutput(string directory)
        {
            DirectoryPath = Path.GetFullPath(directory);
            Directory.CreateDirectory(DirectoryPath);
            string claim = Path.Combine(DirectoryPath, "capture.json");
            // CreateNew arbitrates competing callers, including callers in different processes.
            using (var stream = new FileStream(claim, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                try
                {
                    foreach (string entry in Directory.EnumerateFileSystemEntries(DirectoryPath))
                        if (entry != claim && Path.GetFileName(entry) != "player.log")
                            throw new IOException("Benchmark output already contains data. Use a fresh directory.");
                    using (var writer = new StreamWriter(stream))
                        writer.Write(JsonUtility.ToJson(new Manifest { captureId = CaptureId, status = "capturing" }, true));
                }
                catch { stream.Dispose(); File.Delete(claim); throw; }
            }
        }

        public void WriteMetadata(OptimizeToolPerformanceBenchmark.Metadata metadata)
        {
            metadata.schemaVersion = 2; metadata.captureId = CaptureId;
            WriteAtomic("metadata.json", JsonUtility.ToJson(metadata, true));
        }

        public void Complete(string csv, int samples)
        {
            if (_complete) throw new InvalidOperationException("Capture is already complete.");
            if (File.Exists(Path.Combine(DirectoryPath, "error.txt")) || Directory.Exists(Path.Combine(DirectoryPath, "error.txt")))
                throw new InvalidOperationException("A failed capture cannot be completed.");
            WriteAtomic("frames.csv", csv);
            var result = new Manifest
            {
                captureId = CaptureId, status = "complete", samples = samples,
                framesSha256 = Hash("frames.csv"), metadataSha256 = Hash("metadata.json")
            };
            WriteAtomic("complete.json", JsonUtility.ToJson(result, true));
            _complete = true;
        }

        public void WriteError(string error)
        {
            // Invalidate completion first, even if diagnostic writing subsequently fails.
            string completion = Path.Combine(DirectoryPath, "complete.json");
            if (File.Exists(completion)) File.Delete(completion);
            WriteAtomic("error.txt", error);
        }

        private string Hash(string name)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(Path.Combine(DirectoryPath, name)))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private void WriteAtomic(string name, string contents)
        {
            string path = Path.Combine(DirectoryPath, name), temporary = path + ".tmp-" + CaptureId;
            try
            {
                File.WriteAllText(temporary, contents);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
