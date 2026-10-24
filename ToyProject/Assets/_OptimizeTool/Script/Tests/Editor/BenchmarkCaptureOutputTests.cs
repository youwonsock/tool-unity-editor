using System;
using System.IO;
using NUnit.Framework;
using Common.OptimizeTool.Samples;
using UnityEngine;

namespace Common.OptimizeTool.Tests
{
    public sealed class BenchmarkCaptureOutputTests
    {
        private string _folder;
        [SetUp] public void SetUp() => _folder = Path.Combine(Path.GetTempPath(), "OptimizeCapture-" + Guid.NewGuid().ToString("N"));
        [TearDown] public void TearDown() { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }

        [TestCase("complete.json")]
        [TestCase("frames.csv")]
        [TestCase("error.txt")]
        public void ExistingArtifactsAreRejectedWithoutBeingModified(string artifact)
        {
            Directory.CreateDirectory(_folder); File.WriteAllText(Path.Combine(_folder, artifact), "previous");
            Assert.Throws<IOException>(() => new BenchmarkCaptureOutput(_folder));
            Assert.AreEqual("previous", File.ReadAllText(Path.Combine(_folder, artifact)));
            Assert.IsFalse(File.Exists(Path.Combine(_folder, "capture.json")));
        }

        [Test]
        public void ADirectoryCanOnlyBeClaimedOnce()
        {
            var capture = new BenchmarkCaptureOutput(_folder);
            string before = File.ReadAllText(Path.Combine(_folder, "capture.json"));
            Assert.Throws<IOException>(() => new BenchmarkCaptureOutput(_folder));
            Assert.AreEqual(before, File.ReadAllText(Path.Combine(_folder, "capture.json")));
        }

        [Test]
        public void CompletionCarriesMatchingIdentityAndPayloadHashesAndCanBeInvalidated()
        {
            var capture = new BenchmarkCaptureOutput(_folder);
            var metadata = new OptimizeToolPerformanceBenchmark.Metadata();
            capture.WriteMetadata(metadata);
            capture.Complete("header\nvalue\n", 1);
            string completion = File.ReadAllText(Path.Combine(_folder, "complete.json"));
            StringAssert.Contains(capture.CaptureId, completion);
            StringAssert.Contains("framesSha256", completion); StringAssert.Contains("metadataSha256", completion);
            Assert.AreEqual(2, metadata.schemaVersion); Assert.AreEqual(capture.CaptureId, metadata.captureId);
            capture.WriteError("interrupted");
            Assert.IsFalse(File.Exists(Path.Combine(_folder, "complete.json")));
            Assert.Throws<InvalidOperationException>(() => capture.Complete("changed", 1));
            Assert.IsEmpty(Directory.GetFiles(_folder, "*.tmp-*"));
        }

        [Test]
        public void MissingPayloadCannotPublishCompletion()
        {
            var capture = new BenchmarkCaptureOutput(_folder);
            Assert.Throws<FileNotFoundException>(() => capture.Complete("data", 1));
            Assert.IsFalse(File.Exists(Path.Combine(_folder, "complete.json")));
        }
    }
}
