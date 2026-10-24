# 3,600개 저장·재빌드 수정 검증 자료

최종 측정은 `data/bake/pair-01`부터 `pair-04`까지입니다. 수정 직전 `baseline`과 수정 후 `fixed`를 같은 격리 프로젝트에서 전환했습니다. 진단·검증·제외 실행의 시간은 최종 통계에 섞지 않습니다. 전체 해석은 상위 `PerformanceReport.md`에 있습니다.

## 보존 데이터로 통계 재생성

저장소 루트에서 Python 표준 라이브러리만으로 검사·재계산합니다.

```sh
python3 ToyProject/Assets/_OptimizeTool/Docs/Performance/SaveRebuildFix-2026-10-24/analyze_bakes.py \
  ToyProject/Assets/_OptimizeTool/Docs/Performance/SaveRebuildFix-2026-10-24 \
  --output /tmp/optimizetool-savefix-summary.json
```

분석기는 실행 수·입력 크기·Unity 버전·기하 결과·소스 해시·AC 전원·동시 batch Editor 부재를 검사합니다. 모든 지연 표본을 포함하며 대응 실행 쌍을 단위로 bootstrap합니다. RSS 표본을 별도 독립 실행처럼 세지 않습니다. `summary.json`의 변화율은 각 대응 실행 비율의 중앙값이고 절대 시간 중앙값의 나눗셈과는 다릅니다.

## 새 격리 프로젝트에서 실행

`Assets` 밖의 새로운 작업 폴더를 사용합니다. 원래 실행 중인 Editor를 조작하지 않습니다.

1. 동일 Unity 프로젝트의 독립 복제본을 작업 폴더의 `Project`에 준비합니다. macOS APFS에서는 `cp -cR`로 복제할 수 있습니다. 실행 경로는 스크립트의 `UNITY`에 지정된 Unity 2023.2.20f1입니다. 복제본의 `Temp` 등 프로세스 잠금 상태는 정리하고, 원본의 잠금 파일은 건드리지 않습니다.
2. `source-baseline.zip`을 작업 폴더의 `source-before`, `source-fixed.zip`을 `source-fixed`에 압축 해제합니다. `Project/Assets/_OptimizeTool/Script`에는 보존한 `source-fixed` 전체를 배치합니다. 기존 씬·Material·원본/베이크 에셋은 `preservation.json`의 해시로 확인합니다.
3. `prepare.py`, `run_bakes.py`, `analyze_bakes.py`, `config.json`을 작업 폴더로 복사합니다. `Harness-*.cs.txt`와 `BakeProbe.cs.txt`는 각각 `.txt` 접미사만 제거해 같은 폴더에 둡니다. 테스트와 비교용 코드는 양쪽에 동일하게 배치됩니다.
4. `python3 <작업폴더>/run_bakes.py`를 실행합니다. `prepare.py`는 두 수정 대상 및 동일한 `MeshCombiner.cs`를 지정된 스냅샷으로 배치하고 진단 코드를 제거합니다. A/B, B/A, B/A, A/B 순서로 새 headless Editor를 8번 실행하며 작업 사이 3초를 쉽니다. 컴파일·시작은 측정 타이머 밖입니다.
5. 실행기는 공통 측정 잠금을 획득하고 다른 batch Editor가 끝나기를 기다립니다. 이후 동시 실행이 관측되면 쌍 전체를 `excluded-overlap`에 보존한 뒤 다시 실행합니다. 이 검사는 모든 OS 부하나 상호작용 중인 일반 Editor의 활동까지 차단하지는 않습니다. 전용 장치 실험으로 일반화하지 않습니다.
6. `python3 <작업폴더>/analyze_bakes.py <작업폴더> --output <새 JSON>`으로 분석합니다. `resource.txt`는 `/usr/bin/time -l`의 전체 프로세스 최대 RSS이며, `rss-samples.json`은 `proc_pidinfo`의 약 10 ms 구간 표본입니다.

`probed-baseline.zip`은 작업 폴더의 `probed-baseline`에 압축 해제합니다. `prepare.py baseline --probe`와 `prepare.py fixed --probe`는 각각 별도 진단용 배치를 구성합니다. 이후 같은 `Bake` 진입점을 실행하면 `probe.json`을 기록합니다. 최종 성능 실행에는 이 진단 준비가 필요 없습니다.

`Harness-verify.cs.txt`는 별도 검증용입니다. 일반 하네스 자리에 배치해 `Bake`를 실행하면 저장 에셋을 재임포트하고 SHA-256을 기록하며, 원본 3,600개와 결과를 첫 베이크/재빌드 Undo·Redo로 검증합니다. 이 실행의 시간은 성능 통계에서 제외합니다.

## 테스트·API·소스

- `IntegrationEditMode.xml`, `IntegrationPlayMode.xml`: 외부 레거시 컨트롤러 제거 후 현행 스크립트·프리팹을 반영한 추가 통합 검사도 99/99·38/38 통과했습니다. 해당 입력은 `integration-source-and-prefab.zip`과 `integration-source-sha256.json`에 보존했습니다.
- `EditMode.xml`, `PlayMode.xml`: `Common.OptimizeTool.Tests` 99개, `Common.OptimizeTool.PlayModeTests` 38개 결과. 새 상태·저장 회귀 사례는 `MeshRefactoringTests.cs`에 있습니다. 로그도 gzip으로 보관했습니다.
- Python 계측 테스트: 상위 폴더에서 `python3 -m unittest discover -s <Performance 폴더> -p 'test_*.py'`로 8개를 실행했습니다.
- `api-assemblies.zip`: 최종 측정 중 저장한 제작용 4개 DLL의 전후 스냅샷입니다. 압축은 Assets 밖에서 해제합니다. `ApiCheck.cs.txt`는 Unity 내장 Mono.Cecil로 공개 API와 Unity 직렬화 필드 집합을 비교합니다. 비교용 하네스 타입은 제외합니다. 고정한 측정 스냅샷의 결과는 API 357개, 직렬화 필드 170개, 양방향 차이 0개입니다. 이후 동시 작업의 레거시 컨트롤러 제거는 `snapshot-scope.json`에 별도로 기록했습니다.
- `source-*.zip`: 테스트·메타를 포함한 OptimizeTool 스크립트 스냅샷입니다. `changes.patch`는 이번 작업의 제작용 두 파일과 회귀 테스트 변경만 담습니다.
- `diagnostic-*`: 구간별 시간·호출 수·실제 Renderer 변경 수·최초 파일 바이트 수·GC 세대별 횟수. `verify-*`: 3,600개 동등성 실행의 성공 결과와 Mesh 파일 해시.
- `data/excluded-concurrent-batch`: 다른 Unity 테스트/컴파일과 겹친 초기 수집 전체 및 마지막 중단된 실행. `data/excluded-overlap`: 이후 감지해 쌍 전체를 재실행한 자료. 값이 느리다는 이유로 표본을 제외하지 않았습니다.
- `preservation.json`: 보호 파일 791개의 시작 해시와 최종 차이 목록. 첫 측정 후 검사에서는 전부 같았고, 이후 동시 작업의 프리팹 변경 및 레거시 컨트롤러 메타 삭제 2개는 별도로 기록했습니다. 나머지 789개는 동일합니다. `snapshot-scope.json`에는 해당 외부 코드 변경도 포함했습니다.

그래프 재생성에는 matplotlib이 필요합니다. `plot_results.py`는 같은 폴더의 `summary.json`으로 `comparison.png`와 SVG를 만듭니다. `.cs.txt`는 Unity에서 실행되는 제작용 소스가 아닌 보존 텍스트입니다.
