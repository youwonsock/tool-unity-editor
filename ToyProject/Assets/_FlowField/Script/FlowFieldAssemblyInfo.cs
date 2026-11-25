using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Common.FlowField.Editor")]
// The Editor-only regression tests (Assets/_FlowField/Dev/Tests).
[assembly: InternalsVisibleTo("Common.FlowField.Tests")]
// Measurement tools of this repository (Assets/_FlowField/Dev); harmless where they are absent.
[assembly: InternalsVisibleTo("Common.FlowField.PlayerProbe")]
[assembly: InternalsVisibleTo("Common.FlowField.PlayerProbe.Editor")]
