# TransformPath 2.0

`Common.TransformPath` builds a path from Transform control points and moves
actors with one canonical runtime API. The 2.0 API is intentionally breaking:
the old controller, queue registry, callback overloads, aliases, and event sink
fallback have been removed.

## Runtime lifecycle

`PathData`, `MultiPathData`, `PathFollower`, `PathEventHandler`, and queue
components expose idempotent `Init`/`Release` methods. Authoring components with
a complete serialized configuration initialize from `Awake`; runtime-created
components can be configured first and initialized explicitly.

```csharp
path.ConfigureControlPoints(points);
path.ConfigureBuildSettings(new PathBuildSettings(
    PathData.ECurveType.SplineInterpolating, 500));
path.ConfigureMovementSettings(PathMovementSettings.Speed(3f));
path.Init();

follower.Init();
follower.StartPlayback(PathPlaybackRequest.Single(path, loop: true));
follower.Seek(0.5f);
```

Invalid or incomplete configuration leaves the provider not ready and reports
one error per failed configuration. A rebuild publishes a complete temporary
cache atomically, so consumers never observe a partial path. `PathChanged`
increments `Revision`. Runtime listeners use a fail-fast contract: the first
listener exception propagates to the caller and later listeners are not
invoked. `SegmentChanged` caches its invocation list when listeners are added
or removed, so segment transitions do not create a listener array.

## Runtime allocation profile

The steady-state TransformPath loop is designed to run without allocations
after initialization and the first provider rebuild. Delayed path events use
a reusable game-time scheduler, event validation reuses its recursion set,
and repeated playback of the same provider and revision reuses its session
and sequence snapshot. Queue registrations recycle their internal entries.
The first encounter with a provider or event tree may grow these caches. Event
messages are delegated to the configured `IPathEventReceiver`; logging and
allocations made by that receiver or by user listeners are outside this runtime
budget.

## Internal utility layers

The runtime keeps small purpose-specific internal helpers instead of a
single broad utility class. `PathValueUtility` owns finite/range predicates,
`PathMovementSettingsUtility` owns movement validation and curve snapshots,
`PathGeometryUtility` owns the shared curve and arc-length algorithms, and
`PathProviderUtility` owns provider and route descriptor checks. The editor
uses `PathEditorSerializationUtility` and `PathEditorUndoUtility`; preview
cache ownership and MultiPath auto-link state remain in their respective
collaborators. These helpers do not own Unity lifecycle or playback state.

## Aggregate paths and sequences

- `PathData` implements `IPathMovementProvider`; its `PathMovementSettings` are
  the single source of truth for normal playback. The follower no longer has
  serialized movement authoring fields.
- `PathPlaybackRequest.Single(provider, loop)` uses the provider's movement
  settings. Start it with `StartPlayback(request)`.
- `PathPlaybackRequest.Aggregate(provider, movement, loop)` treats any ready
  provider as one aggregate path and applies the movement override to that
  playback session only; it does not mutate the provider.
- `PathPlaybackRequest.Sequence(provider, loop)` snapshots the ordered
  segments and uses each referenced `PathData`'s movement settings. Start it
  with the same `StartPlayback(request)` method.
  `PathSegmentConfig` stores only the child `PathData` and the destination
  segment's `PreservePreviousSpeed` flag.
  `SegmentChanged` identifies transitions; `CurrentSegmentIndex` and
  `NormalizedTime` are local to the active segment, while
  `GlobalNormalizedTime` is length-weighted across the sequence.

For a segment transition with `PreservePreviousSpeed`, the previous nominal
speed is converted into the destination mode: SpeedBased receives that speed,
while TimeBased receives `segment length / previous speed` as its duration.
The flag is ignored on initial start and Seek, and applies to natural
transitions including the final-to-first loop transition.

Sequence boundaries use `[start, end)`: an exact boundary selects the next
segment at local `0`; only global `1` selects the final segment at local `1`.
One update can cross multiple segments. Remaining delta time is carried forward
after the bounded transition budget, so a large frame does not lose progress.

`Seek` changes position and the event cursor without changing Moving/Paused
state and does not immediately fire skipped events. `SeekSegment(index, local)`
has the same rule; a local value of `1` selects the next segment at `0` unless
it is the final segment.

`PathBuildSettings` contains only runtime curve type and geometry resolution.
The editor-only preview controls (`Uniform`, `DeterministicRandom`, and
`DistanceBased`) do not affect runtime sampling, and deterministic preview
sampling does not modify `UnityEngine.Random` global state.

## Queue

`QueuedPathManager` owns the single registration list and ordered index. Its
concrete API is:

```csharp
int AgentCount { get; }
IQueuedPathAgent GetAgent(int orderedIndex);
bool Register(IQueuedPathAgent agent);
bool Unregister(IQueuedPathAgent agent);
bool TryGetState(IQueuedPathAgent agent, out PathQueueState state);
void ConfigureRoute(IPathProvider provider);
```

`QueuedPathFollower` registers only while its underlying follower is moving.
Spacing slowdown, manual block, external pause, and route-rebuild blocking are
independent constraints. The manager computes a progress snapshot once per
frame, then sends a speed multiplier and global progress clamp to the follower.

The manager and each follower must reference the same route provider instance.
When route geometry changes, the manager waits for follower snapshots to reach
the new route revision before releasing the temporary block. A structural
segment change safely stops and unregisters all agents.

## Events

`PathEventHandler` validates and applies the movement, time-scale, and delayed
effects defined by `PathEventSettingSO`. For a named event, it sends only the
serialized `EventName` string and the follower to the configured receiver:

```text
PathData event
→ PathEventHandler
→ IPathEventReceiver.ReceivePathEvent(EventName, follower)
→ project-specific receiver processing
```

Set `_receiverObject` to an `IPathEventReceiver` when a named event needs
project-specific handling. Effects can also be used without a receiver. Event
dispatch is fail-fast: a receiver exception is propagated before lifecycle,
time-scale, or delayed processing, and a listener exception stops the remaining
dispatch for that event. The sample showcase uses
`TransformPathSampleMessageReceiver` to log the event name and actor.

## ToyProject showcase

`Scene/TransformPathSample.unity` contains separate Normal, Multi, and Queue
lanes. `TransformPathOverviewController` starts the canonical single and
sequence APIs, and `TransformPathShowcaseUI` exposes compact lower-left tabs
for the selected lane. The old full-screen message overlay and EVENT action
were removed. `TransformPathFreeCamera` keeps its class, namespace, and GUID so
the FlowField showcase can reuse it; it provides Perspective focus, RMB look,
WASD movement, Q/E elevation, wheel speed control, Shift boost, and Escape.

Runtime sources live under `Script/Runtime`, editor tooling under
`Script/Editor`, and sample scripts under `Script/Samples`. They are separated into
`Common.TransformPath.Runtime`, `Common.TransformPath.Editor`, and
`Common.TransformPath.Samples` assemblies. Showcase event settings and
materials live under `Settings/Events` and `Settings/Materials`. Runtime files
are grouped by responsibility: `Core`, `Playback`, `Events`, `Queue`,
`Utilities`, and `Components`.

## Authoring checklist

1. Add at least two control points to `PathData`, choose its curve type, and
   set its Movement Mode plus Speed or Duration.
2. Rebuild after changing control points, movement authoring, or runtime build
   settings. Inspector edits alone do not publish runtime revisions.
3. For a sequence, configure `PathSegmentConfig` entries on `MultiPathData`.
4. Start a follower with `PathPlaybackRequest.Single` for a provider-backed
   path, `PathPlaybackRequest.Aggregate` for an explicit movement override, or
   `PathPlaybackRequest.Sequence` for per-segment playback, then pass the
   request to `StartPlayback`.
5. Subscribe to `StateChanged`, `SegmentChanged`, and `Completed` during the
   component's active lifetime, and unsubscribe in `Release`/teardown.

There are no compatibility aliases in 2.0. Use `EPathMoveType`,
`PathMovementSettings`, `PathPlaybackRequest`, `PathSegmentConfig`, `Seek`,
`SeekSegment`, and `IPathEventReceiver` directly. `PathData` is the authoring
source for initial movement; `PathEventHandler` may still change the follower's
runtime Speed or Duration during a session.
