# SECS/GEM 매핑 설계 (연동 전 설계 문서)

> SECS/GEM은 아직 연동되어 있지 않다(Contracts의 `ModuleId.Host`, `PhaseKind.HostReport`가 자리만 남겨 둔 상태). 이 문서는 붙인다면의 매핑 설계다. E30 GEM 용어: **CEID**(Collection Event, 수집 이벤트), **ALID**(Alarm ID), **SVID**(Status Variable, 상태 변수). 호스트는 장비 상태변화를 이벤트로 받고, 상태 변수를 폴링/리포트하며, 원격 명령(RCMD)으로 장비를 제어한다.

## CEID 매핑 — `PhaseKind`·`EquipmentState` → 수집 이벤트

| CEID | 발생 원천 (Contracts) | 보고 내용 |
|---|---|---|
| (통합 후 채움) | `EquipmentState.Initializing` 진입 (`ISimulator.InitializeAsync`) | 초기화 시작 |
| (통합 후 채움) | `EquipmentState.Idle` 진입 | 초기화 완료 / 아이들 |
| (통합 후 채움) | `EquipmentState.Running` 진입 (`ISimulator.StartAsync`) | 로트 시작 |
| (통합 후 채움) | `EquipmentState.Paused` / `Resume` | 일시정지·재개 |
| (통합 후 채움) | `EquipmentState.Aborting` / 완료 후 `Idle` (`AbortAsync`) | 중단 |
| (통합 후 채움) | `EquipmentState.Completed` | 로트 완료 |
| (통합 후 채움) | `WaferTrack.Status` 변화 (Pending→Processing→Measured→Saved/Failed) | 웨이퍼 처리 추적 |
| (통합 후 채움) | `MeasurementCompleted` 이벤트 | 측정값 보고 |
| (통합 후 채움) | `WaferCompleted` 이벤트 | 웨이퍼 요약 보고 |
| (통합 후 채움) | `RunCompleted` 이벤트 (`RunRecord`) | 실행 이력 보고 |
| (통합 후 채움) | `PhaseKind.Wait` 진입/복귀 (`StepEvent.IsWait`, `WaitReason`) | 대기 원인 보고 |

단계(Phase) 수준의 이벤트는 `StepEvent` 로그(`PhaseKind` 20종)를 그대로 CEID로 승격하는 방식을 쓴다. `PhaseKind` 값: Home, MapSlots, PickFromSlot, Align, PlaceOnChuck, VacuumCheck, Illumination, AutoFocus, MapInspect, MoveXY, DescendZ, ContactDetect, Measure, LiftZ, PickFromChuck, ReturnToSlot, Save, HostReport, Wait, AlarmRecovery.

## ALID 매핑 — `AlarmCode` (코드 숫자 그대로)

| ALID | AlarmCode | 심각도(Contracts 기준) |
|---|---|---|
| 1001 | AxisTimeout | Error |
| 1002 | AxisLimit | Error |
| 1003 | AxisNotHomed | Error |
| 1004 | ServoOff | Error |
| 2001 | ContactTimeout | Error |
| 2002 | ContactRetryExceeded | Error |
| 3001 | VacuumNotConfirmed | Error |
| 3002 | InterlockBlocked | Error |
| 4001 | VisionAlignFail | Error |
| 4002 | AutoFocusTimeout | Error |
| 5001 | SmuTimeout | Error |
| 5002 | SmuOverRange | Error |
| 6001 | SaveQueueFull | Warning |
| 6002 | SaveWriteFail | Error |
| 7001 | LoadPortDoor | Error |
| 7002 | RobotCollisionRisk | Error |
| 8001 | CommTimeout | Error |
| 9001 | Emo | Error |

- ALID 숫자는 `AlarmCode` enum 값을 그대로 쓴다. `AlarmSeverity.Info/Warning/Error` → GEM 알람 심각도 대응은 통합 후 확정.
- 알람 발생/해제 → SECS 알람 송수신(S5), 운영자 확인(`AcknowledgeAlarm`) → 호스트 쪽 Ack 흐름.

## SVID 매핑 — 상태 변수

| SVID | 원천 (Contracts) | 단위 |
|---|---|---|
| (통합 후 채움) | `EquipmentSnapshot.SimTime` (시뮬레이션 시간) | s |
| (통합 후 채움) | `RunStats.AverageTact` | s/웨이퍼 |
| (통합 후 채움) | `RunStats.Uph` | 웨이퍼/h |
| (통합 후 채움) | `RunStats.SaveQueueDepth` / `SaveQueueCapacity` | 개 |
| (통합 후 채움) | `EquipmentState` | enum |
| (통합 후 채움) | `WaferTrack` (현재 웨이퍼·사이트 진행률) | - |

## 원격 명령 매핑

| RCMD | `ISimulator` 메서드 | 사전 조건 (Contracts 명세) |
|---|---|---|
| START | `StartAsync(recipe, waferCount)` | Idle에서만 허용 |
| STOP | `AbortAsync` | 진행 중 동작을 끊고 안전 상태(Z 상승·축 정지·진공 유지), 상태는 Idle |
| PAUSE | `Pause()` | 현재 단계가 끝난 뒤 멈춘다(동작 중간에 끊지 않는다) |
| RESUME | `Resume()` | Paused에서 이어간다 |
| ABORT | `AbortAsync` | STOP과 동일 계약 (GEM에서 두 개를 구분한다면 통합 후 확정) |
| RESET | `ResetAsync()` | 모든 Error 알람이 Ack돼 있어야 한다. Uninitialized로 돌린 뒤 Initialize 필요 |

- HCACK(명령 응답)는 `DeviceResult.Ok/Code`로부터 대응한다(허용 → OK, 상태 조건 불일치 → `InterlockBlocked` 등).
- 레시피 전달은 GEM PP(프로세스 프로그램) 개념에 `RecipeDto` JSON(`IRecipeFile`)을 대응시키는 방향.
