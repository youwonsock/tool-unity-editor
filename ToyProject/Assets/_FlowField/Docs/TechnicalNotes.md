# FlowField 기술 참고

[README](../README.md)를 보완하는 구현 요약입니다.

## 어셈블리

| 어셈블리 | 내용 |
| --- | --- |
| `Common.FlowField.Core` | 계약, 격자, Session, 계산 단계, 결과 저장소, Bake 데이터 |
| `Common.FlowField.Runtime` | `FlowFieldManager`, Modifier 컴포넌트, Unity 수명 연결 |
| `Common.FlowField.Editor` | Inspector, Bake, Scene View 표시 |
| `Common.FlowField.Samples` | Agent, HUD, 샘플 카메라 |
| `Common.FlowField.PlayerProbe` | Player 측정 (`Tools/run-player-probe.sh`). Editor와 측정 빌드에서만 컴파일 |
| `Common.FlowField.Tests` | 회귀 테스트 (`Tools/run-tests.sh`) |

## 수명과 게시 규칙

- **입력:** Session은 최신 요청 한 벌과 대기 표시만 보관합니다(`FlowFieldRequestInbox`). Volume은 빌드 시작 때 요청을 BuildContext에 고정하고, Surface는 풀이가 끝나면 최신 요청으로 합성합니다.
- **게시:** 완성된 결과만 게시하고, 일반 조회는 게시 값을 읽은 뒤 최신 장애물·탈출 안전성 검사를 더합니다.
- **끝난 풀이:** 새 입력이 들어와 있어도 게시합니다. 계속 움직이는 Goal 때문에 결과가 끊기지 않게 하기 위해서입니다. Grid나 설정이 바뀌었거나 Goal이 사라진 경우만 거부합니다.
- **Revision:** Surface는 셀 데이터와 Goal이 실제로 바뀔 때, Volume은 결과를 교체할 때 증가합니다. 두 모드를 같은 규칙으로 해석하면 안 됩니다.
- **재진입 없음:** `FlowFieldExecutionBoundary`가 이벤트를 큐에 모아 가장 바깥 작업이 끝난 뒤 전달합니다. `Release`·`Suspend`·Fault 이전의 이벤트는 버리고 `Released`만 항상 전달합니다.
- **수명 종료:** 실행 중인 풀이 Job에 취소를 요청하고 끝날 때까지 기다립니다. Job은 4,096셀마다 취소를 확인합니다.

## Surface2D

<img src="Images/flowfield-surface.png" alt="Surface2D 빌드와 게시 흐름" width="80%">

- Manager가 입력을 관측해 제출하면 Scheduler가 Session을 진행합니다. 제출되지 않은 요청은 빌드하지 않고 예산도 쓰지 않습니다. 직접 `Pump()`를 호출하는 베이크와 테스트는 호출자가 입력을 책임집니다.
- 표면·장애물·Goal·Modifier 영역은 Physics 결과가 한 스냅샷이 되도록 한 프레임에 실행합니다. BFS와 방향은 워커 스레드, Topology는 메인 스레드의 Burst입니다. 합성은 예산에 맞춰 나눕니다.
- BFS가 예정된 빌드는 사전 합성을 생략하고, 완료 후 전체를 한 번 합성합니다.
- 격자가 2,500셀을 넘으면 Scene View는 표시 대상을 줄입니다.

## Volume3D

<img src="Images/flowfield-volume.png" alt="Volume3D 빌드와 게시 흐름" width="80%">

- 셀은 `x + Width × (z + Depth × y)` 순서로 저장합니다. 두 축·세 축 대각선은 중간 셀이 모두 통과 가능할 때만 연결합니다.
- 장애물·Modifier 단계는 64셀씩 진행합니다. 스텝마다 `OverlapBox`로 후보를 모아 후보가 닿는 셀만 정확히 검사합니다(`FlowFieldVolumeCellQuery`).
- **부분 재계산:** Modifier만 바뀌면 게시된 내비게이션에 다시 합성하고, Goal만 바뀌면 장애물 결과와 Topology를 재사용합니다. Goal이 같은 셀 안에서 움직이면 빌드하지 않습니다.
- **추적되지 않은 Collider:** `RequestRebuild()`나 `NotifyCellsDirty()`의 전체 조회에서 반영됩니다.
- **불확실 영역:** 장애물이 바뀐 영역은 게시 때까지 정지합니다. 빌드 시작 때 목록을 옮기고 그 빌드를 게시할 때 지우며, 시작 후 기록된 영역은 다음 빌드까지 남습니다(`FlowFieldVolumeSafetyState`).
- **배열 재사용:** 교체된 게시 배열을 다음 빌드에 씁니다(`FlowFieldVolumeSpareArrays`). 뷰로 내준 필드의 배열은 재사용하지 않습니다.

## Modifier

- Modifier 적용 여부는 두 모드 모두 `FlowFieldVectorUtility.CanApplyModifier`로 판정합니다.
- `CaptureSnapshot()`이 빌드에 쓸 값을 고정합니다. Collider 형상까지 복제하지는 않습니다.
- `Observe()`는 관측 주기마다 호출되므로 할당하지 않게 작성합니다. 값 변경은 `Revision`을 올리거나 `NotifyModifierChanged`로 알립니다.
- 셀 판정은 `FlowFieldOverlapUtility.CellHalfExtent`로 0.0001 줄인 상자를 써서, 격자에 맞춘 Collider가 이웃 셀을 잡지 않게 합니다.

## StaticBaked

- Surface2D는 `FlowFieldStaticBakeData`, Volume3D는 `FlowFieldVolumeBakeData`이며 v6만 허용합니다.
- Volume은 방향·속도·탈출 벡터를 저장하지 않고 NextCell과 면 인덱스(`_escapeFaces`)에서 재구성합니다. Surface는 배열을 `FlowFieldBakeBlob`으로 `byte[]`에 비트 그대로 저장합니다.
- 저장에 실패하면 기존 Asset의 내용, Revision, GUID를 유지합니다.

## Editor 표시

표시 상한은 FullVolume 8,192셀, Slice 4,096셀입니다. Play Mode의 Surface 표시는 합성을 거친 최종 결과이므로 Asset의 원본 방향과 다를 수 있습니다. 왼쪽은 Surface2D 상세, 오른쪽은 Volume3D 중앙 단면입니다.

<p align="center">
  <img src="Images/feature-surface2d-editor-detail.png" alt="Surface2D 통로 주변 상세 Scene View 방향장" width="48%">
  <img src="Images/feature-volume3d-editor-slice.png" alt="Volume3D 중앙 Y 단면 Scene View 방향장" width="48%">
</p>

## UML 갱신

원본은 `Docs/*.puml`이고, 렌더링한 PNG를 `Docs/Images`에 저장해 문서에 넣습니다. Graphviz 없이 PlantUML 1.2024.6으로 렌더링합니다. Docs 폴더에서 실행합니다.

```sh
java -Djava.awt.headless=true -Djava.io.tmpdir="$TMPDIR" -jar plantuml-1.2024.6.jar \
  -tpng -charset UTF-8 -o Images \
  flowfield-uml.puml flowfield-structure.puml flowfield-surface.puml flowfield-volume.puml
```

임시 폴더에 쓸 수 없으면 종료 코드 0으로 0바이트 PNG가 나오니 파일 크기를 확인합니다. 한글은 macOS의 `Apple SD Gothic Neo` 글꼴로 렌더링합니다.
