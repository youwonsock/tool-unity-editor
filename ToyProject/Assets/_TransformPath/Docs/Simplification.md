# TransformPath 구조 단순화

이 문서는 Engine·Scope 통합 당시의 구조와 실행 결과입니다. 이후 이벤트 준비 상태·Queue Pause·Follower 경계 처리·계산 객체를 정리한 현재 결과는 [중복 제거 검증](Deduplication.md)에 있습니다.

- 작업일: 2026-10-24 (Asia/Seoul)
- 환경: macOS, Unity 2023.2.20f1
- 결과: 그래픽 EditMode 107/107, PlayMode 6/6 통과, 실패·건너뛴 테스트 0
- 비교 기준: 이번 단순화 직전의 코드와 자산. 앞선 Aggregate·경로 갱신 수정은 포함한 상태입니다.

## 소유 관계

재생 식별자와 정리 작업만 중계하던 Engine·Scope를 없애고 `PathFollower`가 직접 소유하도록 합쳤습니다. 경로 스냅샷을 가진 `PathPlaybackSession`이 갱신 판단도 맡습니다. Queue는 하나의 `PathQueueSession`에 등록과 대기 상태를 보관합니다.

```text
PathData / MultiPathData
  → PathPlaybackRequest
  → PathFollower: 재생 ID, 상태, 진행·전환, Transform, 종료 정리
      ├─ PathPlaybackSession: 스냅샷, 이벤트 커서, 경로 갱신 판단
      ├─ PathProgressCalculator: 공통 진행 계산
      └─ PathEventHandler: 이벤트 실행과 효과

QueuedPathFollower: 공개 Queue facade와 알림
  ├─ PathFollower: 실제 재생
  └─ PathQueueSession: 등록·제약 연결과 대기·재출발 상태
      └─ QueuedPathManager: 같은 경로의 간격·감속 계산
```

| 항목 | 변경 전 | 변경 후 |
|---|---:|---:|
| 런타임 클래스 선언 | 40 | 36 |
| 런타임 인터페이스 선언 | 16 | 16 |
| 런타임 C# 파일 | 37 | 37 |
| QueuedPathFollower 줄 수 | 992 | 894 |

클래스 수는 런타임 소스의 중첩·정적 클래스를 포함합니다. 파일 수는 기존 GUID를 보존하면서 이미 존재하던 계약을 독립 파일로 옮겼기 때문에 동일합니다.

## 재생 계약

새 코드는 제어와 수명 관리에 `IPathFollower`, 상태 조회에 `IPathPlaybackState`를 사용합니다. `IPathPlaybackControl`, `IPathMovementControl`, `IPathLifecycle`은 기존 코드가 해당 타입으로 대입·캐스팅할 수 있어 삭제하지 않았습니다. `IPathFollower`의 상속 관계와 멤버를 유지하고 세 계약을 `PathLegacyPlaybackContracts.cs`로 모았습니다. `EditorBrowsable(Never)`로 개발 도구의 자동 완성에서 숨기도록 표시했습니다. 실제 표시 여부는 IDE 설정에 따릅니다.

따라서 인터페이스 총수는 유지됩니다. 런타임 내부에서 이 세분화 계약을 별도로 사용하는 경로는 없으며, 기존 호출자를 위한 호환 계약으로 남습니다. Provider·이벤트·Queue 제약 인터페이스는 실제 구현과 소비자가 있으므로 유지했습니다.

## Engine·Scope와 경로 갱신

- `PathPlaybackEngine`의 OwnerId·PlaybackId·StateRevision을 `PathFollower` 필드로 옮겼습니다. 재생 교체·정지·Release 이후 ID와 버전 증가 규칙을 유지합니다. 이전 비직렬화 객체와 같은 도메인 리로드 의미를 위해 재생 상태 필드는 `NonSerialized`로 표시했습니다.
- `PathPlaybackScope`의 범용 정리 목록과 재생마다 생성하던 정리 delegate를 제거했습니다. 공통 `EndPlayback` → `ClearPlaybackResources`가 기존 타입별 정리를 호출합니다. 정리 대상 ID를 먼저 분리하고 새 재생의 ID를 검사해 재진입 콜백에서 시작한 재생을 보호합니다.
- 정지·완료·교체·콜백 실패의 정리, TimeScale 소유권, Queue 제약 해제 및 CharacterController 복원 순서를 유지합니다. 일시정지는 재생과 Controller 억제 소유권을 유지합니다.
- `PathSnapshotRefreshPolicy`의 판단을 `PathPlaybackSession.EvaluateRefresh`로 합쳤습니다. 추가 wrapper나 새 서비스 없이 세션이 자신의 스냅샷과 비교합니다. Sequence 후보 스냅샷은 한 번 만들어 실제 갱신에도 사용합니다.

기하 변경 시 로컬 거리 진행률과 PlaybackId를 유지하고, 이벤트·실제 이동 설정·Sequence 구성이 바뀌면 중단하는 기존 정책을 유지합니다. Aggregate의 명시적 이동 설정은 provider 기본값과 독립적이며, 일시정지 중 갱신은 Resume 후 Tick에서 반영합니다. 상세 정책 표는 [앞선 리팩터링 기록](Refactoring.md#1-경로-변경-정책과-aggregate-오류)에 있습니다.

## Queue 중복 제거

`PathQueueLease`와 `PathQueueHoldPolicy`를 `PathQueueSession`으로 합쳤습니다. 같은 Queue 수명을 공유하는 등록 토큰·제약 연결과 차단·재출발 지연·hysteresis·속도 보간 상태를 한 객체에서 관리합니다. 등록 해제 콜백이 새 등록을 시작해도 이전 토큰만 해제합니다.

`QueuedPathFollower`에서 다음 중복을 제거했습니다.

- 같은 내용을 실행하던 Queue/Playback 콜백 루프를 하나로 통합했습니다. 호출 목록 스냅샷, 호출 순서, 예외 처리와 콜백 사이 PlaybackId·StateRevision 검사를 유지합니다.
- Session 속성을 다시 전달하던 private 속성 7개와 구독 delegate 필드 4개를 제거했습니다. 이벤트는 같은 메서드 그룹으로 해제·등록해 반복 Init/Release에서도 중복 구독을 방지합니다.
- 읽기 위임과 Seek 후 Queue 재계산 후처리를 공유합니다. Unity에서 파괴된 Follower의 비교 결과를 실제 null로 정규화해 기존 기본 반환값을 유지합니다.

`Playback => this`, `UnderlyingFollower`, 모든 공개 메서드와 이벤트를 유지합니다. Pause의 등록 해제, ForceBlock의 등록 유지, Stop·Resume의 Queue 처리는 각 공개 동작에 남겨두었습니다.

## API·직렬화·자산 보존

변경 전에 기존 구현에서 호환성 테스트 9개를 통과시키고 Reflection으로 공개 API 기준을 캡처했습니다. 동일 기준을 변경 후에도 통과했습니다. [API 기준 파일](../Script/Tests/Editor/TransformPathPublicApi.json)의 727개 항목은 공개 타입·상속 인터페이스·생성자·메서드와 매개변수 이름/기본값·속성·이벤트·필드/상수 및 비공개 `SerializeField` 66개를 포함합니다.

기존 메타 파일 118개의 GUID와 바이트 내용이 동일합니다. 씬·프리팹·설정·샘플 파일 19개도 원본과 SHA256이 같습니다. 합계 137개 보호 대상에 내용 변경이 없으며, 네임스페이스·`FormerlySerializedAs` 별칭·asmdef 5개를 유지했습니다. 기존 UML 및 샘플 이미지 자산도 보존했습니다.

아래 5개 파일은 메타 파일을 함께 이동했습니다. 내부 클래스는 제거하거나 통합했고, 해당 파일의 GUID는 옮긴 계약 또는 통합 클래스에서 유지합니다. 이 파일들은 씬에 붙는 MonoBehaviour·ScriptableObject가 아닙니다.

| 변경 전 경로 (`Script/Runtime/` 기준) | 변경 후 경로 | 유지한 GUID |
|---|---|---|
| Playback/PathPlaybackEngine.cs | Core/PathCommandReceipt.cs | `0a4975a22332499db392a2a51c964950` |
| Playback/PathPlaybackScope.cs | Core/PathLegacyPlaybackContracts.cs | `9d1f192cfffd407ba0dcda04d140e915` |
| Playback/PathSnapshotRefreshPolicy.cs | Core/PathPlaybackRequest.cs | `ea2f8e8232a24df5972da9ece0b35ef8` |
| Queue/PathQueueLease.cs | Queue/PathQueueSession.cs | `de433fef2335d41e4ac8f56c1d3280c1` |
| Queue/PathQueueHoldPolicy.cs | Core/PathQueueAgentSettings.cs | `2242d29460d0f45e1894b9d0d53bd9b9` |

## 실행 검증

현재 모듈의 C#/asmdef 66개와 API 기준 파일이 바이트 단위로 일치하는 격리 프로젝트에서 실행했습니다. 기존 씬·프리팹·설정·샘플도 같은 복사본입니다. 다른 모듈을 편집 중인 주 프로젝트 에디터의 작업과 분리해 검증했습니다.

| 실행 | 결과 | 범위 |
|---|---:|---|
| 단순화 전 호환성 기준 | 9/9 | 기존 인터페이스 호출, 식별자, TimeScale 소유권, Queue 콜백·반복 초기화·파괴된 Follower, API 캡처 |
| 그래픽 EditMode | 107/107 | 이식 회귀 24, 런타임 개선 26, 앞선 리팩터링 32, 에디터 UI 9, 단순화 호환성 9, 성능 진단 7 |
| PlayMode | 6/6 | 기존 Showcase 세 레인, Queue Pause/Resume, 비활성화·완료 시 Controller 복원, 실제 프레임 Aggregate 마지막 구간과 갱신, Queue enable 주기별 ID·알림 |

에디터 UI 9개는 Inspector/EditorWindow, MultiPath 자동 연결과 Undo/Redo, 웨이포인트 그룹·멤버 교체 및 동기화를 그래픽 Unity Editor에서 검증합니다. 이번 단순화에서는 별도 수동 화면 순회를 추가하지 않았습니다. 최종 두 실행의 컴파일 오류와 프로세스 실패는 없었습니다.

성능 진단 7개는 에디터에서 수행한 진단이며 실기기 성능 개선을 증명하지 않습니다. 이 환경에서 할당 바이트 측정은 지원되지 않았습니다. 주 프로젝트 전체 모듈 통합 테스트나 Player/실기기 빌드는 이번 결과의 범위에 포함하지 않습니다.

재실행은 `Window → General → Test Runner`에서 EditMode의 `Common.TransformPath.Tests`, PlayMode의 `Common.TransformPath.Tests.PlayMode`를 선택합니다. 명령행에서는 `-runTests -testPlatform EditMode` 또는 `PlayMode`와 해당 namespace의 `-testFilter`를 사용합니다. UI 검증을 포함한 EditMode 실행에서는 `-nographics`를 사용하지 않습니다.

각 테스트 이름·결과·실행 시각·XML 해시, 소스 해시와 보존 검사는 [SimplificationValidation.json](SimplificationValidation.json)에 기록했습니다. 앞선 결과 보고서는 당시 실행 기록으로 유지합니다.
