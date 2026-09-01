# TransformPath

Transform 제어점으로 경로를 만들고 `PathFollower`가 시간/속도 기반으로 이동하는 범용 Unity 시스템입니다. `Common.TransformPath` 네임스페이스를 사용하며 외부 게임 규칙에는 의존하지 않습니다.

## 수명 주기

모든 상태 보유 컴포넌트는 `Awake → Init`, `OnDestroy → Release` 순서를 따릅니다. 입력을 바꿀 때는 `ConfigureControlPoints`/`ConfigureSegments`로 Stale 상태를 만들고 명시적으로 `Rebuild()`합니다. Stale 상태에서 `Sample`, `SampleDistance`, `PathLength`, `GetSegment`을 조회하면 예외입니다. 복구는 `Release → Init`만 허용됩니다.

```csharp
// 비활성 GameObject에서 구성한 뒤 활성화해 Awake → Init 순서를 보장합니다.
pathData.ConfigureControlPoints(points);
pathData.Init();      // 처음 구성할 때
pathData.Rebuild();   // 실행 중 구성 변경 후
Vector3 point = pathData.Sample(0.5f);
pathData.Release();
```

`IPathProvider.Sample`과 `SampleDistance`는 유한하고 경로 범위 안인 입력을 요구합니다. 길이가 0인 경로에서 거리 샘플을 요청하면 예외입니다. `CopyWorldControlPoints`는 유효한 제어점이 두 개 이상일 때만 복사하고, 입력 구조가 부족하면 `ArgumentException`을 던집니다.

## 구성

- 경로 정의: `PathData`, `MultiPathData`
- 이동: `PathFollower`, `QueuedPathFollower`, `QueuedPathManager`
- 이벤트: `PathEventSettingSO`, `PathEventHandler`, `IPathEventReceiver`, `IPathEventSink`
- 시각화: `PathDataLineRenderer`와 에디터 기즈모

쇼케이스 기능 검증용 `TransformPathSampleController`와 `TransformPathSampleMessageReceiver`는 `Script/Temp`에 보관합니다.

`IPathFollower.StartMove(IPathProvider, PathMoveSettings)`로 Provider를 주입할 수 있고 `Seek`으로 진행도를 명시적으로 변경할 수 있습니다. `SetCurveType`/`SetSamplingType`은 설정을 stale 상태로 만들므로 반드시 `Rebuild` 뒤에 조회해야 합니다. `IPathSequenceProvider.GetSegment`은 준비된 MultiPath 구간만 반환합니다. Queue의 `GetDistanceToAhead`는 선행 객체가 없을 때 `null`을 반환하며, `-1` 거리 센티넬은 사용하지 않습니다.

## 이벤트 메시지

`PathEventHandler._eventSinkObject`에는 `IPathEventReceiver` 또는 `IPathEventSink`를 정확히 하나 직렬화해야 합니다. 메시지 이벤트의 Receiver/Sink가 없거나 둘 다 구현한 대상은 예외입니다. EventName이 비어 있는 이동 효과 이벤트는 Receiver 없이 허용됩니다. 지연 이벤트, Curve, 시간·속도 값과 이동 모드는 Coroutine 시작 전에 검증합니다. TimeScale을 바꾼 Handler는 `Release`에서 원래 TimeScale과 FixedDeltaTime을 복원합니다.

## ToyProject 샘플

`Scene/TransformPathSample.unity`는 `(-7,-5) → (-4,4) → (0,-2) → (4,4) → (7,-5)` 제어점의 S자 경로와 속도 3의 Capsule Actor를 포함합니다. `PathDataLineRenderer`가 준비된 결과만 그리고, `Settings/TransformPathMidpointEvent.asset`의 `TransformPath.Sample.Midpoint`를 `Path Message Receiver` 오브젝트가 수신합니다.

전체 Demo 계층은 `Prefab/TransformPathShowcase.prefab`에서 재사용할 수 있습니다.

`TransformPathSampleMessageReceiver`는 `IPathEventReceiver`를 구현하며 `LastMessage`, `ReceivedCount`, `LastFollower`를 공개합니다. 수신 시 Console 로그, Game View 오버레이, 수신 횟수와 Actor의 0.5초 노란색 점멸을 확인할 수 있습니다.

`TransformPathOverviewController`가 기본 경로, MultiPath 두 구간, Queue Follower 3개를 하나의 씬에서 실행합니다. `1/2/3`은 각각 Linear/Uniform, SplineInterpolating/DistanceBased, SplineApproximating/Random 조합을 `Set... → Rebuild → Reset → Resume` 순서로 적용합니다. `Q`는 Queue 표시, `Space`는 Pause/Resume, `R`은 Reset, `←/→`는 Seek, `E`는 테스트 메시지를 즉시 발화합니다. 월드 공간 Overview Board에는 경로 Revision, 진행도, `Sample`/`SampleDistance` 결과, 복사된 제어점 수, 첫 MultiPath 세그먼트 준비 상태, Queue 선행 거리, 마지막 메시지와 수신 횟수가 표시됩니다.

## Editor 도구

PathData와 MultiPathData의 Inspector는 직렬화 값을 자동 보정하지 않습니다. 제어점 동기화는 `Sync Path Points` 버튼으로만 실행되며, 이후 `Rebuild()`가 명시적으로 수행됩니다. `PathDataLineRenderer`는 PathData를 초기화하거나 재빌드하지 않고 준비된 `PathChanged` 결과만 다시 그립니다.
