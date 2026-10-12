namespace Contracts;

/// <summary>
/// 시퀀스 엔진의 외부 계약. UI 두 개(Avalonia·WinForms)와 테스트가 이것만 본다.
/// 스레드 규약: 이벤트는 임의의 스레드에서 올 수 있다. UI는 자기 디스패처로 넘겨 그린다.
/// 명령 의미:
///  - InitializeAsync: 전 모듈 홈잉. Uninitialized/Alarm(복구 후)에서만 허용.
///  - StartAsync: 로트 1회 실행. 끝나거나 Abort될 때까지 완료되지 않는다. Idle에서만 허용.
///  - Pause: 현재 단계가 끝난 뒤 다음 단계로 넘어가지 않고 멈춘다(동작 중간에 끊지 않는다).
///  - Resume: Paused에서 이어간다.
///  - AbortAsync: 진행 중 동작을 CancellationToken으로 끊고 안전 상태(Z 상승·축 정지·진공 유지)로 간다. 상태는 Idle(웨이퍼가 척에 남아 있을 수 있음).
///  - ResetAsync: 알람을 지우고(모두 Ack돼 있어야 함) Uninitialized로 돌린다. 이후 InitializeAsync 필요.
///  - AcknowledgeAlarm: 운영자 확인. Ack되지 않은 Error 알람이 있으면 Reset 거부.
/// </summary>
public interface ISimulator
{
    EquipmentSnapshot Snapshot { get; }
    IReadOnlyList<StepEvent> Log { get; }
    RecipeDto? CurrentRecipe { get; }

    event Action<EquipmentSnapshot>? SnapshotChanged;    // ≤30 Hz
    event Action<StepEvent>? StepLogged;                 // 단계 시작·종료마다(같은 Seq가 End 채워져 두 번 올 수 있다)
    event Action<Alarm>? AlarmRaised;
    event Action<Alarm>? AlarmCleared;
    event Action<MeasurementResult>? MeasurementCompleted;
    event Action<WaferSummary>? WaferCompleted;
    event Action<RunRecord>? RunCompleted;

    Task<DeviceResult> InitializeAsync(CancellationToken ct);
    Task<DeviceResult> StartAsync(RecipeDto recipe, int waferCount, CancellationToken ct);
    void Pause();
    void Resume();
    Task AbortAsync();
    Task<DeviceResult> ResetAsync();
    void AcknowledgeAlarm(string alarmId);
}
