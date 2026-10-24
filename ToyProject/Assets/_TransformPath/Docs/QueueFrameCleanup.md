# TransformPath Queue 프레임 처리와 저장 구조 정리

- 작업일: 2026-10-24 (Asia/Seoul)
- 환경: macOS, Unity 2023.2.20f1
- 기준: [Stop·Release 재진입과 세션 중복 정리](LifecycleCleanup.md) 이후 코드와 자산

## 재진입 문제와 수정

Manager는 프레임 시작에 agent·등록 토큰·진행률을 복사합니다. 기존에는 앞 agent의 `ApplyQueueState` 콜백이 뒤 agent를 해제해도 복사본을 계속 처리해, 해제된 agent의 상태를 `_states`에 다시 저장하고 `ApplyQueueState`도 호출했습니다. 실제 등록이 사라진 뒤에도 agent 기준 `TryGetState`가 true를 반환했습니다.

새 EditMode 테스트 10개를 수정 전에 실행한 결과 4개 통과·6개 실패였습니다. 해제, 같은 재생 ID로 재등록, 새 재생으로 등록 교체, 등록 갱신 없이 재생 ID 변경, Manager Release, 경로 교체의 6가지 콜백 모두 오래된 항목에 상태를 전달했습니다.

`TryGetCurrentEntry`는 기존 agent → 등록 인덱스 조회를 사용해 현재 등록 토큰과 프레임 토큰, 현재 재생 ID를 확인합니다. 계산 전에 검사하고, 계약 속성 접근도 사용자 코드를 실행할 수 있으므로 발행 직전에 다시 검사합니다. 불일치하면 상태 저장과 통지를 모두 건너뜁니다. 재등록·신규 등록은 다음 프레임에서 계산합니다. 토큰을 찾기 위한 프레임별 선형 탐색은 추가하지 않았습니다.

등록 검사만 적용한 중간 검증에서 10개 모두 통과한 뒤 저장 구조를 정리했습니다.

## 상태와 스냅샷 저장

- Manager의 별도 `_states` 사전을 제거하고 기존 `QueueEntry` 안에 nullable `PathQueueState`를 보관합니다. null은 아직 결과가 없음을 뜻합니다. 해제·레코드 풀 재사용 시 상태도 비워지므로 등록 수명과 조회 가능한 상태의 수명이 일치합니다.
- 두 공개 `TryGetState`는 기존 등록 조회 경로를 사용하고 공통 내부 상태 읽기로 결과를 반환합니다. 등록 직후·해제 후·다른 Manager의 토큰에는 false, 첫 계산 뒤에는 true를 반환합니다. 중복 등록은 기존 결과를 유지합니다. 현재 콜백 안에서 자신의 발행된 상태를 조회할 수도 있습니다.
- `PathRuntimeDriver`의 대상·재생 ID·등록 버전 목록 3개를 `List<FrameEntry>` 하나로 합쳤습니다. `FrameEntry`는 readonly 값 타입이며 세 값이 함께 복사됩니다. 실행 순서, 같은 순서의 등록 안정성, 중복 등록 무시, 해제·재등록·재생 교체 시 해당 프레임 실행 보류와 오류 처리 의미를 유지합니다.
- 쓰기만 하던 `PathFollower._queueConstraintRouteRevision`과 사용하지 않는 `StartFreePlayback.previousPlaybackId`를 제거했습니다. 실제 RouteRevision·프레임 검증은 그대로 유지합니다.
- Manager의 `FindRegistrationIndex`도 기존 `PathQueueRegistration.Matches`를 사용합니다. 요청 토큰이 해당 Manager 소유인지 검사하는 바깥 조건을 유지합니다.

살아 있는 나머지 agent의 앞 agent·거리·진행 상한은 기존처럼 프레임 시작 스냅샷을 사용합니다. 예를 들어 프레임 중 중간 agent가 해제되면 그 뒤 agent는 해당 프레임에 기존 간격을 사용하고 다음 프레임에 변경된 순서를 반영합니다. 이 변경은 오래된 등록에 결과를 저장·전달하는 것을 차단합니다.

## 검증

최종 결과·모든 테스트별 상태·소스 및 XML 해시·보존 검사는 [QueueFrameCleanupValidation.json](QueueFrameCleanupValidation.json)에 기록했습니다.

- 그래픽 EditMode **154/154 통과**, 실패·건너뜀 0.
- PlayMode **11/11 통과**, 실패·건너뜀 0.
- EditMode에 포함된 에디터 UI 회귀 **9/9 통과**: MultiPath 양쪽 연결점 동기화, 자동 연결 해제, Inspector 강제 동기화·Rebuild, 연결점 Undo/Redo, 웨이포인트 그룹 동기화·기하 갱신·Undo/Redo·멤버 변경·비활성 그룹.
- 공개 API·직렬화 기준 727개 항목(비공개 직렬화 필드 66개 포함) 비교와 기존 Driver의 순서·재등록·재생 교체·오류 처리 회귀 통과.
- 신규 EditMode 10개는 앞서 실패한 6가지 변경, 새 agent의 다음 프레임 참여, 상태 조회·풀 재사용·소유권, 자기 해제, 남은 agent의 프레임 간격 정책을 검증합니다.
- 신규 PlayMode 1개는 실제 Queue Follower를 프레임 중 해제하거나 새 재생으로 교체하는 두 경우를 확인합니다. 기존 프레임 결과가 상태 조회에 나타나지 않고, 새 재생이 이후 프레임에서 이동하며 Controller·등록 상태가 정상인지 확인합니다.

최초 PlayMode에서 기존 10개는 통과했고 신규 1개는 관측용 agent를 Queue에 남겨 새 재생의 앞을 막는 테스트 구성 때문에 이동 검증이 실패했습니다. 관측 직후 해당 agent를 해제하도록 테스트만 수정하고 전체 검증을 다시 수행했습니다.

검증은 C#/asmdef와 API 기준 파일이 동일한 격리 프로젝트에서 수행했습니다. 메인 에디터는 다른 모듈을 실행 중이므로 활성 씬을 전환하거나 저장하지 않았습니다. 기존 네임스페이스·FormerlySerializedAs·GUID·메타 경로·씬·프리팹·설정·샘플·asmdef·에디터 코드·문서 이미지를 보존했습니다. 이전 검증 문서는 당시 결과로 유지합니다.

이 결과는 TransformPath 모듈 검증입니다. 전체 프로젝트 통합, Player/실기기 실행, 성능 개선 수치 측정은 수행하지 않았습니다. 그래픽 에디터 테스트는 실제 Inspector 렌더링·입력 이벤트와 Undo/Redo를 자동화하고 화면을 확인했으며 별도 수동 조작 검증은 수행하지 않았습니다.

재실행: `Window → General → Test Runner`에서 EditMode의 `Common.TransformPath.Tests`, PlayMode의 `Common.TransformPath.Tests.PlayMode`를 실행합니다. UI 회귀가 포함된 EditMode는 batchmode 없이 그래픽 에디터에서 실행해야 합니다.
