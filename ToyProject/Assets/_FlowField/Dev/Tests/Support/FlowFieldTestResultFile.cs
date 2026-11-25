using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// Writes every finished test of a run, with its result and message, to
    /// Library/FlowFieldTests/LastRun.txt. A run started from a tool that
    /// reports only some failures can then be read in full.
    /// </summary>
    [InitializeOnLoad]
    internal static class FlowFieldTestResultFile
    {
        internal const string FilePath = "Library/FlowFieldTests/LastRun.txt";

        static FlowFieldTestResultFile()
            => ScriptableObject.CreateInstance<TestRunnerApi>().RegisterCallbacks(new Callbacks());

        private sealed class Callbacks : ICallbacks
        {
            private readonly List<string> _lines = new List<string>();

            public void RunStarted(ITestAdaptor testsToRun) => _lines.Clear();

            public void TestStarted(ITestAdaptor test)
            {
            }

            public void TestFinished(ITestResultAdaptor result)
            {
                if (result.HasChildren)
                    return;
                string message = string.IsNullOrEmpty(result.Message) ? string.Empty : " :: " + result.Message.Trim().Replace("\n", " | ");
                _lines.Add(result.TestStatus + " " + result.FullName + " (" + result.Duration.ToString("F1") + "s)" + message);
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                _lines.Insert(0, "passed=" + result.PassCount + " failed=" + result.FailCount
                    + " skipped=" + result.SkipCount + " inconclusive=" + result.InconclusiveCount);
                File.WriteAllLines(FilePath, _lines);
            }
        }
    }
}
