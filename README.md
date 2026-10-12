# 웨이퍼 측정 시퀀스 시뮬레이터

접촉식 4점 프로브 면저항 계측 장비의 **제어 시퀀스·처리량(Tact/UPH)·측정 데이터**를 가상 장치 위에서 재현하는 C#/.NET 10 데스크톱 시뮬레이터다. 엔진 하나를 WinForms(장비 HMI 표준 스택)와 Avalonia(맥 시연용) 두 화면이 공유한다. 모든 수치는 가상이며 특정 회사·장비의 값이 아니다.

[![CI](https://github.com/Parkchenil/wafer-sequence-sim/actions/workflows/ci.yml/badge.svg)](https://github.com/Parkchenil/wafer-sequence-sim/actions/workflows/ci.yml)

## 왜 만들었나

장비 제어 소프트웨어의 핵심은 "웨이퍼 한 장이 왜 이만큼 걸리는가"를 로그로 쪼개고, 설정 하나를 바꿨을 때 어느 단계가 얼마나 줄어드는지 숫자로 보여 주는 일이라고 생각한다. 그 일을 실제 장비 없이도 연습하고 설명할 수 있는 도구가 필요했다. 그래서 장비를 모듈별 상태기계로 모델링하고, 레시피(대기 시간·접촉 방식·언로딩 중첩·저장 방식)를 바꾸면 Tact와 간트 차트가 어떻게 변하는지 보이는 시뮬레이터를 만들었다. C# WinForms는 이 프로젝트로 처음 썼고, 그 과정 자체를 포트폴리오에 담았다.

## 화면

![WinForms 진행 중](docs/images/winforms-running.png)

위는 GitHub Actions의 윈도우 러너가 WinForms 앱을 진짜 엔진으로 돌리다 첫 웨이퍼 네 번째 사이트를 측정하는 순간에 `DrawToBitmap`으로 저장한 화면이다(사람 손을 거치지 않은 캡처).

![시연](docs/images/demo-avalonia.gif)

위는 맥에서 Avalonia 화면으로 로트를 시작한 직후 40초를 녹화한 것이다([mp4](docs/images/demo-avalonia.mp4)). 아래는 정지 화면이다.

| WinForms (CI 윈도우 러너가 헤드리스로 캡처) | Avalonia (맥에서 실행) |
| --- | --- |
| ![WinForms 완료](docs/images/winforms-main.png) | ![Avalonia 메인](docs/images/avalonia-main.png) |
| ![WinForms 웨이퍼 맵](docs/images/winforms-map.png) | ![Avalonia 웨이퍼 맵](docs/images/avalonia-map.png) |
| ![WinForms 통계](docs/images/winforms-stats.png) | ![Avalonia 통계](docs/images/avalonia-stats.png) |

화면 구성은 둘 다 같다. 상단 지표(상태·시뮬레이션 시간·평균 Tact·UPH·진행·저장 큐·알람), 왼쪽 레시피·조작, 가운데 **2D 도식**(로드포트·로봇·얼라이너·척 위 측정 사이트·스테이지 십자선·Z 헤드 측면도·상태등)과 **모듈 간트 차트**(행=모듈, 막대=단계, 빗금=대기), 오른쪽 로그·웨이퍼 맵·통계(단계별 누적, 두 실행 비교)·알람(Ack)·장치 상태 탭이다.

## 무엇을 시뮬레이션하나

웨이퍼 1장은 `픽업 → 정렬 → 척 탑재 → 진공 확인 → 조명 → [사이트마다: XY 이동 → (오토포커스·맵 검사) → Z 하강 → 접촉 감지 → 전압 N회 측정 → 리프트] → 안전 높이 → 반출 ∥ 저장` 순서로 흐른다. 레시피로 바꿀 수 있는 것:

- 측정 위치 패턴(1/5/9/49점·사용자 정의), 사이트당 반복 측정 수와 1회 측정 시간
- 모션: 이동 속도·가속(사다리꼴 프로파일), Z 접근 방식(연속 하강 vs 스텝 하강), 사이트 사이 리프트 높이, 접촉 감지 타임아웃·재시도
- 고정 대기: 진공 확인 횟수·시간, 조명 안정, 오토포커스, 맵 검사 범위(전수 vs 첫 사이트만)
- 핸들링 중첩: 측정 중 다음 웨이퍼를 미리 정렬하고 반출과 저장을 겹친다
- 결과 저장: 동기(시퀀스가 기다림) vs 비동기 큐(용량·쓰기 시간)
- 시계: 실시간(배속) vs 즉시 계산

측정값은 가상 SMU가 SCPI 명령(`:SOUR:CURR`, `:MEAS:VOLT?`)으로 돌려주는 전압을 Rs = 4.532·V/I(가장자리 보정)로 계산한다. 분포 모델은 웨이퍼별 seed로 정해지는 방사형 분포 + 잡음이다. 웨이퍼마다 평균·σ·균일도 2종·재현성(같은 사이트 반복 σ)을 내고 컬러맵으로 그린다.

## 구조

![아키텍처](docs/architecture.svg)

| 프로젝트 | 역할 |
| --- | --- |
| `src/Contracts` | 계약만. `ISimulator`, 장치 인터페이스, DTO, 알람 코드, 레시피, `VirtualTimeProvider`. 모든 프로젝트가 이것만 참조한다 |
| `src/Core.Sequence` | 시퀀스 엔진. 모듈별 상태기계 10개, 웨이퍼 추적, 자원 점유 스케줄러(파이프라인), 인터락, 알람·안전 상태·복구, 저장 큐(역압), 단계 로그·통계 |
| `src/Core.Measurement` | Rs 계산·가장자리 보정, 면저항 분포 모델, 측정 패턴, 통계·재현성, SQLite 이력, 실행 비교, 레시피 JSON, CSV, 로그 지문 |
| `src/Devices` | 이름 붙은 IO 포인트(`IDigitalIO`) 위의 장치: 축(사다리꼴 프로파일·소프트 리밋·홈잉), Z 헤드 접촉 커플링, SCPI 가상 SMU, **Modbus TCP**(FC1/3/5/6/16 직접 구현) 가상 PLC, 비전·로드포트·로봇·얼라이너, `WaitFor(조건, 타임아웃, 알람코드)` |
| `src/App.WinForms` | C# WinForms HMI. GDI+ 도식·간트·맵. `--screenshot` 헤드리스 캡처(CI) |
| `src/App.Avalonia` | 같은 화면을 Avalonia로. 맥 실행·시연용 |
| `tests/Tests` | xUnit 66건: 시퀀스(결정성·역압·중첩·알람 복구·인터락·EMO·Pause/Abort·RunId), 측정, 장치(프로파일·커플링·SCPI·Modbus TCP·seed), 조립 통합(인메모리·프로토콜·실시간 경로) |

## 설계 결정

1. **모듈별 상태기계와 스케줄러.** 장비 상태를 열거형 하나로 두면 "언로딩 중첩" 같은 동시 동작을 표현할 수 없다. 로드포트·로봇·얼라이너·스테이지·Z 헤드·SMU·비전·진공·저장·호스트가 각자 상태를 갖고, 척·로봇·얼라이너는 자원으로 점유·해제한다. 장비 전체 상태는 모듈 상태에서 계산한다.
2. **시간원은 하나, 난수는 seed에서만.** `DateTime.Now`·`Stopwatch`·`Task.Delay(ms)`를 금지하고 `VirtualTimeProvider` 하나로만 기다린다. 즉시 모드는 가상 타이머 큐를 가장 이른 순서로 발화시켜 겹친 동작은 겹친 만큼만 시간이 흐른다. 같은 레시피+seed면 로그가 같고, 로그 지문(SHA-256)으로 재현을 검증한다.
3. **측정값은 절대 버리지 않는다.** 비동기 저장 큐가 가득 차면 시퀀스가 `Wait(저장 큐 가득)`로 멈추고, 그 대기가 간트에 보인다. 처리량을 올리려다 데이터를 잃는 쪽보다 느려지는 쪽을 택했다.
4. **인터락은 시퀀스 if문이 아니라 명령 계층에.** XY 이동·Z 하강·로봇 진입·진공 해제 직전에 `IInterlock`이 사유와 함께 거부한다. 알람이 나면 Z 상승·축 정지의 안전 상태로 가고, 운영자 Ack → Reset → 재초기화 경로를 탄다.
5. **IO 포인트 추상화 위에 장치와 프로토콜.** 가상 장치도 실제처럼 "명령 → 상태 폴링 → 타임아웃 → 알람 코드"로 동작한다. 같은 장치 묶음을 인메모리 전송과 SCPI/Modbus TCP 전송으로 바꿔 끼울 수 있고, 통합 테스트는 두 경로를 모두 돈다.

## 실행

```bash
# .NET 10 SDK
dotnet test                                   # 66건
dotnet run --project src/App.Avalonia         # 맥·리눅스·윈도우
dotnet run --project src/App.WinForms         # 윈도우
dotnet run --project src/App.WinForms -- --screenshot out.png --wafers 2   # 창 없이 PNG 3장
```

CI는 ubuntu에서 빌드·테스트하고, windows 러너에서 WinForms를 빌드해 진짜 엔진으로 로트를 돌린 뒤 `DrawToBitmap`으로 화면을 저장한다. README의 WinForms 이미지는 그 아티팩트다.

## 공고 항목과의 대응

[docs/job-mapping.md](docs/job-mapping.md)에 담당업무·자격·우대 항목별로 어느 클래스가 그 근거인지 적었다. SECS/GEM은 붙이지 않았지만 [docs/secs-gem-mapping.md](docs/secs-gem-mapping.md)에 단계·알람을 CEID/ALID로 어떻게 올릴지 설계해 두었다. 용어는 [docs/glossary.md](docs/glossary.md).

## 한계

- 실제 모션 보드·PLC·SMU·SECS/GEM은 연동하지 않았다. 장치는 전부 가상이고, 프로토콜은 인메모리·TCP 가상 서버와 주고받는다.
- WinForms는 맥에서 컴파일만 되고 실행은 윈도우(CI)에서만 확인했다. Avalonia 화면이 맥 시연용이다.
- MSSQL·PostgreSQL은 `IResultStore` 뒤에 붙이는 자리만 있다(기본 SQLite).
- .NET Framework 앱에서 쓰려면 `Contracts`·`Core`를 netstandard2.0으로 멀티타깃하면 된다(미구현).
- 즉시 모드의 가상 타이머 스케줄러는 "스레드풀이 조용해지면 다음 타이머"라는 휴리스틱을 쓴다. CPU를 많이 쓰는 콜백이 섞이면 순서가 흔들릴 수 있어 테스트는 5회 반복으로 확인했다.

## 제작 과정

2026년 10월 12일 저녁, 3시간 안에 끝내는 것을 목표로 했다. 내가 한 것은 계약(`src/Contracts`) 설계와 모듈 경계, 작업 분배, 통합, 검증이다. 구현은 Claude Code로 조율한 작업자 에이전트(Codex) 5명이 모듈별로 병렬로 썼고, 모듈마다 독립 리뷰 에이전트가 코드를 읽어 발견한 것(요약이 호출자 리스트를 공유해 다음 웨이퍼에 지워지는 버그, 같은 RunId 덮어쓰기, 동기 Modbus 읽기가 스냅샷을 막는 문제 등)을 반영했다. 통합에서 내가 직접 고친 것은 정적 초기화 데드락, 즉시 모드 스케줄러, 척 재석 센서 커플링, 대기 헬퍼의 경과 측정, 엔진이 UI 스레드를 붙잡던 정지, 로드포트 슬롯 점유 기본값이다. 커밋 이력에 순서가 남아 있다.
