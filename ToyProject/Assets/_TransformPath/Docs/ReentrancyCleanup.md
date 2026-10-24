# TransformPath 재진입 오류와 데이터 중복 정리

- 작업일: 2026-10-24 (Asia/Seoul)
- 환경: Unity 2023.2.20f1, macOS
- 기준: [Queue 프레임 정리](QueueFrameCleanup.md) 이후의 코드·자산
- 상세 결과: [ReentrancyCleanupValidation.json](ReentrancyCleanupValidation.json)

## 재현과 수정

분석에서 발견한 재진입 오류 4종을 확장해 EditMode 회귀 12개로 고정했습니다. 수정 전에는 정상 완료·정상 재등록 2개가 통과하고, 문제 상황 10개가 실패했습니다. 수정 후 12개 모두 통과했습니다.

| 상황 | 이전 결과 | 수정 |
|---|---|---|
| Manager의 해제 통지 중 다시 Release | 중첩 호출이 공유 해제 목록을 지워 인덱스 예외 발생, 뒤 agent 통지 누락 | 이미 해제 중이면 바깥 호출이 목록·통지·레코드 회수를 끝내도록 유지 |
| 등록 교체 콜백에서 Manager Release 또는 경로 교체 | 해제된 Manager 또는 다른 경로에 agent가 다시 등록됨 | 등록 진입, 경로 갱신 후, 교체 콜백 후에 동일한 등록 조건 검사. 무효 요청은 InvalidOperationException으로 거절 |
| Single·Aggregate·Sequence 완료 콜백에서 Follower Release | Tick이 제거된 세션에 접근해 NullReferenceException 발생 | 콜백 후 재생 변경·중단을 먼저 검사. 구간 전환 여부는 이미 계산한 값으로 처리 |
| Queue Completed 콜백에서 새 재생 시작 | 이전 완료의 OnCompleted가 새 재생 상태에 전달됨 | 콜백 묶음의 실행 유지 여부를 반환하고 변경 시 후속 알림 묶음 중단 |

Manager의 일반 경로 교체·구조 갱신 중 콜백이 Manager를 해제하거나 재구성한 경우도 이전 작업이 이후 상태를 덮어쓰지 않도록 검사합니다. 해제 콜백 하나가 예외를 던져도 나머지 agent 통지와 풀 회수를 진행하고 원래 예외를 전달합니다. 정상적인 재진입 등록은 새 토큰을 유지합니다.

Queue의 차단·재개 알림 뒤 속도 알림에도 같은 콜백 중단 규칙을 적용했습니다. 정상 완료는 기존처럼 Completed 다음 OnCompleted를 한 번씩 전달합니다.

## 비교·정렬·상태 소유권

- `PathRuntimeEvent`가 이벤트 위치 비교와 배열의 안정 정렬을 제공합니다. `PathData`의 발행 과정과 `PathEventSourceSnapshot`의 준비 과정이 같은 구현을 사용합니다. 같은 위치의 이벤트는 기존 작성 순서를 유지합니다.
- `PathEventDefinition`이 재귀적인 정의 비교를 소유합니다. 이름, 사용 플래그, 값, 기간, 곡선, 지연 시간과 자식 정의를 기존 기준으로 비교합니다. 내부의 불변 곡선을 직접 비교하므로 비교용 곡선 복사도 제거했습니다.
- 이벤트 작성 데이터 변환은 `PathEventDefinition.FromAuthoring`으로 통일했습니다. 중복 위임만 하던 내부 `PathEventDefinitionFactory` 타입은 제거했고, 기존 스크립트 경로와 `.meta`·GUID는 유지했습니다.
- Manager의 `_routeStructure`는 구조 비교에 필요한 Provider 참조만 보관합니다. descriptor 검증은 유지하며, 사용하지 않던 이동 설정·곡선·속도 보존 옵션의 추가 복사를 없앴습니다.
- Queue Follower의 `_managerState`와 `_hasManagerState`를 nullable `PathQueueState` 하나로 통합했습니다. 결과가 없을 때 null을 사용하므로 해제·Seek·재등록 시 이전 결과의 참조도 함께 지워집니다.
- 재생 ID, 상태 버전, 등록 토큰, 프레임 스냅샷, Queue의 대기·속도 완화 상태는 각자의 수명과 동작이 있어 유지합니다.

새 EditMode 이벤트 테스트는 15가지 정의 변경에 대해 발행 Revision과 재생 스냅샷의 비교가 일치하는지 확인합니다. 변경 없는 Rebuild, 발행 전 작성 데이터 수정의 격리, 같은 위치의 이벤트 순서, 외부 Provider 준비 시 원본 보존도 검증합니다.

## 현재 실행 구조와 UML

재생 요청은 Follower가 처리하고 Session이 경로·이벤트 스냅샷과 갱신 정책을 관리합니다. RuntimeDriver의 한 프레임은 EventHandler(-200), Queue Manager(-180), Queue Follower(-110), Follower(-100) 순서입니다.

[TransformPath.uml](../TransformPath.uml)에 현재 Session·스냅샷·Queue 상태 소유권, 값 타입, 호환용 인터페이스와 정적 계산 의존성을 반영했습니다. 삭제된 Engine과 정적 유틸리티의 잘못된 합성 관계를 제거했습니다. PlantUML 1.2024.6의 ELK 레이아웃으로 렌더링하고 결과를 확인했습니다. 기존 문서 이미지와 메타는 보존했습니다.

## 최종 검증

| 검증 | 결과 |
|---|---:|
| 수정 전 재진입 회귀 | 12개 중 2개 통과·10개 실패 |
| 수정 후 재진입 회귀 | 12/12 통과 |
| 그래픽 EditMode 전체 | 168/168 통과 |
| PlayMode 전체 | 14/14 통과 |
| EditMode에 포함된 에디터 UI 회귀 | 9/9 통과 |

최종 EditMode·PlayMode는 실패·건너뜀 0, 프로세스 종료 코드 0이며 컴파일 오류가 없습니다. 공개 API·직렬화 기준 727개 항목(비공개 직렬화 필드 66개 포함) 비교도 통과했습니다.

PlayMode 신규 3개는 실제 프레임에서 아래 사항을 검증합니다.

1. 실제 Queue Follower들의 중첩 Manager 해제, 모든 해제 알림, 등록 제거와 CharacterController 복원.
2. 완료 콜백의 Release·새 재생 시작, 이전 알림 중단과 새 재생의 후속 이동.
3. Manager 프레임 안의 등록 교체 콜백이 Manager를 해제하거나 경로를 바꾼 경우 무효 등록 방지.

UI 회귀는 실제 Inspector 입력과 렌더링을 통해 MultiPath 자동 연결·해제·강제 동기화·Rebuild, 연결점 Undo/Redo, 웨이포인트 그룹 동기화·기하 갱신·Undo/Redo·멤버 변경·비활성 그룹을 검증했습니다. MultiPath와 그룹 Inspector의 최종 캡처도 확인했습니다.

검증은 C#/asmdef 70개와 API 기준 파일이 같은 격리 Unity 프로젝트에서 진행했습니다. 메인 에디터의 기존 씬을 전환하거나 저장하지 않았습니다. 기존 네임스페이스·직렬화 별칭, 메타 파일 131개, 씬·프리팹·설정·샘플 파일 19개, asmdef 5개, 에디터 코드와 메타 26개, 기존 이미지와 메타 26개의 보존을 확인했습니다. 기존 파일 삭제·이동은 없습니다.

전체 프로젝트 통합, Player·실기기 실행과 성능 벤치마크는 이번 검증 범위에 포함하지 않았습니다. 에디터 UI는 자동화된 그래픽 회귀와 캡처 확인이며 별도 수동 조작 세션은 수행하지 않았습니다.

재실행: `Window → General → Test Runner`에서 EditMode의 `Common.TransformPath.Tests`, PlayMode의 `Common.TransformPath.Tests.PlayMode`를 실행합니다. UI 회귀를 포함하려면 EditMode는 그래픽 에디터에서 실행해야 합니다.
