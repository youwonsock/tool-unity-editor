# TransformPath 수명 처리와 중복 정리

- 작업일: 2026-10-24 (Asia/Seoul)
- 환경: macOS, Unity 2023.2.20f1
- 기준: [이벤트 준비·Queue Pause·Follower 경계 정리](Deduplication.md) 이후의 코드와 자산

## Stop·Release 재진입

`QueuedPathFollower.StopMove`가 내부 Follower를 정지하면 Ready 상태 알림이 동기적으로 전달됩니다. 이 콜백에서 새 Queue 재생을 시작한 뒤 기존 Stop으로 돌아오면, 기존 코드는 현재 등록을 해제하고 새 재생까지 종료했습니다. 등록된 Queue의 Release에서도 등록 해제 중 발생한 Ready 콜백이 만든 새 재생을 후속 Release가 종료했습니다.

수정 전 신규 EditMode 15개 중 7개가 실패했습니다. 실패는 Stop의 같은/다른 Manager 교체 4개(이동/일시정지), Release의 같은/다른 Manager 및 자유 이동 교체 3개입니다. 수정 후 15개 모두 통과했습니다.

- Stop은 호출 전 등록 토큰을 보관합니다. 내부 명령이 반환한 receipt의 재생 ID가 현재와 다르거나 현재 등록이 다른 토큰이면 이전 명령의 후처리를 종료합니다. 정상 Stop의 등록 해제·차단 해제·속도 초기화는 유지합니다.
- 등록된 Queue의 Release는 기존에도 detach를 통해 발생하던 Stop을 명시적으로 호출합니다. 반환 receipt와 현재 재생 ID가 다르면 구독 해제·초기화 해제·내부 Follower Release를 실행하지 않습니다. 새 재생을 시작하지 않은 경우에는 기존 Release를 마칩니다.
- 기존 `PathQueueSession.Release`는 해제 시작 시 확보한 토큰으로만 제약과 등록을 정리합니다. 오래된 토큰과 다른 소유자의 토큰이 현재 등록을 건드리지 못하는 동작을 회귀 테스트로 확인했습니다.

새 Engine·Scope·수명 관리 객체나 상태 필드를 추가하지 않았습니다. PathFollower의 이벤트별 알림 순서와 재진입 검사, 공개 receipt 형식도 유지합니다.

## 중복 제거

`PathPlaybackSession.ProviderMovementSettings`는 Single 생성 시 `MovementSettings`와 같은 설정을 보관하므로 제거했습니다. nullable 생성자 인자와 HasValue 분기도 제거하고 Single 설정 변경 비교에 기존 스냅샷을 사용합니다. Aggregate는 명시적인 override를, Sequence는 구간 descriptor를 계속 사용합니다. Pause 중 경로 변경을 Resume 이후 반영하는 정책도 유지합니다.

`PathFollower.SameQueueRegistration`과 `PathQueueSession.Matches`는 `PathQueueRegistration.Matches` 내부 함수 하나로 통합했습니다. 기존처럼 유효성·소유자 참조·등록 ID·재생 ID를 비교합니다. RouteRevision과 프레임 순서 검사는 제약 적용 경로에서 별도로 유지합니다. 공개 API는 추가하지 않았습니다.

`PathEventDefinitionFactory.Create(PathEventSettingSO)` 전달 함수는 제거했습니다. Handler와 entry 변환이 기존 `PathEventDefinition.FromAuthoring`을 직접 사용합니다. 작성 entry를 runtime event로 만드는 Factory, 불변 스냅샷, 곡선 복사, 지연 이벤트 순서·취소와 유효성 검사는 유지합니다. 모든 기존 파일과 GUID는 제자리에 있습니다.

## 검증

최종 실행 결과와 모든 테스트별 상태, 소스 해시, 원본 XML 경로·해시는 [LifecycleCleanupValidation.json](LifecycleCleanupValidation.json)에 기록했습니다.

- 그래픽 EditMode: **144/144 통과**, 실패·건너뜀 0. 공개 API·직렬화 필드 기준 비교 포함.
- PlayMode: **10/10 통과**, 실패·건너뜀 0. Stop 및 Release 콜백에서 자유 이동·같은 Manager·다른 Manager로 교체한 뒤 실제 프레임의 이동, 수동 차단·재개, CharacterController와 TimeScale 소유권·복원을 확인합니다.
- 에디터 UI 회귀: EditMode에 포함된 **9/9 통과**. MultiPath 양쪽 끝점 자동 연결, 연결점 Undo/Redo, Inspector 강제 동기화·Rebuild, 웨이포인트 그룹 이동·기하 재생성, 그룹 Undo/Redo, 비활성 그룹, Inspector 멤버 교체·정렬을 확인합니다. 실제 EditorWindow에서 Inspector를 그려 입력 이벤트를 보내며, 결과 화면도 확인했습니다.
- 수정 전 실패 7개와 수정 후 통과 15개를 별도 결과로 보관합니다. 최초 PlayMode의 신규 테스트 2개는 TimeScale 조정 시간을 0으로 지정해 기존 유효성 검사에 실패했습니다. 테스트 입력을 0.1초로 고친 뒤 다시 실행했습니다. 런타임의 유효성 조건은 변경하지 않았습니다.

검증은 모듈의 최종 C#/asmdef와 API 기준 파일이 같은 격리 프로젝트에서 수행했습니다. 메인 에디터가 다른 모듈을 실행하고 있어 해당 씬을 전환하거나 저장하지 않았습니다. 전체 프로젝트 통합·Player/실기기·성능 개선 측정은 이번 검증 범위에 포함하지 않습니다.

기존 공개 API 기준 727개 항목(비공개 직렬화 필드 66개 포함), 네임스페이스, FormerlySerializedAs, asmdef, 기존 메타 파일의 경로·GUID·내용과 씬·프리팹·설정·샘플·문서 이미지를 보존했습니다. 기존 에디터 구현도 변경하지 않았습니다. 이전 검증 문서는 당시 결과로 유지합니다.

재실행: `Window → General → Test Runner`에서 EditMode의 `Common.TransformPath.Tests`, PlayMode의 `Common.TransformPath.Tests.PlayMode`를 실행합니다. 에디터 UI 테스트를 포함하려면 EditMode는 그래픽 에디터에서 실행해야 하며 batchmode는 사용하지 않습니다.
