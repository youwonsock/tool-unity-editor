using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// Collects FlowField log lines during a test. The Test Framework already
    /// fails a test on errors and exceptions; this also fails it on a
    /// "[FlowField" warning or message the test did not expect.
    /// </summary>
    internal sealed class FlowFieldTestLog
    {
        private readonly List<string> _unexpected = new List<string>();
        private readonly List<Regex> _expected = new List<Regex>();

        internal void Start()
        {
            _unexpected.Clear();
            _expected.Clear();
            Application.logMessageReceivedThreaded += OnLog;
        }

        internal void Expect(string pattern) => _expected.Add(new Regex(pattern));

        internal void StopAndAssertClean()
        {
            Application.logMessageReceivedThreaded -= OnLog;
            List<string> lines;
            lock (_unexpected)
                lines = new List<string>(_unexpected);
            Assert.That(lines, Is.Empty, "Unexpected FlowField log lines:\n" + string.Join("\n", lines));
        }

        private void OnLog(string message, string stackTrace, LogType type)
        {
            if (message == null || !message.StartsWith("[FlowField"))
                return;
            foreach (Regex pattern in _expected)
                if (pattern.IsMatch(message))
                    return;
            lock (_unexpected)
                _unexpected.Add(type + ": " + message);
        }
    }
}
