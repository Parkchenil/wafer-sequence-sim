# 용어 사전

- **Tact** — 웨이퍼 한 장을 처리하는 데 걸린 시간(픽업부터 반납까지). Contracts에서는 `WaferSummary.Tact`, `RunStats.AverageTact`로 기록된다.
- **UPH (Units Per Hour)** — 시간당 처리 웨이퍼 수. 평균 Tact의 역수로 계산된다(3600 / 평균 Tact초). `RunStats.Uph`.
- **면저항 (Sheet Resistance, Rs)** — 얇은 막의 면적당 전기 저항. 단위 Ω/sq(옴 퍼 스퀘어). 4점 프로브 측정의 대상 값이며, 근사식 Rs = 4.532 · V/I에 가장자리 보정계수를 곱해 계산한다(`IRsCalculator.RsFromVI`).
- **4점 프로브 (Four-point probe)** — 한 줄로 배열된 4개 탐침으로 바깥 두 개에 전류를 흘리고 안쪽 두 개로 전압을 읽어 접촉저항의 영향을 없애는 측정법. 이 시뮬레이터의 측정 방식이며, `ISourceMeasureUnit`(전류원·전압계)과 `IProbePosition`(프로브 좌표)이 이에 해당한다.
- **듀얼 컨피규레이션 (Dual configuration)** — (통합 후 채움)
- **인터락 (Interlock)** — 위험한 동작 조합을 사전에 막는 안전 규칙. 이 프로젝트에서는 시퀀스 if문이 아니라 명령 계층의 `IInterlock`(XY 이동·Z 하강·로봇 진입·진공 해제 검사)에서 사유와 함께 거부한다.
- **역압 (Backpressure)** — 다운스트림(저장 큐)이 밀리면 업스트림(측정)이 대기하는 정책. 저장 큐가 가득 차도 측정값을 버리지 않고 대기·알람(`AlarmCode.SaveQueueFull`)으로 처리한다.
- **TimeProvider** — .NET의 시간 추상화. 이 프로젝트는 `VirtualTimeProvider` 하나로 모든 시간을 읽는다(DateTime.Now·Stopwatch 금지). Realtime(배속 가능)과 Instant(대기 없이 즉시 완결) 두 모드를 갖는다.
- **SCPI** — 계측기 표준 명령 언어(예: `:SOUR:CURR 1e-3`, `:MEAS:VOLT?`). 가상 SMU가 실제 장비로 갈아끼워질 때 이 프로토콜로 말한다.
- **Modbus** — 산업용 통신 프로토콜. 이 프로젝트에서는 Modbus TCP가 IO 포인트(`IDigitalIO`, `iomap.json`) 아래의 통신 구현 후보다.
- **CEID (Collection Event ID)** — SECS/GEM에서 호스트에 보고하는 수집 이벤트의 번호. 상태 전이·측정 완료 같은 사건을 알린다. `docs/secs-gem-mapping.md` 참조.
- **ALID (Alarm ID)** — SECS/GEM의 알람 번호. 이 프로젝트는 `AlarmCode` enum 숫자(1001~9001)를 그대로 ALID로 쓴다.
- **FOUP** — 웨이퍼 25장을 담는 수송 컨테이너. 로드포트의 슬롯 단위이며 `PhaseKind.PickFromSlot`·`MapSlots`가 이를 다룬다.
- **노치 (Notch)** — 웨이퍼 가장자리의 방향 표식. 좌표계에서 -Y(6시) 방향이며 얼라이너가 이를 기준으로 정렬한다(`IAligner.AlignAsync`).
