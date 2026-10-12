# 웨이퍼 시퀀스 시뮬레이터 (README 초안)

> 최종 README로 가기 위한 골격. "(통합 후 채움)"은 구현이 머지되면 채운다.

## 이것은 무엇인가

한 줄 소개: 웨이퍼 면저항(4점 프로브) 측정 장비의 운용 시퀀스를 가상 장치 위에서 시뮬레이션하는 C#/.NET 데스크톱 앱. UI는 Avalonia와 WinForms 두 벌, 엔진은 하나를 공유한다.

## 왜 만들었나

(지원자 1인칭 3문장 자리 — 통합 후 채움)

-

## 화면

### 대표 화면 (WinForms)
![WinForms 대표 화면](images/winforms-main.png) <!-- 이미지 자리: 통합 후 캡처 -->

### 2D 도식 (Avalonia)
![Avalonia 도식 화면](images/avalonia-motion.png) <!-- 이미지 자리: 로봇 확장·스테이지 이동·Z 접촉·진공 상태를 MotionSnapshot 기반 2D 도식으로 렌더링 -->

### 웨이퍼 맵
![웨이퍼 맵](images/wafer-map.png) <!-- 이미지 자리: 레시피 패턴(Center1/Standard5/Standard9/Standard49/Custom) 사이트 위치와 면저항 분표 -->

## 구조

```
src/
  Contracts/          // 프로젝트 전체의 계약. 인터페이스·DTO·알람 코드·레시피. 구현은 없다.
  Core.Sequence/      // 모듈별 상태기계와 스케줄러. ISimulator의 실제 구현. (통합 후 채움)
  Core.Measurement/   // Rs 계산·통계·저항 분포 모델(IResistanceField 구현). (통합 후 채움)
  Devices/            // IO 포인트 추상화 위의 가상 장치·프로토콜(SCPI·Modbus TCP). (통합 후 채움)
  App.Avalonia/       // 맥 시연용 UI. (통합 후 채움)
  App.WinForms/       // 윈도우 UI. (통합 후 채움)
tests/Tests/          // xUnit. (통합 후 채움)
```

![아키텍처](images/architecture.svg) <!-- 이미지 자리: Contracts를 중심에 두고 Core·Devices·App·Tests가 의존하는 방향 -->

## 설계 결정

1. **모듈별 상태기계와 스케줄러** — 장비를 LoadPort·Robot·Aligner·Stage·ZHead·Smu·Vision·Vacuum·Storage·Host 모듈로 나누고 각자 상태기계를 갖는다. 장비 전체 상태(EquipmentState)는 모듈 상태에서 계산되는 값이지 별도 상태기계가 아니다.
2. **TimeProvider 단일 시간원과 seed 재현성** — 모든 시간은 VirtualTimeProvider 하나에서만 읽는다(DateTime.Now·Stopwatch 금지). Realtime(배속 포함)과 Instant(즉시 완결) 두 모드. 난수는 레시피 Seed에서 파생한 Random만 쓰므로 같은 레시피+seed는 같은 로그가 나오고, 로그 지문(ILogDigest)으로 재현을 검증한다.
3. **저장 큐 역압 정책(측정값 불폐기)** — 저장은 동기/비동기 두 모드이고 비동기 저장 큐가 가득 차면 측정값을 버리지 않는다. 대기·알람(SaveQueueFull)으로 표현한다.
4. **인터락은 명령 계층에** — 시퀀스 if문으로 막지 않고 IInterlock(이동·하강·로봇 진입·진공 해제)에서 사유와 함께 거부한다.
5. **IO 포인트 추상화 위의 장치와 프로토콜(SCPI·Modbus TCP)** — 이름 붙은 IO 포인트(iomap.json) 위에 IDigitalIO·IAxis·ISourceMeasureUnit 같은 장치 인터페이스를 두고, 가상 장치를 실제 프로토콜 말하는 구현으로 갈아끼운다.

## 실행 방법

- .NET 10 SDK 필요. 저장소 전체는 net10.0 타깃.
- 맥: `dotnet run --project src/App.Avalonia`
- 윈도우: `dotnet run --project src/App.WinForms`
- 시뮬레이션만 먼저 보기: (통합 후 채움 — CLI/Instant 모드 진입 방법)
- ClockMode를 Instant로 두면 실제 대기 없이 전체 로트가 즉시 완결된다(테스트·리플레이용).

## 테스트·CI

- 테스트: `dotnet test` (xUnit, Microsoft.Extensions.TimeProvider.Testing 사용)
- CI: (통합 후 채움 — 워크플로 구성 확정 시)
- WinForms UI는 CI에서만 실행하고, Avalonia는 맥 시연용이다. (※ 최종 README에서 두 항목의 문구 정리 필요)

## 한계와 다음 단계

- 실제 모션 보드·PLC·SECS/GEM은 연동하지 않았다. 장치는 가상 구현이고 SECS/GEM은 Contracts에 자리(Host 모듈)만 남겨 두었다.
- WinForms는 CI에서만 실행하고, Avalonia는 맥 시연용이다.
- MSSQL·PostgreSQL은 IResultStore 인터페이스 뒤에 붙이는 것으로 계약만 정의되어 있고 구현은 되어 있지 않다(기본은 SQLite).
- 기존 .NET Framework 앱에서 재사용하려면 Contracts·Core를 netstandard2.0으로 멀티타깃하는 것이 경로다.
- 다음 단계: (통합 후 채움)

## 제작 과정

(1인칭 3~5문장 자리 — 통합 후 채움)

Claude Code로 설계를 잡고 작업자 에이전트를 모듈별로 병렬 배치했고, 각 산출물을 독립 리뷰로 통합했다. 총 3시간.
