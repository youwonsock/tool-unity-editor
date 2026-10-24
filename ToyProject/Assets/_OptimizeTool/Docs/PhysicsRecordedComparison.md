# 녹화 당시 원본을 기준으로 Prefab 비교

검증일: 2026-10-24. Unity 2023.2.20f1, `OptimizeToolSample`의 실제 Game View와 Editor 테스트 기준이다.

## 원인 분석

기존 `ComparePhysics()`는 `ResetSource()` 후 Rigidbody를 다시 동적으로 전환했다. 화면 왼쪽과 오차 수치는 녹화 당시의 움직임이 아니라 두 번째 물리 시뮬레이션이었다. Reset은 위치·회전·속도를 복원하지만 물리 엔진 전체 상태를 복원하지 않는다. 이전 검증에서 관찰한 약 0.108m·12.92°에는 재실행의 차이가 섞여 있어 녹화 정확도 수치로 사용할 수 없었다. 특정 접촉 캐시 하나가 그 차이 전부를 만들었다고 단정하지는 않는다.

독립된 원본 관찰값을 도입한 첫 검증에서는 0.000882m·0.113289°의 실제 커브 오차가 드러났다. 기존 위치 임계값·표본 간격에 따른 키 축약 외에, 회전 축약에 사용한 `Quaternion.Angle`의 작은 각도 처리도 원인이었다. 현재 Editor에서 identity와 0.02° 회전을 비교한 반환값은 0이어서 설정된 0.001° 임계값보다 큰 움직임을 놓칠 수 있었다. [Unity 공개 Quaternion 구현](https://github.com/Unity-Technologies/UnityCsReference/blob/master/Runtime/Export/Math/Quaternion.cs)의 작은 내적 차이 처리와 일치하는 관찰이다.

## 변경한 실행 흐름

1. RECORD는 기존처럼 실제 Rigidbody 물리를 실행한다. `PhysicsRecordingSession`은 커브용 축약 키와 별도로 모든 물리 관찰 시각·로컬 위치·회전을 보존한다. 틱 사이 종료 시각은 기존 보간 규칙을 적용한다.
2. 기존 `.anim`·`.prefab`과 함께 `.reference.asset`에 원본 관찰값을 저장한다. 기준 데이터는 저장 클립을 역산하거나 다시 샘플링해 만들지 않는다. 세 경로를 쓰기 전에 검증하고 실패하면 이전 파일들을 모두 복원한다. 기존 Clip·Prefab GUID와 연결, 동기 저장 완료 API는 유지한다.
3. COMPARE에서는 원본 Rigidbody를 Kinematic으로 유지한다. 왼쪽 RECORDED ORIGINAL에는 원본 관찰값을, 오른쪽 RECORDED PREFAB에는 저장된 커브를 같은 시각에 적용한다. 두 전시 공간의 배치 간격을 제외하고 Transform 경로별 로컬 자세를 비교한다.
4. 최대 오차는 시작·종료를 포함한 **모든 원본 관찰 시각**에서 계산한다. 현재 비교의 물리 스텝이 녹화 때보다 커도 중간 관찰값 검사를 생략하지 않는다. 화면의 임의 시각 사이에서는 원본 관찰값을 보간해 표시한다. 물리 틱 사이의 실제 연속 궤적 전체를 측정했다는 의미는 아니다.
5. 회전 차이는 double 정규화·내적으로 계산한다. 동일한 회전인 q와 -q를 구분하지 않으며, 작은 회전을 0으로 무시하는 규칙을 쓰지 않는다. 녹화의 회전 임계값 판정과 비교에서 같은 함수를 사용한다.

기준 데이터가 없는 예전 녹화는 Prefab 재생을 계속 지원한다. 왼쪽에 REFERENCE MISSING, 오차에는 unavailable을 표시하며 새 RECORD로 독립된 기준 데이터를 만들 수 있다. 같은 클립을 양쪽에 적용해 오차 0으로 표시하지 않는다.

## 검증 결과

| 항목 | 결과 |
| --- | --- |
| EditMode | 65/65 통과 |
| PlayMode | 19/19 통과 |
| 실제 녹화 | Rigidbody 6개, 정지 후 낙하·충돌, 3.2초 저장 |
| 원본 기준 | Transform 7개, 관찰 시각 162개 모두 검사 |
| 실제 최대 위치 차이 | 0.00000500856368m |
| 실제 최대 회전 차이 | 0.0006523367° |
| 다른 중력·스텝으로 비교 | 중력 +40m/s², 스텝 0.07초에서도 동일 오차·전체 관찰값 검사 |
| 독립성 검사 | 테스트에서 저장 커브를 0.25m·약 90° 변조하면 큰 위치·회전 오차 검출 |
| 재진입 | Play 종료·강제 재임포트·재진입 후 재녹화 없이 같은 162개와 같은 오차 확인 |
| 저장 보존 | Clip·Prefab GUID 유지, Reset·Compare에서 세 파일 바이트 유지 |
| Prefab 단독 재생 | 자동 재생·정지 구간·움직임·종료 확인, Rigidbody·Collider·MonoBehaviour 0개 |
| 실패·수명주기 | 신규/덮어쓰기 저장 실패 복원, 비활성화 저장, 재녹화, Reset, 모드 전환 회귀 통과 |
| 실제 Editor 콘솔 | 최종 검증 오류·경고 0개 |

잔여 오차를 강제로 0으로 만들지 않았다. 설정된 표본 간격·위치/회전 임계값과 커브 보간에 따른 차이는 그대로 측정한다. 더 거친 녹화 설정에서는 이 값이 커질 수 있다.

![녹화 당시 원본과 Prefab의 동일 시각 비교](Images/feature-physics-recorded-original-comparison.png)

기본 녹화를 실제로 갱신했으며 이전 `.anim`·`.prefab`·메타는 `ToyProject/Logs/PhysicsRecordedComparison/before-*`에 백업했다. 이번 검증 전후 기존 에셋을 대조했을 때 `.anim`만 내용이 바뀌었고, 기준 에셋은 새로 추가됐다. 씬·쇼케이스 Prefab·기존 설정과 메타 파일은 그대로다. 최종 Editor는 Play 종료·씬 dirty=false 상태다.

XML, 소스 스냅샷, 실제 관찰값, 검증 C# 코드, 스크린샷은 `ToyProject/Logs/PhysicsRecordedComparison/`에 보관했다. 첫 실패와 수정 후 재검증도 attempt 폴더에 보존했다. 이 결과는 Editor 검증이며 Player·디바이스·장시간 녹화를 검증한 결과는 아니다.

재녹화·저장 실패 복원·기준 데이터가 없는 기존 Prefab의 후속 검증과 상태 표시 수정은 [회귀 검증 기록](PhysicsRecorderRegressionValidation.md)에 정리했다.
