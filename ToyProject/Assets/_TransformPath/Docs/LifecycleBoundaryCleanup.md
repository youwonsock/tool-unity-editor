# TransformPath Queue 수명과 알림 경계 정리

- 작업일: 2026-10-24 (Asia/Seoul)
- 환경: Unity 2023.2.20f1, macOS
- 상세 증거: [LifecycleBoundaryCleanupValidation.json](LifecycleBoundaryCleanupValidation.json)

## 변경 내용

### Queue Revision과 제약 수명

`PathQueueRegistration`은 등록 소유권을 나타내며 `RouteRevision`은 등록 당시 버전으로 유지합니다. `PathQueueConstraint.RouteRevision`은 이번 계산 버전입니다. Follower는 제약을 현재 Provider Revision과 비교하므로 기하 변경 후에도 동일한 등록에서 차단·감속·진행 상한을 갱신합니다. 토큰 소유자·재생 ID와 프레임 순서 검사도 유지합니다.

Queue facade 역시 다른 재생, 오래된/미래 경로 버전, 이전 프레임 결과를 저장하지 않습니다. 기존 API의 생략된 `PathQueueState.PlaybackId == 0`은 호환성을 위해 허용합니다. 정상 갱신 후 최초 등록 토큰으로 해제할 수 있습니다. 구조·이동·이벤트 변경에 대한 기존 중단 정책은 유지합니다.

### 시작과 Pause·Resume

등록은 `TryRegisterWithManager` 한 곳에서 참여 모드·Manager·현재 Moving 여부·등록 유무를 확인합니다. 시작, Moving 알림, Tick 재시도와 Loop가 같은 함수를 사용합니다. 이미 Moving인 재생을 교체해 StateChanged가 생략되는 경우에도 한 번 등록합니다. 시작 콜백의 Pause·Detach·새 Start를 이전 명령이 되돌리지 않습니다.

Free 시작의 Queue 초기화와 Start·Pause·Resume의 Animator 변경은 알림 전에 완료합니다. 콜백 이후 덮어쓰는 처리를 제거했으므로 새 재생·중첩 Pause/Resume뿐 아니라 Duration만 수정하는 콜백도 현재 Animator 상태를 유지합니다. 시작 후 위치 반영은 명령 receipt로 여전히 유효한지 검사합니다.

### 이벤트 비교와 알림 재진입

이벤트 비교는 준비 때와 동일한 안정 정렬을 적용한 스냅샷 사이에서 수행합니다. 외부 Provider가 정렬되지 않은 이벤트를 제공해도 좌표 변경만으로 재생이 중단되지 않습니다. 동일 위치 이벤트는 작성 순서를 유지합니다. 갱신 후보 스냅샷을 한 번 만들어 비교와 최종 갱신에 재사용합니다.

Follower의 상태·구간·Loop·완료 알림은 공통 내부 호출 함수에서 예외 정리와 재생/상태 변경을 검사합니다. QueueInvalidated도 다른 Queue 알림과 같은 검사를 사용하며, Stop의 Ready 콜백이나 앞선 invalidation listener가 시작한 새 재생에 이전 알림을 전달하지 않습니다. 정상 무효화는 기존 순서로 각 listener에 한 번 전달합니다.

### 작은 상태와 캐시 정리

- Session의 인덱스 하나를 위한 `PathEventCursor` 객체를 `NextEventIndex` 값과 커서 초기화 함수로 통합했습니다.
- Manager가 경로 구조의 동일성을 확인한 뒤 같은 Provider 참조 목록을 다시 수집하던 처리를 제거했습니다.
- 재생 ID·상태 버전·등록 토큰·프레임 스냅샷·Queue 완화 상태와 공개 호환 인터페이스는 유지합니다.
- [TransformPath.uml](../TransformPath.uml)의 커서, 등록 함수, 등록/계산 Revision과 알림 순서를 갱신하고 ELK 렌더링 결과를 확인했습니다.

## 검증

| 항목 | 결과 |
|---|---:|
| 수정 전 초기 회귀 | 17개 중 6개 통과·11개 실패 |
| 최종 신규 EditMode 회귀 | 24/24 통과 |
| 최종 그래픽 EditMode 전체 | 192/192 통과 |
| 최종 PlayMode 전체 | 18/18 통과 |
| EditMode에 포함된 에디터 UI 회귀 | 9/9 통과 |

실패·건너뜀 0, 최종 프로세스 종료 코드 0, 컴파일 오류 0입니다. 최초 회귀 17개 이후 결과의 오래된 버전·프레임·다른 재생 차단 4개와 Duration 변경 콜백 3개를 보강했습니다.

신규 EditMode는 Single·Aggregate·Sequence의 기하 변경 후 Queue 차단/재개와 토큰 해제, 제약 버전·프레임 검사, 시작 콜백 Pause, Moving 상태 교체, 외부 이벤트 정렬, Animator 재진입, QueueInvalidated 재진입과 정상 알림을 확인합니다.

신규 PlayMode 4개는 실제 프레임에서 기하 변경 후 차단·재개, Queued/Free 시작 콜백 Pause와 명시적 Resume, Animator Pause/Resume 재진입, 무효화 콜백의 새 재생 보존을 확인합니다. 기존 Showcase와 완료·이벤트·Queue 회귀도 함께 통과했습니다.

에디터 UI는 그래픽 에디터에서 Inspector 입력·렌더링을 사용해 MultiPath 자동 연결·해제·강제 동기화·Rebuild, Undo/Redo, 웨이포인트 그룹 이동·기하 갱신·멤버 변경·비활성 그룹을 검증했습니다. MultiPath와 그룹 Inspector 캡처도 확인했습니다.

## 보존과 검증 범위

공개 API 기준 727개 항목(비공개 직렬화 필드 66개 포함)을 비교했습니다. 기존 메타 134개와 보호 파일 183개의 바이트가 동일하며, 기존 씬·프리팹·설정·샘플 19개 파일, 에디터 코드·메타 26개, 이미지·메타 26개, asmdef 5개를 유지했습니다. 네임스페이스·직렬화 별칭 변경, 기존 파일 삭제·이동, 중복 GUID가 없습니다. 런타임 코드 줄 수는 8,294에서 8,228로 줄었습니다.

검증한 격리 프로젝트와 최종 C#/asmdef 71개의 바이트가 일치합니다. 메인 씬을 전환하거나 저장하지 않았습니다. 전체 프로젝트 통합·Player·실기기·성능 벤치마크와 별도 수동 포인터 조작은 이번 검증 범위에 포함하지 않았습니다.

재실행: `Window → General → Test Runner`에서 EditMode `Common.TransformPath.Tests`, PlayMode `Common.TransformPath.Tests.PlayMode`를 실행합니다. UI 회귀를 포함하려면 그래픽 에디터를 사용해야 합니다.
