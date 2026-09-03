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
path.Init();

follower.Init();
follower.StartMove(path, PathMoveSettings.Speed(3f, loop: true));
follower.Seek(0.5f);
```

Invalid or incomplete configuration leaves the provider not ready and reports
one error per failed configuration. A rebuild publishes a complete temporary
cache atomically, so consumers never observe a partial path. `PathChanged`
increments `Revision`; listeners are invoked from a snapshot and one listener
exception does not stop the others.

## Aggregate paths and sequences

- `StartMove(IPathProvider, PathMoveSettings)` treats the provider as one
  aggregate path. A `MultiPathData` passed here is sampled as one continuous
  length-indexed route.
- `StartSequence(IPathSequenceProvider, PathSequenceSettings)` snapshots the
  ordered segments and applies each segment's move type, value, and time curve.
  `SegmentChanged` identifies transitions; `CurrentSegmentIndex` and
  `NormalizedTime` are local to the active segment, while
  `GlobalNormalizedTime` is length-weighted across the sequence.

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

`PathEventHandler` is optional authoring support for `PathEventSettingSO`. Set
its `_receiverObject` to an `IPathEventReceiver` when a named event needs a
receiver; speed, duration, time-scale, and delayed effects can also be used
without a receiver. Receiver and event listeners are isolated so an exception
does not prevent movement cleanup.

## ToyProject showcase

`Scene/TransformPathSample.unity` contains separate Normal, Multi, and Queue
lanes. `TransformPathOverviewController` starts the canonical single and
sequence APIs, and `TransformPathShowcaseUI` exposes compact lower-left tabs
for the selected lane. The old full-screen message overlay and EVENT action
were removed. `TransformPathFreeCamera` keeps its class, namespace, and GUID so
the FlowField showcase can reuse it; it provides Perspective focus, RMB look,
WASD movement, Q/E elevation, wheel speed control, Shift boost, and Escape.

The sample scripts live under `Script/Samples`. Runtime, editor, and sample
code are separated into `Common.TransformPath.Runtime`,
`Common.TransformPath.Editor`, and `Common.TransformPath.Samples` assemblies.

## Authoring checklist

1. Add at least two control points to `PathData` and choose its curve type.
2. Rebuild after changing control points or runtime build settings.
3. For a sequence, configure `PathSegmentConfig` entries on `MultiPathData`.
4. Start a follower with `StartMove` for aggregate playback or `StartSequence`
   for per-segment playback.
5. Subscribe to `StateChanged`, `SegmentChanged`, and `Completed` during the
   component's active lifetime, and unsubscribe in `Release`/teardown.

There are no compatibility aliases in 2.0: use `EPathMoveType`, `MoveType`,
`PathSegmentConfig`, `Seek`, `SeekSegment`, and `IPathEventReceiver` directly.
