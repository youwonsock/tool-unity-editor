# tool-unity-editor

Unity 프로젝트에서 쓰던 기능을 독립 모듈로 분리하고, `ToyProject`에서 동작 예제를 제공하는 저장소입니다.

| 모듈 | 내용 |
| --- | --- |
| [FlowField](./ToyProject/Assets/_FlowField/README.md) | Goal과 장애물로 방향과 속도 배율을 계산하는 방향장 (Surface2D, Volume3D) |
| [TransformPath](./ToyProject/Assets/_TransformPath/README.md) | Transform 제어점 경로의 재생, 연결 경로, Queue 이동 |
| [OptimizeTool](./ToyProject/Assets/_OptimizeTool/README.md) | Mesh 결합·컬링 베이크와 물리 움직임 기록을 수행하는 Editor 도구 |

## 실행

- Unity 2023.2.20f1 / URP로 `ToyProject`를 엽니다.
- 샘플 씬은 `FlowFieldSample`, `FlowFieldVolume3DTest`, `TransformPathSample`, `OptimizeToolSample`이며, 화면의 Overview Board가 상태와 조작법을 보여 줍니다.
- 모듈은 `Assets/_모듈명/` 아래에 `Script`, `Scene`, `Prefab`, `Settings`, `Docs`로 나뉘고, 샘플 코드는 `Script/Samples`에 따로 둡니다.

## 빌드 확인

아래 스크립트는 작업 트리의 스냅샷을 별도 Git worktree에서 빌드하므로 Editor를 연 채로 돌릴 수 있습니다. Unity Hub가 켜져 있어야 하며, 옵션과 시간 제한은 각 스크립트 첫머리에 있습니다.

| 스크립트 | 내용 |
| --- | --- |
| `Tools/run-player-probe.sh` | Mac Player에서 FlowField 측정과 모든 샘플 씬 실행. 결과는 CSV |
| `Tools/run-transformpath-probe.sh` | Mac Player에서 TransformPath 측정 |
| `Tools/run-il2cpp-build.sh` | Android IL2CPP 빌드. 기기에서 실행하지는 않습니다 |
