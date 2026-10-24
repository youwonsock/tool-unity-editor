# TransformPath Seek·시작 준비 정리

- 작업일: 2026-10-24 (Asia/Seoul)
- 환경: Unity 2023.2.20f1, macOS
- 상세 증거: [SeekPreparationCleanupValidation.json](SeekPreparationCleanupValidation.json)

## Seek 재진입과 잔여 시간

이벤트 A에서 뒤로 Seek하면 이벤트 커서는 새 위치로 돌아가지만, 이전 Tick의 이벤트 순회가 계속되어 같은 Tick에서 A가 중복 실행되고 아직 새 위치에서 도달하지 않은 B도 실행됐습니다. Single·Aggregate·Sequence 모두 재현됐고, 다른 구간으로 SeekSegment한 뒤에도 이전 구간 이벤트가 이어졌습니다.

두 API를 내부 SeekTo로 통합했습니다. 실제 위치가 바뀌면 기존 실행 버전을 갱신해 이전 이벤트 순회와 Tick을 중단하고, 잔여 시간·Queue 제약 프레임·이벤트 커서·위치와 StateRevision/receipt를 공통 처리합니다. PlaybackId는 유지합니다. 같은 위치로의 Seek는 Changed=false로 기존 Tick·이벤트 순회·상태 버전을 유지합니다.

SeekSegment에도 일반 Seek와 같은 잔여 시간 초기화를 적용했습니다. 짧은 Loop 구간에서 한 Tick의 전환 한도에 걸린 뒤 SeekSegment해도 다음 Tick이 이전 프레임 시간까지 소비하지 않습니다. 이동 설정 변경은 기존처럼 StateRevision으로 표현하며, 속도·Duration 보간을 Seek와 같은 Tick 중단으로 취급하지 않습니다.

## 시작 준비·검증 중복 제거

Direct·Free·Queued 시작이 내부 PreparePlayback에서 기존 PathPlaybackSession과 스냅샷을 준비·검증하고, StartPreparedPlayback에 지역 변수로 전달합니다. Queue의 기존 재생을 해제하기 전에 요청을 검증하는 순서는 유지합니다. 별도 Engine·Scope나 지속적인 준비 상태는 추가하지 않았습니다.

기존 Queue 경로는 사전 검증과 실제 시작이 각각 Session/스냅샷을 만들고 이벤트를 검사했습니다. 이제 정상 시작은 준비된 후보를 재사용하고, 시작 직전에 Provider가 준비되지 않았거나 Revision이 변경됐으면 새 후보를 검증합니다. 준비 후 무효화된 Provider는 기존 재생을 교체하지 않습니다.

외부 이벤트 소스의 실제 읽기 횟수를 세어 Single·Aggregate·Sequence 각각의 Direct·Free·Queued 시작이 원본 이벤트를 한 번만 복사하고, 변경 없는 재시작은 기존 스냅샷을 재사용함을 확인했습니다. 실제 성능·할당량 벤치마크는 수행하지 않았습니다.

## 최소 Duration 경계 보정

새 PlayMode 잔여 시간 검증 중 허용되는 최소 Duration 0.001초를 다른 값으로 변경하면 진행률을 0으로 계산하는 문제도 확인했습니다. 유효 하한을 제외하던 비교 조건을 포함하도록 수정했습니다. 같은 경계의 명목 속도 계산도 함께 보정해 최소 Duration 구간에서 다음 구간으로 이전 속도를 보존할 수 있습니다.

최소 Duration에서 Seek한 20% 위치를 Duration 10초로 변경하고 0.1초 전진하면 21%가 되는 것과, 다음 SpeedBased/TimeBased 구간의 속도 보존을 EditMode로 검증했습니다.

## 최종 검증

| 항목 | 결과 |
|---|---:|
| 수정 전 초기 Seek 재현 | 9개 중 4개 통과·5개 실패 |
| 신규 EditMode 회귀 | 34/34 통과 |
| 그래픽 EditMode 전체 | 246/246 통과 |
| PlayMode 전체 | 24/24 통과 |
| EditMode에 포함된 에디터 UI 회귀 | 9/9 통과 |

실패·건너뜀 0, 최종 프로세스 종료 코드 0, 컴파일 오류 0입니다. 최초 PlayMode의 최소 Duration 사례 1개 실패와 수정 후 최종 결과도 JSON에 구분해 보관했습니다.

신규 EditMode는 뒤·앞 Seek와 구간 변경의 이벤트 순서, 합법적인 다음 Tick의 이벤트 재통과, no-op Seek, Loop 경계 중단, 구간 전환 잔여 시간, Queue 제약·등록 보존, 시작 준비 읽기 횟수, 잘못된 요청과 준비 후 Provider 변경, 최소 Duration 진행률·속도 보존을 검증합니다.

신규 PlayMode 3개는 실제 프레임에서 Single·Aggregate·Sequence 이벤트 Seek, SeekSegment 후 새 구간의 이벤트 진행, 전환 한도 이후 잔여 시간 제거를 확인합니다. 마지막 사례는 Time.captureDeltaTime으로 0.1초 프레임을 만들고 테스트 종료 시 원래 값을 복원합니다. 기존 Showcase·Queue·이벤트·수명 회귀도 함께 통과했습니다.

에디터 UI는 그래픽 Inspector 입력과 렌더링으로 MultiPath 자동 연결·해제·강제 동기화·Rebuild, 연결점 Undo/Redo, 웨이포인트 그룹 동기화·기하 갱신·Undo/Redo·멤버 변경·비활성 그룹을 확인했습니다. Inspector 캡처도 확인했습니다. [TransformPath.uml](../TransformPath.uml)을 현재 준비·시작·Seek 흐름으로 갱신하고 ELK 렌더링을 확인했습니다.

## 보존과 검증 범위

공개 API 기준 727개 항목(비공개 직렬화 필드 66개 포함) 비교를 통과했습니다. 기존 메타 140개와 보호 파일 189개의 바이트가 동일합니다. 기존 씬·프리팹·설정·샘플 19개, 에디터 코드·메타 26개, 이미지·메타 26개, asmdef 5개를 유지했습니다. 네임스페이스·직렬화 별칭 변경, 기존 파일 삭제·이동, 중복 GUID가 없습니다.

최종 C#/asmdef 73개와 동일한 소스의 격리 Unity 프로젝트에서 검증했습니다. 메인 씬을 전환하거나 저장하지 않았습니다. 전체 프로젝트 통합·Player·실기기·성능/힙 벤치마크와 별도 수동 포인터 조작은 이번 검증 범위에 포함하지 않았습니다.

재실행: `Window → General → Test Runner`에서 EditMode `Common.TransformPath.Tests`, PlayMode `Common.TransformPath.Tests.PlayMode`를 실행합니다. UI 회귀를 포함하려면 그래픽 에디터를 사용해야 합니다.
