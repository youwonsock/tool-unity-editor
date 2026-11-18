using System.Runtime.CompilerServices;

// Only the recorder's own editor assembly saves takes, so only it sees the take and exporter internals.
[assembly: InternalsVisibleTo("Common.OptimizeTool.PhysicsRecording.Editor")]
