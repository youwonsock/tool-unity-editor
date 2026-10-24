# TransformPath 런타임 및 에디터 개선 검증

이 문서는 해당 개선 당시의 구조와 실행 결과를 기록합니다. 이후 Scope를 Follower에 통합한 현재 구조 및 최종 검증은 [구조 단순화](Simplification.md)를 참고하세요.

- 검증일: 2026-10-23 (Asia/Seoul)
- 환경: macOS, Unity 2023.2.20f1
- 최종 대상: 현재 ToyProject의 `Common.TransformPath`
- 결과: 그래픽 에디터 EditMode 59/59, PlayMode 4/4 통과. 기존 씬·프리팹·샘플과 직렬화 계약을 유지했습니다.

## 문제와 변경

### MultiPath 마지막 구간 샘플링

`MultiPathData`는 구간 시작 거리만 저장하지만, 기존에는 마지막 끝점까지 포함하는 누적 거리 배열용 검색 함수를 사용했습니다. 두 구간 `0→10`, `10→30`에서 `SampleDistance(20)`과 Aggregate 진행률 `2/3`의 위치가 20 대신 10이 되는 문제를 재현했습니다. Sequence는 별도의 올바른 탐색을 사용해 이 재현에서 정상 동작했습니다.

구간 시작 거리 전용 이분 탐색 `PathGeometryUtility.FindSegmentStartIndex`를 추가하고, MultiPath 직접 샘플링과 Sequence Seek가 공유하도록 변경했습니다. 정확한 내부 경계는 다음 구간을 선택하는 `[start, end)` 규칙을 사용하며 전체 끝점은 마지막 구간의 끝점을 반환합니다. 연결되지 않은 구간도 자동으로 연결하거나 변형하지 않습니다.

1·2·3·5개 구간의 전체 범위, 범위 밖 입력의 제한, 정확한 경계, 마지막 구간 내부, 중첩 MultiPath, TimeBased/SpeedBased Aggregate 진행, Sequence와 Aggregate Seek 일치, 실제 프레임에서의 최종 구간 이동을 검증했습니다.

### Runtime Driver 등록 검사

각 Tick 대상마다 수행하던 `List.Contains`를 제거하고 활성 등록 버전 딕셔너리를 조회합니다. 등록 대상 수에 따른 프레임 순회 중 등록 검사 비용을 기대 O(N)으로 줄였습니다. 등록·해제 시 정렬 목록을 편집하는 비용과 Queue의 매 프레임 정렬은 별도입니다. 실기기 처리 시간이나 FPS 개선 수치는 측정하지 않았습니다.

해제된 객체는 등록 버전 딕셔너리에서도 제거합니다. 전역 증가 등록 버전을 사용하므로 같은 PlaybackId로 같은 프레임에 해제·재등록해도 현재 프레임의 오래된 스냅샷으로 실행되지 않습니다. 실행 중 해제, 재등록, 재생 ID 교체, 새 등록, 중복 등록, 동일 우선순위 순서, 예외 이후 다른 대상 실행을 검증했습니다.

### 재생 Scope 정리

기존에 정리 작업이 연결되지 않았던 `PathPlaybackScope`에 지연 이벤트·속도 조정·TimeScale 효과와 follower의 Queue 제약 정리를 연결했습니다. 정지·Release·완료·재생 교체·콜백 실패에서 같은 정리 경로를 사용합니다. Scope 필드를 먼저 분리한 뒤 Dispose하고 재생 ID를 검사해 이전 Scope가 새 재생을 정리하지 않도록 합니다.

Queue adapter는 기존대로 Manager 등록을 소유합니다. CharacterController는 자신이 비활성화한 경우만 복원하며, 일시정지 중에는 비활성 상태를 유지합니다. 종료 콜백에서 새 재생과 새 TimeScale 효과를 시작하는 경우도 검증했습니다.

### 웨이포인트 그룹 속성 변경 감지

전체 그래픽 UI 재검증 중 Hierarchy 변경 없이 Inspector 그룹 목록만 편집하면 동기화 대상 캐시가 갱신되지 않는 경우를 확인했습니다. 기존 감지 경로가 구조 변경만 처리하던 것이 원인이었습니다.

`PathWaypointSyncLinker`가 Registry·MultiPathData·PathData 속성 변경도 감지하도록 수정했습니다. Transform 이동은 대상 캐시를 무효화하지 않아 이전 위치와 비교할 수 있도록 유지합니다. 그룹 삭제 후 추가, 목록 크기를 유지한 멤버 참조 교체, 제거된 멤버의 독립 이동, 그룹 동기화, 경로 캐시 재빌드, Undo/Redo를 검증했습니다.

## 최종 실행 결과

최종 검증은 현재 프로젝트의 그래픽 Unity Editor에서 MCP를 통해 실행했습니다. 각 테스트가 생성한 임시 오브젝트와 EditorWindow는 종료 시 정리합니다.

| 대상 | 결과 | 확인 범위 |
|---|---:|---|
| 기존 이식 회귀 | 24/24 | Queue 순서·상한·전환, Controller 수명, 회전, Resume 설정, 기존 프리팹 참조 |
| 런타임 개선 회귀 | 26/26 | 샘플링·Aggregate·Driver 등록 변경·Scope 종료 및 재진입 |
| 에디터 UI | 9/9 | 실제 CustomEditor 체크박스·버튼, 자동 연결, Undo/Redo, 그룹 및 멤버 목록 변경 |
| PlayMode | 4/4 | 기존 Showcase 세 레인과 Queue 일시정지·재개, Controller 복원, 실제 Aggregate 이동 |

- EditMode 작업 ID: `ec9c7ea03a53411b8f49ae4d427e59d7`
- PlayMode 작업 ID: `b0e06b744dbc4fa7b634b8e76710b89c`
- 최종 검증 직전과 직후 씬·프리팹·설정·샘플·메타 등 보호 파일 125개의 SHA-256이 모두 일치했습니다. 신규 회귀 테스트의 메타도 이 최종 보호 목록에 포함됩니다. 이번 개선 초기 보호 파일 124개도 수정하지 않았습니다.
- 기존 네임스페이스·어셈블리·직렬화 필드·GUID를 변경하지 않았습니다. 새 테스트와 이 문서에만 새 GUID를 부여했습니다.
- UI 검증은 원본 CustomEditor를 임시 EditorWindow에 표시하고 Unity 입력 이벤트 및 SerializedObject/Undo를 사용합니다. OS 포인터의 Scene View 핸들 드래그와 실기기 성능 측정은 검증 범위에 포함하지 않습니다.

재실행: `Window > General > Test Runner`에서 EditMode의 `TransformPathPortRegressionTests`, `TransformPathRuntimeImprovementTests`, `TransformPathEditorUiTests`를 선택합니다. UI 검증은 그래픽 에디터가 필요합니다. PlayMode는 `TransformPathPortPlayModeTests`를 선택합니다.

## 유지한 동작 경계

- 지연 이벤트는 게임 시간 기준으로, `Time.timeScale == 0`이면 지연 시간도 멈춥니다.
- `Sample(normalizedTime)`과 `GlobalNormalizedTime`은 경로 길이에 대한 비율입니다.
- 런타임 작성용 Transform 변경에는 명시적인 Rebuild가 필요합니다.
- Queue는 동일한 경로 위의 간격을 관리합니다. 물리 충돌, 교차 경로 회피, 원형 경로 선두·꼬리 간격 처리를 추가하지 않았습니다.
