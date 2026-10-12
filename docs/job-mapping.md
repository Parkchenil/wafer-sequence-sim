# 공고 담당업무 ↔ 구현 위치 매핑

> 구현 위치는 프로젝트·클래스 수준. Contracts에서 확정되는 것만 채웠고 나머지는 통합 후 채운다.

## 담당업무

| 공고 항목 | 구현 위치 | 비고 |
|---|---|---|
| C# WinForms/.NET 장비 제어 앱 | `src/App.WinForms` (Form1), `src/App.Avalonia` | UI 두 벌이 `ISimulator` 계약 하나만 본다 (`src/Contracts/ISimulator.cs`) |
| 모션 제어·DIO·인터락·시퀀스 설계 | 계약: `Contracts/Devices.cs` (`IAxis`, `IDigitalIO`, `IInterlock`, `IDeviceSet`, `IWaitHelper`) / 시퀀스: `Core.Sequence/SequenceEngine`(모듈별 상태기계·자원 점유 스케줄러·웨이퍼 추적·`Interlock`), 축 명령 `Devices/SimulatedAxis`(사다리꼴 프로파일·소프트 리밋·홈잉·EMO 확인) | 인터락은 시퀀스 if문이 아니라 명령 계층(`IInterlock`)에서 막는다 |
| PLC·EtherNet/IP·Modbus TCP·TCP/IP·Serial 통신 | `Devices/Modbus.cs`(Modbus TCP FC1/3/5/6/16 클라이언트·가상 PLC 서버, 인메모리·TCP 전송), `Devices/Scpi.cs`(SCPI SMU 클라이언트·가상 서버), `Devices/ModbusDigitalIO`(포인트 이름→코일 주소 맵) | EtherNet/IP·Serial은 미구현. `IByteTransport`/`IDuplexTransport` 뒤에 붙이는 구조 |
| 상태 모니터링·알람·로그·이력 | 상태: `Contracts/Dtos.cs` (`ModuleState`, `EquipmentState`, `ModuleStatus`, `EquipmentSnapshot`) / 알람: `Alarm`, `AlarmCode` (Dtos.cs) / 로그: `StepEvent` / 이력: `Contracts/Measurement.cs` (`IResultStore`, `RunRecord`) | 스냅샷 발행은 30 Hz 이하 스로틀 |
| 사내 공용 제어 플랫폼 | `Contracts`(계약만 담은 프로젝트) + `Devices/DeviceSetFactory`(같은 장치 묶음을 인메모리·프로토콜·TCP로 갈아끼움) | 방향성: 장치 계층(`IDeviceSet`)과 시퀀스 엔진(`ISimulator`) 분리가 그 기반 |
| 측정 데이터 처리·통계·차트·맵 UI | 데이터·통계 계약: `Contracts/Measurement.cs` (`IRsCalculator`, `WaferSummary`, `RunStats`) / 차트·맵 UI: `App.WinForms/Panels.cs`(GDI+ 간트·웨이퍼 맵·통계 막대), `App.Avalonia/GanttView·WaferMapView·EquipmentView` | 통계: 평균·σ·균일도(σ%·range%)·반복성(σ%) |
| 리포트·DB 출력·상위 연동 | CSV: `ICsvExporter` / DB: `IResultStore`(SQLite 기본, MSSQL·PostgreSQL은 인터페이스 뒤 미구현) / 상위 연동: `ModuleId.Host`, `PhaseKind.HostReport` (SECS/GEM 자리) | 레시피 파일: `IRecipeFile` |
| 정확도·재현성 데이터 분석 | `Contracts/Dtos.cs` `MeasurementResult.RsSigma`(사이트 반복 σ), `WaferSummary.RepeatabilityPct`, `Contracts/Measurement.cs` `ILogDigest` | 같은 레시피+seed = 같은 로그. 로그 지문으로 재현 검증 |
| 하드웨어 협업 | `Contracts/IoPoints`(전장 IO 맵의 포인트 이름), `Devices/ContactSensorCoupling`(Z 위치↔접촉 센서), `Devices/InMemoryDigitalIO`(진공·도어 응답 지연) | 근거 계약: `IoPoints`(iomap.json 키), `IProbePosition`, `IResistanceField` |
| 현장 셋업 | 레시피 JSON(`Core.Measurement/RecipeFile`) 저장·불러오기, 실행 이력 SQLite(`SqliteResultStore`), 두 실행 비교(`RunComparer`) | 계약 수준 근거: `IRecipeFile`(레시피 JSON 저장·불러오기), `IWaitHelper`(타임아웃·알람) |
| 요구사항~문서화 | `docs/` (이 문서 포함) | 코드 주석·문서 모두 한글 규약 |

## 자격 요건

| 항목 | 근거 위치 |
|---|---|
| C#/.NET | 전체 net10.0 (`Directory.Build.props`), 최신 C# 레코드·패턴 매칭 사용 |
| WinForms | `src/App.WinForms/MainForm.cs`(SplitContainer 3단·ToolStrip·StatusStrip·ListView VirtualMode·TabControl), `Panels.cs`(OnPaint 커스텀 패널), `Program.cs`(`--screenshot` 헤드리스 캡처) |
| 멀티스레드·async | 전 계약이 async + CancellationToken (`Contracts/Devices.cs`), 이벤트는 임의 스레드에서 발행·UI는 디스패처로 넘김 (`ISimulator.cs` 주석), `FakeSimulator`의 lock 기반 로그·스냅샷 |
| Git | 저장소 관리 (commits로 확인) |
| 로그·재현 이슈 추적 | `StepEvent`(Seq로 시작/종료 연결), `ILogDigest`(재현 검증), `RunComparison`·`PhaseDelta`(실행 간 비교) |

## 우대 사항

| 항목 | 근거 위치 |
|---|---|
| 장비 제어 경험 | `ModuleId` 10개 모듈 모델링, `PhaseKind` 20단계 시퀀스 (`Contracts/Dtos.cs`) |
| 모션·비전·센서 연동 | `IAxis`(사다리꼴 속도 프로파일), `IVision`(오토포커스·패턴 정렬 `AlignOffset`), `IoPoints`의 DI 센서들 |
| PLC·산업 통신 | `Devices/Modbus.cs`(Modbus TCP 프레임 직접 구현), `Devices/Scpi.cs` |
| 수치해석·피팅 | `IRsCalculator.RsFromVI`(Rs = 4.532·V/I, 가장자리 보정계수), `IResistanceField`(좌표별 참값 모델 + 잡음 샘플) |
| SEMI 표준 | `ModuleId.Host`(SECS/GEM 자리), `docs/secs-gem-mapping.md` (CEID·ALID·SVID 매핑 설계) |
| DB | `IResultStore` (SQLite 기본 구현, MSSQL·PostgreSQL 확장 여지) |
| Framework→.NET 마이그레이션 | README 설계 결정: Contracts·Core를 netstandard2.0 멀티타깃 경로 |
| 소규모 팀 완결 | 1인 설계~문서화 전 과정 (README 제작 과정 절 참조) |
| 현장 대응 | `AlarmSeverity` 3단계 + `AcknowledgeAlarm`(운영자 확인 후 Reset), `AlarmCode` 19종 체계 |
| Claude AI 활용 | Claude Code 설계 + 작업자 병렬 + 독립 리뷰 (README 제작 과정 절 참조) |
