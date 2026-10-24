# 알림 경계와 현재 API 정리

검증일: 2026-10-24 · Unity 2023.2.20f1 · `Common.TransformPath`

## 변경 결과

시작 콜백에서 Duration·Speed를 바꿔도 초기 위치를 적용합니다. 시작 후처리의 유효성은 공개 StateRevision 대신 재생 ID와 Tick 경계로 판단합니다. Stop·Pause·Seek·새 Start는 이전 시작 처리를 중단하며, 콜백의 회전 설정과 CharacterController 복원 동작은 유지합니다.

Queue 콜백에서 ForceBlock·ForceUnblock·Detach·재등록 등으로 알림의 의미가 바뀌면 이전 리스너 순회와 후속 알림 묶음을 중단합니다. 이를 위한 내부 알림 Revision은 공개 PlaybackId·StateRevision과 분리했습니다. 이후 프레임에서 발생하는 정상 알림은 계속 전달됩니다. 완료·무효화와 Source.StopMove의 Ready 알림 이후에도 같은 검사를 적용합니다.

Replaced 해제는 기존 해제 사유 처리에서 이동을 유지하므로 중복된 `_suppressDetachStop` 래퍼를 제거했습니다. Explicit Detach와 Loop 재등록에서 필요한 보호는 유지합니다.

## 제거한 이전 계약

이번 정리에서는 사용자의 레거시·이전 버전 호환 코드 제거 요청에 따라 다음 공개 계약도 정리했습니다.

| 제거 대상 | 현재 계약 |
|---|---|
| `IPathPlaybackControl`, `IPathMovementControl`, `IPathLifecycle` | `IPathFollower`에 기존 명령 10개를 직접 선언하고 `IPathPlaybackState` 상속 |
| 인터페이스 전용 `PathLegacyPlaybackContracts.cs`, 빈 `PathEventDefinitionFactory.cs` | 두 파일과 해당 `.meta` 삭제. 실행 구현·직렬화 자산 참조 없음 |
| `PathSegmentAuthoring._providerObject`의 `FormerlySerializedAs("_pathData")` | 현재 필드명만 사용. 프로젝트의 MultiPath 직렬화 블록 2개 모두 이미 현재 필드 사용 |
| 소유자 없는 공개 `PathQueueRegistration` 생성자 | `QueuedPathManager.Register`가 발급하는 소유권 있는 토큰 사용 |
| `PathQueueState`의 PlaybackId·FrameId 기본 인자와 재생 ID 0 허용 | 두 값을 명시하고 현재 재생 ID와 다른 계산 결과는 거부 |
| Release 후 구독이 없는 facade의 특수 Pause 후처리 | `Init`으로 다시 초기화한 facade의 StateChanged 처리 사용 |

일반적인 Unity 오브젝트 파괴·해제 상태의 안전한 조회, 실제로 사용 중인 이벤트 값과 미리보기 대체 그리기는 현재 기능이므로 유지했습니다. 샘플의 다른 `_pathData` 필드는 위 세그먼트 별칭과 관계없는 현행 필드입니다.

공개 API 기준은 727개에서 714개로 갱신했습니다. 정확한 차이는 기존 선언·상속 관계·생성자 표현 24개 제거와 직접 명령 선언·필수 인자 생성자 표현 11개 추가입니다. 예상 목록 밖의 차이는 없고, 직렬화되는 비공개 필드 66개도 그대로입니다. 제거한 인터페이스·생성자·기본 인자를 사용하던 외부 소스는 현재 계약을 사용해야 합니다.

README는 현재 책임·재생 요청·프레임 순서·Seek·Queue·이벤트 수명·에디터 사용법으로 다시 구성했습니다. UML도 현재 인터페이스와 Queue 알림 경계를 반영했습니다. 과거 Docs는 작업 당시의 검증 기록으로 보존했습니다.

## 검증

| 검증 | 결과 |
|---|---|
| 수정 전 재현 | 8개 중 4개 실패: 시작 위치 3개, ForceBlock 후 이전 알림 계속 전달 1개 |
| 알림 경계 집중 검사 | 32/32 통과 |
| 최종 EditMode | 279/279 통과, 건너뜀 0 |
| 최종 PlayMode | 27/27 통과, 건너뜀 0 |
| 그래픽 에디터 회귀 | EditMode에 포함된 9/9 통과 |
| 현재 공개 API·직렬화 필드 | 기준 일치 |
| UML | PlantUML PNG 렌더링 및 이미지 확인 |

시작 설정 변경은 Single·Aggregate·Sequence에서 확인했습니다. Stop·Pause·Seek·새 Start·회전 설정 변경, Queue의 차단·재개·속도·완료·무효화 콜백, Replaced·Detach·Loop도 검사했습니다. PlayMode에서는 실제 프레임 진행과 CharacterController 억제·복원, Queue 재개와 알림 중단을 확인했습니다.

에디터 검사는 실제 Custom Inspector를 그래픽 EditorWindow에 그리고 MouseDown·MouseUp 이벤트를 전달했습니다. MultiPath 양방향 자동 연결, 비활성화, 강제 동기화·Rebuild 버튼, 웨이포인트 그룹의 임의 멤버 이동·목록 변경·비활성화, Undo/Redo와 정렬 버튼을 검증했습니다. Inspector 캡처도 확인했습니다. 운영체제 포인터를 직접 조작한 검증은 아닙니다.

## 보존과 범위

이번 작업 시작 시점의 바이트 해시와 비교했습니다. 씬·프리팹·설정·샘플, asmdef, 기존 이미지와 과거 검증 문서는 변경하지 않았습니다. 삭제한 미사용 스크립트 2개의 GUID를 참조하는 `.unity`·`.prefab`·`.asset`은 전체 `ToyProject/Assets` 검색에서 없었습니다. 삭제한 `.meta` 2개를 제외한 기존 `.meta` 141개는 바이트가 같습니다. 네임스페이스와 남아 있는 자산 GUID는 유지했습니다.

검증은 현재 C#·asmdef·API 기준 파일과 바이트가 같은 별도 Unity 프로젝트에서 수행했습니다. 기존 작업 중인 Unity 인스턴스의 씬이나 재생 상태는 조작하지 않았습니다. 전체 게임 통합·Player 빌드·기기·성능 검증은 이 결과에 포함하지 않습니다.

원본 결과 경로, SHA-256, API 차이와 자산 보존 목록은 [검증 JSON](NotificationAndApiCleanupValidation.json)에 있습니다.
