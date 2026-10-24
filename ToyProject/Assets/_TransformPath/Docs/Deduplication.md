# TransformPath 중복·상태 정리와 검증

- 작업일: 2026-10-24 (Asia/Seoul)
- 환경: macOS, Unity 2023.2.20f1
- 기준: [Engine·Scope 통합](Simplification.md) 이후의 코드와 자산

## 이벤트 준비 데이터

`PathEventHandler._preparedEvents`는 이벤트를 복사한 뒤 개수로 Scheduler 용량을 확보할 때만 사용했습니다. 실제 실행은 Session의 이벤트 소스와 커서를 사용하므로 준비 목록을 제거했습니다. Single/Aggregate는 소스의 개수, Sequence는 구간별 개수의 합만 사용합니다.

기존 이벤트 검증, 불변 스냅샷, 같은 위치 이벤트의 작성 순서와 다음 경로 이벤트가 이전 지연 예약을 취소하는 동작을 유지합니다. 외부 작성 SO를 매 프레임 다시 읽지 않습니다. 사용되지 않던 Scheduler `Revision`과 Factory의 목록 변환 overload도 제거했습니다. 지연 이벤트의 게임 시간 기준은 유지합니다.

## Queue Pause와 재진입

변경 전 20개 회귀 테스트 중 18개가 통과하고, Pause 콜백이 새 재생을 시작하는 2개가 실패했습니다. 기존 공개 `PauseMove`는 내부 Follower의 상태 알림에서 Pause 처리를 한 뒤, 콜백이 반환되면 같은 처리를 다시 실행했습니다. 이때 콜백에서 만든 새 Queue 등록을 해제하거나 새 자유 재생을 차단했습니다.

초기화된 facade는 `HandleUnderlyingStateChanged`에서 `ApplyPausedState`를 한 번 실행합니다. 공개 Pause 메서드는 내부 명령의 receipt를 반환합니다. 초기화 해제된 facade를 통해 외부에서 재시작한 Follower를 Pause하는 기존 경로에는 ID·Paused 상태 검사를 붙인 fallback을 남겼습니다.

PlayMode 검증에서 새 Queue 등록은 남아도 실제 진행률이 증가하지 않는 추가 경로를 확인했습니다. Pause 상태 알림에서 설정한 `ExternalPause`가 새 Moving 상태에도 남아 있었습니다. `Moving` 상태 알림이 해당 플래그를 해제하도록 정리했습니다. 공개 Resume와 내부 Follower 직접 Resume 모두 같은 처리를 사용합니다. 초기화 해제 상태의 기존 Resume 호출도 유지합니다. 수동 `ForceBlock`은 독립적인 `ManualBlock` 상태를 계속 사용합니다.

등록·상태 확인에 더해 Manager → Queue → Follower 순서의 Tick에서 진행률 증가를 검증합니다. 실제 PlayMode에서도 자유 재생 및 Queue 재생으로의 콜백 교체 후 이동과 Controller 복원을 확인합니다.

## Follower 경계·완료 처리

Single/Aggregate의 시간·속도 분기 뒤에 반복되던 이벤트 처리와 경계 판정을 모았습니다. 이벤트 콜백은 진행 값과 Duration을 바꿀 수 있으므로, 이동 계산에 사용한 경계 값은 보관하고 경계 판정은 콜백 뒤에 수행합니다.

`TryProcessPlaybackBoundary`는 Single/Aggregate/Sequence의 경계에서 다음 구간, Loop 또는 완료를 선택합니다. `ApplyBoundaryPositionAndFlushEvents`가 끝점 위치 적용과 남은 이벤트 처리를 공유합니다. 기존 완료 처리는 정지 상태·진행률을 설정하고 재생 revision을 증가시킨 뒤 이 공통 처리를 호출합니다.

다음 관찰 가능한 순서를 유지합니다.

- Single/Aggregate Loop: 끝점 위치와 이벤트 → Loop 알림 → 진행률 초기화.
- Sequence 다음 구간: 끝점 위치와 이벤트 → 새 구간 초기화 → SegmentChanged.
- Sequence Loop: 끝점 위치와 이벤트 → 첫 구간 초기화 → Loop 알림 → SegmentChanged.
- 완료: 끝점 위치와 이벤트 → 재생 효과·제약 정리 → Controller 복원 → Completed 상태 알림 → Completed 이벤트.

콜백이 Pause·Stop하거나 재생을 교체하면 기존 ID·revision 검사를 통해 이전 처리를 중단합니다. Queue 진행 상한과 Sequence의 프레임 잔여 시간·전환 횟수 제한도 유지합니다. 새 Engine·Scope·전략 객체를 추가하지 않았습니다.

## 상태 없는 계산

`PathEventRuntime`과 `PathQueueCoordinator`를 정적 클래스로 바꿨습니다. Handler와 Validator의 Runtime 객체 생성, Manager의 Coordinator 객체와 설정 복사 속성을 제거했습니다.

Manager의 설정 5개는 Tick 시작 시 지역 변수로 보관해 순수 계산 함수에 전달합니다. 선행 agent의 콜백에서 설정을 변경해도 같은 프레임의 후속 agent는 기존 값을 사용하고, 다음 프레임부터 새 값을 사용하는 동작을 유지합니다.

런타임 클래스 선언 수는 36개, 인터페이스 수는 16개로 유지됩니다. 상태 없는 클래스 2개가 정적으로 바뀌었으며, 런타임 소스는 총 91줄 감소했습니다. Follower는 1,468줄에서 1,434줄로 줄었습니다. 파일과 기존 GUID는 이동 없이 유지했습니다.

## 검증과 보존

최종 결과는 그래픽 EditMode **129/129**, PlayMode **8/8** 통과이며 실패·건너뛴 테스트는 0입니다. 테스트별 결과·소스 해시·보존 검사는 [DeduplicationValidation.json](DeduplicationValidation.json)에 기록했습니다. 검증은 모듈의 C#/asmdef 67개 및 API 기준 파일이 동일한 격리 프로젝트에서 수행했습니다. 그래픽 EditMode는 기존 Inspector/EditorWindow·Undo/Redo 테스트 9개와 성능 진단 7개를 포함하며, PlayMode는 실제 Unity 프레임에서 실행했습니다. 최종 실행의 컴파일 오류와 프로세스 실패는 없었습니다.

추가 EditMode 22개는 요청 종류·시간/속도·Loop/완료 조합 12개, 공개/내부 Pause·Resume 4개, Pause 콜백 교체 2개, 이벤트 재준비·동일 시각 순서·지연 취소 2개, 초기화 해제 fallback 1개, 프레임 중 Manager 설정 변경 1개입니다. 추가 PlayMode 2개는 콜백 교체 후 실제 이동과 시간/속도 혼합 Sequence의 Loop Pause·완료 순서를 검증합니다.

공개 API·직렬화 기준 727개 항목을 기존 기준과 비교합니다. 기존 메타 파일 122개, 씬·프리팹·설정·샘플 19개를 포함한 보호 대상 141개의 내용과 GUID, 네임스페이스·FormerlySerializedAs·asmdef를 유지합니다. 기존 에디터 코드·샘플·이미지는 수정하지 않았습니다.

초기 전체 EditMode 실행에서 정확히 0을 기대한 테스트가 `1.19e-8`의 부동소수점 값으로 실패해 허용 오차를 적용했습니다. 초기 PlayMode의 재출발 실패와 후속 단독 진단은 외부 정지 플래그 수정의 근거로 기록합니다. 과거 실행 보고서는 당시 결과로 유지합니다.

이 검증은 TransformPath 모듈의 동작과 에디터 UI 회귀 범위입니다. 주 프로젝트 전체 모듈 통합, Player/실기기 실행, 성능 개선 수치는 별도로 측정하지 않았습니다. 기존 성능 진단 테스트의 할당 바이트 측정은 이 에디터 환경에서 지원되지 않습니다.

재실행: `Window → General → Test Runner`에서 EditMode의 `Common.TransformPath.Tests`, PlayMode의 `Common.TransformPath.Tests.PlayMode`를 실행합니다. UI 검증을 포함한 EditMode 명령행 실행에서는 `-nographics`를 사용하지 않습니다.
