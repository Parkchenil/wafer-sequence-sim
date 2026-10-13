using Contracts;
namespace Core.Sequence;

public sealed partial class SequenceEngine
{
    public TimeProvider CommunicationTimeProvider { get; set; } = TimeProvider.System;
    public TimeSpan CommunicationTimeout { get; set; } = TimeSpan.FromSeconds(120);
    private volatile bool _communicationLost, _sensorDelay;
    private bool _needsReconcile;
    private string _recovery = "통신 정상 · 복구 이력 없음";
    private InterlockSnapshot? _interlockDetail;
    private ProbeSnapshot? _probeDetail;
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    public IStorageRecovery? StorageRecovery => _store as IStorageRecovery;
    public void SetCommunicationFailure(bool enabled) { _communicationLost = enabled; if (enabled) _needsReconcile = true; _recovery = enabled ? "통신 단절 · 진행 동작 종료 후 신규 명령 대기" : "통신 복원 · 다음 명령 전 실제 가상 상태 재확인"; Publish(); }
    public void SetSensorDelay(bool enabled) { _sensorDelay = enabled; Publish(); }
    public void SetDatabaseFailure(bool enabled) { if (StorageRecovery is { } s) s.InjectWriteFailure = enabled; Publish(); }
    public async Task RecoverStorageAsync(CancellationToken ct) { if (StorageRecovery is { } s) { s.InjectWriteFailure = false; await s.RecoverAsync(ct); foreach (var w in _tracker.Items.Where(w => w.Status == WaferStatus.Journaled && !s.IsWaferPending(_runId, w.WaferId))) _tracker.Update(w.WaferId, status: WaferStatus.Saved); } Publish(); }
    private FaultSnapshot FaultState() => new(_communicationLost, _sensorDelay, StorageRecovery?.InjectWriteFailure ?? false, _recovery, StorageRecovery?.PendingCount ?? 0, StorageRecovery?.JournalPath ?? "저널 미구성") { JournalStatus = StorageRecovery?.JournalStatus ?? "" };
    private async Task<DeviceResult> CommunicationGate(string request, CancellationToken ct)
    {
        if (!_communicationLost) return await CommunicationGateCore(request, ct);
        var started = _clock.Elapsed;
        var step = new StepEvent(Interlocked.Increment(ref _seq), _currentWafer ?? "", _currentSite, ModuleId.Host, PhaseKind.Wait, started, null, true, "통신 복구 / 상태 재확인", request);
        lock (_gate) { _log.Add(step); M(ModuleId.Host).Enter(true, "통신 복구 대기", started); }
        try { StepLogged?.Invoke(step); } catch { }
        try { return await CommunicationGateCore(request, ct); }
        finally
        {
            var done = step with { End = _clock.Elapsed };
            lock (_gate) { _log.Add(done); _phaseTotals[PhaseKind.Wait] = _phaseTotals.GetValueOrDefault(PhaseKind.Wait) + done.Duration!.Value; M(ModuleId.Host).Exit(true, _clock.Elapsed); }
            try { StepLogged?.Invoke(done); } catch { }
        }
    }
    private async Task<DeviceResult> CommunicationGateCore(string request, CancellationToken ct)
    {
        var since = _clock.Elapsed;
        var wallStart = CommunicationTimeProvider.GetTimestamp();
        while (_communicationLost)
        {
            _interlockDetail = new(request, Array.AsReadOnly(new[] { new InterlockCondition("통신", "미확인", "연결 및 상태 재확인", false) }), since, CommunicationTimeProvider.GetElapsedTime(wallStart), "사용자 복구 대기 · 벽시계 제한 / 중단 가능 (가상 장치 timeout과 별도)");
            Publish();
            if (CommunicationTimeProvider.GetElapsedTime(wallStart) >= CommunicationTimeout) return DeviceResult.Fail(AlarmCode.CommTimeout, "통신 사용자 복구 대기 벽시계 제한 초과");
            var emo = await RefreshEmoAsync(ct); if (!emo.Ok) return emo;
            await Task.Delay(TimeSpan.FromMilliseconds(50), CommunicationTimeProvider, ct);
        }
        if (!_needsReconcile) return DeviceResult.Success;
        await _reconcileGate.WaitAsync(ct);
        try
        {
            if (!_needsReconcile) return DeviceResult.Success;
            // 진행 중이던 가상 명령을 다시 발행하지 않고 종료 지점의 센서와 소유권을 대조한다.
            var drainStarted = _clock.Elapsed;
            DeviceResult<AxisStatus>[] positions;
            while (true)
            {
                positions = await Task.WhenAll(_d.StageX.GetStatusAsync(ct), _d.StageY.GetStatusAsync(ct), _d.ZAxis.GetStatusAsync(ct));
                if (positions.Any(p => !p.Ok || p.Value is null)) return DeviceResult.Fail(AlarmCode.InterlockBlocked, "복구 축 상태 미확인");
                if (!_d.Robot.Extended && Volatile.Read(ref _stageMoving) == 0 && positions.All(p => !p.Value!.Moving)) break;
                if (_clock.Elapsed - drainStarted > TimeSpan.FromSeconds(30)) return DeviceResult.Fail(AlarmCode.InterlockBlocked, "진행 동작 정지 확인 30 s 초과");
                await Task.Delay(TimeSpan.FromMilliseconds(20), _clock, ct);
            }
            var chuck = await _d.Io.ReadAsync(IoPoints.ChuckWaferPresent, ct);
            var extended = await _d.Io.ReadAsync(IoPoints.RobotExtended, ct);
            var tracks = _tracker.Items;
            var held = tracks.Count(w => w.Location == WaferLocation.RobotArm);
            var aligned = tracks.Count(w => w.Location == WaferLocation.Aligner);
            var robot = (_d.Robot as IRobotTransportSource)?.Transport;
            if (!chuck.Ok || !extended.Ok || positions.Any(p => !p.Ok || p.Value is null || p.Value.Moving) ||
                chuck.Value != tracks.Any(w => w.Location == WaferLocation.Chuck) || extended.Value != _d.Robot.Extended ||
                held != (_d.Robot.HoldingWafer ? 1 : 0) || robot is not null && aligned != (robot.AlignerOccupied ? 1 : 0))
                return DeviceResult.Fail(AlarmCode.InterlockBlocked, "복구 상태 불일치 · 축/척 재석/로봇 보유/얼라이너 확인 필요");
            _needsReconcile = false; _recovery = "복구 확인 완료 · 축 정지/척 재석/로봇 보유/얼라이너 일치";
            _interlockDetail = new(request, Array.AsReadOnly(new[] { new InterlockCondition("통신 및 장치 상태", "재확인 일치", "축 정지 / 재석 / 보유 일치", true) }), since, _clock.Elapsed - since, "통과 · 복구 상태 대조 완료");
            return DeviceResult.Success;
        }
        finally { _reconcileGate.Release(); }
    }
    private void ObserveGate(PhaseKind phase, DeviceResult result)
    {
        if (phase is not (PhaseKind.MoveXY or PhaseKind.DescendZ or PhaseKind.PlaceOnChuck or PhaseKind.PickFromChuck)) return;
        var conditions = ((Interlock)Interlock).LastConditions;
        _interlockDetail = new(phase.ToString(), conditions, _clock.Elapsed, TimeSpan.Zero, result.Ok ? "통과" : "차단: " + result.Message);
    }
    private void ProbeState(ProbePhase phase, int? samples = null, double? deviation = null, int? contactRetry = null, int? qualityRetry = null, bool? observed = null, string verdict = "")
    {
        var prior = _probeDetail;
        _probeDetail = new(phase, _d.ZAxis.PositionUm, _d.Probe.InContact, observed, samples ?? prior?.SampleCount ?? 0, deviation ?? prior?.DeviationPct,
            contactRetry ?? prior?.ContactRetry ?? 0, qualityRetry ?? prior?.QualityRetry ?? 0, _recipe?.QualityTolerancePct ?? 1, _recipe?.QualityMinSamples ?? 3, verdict) { ContactMaxRetry = _recipe?.ContactMaxRetry ?? 0, QualityMaxRetry = _recipe?.QualityMaxRetry ?? 0, LastSampleV = prior?.LastSampleV };
        Publish();
    }
}
