# TransformPath 완료 판정·Queue 소유권·캐시 정리

- 작업일: 2026-10-24 (Asia/Seoul)
- 환경: Unity 2023.2.20f1, macOS
- 상세 증거: [BoundaryOwnershipCleanupValidation.json](BoundaryOwnershipCleanupValidation.json)

## 완료 판정 통일

Single·Aggregate는 이벤트 처리 이전에 캡처한 Duration과 이벤트 처리 후 재계산된 경과 시간을 비교하고 있었습니다. Duration 5초 경로의 50% 이벤트에서 20초로 늘리면 즉시 완료되고, 끝부분 이벤트에서 2.5초로 줄이면 끝에 도착해도 완료가 다음 Tick으로 밀렸습니다. Sequence는 현재 Duration을 사용해 같은 조건에서 정상 동작했습니다.

`PathFollower.HasReachedSegmentEnd`로 완료 조건을 공유합니다. TimeBased는 이벤트 처리 후의 현재 Duration과 현재 경과 시간을 비교하고, SpeedBased는 기존 구간 길이와 이동 거리 기준을 유지합니다. Queue 진행 상한과 이벤트·Loop·구간 전환·완료 알림 순서는 유지합니다.

## 내부 Follower 교체와 Queue 소유권

이미 Moving인 내부 Follower에 직접 `StartPlayback`을 호출하면 StateChanged가 발생하지 않아, 이전 Queue 등록이 남고 새 재생에는 제약이 빠졌습니다. Queue는 차단·등록 상태를 보고하면서도 실제로 이동할 수 있었습니다.

Queue Tick과 Moving 알림은 같은 등록 함수에서 참여 모드, Manager, Provider, 토큰의 PlaybackId를 확인합니다. 동일 경로의 재생 교체는 이전 토큰만 해제하고 새 재생으로 재등록합니다. 새 Manager 결과 전에는 대기하며, 수동 정지와 요청한 최대 진행률을 보존합니다. 이전 토큰으로 새 등록을 해제할 수 없습니다.

다른 경로 또는 해제·비활성 Manager에 대한 직접 시작은 ProviderInvalid 또는 ManagerReleased로 무효화하고 종료합니다. 정상 StartQueuedPlayback의 동일 경로 계약을 직접 시작에도 적용합니다. Ready·무효화 콜백이 선택한 새 재생은 보존하며, 내부 무효화가 끝난 뒤 이전 Moving 알림을 전달하지 않습니다.

## 미리보기 캐시 수명

정적 미리보기 캐시에 경로 24개를 만든 뒤 삭제해도 24개가 남는 문제를 재현했습니다. 기존 PathEditorChangeMonitor의 구조 변경과 편집 모드 갱신에 삭제된 PathData 항목의 정리를 연결했습니다. 살아 있는 경로는 기존 미리보기와 버퍼를 재사용합니다. PlayMode 전환과 어셈블리 리로드에서는 캐시를 비웁니다.

삭제 후 캐시 해제뿐 아니라 Preview Scene 종료 시 해당 경로의 캐시가 제거되고 다른 씬의 살아 있는 미리보기는 같은 객체로 유지되는 것을 검증했습니다. 이 테스트는 활성 씬을 변경하거나 저장하지 않습니다. 정리 이후 실제 힙 회수량이나 성능은 측정하지 않았습니다.

## 중복 상태 정리

- Queue의 ExternalPause를 제거했습니다. Pause 상태는 내부 Follower가 소유하고, Queue는 기존 Pause 알림 처리에서 등록을 해제하고 차단·배율을 반영합니다. Moving일 때만 Queue 정책을 계산하는 기존 흐름과 해제된 facade의 Pause 호환 처리를 유지합니다.
- Manager의 전역 `_awaitingFollowerSnapshots`와 전체 Agent를 다시 확인하는 AllSnapshotsCurrent 순회를 제거했습니다. 각 Agent의 SnapshotRevision과 현재 RouteRevision을 직접 비교합니다. 새 등록 또는 이후 뒤처진 스냅샷도 같은 규칙으로 차단하고, 갱신되면 재개합니다.
- 재생·상태 ID, 등록 토큰, 프레임 스냅샷, 수동 정지·재개 지연·속도 완화 상태와 공개 호환 인터페이스는 유지합니다.
- [TransformPath.uml](../TransformPath.uml)에 공통 완료 판정, 직접 교체의 소유권 조정과 상태 소유권을 반영하고 ELK 렌더링 결과를 확인했습니다.

## 최종 검증

| 항목 | 결과 |
|---|---:|
| 수정 전 초기 재현 회귀 | 12개 중 6개 통과·6개 실패 |
| 신규 EditMode 회귀 | 20/20 통과 |
| 그래픽 EditMode 전체 | 212/212 통과 |
| PlayMode 전체 | 21/21 통과 |
| EditMode에 포함된 에디터 UI 회귀 | 9/9 통과 |

최종 실패·건너뜀 0, 프로세스 종료 코드 0, 컴파일 오류 0입니다. 초기 재현 12개에 직접 교체의 진행 상한·잘못된 참여·콜백 교체, Agent별 스냅샷 검사, Preview Scene 종료 검증을 추가했습니다.

신규 PlayMode 3개는 실제 프레임에서 Single·Aggregate·Sequence의 Duration 이벤트 후 이동 지속, 직접 Follower 교체 후 수동 대기와 재개, 다른 경로로 교체한 경우의 무효화와 콜백이 선택한 새 재생을 확인합니다. 기존 Showcase·Queue·완료·이벤트·Pause/Resume 회귀도 함께 통과했습니다.

UI 회귀는 그래픽 에디터에서 Inspector 입력·렌더링을 사용합니다. MultiPath 자동 연결·해제·강제 동기화·Rebuild, 연결점 Undo/Redo, 웨이포인트 그룹 동기화·기하 갱신·Undo/Redo·멤버 변경·비활성 그룹을 검증하고 Inspector 캡처를 확인했습니다.

## 보존과 범위

공개 API 기준 727개 항목(비공개 직렬화 필드 66개 포함) 비교를 통과했습니다. 기존 메타 137개와 보호 파일 185개가 동일합니다. 기존 씬·프리팹·설정·샘플 19개 파일, 이미지·메타 26개, asmdef 5개를 유지했습니다. 에디터 구현은 캐시 수명 처리를 위해 PathDataScenePreviewCache.cs만 수정했으며 해당 메타와 GUID를 보존했습니다. 네임스페이스·직렬화 별칭 변경, 기존 파일 삭제·이동, 중복 GUID가 없습니다.

최종 C#/asmdef 72개와 동일한 격리 Unity 프로젝트에서 검증했습니다. 메인 씬을 전환하거나 저장하지 않았습니다. 전체 프로젝트 통합·Player·실기기 실행, 성능·힙 벤치마크와 별도 수동 포인터 조작은 이번 검증 범위에 포함하지 않았습니다.

재실행: `Window → General → Test Runner`에서 EditMode `Common.TransformPath.Tests`, PlayMode `Common.TransformPath.Tests.PlayMode`를 실행합니다. 에디터 UI 회귀를 포함하려면 그래픽 에디터를 사용해야 합니다.
