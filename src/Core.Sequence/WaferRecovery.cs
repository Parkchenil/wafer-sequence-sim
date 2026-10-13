using Contracts;
namespace Core.Sequence;

public sealed partial class SequenceEngine
{
    private int _recovering;
    private CancellationTokenSource? _recoveryCancel;
    public void CancelRecovery() => _recoveryCancel?.Cancel();
    public async Task<DeviceResult> RecoverWafersAsync(CancellationToken ct)
    {
        lock (_controlGate)
        {
            if (_recovering != 0 || _runCts is not null || _starting != 0 || _initializing != 0) return DeviceResult.Fail(AlarmCode.InterlockBlocked, "활성 동작 종료 후 안전 회수 가능");
            _recovering = 1;
        }
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cancel.CancelAfter(TimeSpan.FromSeconds(60)); _recoveryCancel = cancel;
        var token = cancel.Token;
        try
        {
            if (_runCts is not null || Volatile.Read(ref _starting) != 0 || Volatile.Read(ref _initializing) != 0) return DeviceResult.Fail(AlarmCode.InterlockBlocked, "활성 로트/초기화 종료 후 회수 가능");
            var validation = await ValidateRecovery(token); if (!validation.Ok) return validation;
            var r = _recipe ?? new RecipeDto();
            var speed = r.TravelSpeedMmPerS;
            async Task Require(string id, ModuleId module, string detail, Func<CancellationToken, Task<DeviceResult>> action)
            {
                token.ThrowIfCancellationRequested();
                if (_communicationLost || _sensorDelay) throw new InvalidOperationException("통신/센서 장애 중 회수 거부");
                var emo = await RefreshEmoAsync(token); if (!emo.Ok) throw new InvalidOperationException(emo.Message);
                _recovery = "안전 회수 · " + detail;
                var first = new StepEvent(Interlocked.Increment(ref _seq), id, null, module, PhaseKind.AlarmRecovery, _clock.Elapsed, null, false, null, detail);
                lock (_gate) _log.Add(first); StepLogged?.Invoke(first); Publish();
                var result = await action(token);
                var last = first with { End = _clock.Elapsed, Note = detail + (result.Ok ? " · 확인 완료" : " · 거부: " + result.Message) };
                lock (_gate) _log.Add(last); StepLogged?.Invoke(last);
                if (!result.Ok) throw new InvalidOperationException(result.Message);
            }
            await Require("", ModuleId.Smu, "전류 출력 해제", c => _d.Smu.OutputAsync(false, c));
            // 중단 로봇을 먼저 알려진 경로로 수납해야 XY와 Z 계측 영역을 다시 사용할 수 있다.
            if (_d.Robot.Extended)
            {
                if (_d.Robot is not IRobotRecovery robotRecovery) throw new InvalidOperationException("알려진 가상 수납 경로 없음");
                await Require("", ModuleId.Robot, "중단 위치에서 안전 후퇴", c => robotRecovery.WithdrawAsync(r.RobotMoveTime, c));
            }
            await Require("", ModuleId.ZHead, "Z 안전 높이", c => _d.ZAxis.MoveAbsoluteAsync(Math.Max(r.ZSafeHeightUm, _d.ZAxis.PositionUm), speed, r.AccelMmPerS2, TimeSpan.FromSeconds(30), c));
            await Require("", ModuleId.Stage, "X 원점", c => _d.StageX.MoveAbsoluteAsync(0, speed, r.AccelMmPerS2, TimeSpan.FromSeconds(30), c));
            await Require("", ModuleId.Stage, "Y 원점", c => _d.StageY.MoveAbsoluteAsync(0, speed, r.AccelMmPerS2, TimeSpan.FromSeconds(30), c));
            await Require("", ModuleId.Vacuum, "진공 해제", c => _d.Io.WriteAsync(IoPoints.VacChuckOn, false, c));
            var vacuum = await _d.Io.ReadAsync(IoPoints.VacChuckSense, token);
            if (!vacuum.Ok || vacuum.Value) throw new InvalidOperationException("진공 해제 센서 미확인");
            _vacuumOn = _vacuumConfirmed = false;
            foreach (var location in new[] { WaferLocation.RobotArm, WaferLocation.Aligner, WaferLocation.Chuck })
            {
                foreach (var wafer in _tracker.Items.Where(w => w.Location == location))
                {
                    if (location == WaferLocation.Aligner) await Require(wafer.WaferId, ModuleId.Robot, "얼라이너에서 회수", c => _d.Robot.PickFromAlignerAsync(r.RobotMoveTime, c));
                    if (location == WaferLocation.Chuck) await Require(wafer.WaferId, ModuleId.Robot, "척에서 회수", c => _d.Robot.PickFromChuckAsync(r.RobotMoveTime, c));
                    await Require(wafer.WaferId, ModuleId.Robot, $"원래 슬롯 {wafer.Slot} 반환", c => _d.Robot.ReturnToSlotAsync(wafer.Slot, r.RobotMoveTime, c));
                    _tracker.Update(wafer.WaferId, WaferLocation.LoadPort, wafer.Status);
                }
            }
            validation = await ValidateRecovery(token);
            if (!validation.Ok || _d.Robot.Extended || _tracker.Items.Any(w => w.Location is WaferLocation.RobotArm or WaferLocation.Aligner or WaferLocation.Chuck)) throw new InvalidOperationException("최종 수납/소유권 확인 실패: " + validation.Message);
            _needsReconcile = false;
            // 실제 반환과 센서 대조가 끝난 뒤에만 알람 및 실행 상태를 해제한다. 측정/불량 기록은 보존한다.
            lock (_gate)
            {
                for (var i = 0; i < _alarms.Count; i++) if (_alarms[i].ClearedAt is null) _alarms[i] = _alarms[i] with { Acked = true, ClearedAt = _clock.Elapsed };
                foreach (var module in _modules) { module.Set(ModuleState.Uninitialized, "회수 확인", _clock.Elapsed); module.Set(ModuleState.Homing, "축 상태 대조 완료", _clock.Elapsed); module.Set(ModuleState.Idle, "안전 회수 완료", _clock.Elapsed); }
            }
            _recovery = "안전 회수 완료 · 원래 슬롯 반환/수납/센서 대조 확인 · 다음 로트 가능";
            if (_store is not null && _runId.Length > 0) await _store.SaveLogAsync(_runId, Log, token);
            return DeviceResult.Success with { Message = _recovery };
        }
        catch (OperationCanceledException) { _recovery = "안전 회수 중단/60초 제한 · 현재 위치 보존 · 재확인 필요"; return DeviceResult.Fail(AlarmCode.InterlockBlocked, _recovery); }
        catch (Exception error) { _recovery = "안전 회수 거부: " + error.Message; return DeviceResult.Fail(AlarmCode.InterlockBlocked, _recovery); }
        finally { _recoveryCancel = null; Volatile.Write(ref _recovering, 0); Publish(); }
    }
    private async Task<DeviceResult> ValidateRecovery(CancellationToken ct)
    {
        DeviceResult Block(string why) { _recovery = "안전 회수 거부: " + why; return DeviceResult.Fail(AlarmCode.InterlockBlocked, _recovery); }
        if (_communicationLost || _sensorDelay) return Block("통신/센서 장애 해제 필요");
        var emo = await RefreshEmoAsync(ct); if (!emo.Ok) return Block(emo.Message);
        foreach (var axis in new[] { _d.StageX, _d.StageY, _d.ZAxis })
        {
            var status = await axis.GetStatusAsync(ct);
            if (!status.Ok || status.Value is not { ServoOn: true, Homed: true, Moving: false, LimitPlus: false, LimitMinus: false } a || !double.IsFinite(a.PositionUm)) return Block("축 정지/원점/위치 미확인");
        }
        var chuck = await _d.Io.ReadAsync(IoPoints.ChuckWaferPresent, ct);
        var extended = await _d.Io.ReadAsync(IoPoints.RobotExtended, ct);
        var contact = await _d.Io.ReadAsync(IoPoints.ProbeContact, ct);
        var door = await _d.Io.ReadAsync(IoPoints.LoadPortDoorOpened, ct);
        var map = await _d.LoadPort.MapSlotsAsync(TimeSpan.Zero, ct);
        var telemetry = (_d.Robot as IRobotTransportSource)?.Transport;
        var tracks = _tracker.Items;
        if (!door.Ok || door.Value != _d.LoadPort.DoorOpen || tracks.Any(w => w.Location is WaferLocation.Chuck or WaferLocation.RobotArm or WaferLocation.Aligner) && !door.Value) return Block("로드포트 도어 상태 미확인");
        if (!chuck.Ok || !extended.Ok || !contact.Ok || contact.Value != _d.Probe.InContact || !map.Ok || map.Value is null || telemetry is null ||
            extended.Value != _d.Robot.Extended || telemetry.HoldingWafer != _d.Robot.HoldingWafer ||
            tracks.Count(w => w.Location == WaferLocation.Chuck) != (chuck.Value ? 1 : 0) ||
            tracks.Count(w => w.Location == WaferLocation.RobotArm) != (_d.Robot.HoldingWafer ? 1 : 0) ||
            tracks.Count(w => w.Location == WaferLocation.Aligner) != (telemetry.AlignerOccupied ? 1 : 0)) return Block("재석/보유/접촉/위치 소유권 불일치");
        if (tracks.Select(w => w.Slot).Distinct().Count() != tracks.Count || tracks.Any(w => map.Value.Contains(w.Slot) != (w.Location is WaferLocation.LoadPort or WaferLocation.Done))) return Block("원래 슬롯 점유/소유권 불일치");
        if (!_d.Robot.Extended && telemetry.Zone != RobotZone.Retracted) return Block("로봇 수납 영역 미확인");
        return DeviceResult.Success;
    }
}
