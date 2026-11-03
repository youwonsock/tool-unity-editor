# TransformPath

Transform 제어점으로 경로를 만들고 단일 경로, 연결 경로, Queue 이동을 재생하는 시스템입니다. 네임스페이스는 `Common.TransformPath`입니다.

| 항목 | 내용 |
| --- | --- |
| 개발 | 유원석 (You Won Sock) · [GitHub](https://github.com/youwonsock) |
| 환경 | Unity 2023.2.20f1, C# (샘플 표시는 URP, UGUI) |
| 곡선 | `Linear`, `SplineApproximating` (B-Spline), `SplineInterpolating` (Catmull–Rom) |
| 이동 방식 | `TimeBased`: Duration과 TimeCurve · `SpeedBased`: 거리와 속도 |
| 예제 | `Scene/TransformPathSample.unity`, `Prefab/TransformPathShowcase.prefab` |

구현 세부는 [TechnicalNotes.md](Docs/TechnicalNotes.md)에 있습니다.

## 시작하기

1. 샘플 씬을 실행해 Normal, Multi, Queue 레인을 확인합니다. macOS 입력 소스가 한글이면 샘플의 글자 키가 동작하지 않습니다.
2. GameObject에 `PathData`를 추가하고 Inspector의 **Create Path Points**로 제어점을 2개 이상 만듭니다.
3. Movement에서 Duration이나 Speed(`0.001..9999`)를 지정하고 **Rebuild Runtime Path**를 누릅니다.
4. 이동할 GameObject에 `PathFollower`를 추가하고 재생을 시작합니다.

```csharp
using Common.TransformPath;
using UnityEngine;

public sealed class TransformPathReadmeExample : MonoBehaviour
{
    [SerializeField] private PathData _path = null;
    [SerializeField] private PathFollower _follower = null;

    private void Start()
    {
        _path.Init();
        if (!_path.IsReady)
        {
            Debug.LogError("PathData의 제어점과 이동 설정을 확인하세요.", _path);
            return;
        }

        _follower.StartPlayback(PathPlaybackRequest.Single(_path));
    }
}
```

## 구조

<img src="Docs/Images/transformpath-frame.png" alt="TransformPath 재생 시작부터 완료까지의 프레임 흐름" width="55%">

<img src="Docs/Images/transformpath-structure.png" alt="TransformPath 주요 타입의 구조" width="100%">

- **Provider** (`PathData`, `MultiPathData`)는 검증한 기하와 이벤트를 발행하고, 내용이 바뀌면 `Revision`을 올립니다.
- **PathFollower**는 재생과 이동을 맡고, **QueuedPathFollower**는 여기에 Queue 참여를 더합니다. 간격은 `QueuedPathManager`가 계산합니다.
- **PathRuntimeDriver**가 한 프레임의 실행 순서를, **PathExecutionBoundary**가 콜백 시점을 정합니다.

## 재생 API

| 요청 | 이동 설정 |
| --- | --- |
| `PathPlaybackRequest.Single(path, loop)` | 경로의 설정 |
| `PathPlaybackRequest.Aggregate(provider, settings, loop)` | 지정한 설정으로 Provider 전체 |
| `PathPlaybackRequest.Sequence(sequence, loop)` | 구간별 설정 |

| 명령 | 조건 |
| --- | --- |
| `StartPlayback`, `StopMove`, `PauseMove`, `ResumeMove` | `StartPlayback`은 준비된 Provider가 필요 |
| `Seek(0..1)`, `SeekSegment(index, local)` | `SeekSegment`는 Sequence 전용. 건너뛴 이벤트는 실행하지 않음 |
| `SetSpeed(value)`, `SetDuration(value)` | 현재 구간의 이동 방식과 맞을 때, `0.001..9999` |

- 명령은 `PlaybackId`, `StateRevision`, `Changed`를 담은 `PathCommandReceipt`를 반환합니다. 조건을 어기면 `InvalidOperationException`, 값이 범위 밖이면 `ArgumentOutOfRangeException`입니다.
- 런타임에 제어점이나 `PathEventSettingSO`를 바꾸면 `path.Rebuild()`를 호출합니다. 기하만 바뀌면 재생이 이어지고, 사용 중인 이동 설정·이벤트·시퀀스 구성이 바뀌면 멈춥니다.
- 콜백(`StateChanged`, `SegmentChanged`, `Completed`, Queue 알림, 이벤트 수신기)은 그것을 일으킨 작업이 끝난 뒤 순서대로 호출됩니다. 콜백 안에서 모든 명령을 쓸 수 있고, 예외는 Console에 기록만 됩니다.

## Queue

```csharp
_queue.StartQueuedPlayback(_manager, PathPlaybackRequest.Single(_path));
```

Agent와 Manager는 같은 Provider 인스턴스를 씁니다. 세 번째 인자로 최대 진행률을 지정하면 그 지점에서 멈추며 완료되지 않습니다.

| 명령 | 의미 |
| --- | --- |
| `StartFreePlayback(request)` | Queue를 떠나 일반 재생 |
| `ForceBlock`, `ForceUnblock` | 등록을 유지한 채 대기, 해제 |
| `PauseMove`, `ResumeMove` | 등록을 해제하고 멈춤, 재개하며 재등록 |
| `DetachQueue` | 재생을 유지하고 Queue만 해제 |

`OnBlocked`·`OnResumed`·`OnSpeedChanged`로 대기 상태를, `QueueInvalidated`로 경로 무효·Manager 해제·최대 진행률 초과를 알립니다. Queue는 같은 경로 위의 진행 거리만 봅니다. Loop 이음매의 간격, 물리 충돌, 교차 경로 회피는 처리하지 않습니다.

<p align="center">
  <img src="Docs/Images/feature-queue-running.png" alt="Queue 레인이 간격을 유지하며 이동하는 화면" width="48%">
  <img src="Docs/Images/feature-queue-blocked.png" alt="선두를 막아 뒤따르는 Agent가 대기하는 화면" width="48%">
</p>

## 이벤트

`PathData`의 이벤트(`0..0.995` 위치)는 `Rebuild` 때 복사되어 위치순으로 실행됩니다. 속도·Duration·TimeScale 조정, 지연 이벤트, Pause와 Resume을 지원합니다. 이름 알림은 `IPathEventReceiver`를 `PathEventHandler`의 **Receiver Object**에 연결하거나 `OnEventDispatched`를 구독해 받습니다. Pause 이벤트의 구성 규칙은 TechnicalNotes에 있습니다.

<p align="center">
  <img src="Docs/Images/feature-path-events.png" alt="Pause 이벤트에서 멈춘 화면" width="48%">
  <img src="Docs/Images/feature-path-events-resumed.png" alt="지연 뒤 Resume으로 다시 이동하는 화면" width="48%">
</p>

## 기능

**경로:** 왼쪽은 `Linear`, 오른쪽은 곡선 경로를 끝 부근으로 Seek한 화면입니다.

<p align="center">
  <img src="Docs/Images/feature-path-linear.png" alt="Linear 경로 재생 화면" width="48%">
  <img src="Docs/Images/feature-playback-seek.png" alt="곡선 경로에서 Seek한 화면" width="48%">
</p>

**연결 경로:** `MultiPathData`가 여러 Provider를 구간별 이동 설정과 함께 잇습니다. 전체와 구간 진행률을 각각 Seek할 수 있습니다.

<p align="center">
  <img src="Docs/Images/feature-multipath-sequence.png" alt="MultiPath 구간 재생 화면" width="48%">
  <img src="Docs/Images/feature-multipath-seek.png" alt="MultiPath Seek 화면" width="48%">
</p>

**Editor 작성:** 제어점은 Scene View에서, 이벤트는 Inspector에서 편집합니다. 연결점과 그룹 동기화는 Undo/Redo를 지원합니다.

<p align="center">
  <img src="Docs/Images/feature-editor-authoring.png" alt="Scene View의 경로 작성 화면" width="48%">
  <img src="Docs/Images/feature-editor-multipath.png" alt="Scene View의 MultiPath 연결점 편집 화면" width="48%">
</p>
