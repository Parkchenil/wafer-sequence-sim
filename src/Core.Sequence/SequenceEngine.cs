using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Contracts;

namespace Core.Sequence;

public abstract class ModuleMachine
{
    private readonly object _gate = new();
    private int _active, _waiting;
    protected ModuleMachine(ModuleId id) { Id = id; }
    public ModuleId Id { get; }
    public ModuleState State { get; private set; } = ModuleState.Uninitialized;
    public string Detail { get; private set; } = "";
    public TimeSpan Since { get; private set; }
    public bool Set(ModuleState state, string detail, TimeSpan at)
    {
        lock (_gate)
        {
            var allowed = state == State || state is ModuleState.Alarm or ModuleState.Uninitialized
                || State == ModuleState.Uninitialized && state == ModuleState.Homing
                || State == ModuleState.Homing && state == ModuleState.Idle
                || State == ModuleState.Idle && state == ModuleState.Paused
                || State == ModuleState.Paused && state == ModuleState.Idle;
            if (!allowed) return false;
            State = state; Detail = detail; Since = at;
            if (state == ModuleState.Uninitialized) { _active = 0; _waiting = 0; }
            return true;
        }
    }
    public void Enter(bool wait, string detail, TimeSpan at) { lock (_gate) { _active++; if (wait) _waiting++; if (State != ModuleState.Alarm) { State = wait ? ModuleState.Waiting : ModuleState.Busy; Detail = detail; Since = at; } } }
    public void Exit(bool wait, TimeSpan at) { lock (_gate) { _active = Math.Max(0, _active - 1); if (wait) _waiting = Math.Max(0, _waiting - 1); if (State != ModuleState.Alarm) { State = _active == 0 ? ModuleState.Idle : _waiting == _active ? ModuleState.Waiting : ModuleState.Busy; Detail = _active == 0 ? "" : Detail; Since = at; } } }
    public ModuleStatus Status { get { lock (_gate) return new(Id, State, Detail, Since); } }
}
public sealed class LoadPortModule() : ModuleMachine(ModuleId.LoadPort);
public sealed class RobotModule() : ModuleMachine(ModuleId.Robot);
public sealed class AlignerModule() : ModuleMachine(ModuleId.Aligner);
public sealed class StageModule() : ModuleMachine(ModuleId.Stage);
public sealed class ZHeadModule() : ModuleMachine(ModuleId.ZHead);
public sealed class SmuModule() : ModuleMachine(ModuleId.Smu);
public sealed class VisionModule() : ModuleMachine(ModuleId.Vision);
public sealed class VacuumModule() : ModuleMachine(ModuleId.Vacuum);
public sealed class StorageModule() : ModuleMachine(ModuleId.Storage);
public sealed class HostModule() : ModuleMachine(ModuleId.Host);

public sealed class WaferTracker
{
    private readonly List<WaferTrack> _items = []; private readonly object _gate = new();
    public IReadOnlyList<WaferTrack> Items { get { lock (_gate) return _items.ToArray(); } }
    public void Reset(int count, int sites) { lock (_gate) { _items.Clear(); for (int i = 1; i <= count; i++) _items.Add(new($"W{i:D3}", i, WaferLocation.LoadPort, WaferStatus.Pending, 0, sites)); } }
    /// <summary>로드포트 매핑 결과(점유 슬롯 목록)로 웨이퍼별 슬롯을 다시 배정한다. 이전 로트가 중단돼 비어 있는 슬롯을 건너뛴다.</summary>
    public void AssignSlots(IReadOnlyList<int> slots) { lock (_gate) { for (int i = 0; i < _items.Count && i < slots.Count; i++) _items[i] = _items[i] with { Slot = slots[i] }; } }
    public void Update(string id, WaferLocation? location = null, WaferStatus? status = null, int? sitesDone = null)
    { lock (_gate) { var i = _items.FindIndex(x => x.WaferId == id); if (i >= 0) { var w = _items[i]; _items[i] = w with { Location = location ?? w.Location, Status = status ?? w.Status, SitesDone = sitesDone ?? w.SitesDone }; } } }
}

public sealed class Interlock(IDeviceSet devices, Func<bool> vacuumConfirmed, Func<double> safeHeight, Func<DeviceResult> emo, Func<bool> stageMoving, Func<bool>? chuckOwned = null) : IInterlock
{
    public IReadOnlyList<InterlockCondition> LastConditions { get; private set; } = [];
    private DeviceResult Evaluate(PhaseKind phase)
    {
        var safety = emo();
        var zone = (devices.Robot as IRobotTransportSource)?.Transport.Zone ?? RobotZone.Unknown;
        var conditions = new List<InterlockCondition> { new("EMO", safety.Ok ? "해제 확인" : safety.Message, "해제 확인", safety.Ok) };
        if (phase != PhaseKind.DescendZ) conditions.Add(new("Z 안전 높이", $"{devices.ZAxis.PositionUm:F1} µm", $"≥ {safeHeight():F1} µm", double.IsFinite(devices.ZAxis.PositionUm) && devices.ZAxis.PositionUm >= safeHeight()));
        if (phase is PhaseKind.MoveXY or PhaseKind.DescendZ)
        {
            conditions.Add(new("진공", vacuumConfirmed() ? "확인" : "미확인", "확인", vacuumConfirmed()));
            conditions.Add(new("반송 영역", zone.ToString(), "수납 또는 분리 반송 영역", !devices.Robot.Extended || zone == RobotZone.IsolatedTransfer));
        }
        if (phase is PhaseKind.DescendZ or PhaseKind.PlaceOnChuck) conditions.Add(new("XY", stageMoving() ? "이동" : "정지", "정지", !stageMoving()));
        if (phase == PhaseKind.PlaceOnChuck)
        {
            conditions.Add(new("척 원점", $"X {devices.StageX.PositionUm:F1}, Y {devices.StageY.PositionUm:F1} µm", "X/Y 원점", Math.Abs(devices.StageX.PositionUm) <= .01 && Math.Abs(devices.StageY.PositionUm) <= .01));
            conditions.Add(new("척 자원", (chuckOwned?.Invoke() ?? true) ? "잠금 소유" : "미소유", "잠금 소유", chuckOwned?.Invoke() ?? true));
        }
        LastConditions = conditions.AsReadOnly();
        var failed = conditions.FirstOrDefault(c => !c.Passed);
        return !safety.Ok ? safety : failed is null ? DeviceResult.Success : DeviceResult.Fail(AlarmCode.InterlockBlocked, failed.Name + " 조건 미충족");
    }
    public DeviceResult CheckMoveXY() => Evaluate(PhaseKind.MoveXY);
    public DeviceResult CheckDescendZ() => Evaluate(PhaseKind.DescendZ);
    public DeviceResult CheckRobotAccess() => Evaluate(PhaseKind.PlaceOnChuck);
    public DeviceResult CheckVacuumOff() => Evaluate(PhaseKind.VacuumCheck);
}

public sealed partial class SequenceEngine : ISimulator
{
    private readonly IDeviceSet _d; private readonly IWaitHelper _wait; private readonly VirtualTimeProvider _clock; private readonly IRsCalculator _rs; private readonly ISitePatternProvider _sites; private readonly IResultStore? _store; private readonly ILogDigest? _digest;
    private readonly ModuleMachine[] _modules = [new LoadPortModule(), new RobotModule(), new AlignerModule(), new StageModule(), new ZHeadModule(), new SmuModule(), new VisionModule(), new VacuumModule(), new StorageModule(), new HostModule()];
    // 화면 알림 제한과 이력 시작 시각만 벽시계를 사용한다.
    private readonly TimeProvider _wall = TimeProvider.System; private readonly object _publishGate = new(); private long _lastWallSnapshot; private ITimer? _snapshotTimer;
    private readonly WaferTracker _tracker = new(); private readonly List<StepEvent> _log = []; private readonly List<Alarm> _alarms = []; private readonly List<WaferSummary> _summaries = []; private readonly object _gate = new();
    private TimeSpan? _runEndedAt;
    private readonly Dictionary<PhaseKind, TimeSpan> _phaseTotals = []; private readonly object _controlGate = new(); private readonly SemaphoreSlim _emoPulse = new(0);
    private readonly ConcurrentDictionary<string, WaferSummary> _pendingSaves = new();
    private readonly Dictionary<string, (TimeSpan Start, TimeSpan End)> _saveSchedule = []; private TimeSpan _saveAvailableAt;
    private readonly SemaphoreSlim _robot = new(1), _aligner = new(1), _chuck = new(1), _safetyGate = new(1);
    private CancellationTokenSource? _runCts; private TaskCompletionSource _runDone = new(TaskCreationOptions.RunContinuationsAsynchronously); private TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IReadOnlyList<int> _slots = [];
    private RecipeDto? _recipe; private IReadOnlyList<SitePoint> _points = []; private Random _random = new(1); private bool _pause, _aborting, _completed, _vacuumOn, _vacuumConfirmed, _lampOn; private string? _currentWafer; private int? _currentSite; private long _seq, _alarmSeq; private bool _chuckHeld; private int _stageMoving, _queueDepth, _queueCapacity, _starting, _initializing, _emoAlarmRaised; private string _runId = ""; private DateTimeOffset _startedAtWall; private DeviceResult _emoCheck = DeviceResult.Fail(AlarmCode.ConfigError, "EMO 상태 미확인");
    public SequenceEngine(IDeviceSet devices, IWaitHelper wait, VirtualTimeProvider clock, IRsCalculator rs, ISitePatternProvider sites, IResultStore? store, ILogDigest? digest)
    { _d = devices; _wait = wait; _clock = clock; _rs = rs; _sites = sites; _store = store; _digest = digest; Interlock = new Interlock(devices, () => _vacuumConfirmed, () => _recipe?.ZSafeHeightUm ?? 300, () => Volatile.Read(ref _emoCheck), () => Volatile.Read(ref _stageMoving) != 0, () => _chuckHeld); if (devices.Robot is IRobotTransportSource source) source.TransportChanged += OnRobotTransport; }
    public IInterlock Interlock { get; }
    public RecipeDto? CurrentRecipe => _recipe;
    public IReadOnlyList<StepEvent> Log { get { lock (_gate) return _log.OrderBy(x => x.Start).ThenBy(x => x.WaferId).ThenBy(x => x.SiteIndex ?? -1).ThenBy(x => x.Module).ThenBy(x => x.Phase).ThenBy(x => x.Note).ThenBy(x => x.End.HasValue).ToArray(); } }
    public EquipmentSnapshot Snapshot => BuildSnapshot();
    public event Action<EquipmentSnapshot>? SnapshotChanged; public event Action<StepEvent>? StepLogged; public event Action<Alarm>? AlarmRaised; public event Action<Alarm>? AlarmCleared; public event Action<MeasurementResult>? MeasurementCompleted; public event Action<WaferSummary>? WaferCompleted; public event Action<RunRecord>? RunCompleted;
    private string? _lastRobotSubphase;
    private void OnRobotTransport(RobotTransportTelemetry state)
    {
        StepEvent? parent; lock (_gate) parent = _log.LastOrDefault(e => e.Module == ModuleId.Robot && e.End is null);
        if (state.Subphase != _lastRobotSubphase)
        {
            _lastRobotSubphase = state.Subphase;
            if (parent is not null)
            {
                var detail = new StepEvent(Interlocked.Increment(ref _seq), parent.WaferId, null, ModuleId.Robot, parent.Phase, _clock.Elapsed, _clock.Elapsed, false, null, state.Subphase) { IsDetail = true };
                lock (_gate) _log.Add(detail);
                try { StepLogged?.Invoke(detail); } catch { }
            }
        }
        // 계측 중인 웨이퍼와 선행 반송 웨이퍼는 다를 수 있다. 활성 로봇 단계가 소유자다.
        if (parent?.WaferId is { } id && state.HandoffLocation is { } location)
            _tracker.Update(id, location, Volatile.Read(ref _recovering) == 0 ? WaferStatus.Processing : null);
        Publish();
    }
    private ModuleMachine M(ModuleId id) => _modules[(int)id];
    private EquipmentState State { get { lock (_gate) return _alarms.Any(a => a.ClearedAt is null && a.Severity == AlarmSeverity.Error) ? EquipmentState.Alarm : _aborting ? EquipmentState.Aborting : _pause && _runCts is not null ? EquipmentState.Paused : _runCts is not null ? EquipmentState.Running : _modules.Any(m => m.State == ModuleState.Homing) ? EquipmentState.Initializing : _modules.Any(m => m.State == ModuleState.Uninitialized) ? EquipmentState.Uninitialized : _completed ? EquipmentState.Completed : EquipmentState.Idle; } }
    /// <summary>대기로 분류된 시간(IsWait=true인 완료 단계의 합). 고정 대기·큐 대기·인터락 대기가 전부 들어간다.</summary>
    private long WaitTicks() { lock (_gate) return _log.Where(e => e.IsWait && e.End is { }).Sum(e => e.Duration!.Value.Ticks); }
    private EquipmentSnapshot BuildSnapshot()
    {
        lock (_gate)
        {
            var totals = new Dictionary<PhaseKind, TimeSpan>(_phaseTotals);
            var elapsed = _runEndedAt ?? _clock.Elapsed; var avg = _summaries.Count == 0 ? (TimeSpan?)null : TimeSpan.FromTicks((long)_summaries.Average(x => x.Tact.Ticks));
            var stats = new RunStats(_summaries.Count, _tracker.Items.Count, elapsed, avg, avg is { Ticks: > 0 } ? 3600d / avg.Value.TotalSeconds : null, totals, elapsed.Ticks == 0 ? 0 : (double)WaitTicks() / elapsed.Ticks, Math.Clamp(Volatile.Read(ref _queueDepth), 0, _queueCapacity), _queueCapacity);
            var motion = new MotionSnapshot(_d.StageX.PositionUm, _d.StageY.PositionUm, _d.ZAxis.PositionUm, _d.Probe.InContact, _vacuumOn, _vacuumConfirmed, _lampOn, _d.Vision.Focused, _d.Robot.Extended, _d.LoadPort.DoorOpen, _d.Aligner.AngleDeg);
            return new(elapsed, State, _modules.Select(x => x.Status).ToArray(), _tracker.Items, motion, stats, _alarms.Where(x => x.ClearedAt is null).ToArray(), _currentWafer, _currentSite, _points) { RobotTransport = (_d.Robot as IRobotTransportSource)?.Transport, Interlock = _interlockDetail, ProbeDetail = _probeDetail is { } p ? p with { HeightUm = _d.ZAxis.PositionUm, TrueContact = _d.Probe.InContact } : null, Faults = FaultState() };
        }
    }
    private void Publish()
    {
        lock (_publishGate)
        {
            var stamp = _wall.GetTimestamp();
            var gap = _lastWallSnapshot == 0 ? TimeSpan.MaxValue : _wall.GetElapsedTime(_lastWallSnapshot, stamp);
            var interval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 30);
            if (gap < interval)
            {
                _snapshotTimer ??= _wall.CreateTimer(_ => { lock (_publishGate) { _snapshotTimer?.Dispose(); _snapshotTimer = null; } Publish(); }, null, interval - gap, Timeout.InfiniteTimeSpan);
                return;
            }
            _lastWallSnapshot = stamp;
        }
        try { SnapshotChanged?.Invoke(BuildSnapshot()); } catch { }
    }
    private async Task<DeviceResult> Step(string wafer, int? site, ModuleId module, PhaseKind phase, Func<CancellationToken, Task<DeviceResult>> action, CancellationToken ct, bool wait = false, string? reason = null, string note = "", bool retryable = false, TimeSpan? startOverride = null, TimeSpan? endOverride = null)
    {
        await PauseGate(ct); ct.ThrowIfCancellationRequested();
        if (module != ModuleId.Storage)
        {
            var communication = await CommunicationGate(phase.ToString(), ct);
            if (!communication.Ok) { Raise(communication.Code, module, communication.Message); return communication; }
        }
        await PauseGate(ct);
        var emo = module == ModuleId.Storage ? DeviceResult.Success : await RefreshEmoAsync(ct);
        var vacuum = emo.Ok && _vacuumConfirmed && (module == ModuleId.Stage && phase == PhaseKind.MoveXY || module == ModuleId.ZHead && phase == PhaseKind.DescendZ) ? await RefreshVacuumAsync(ct) : DeviceResult.Success;
        DeviceResult gate;
        lock (Interlock)
        {
            var command = CommandGate(module, phase, note);
            gate = !emo.Ok ? emo : !vacuum.Ok ? vacuum : command;
            ObserveGate(phase, gate);
        }
        if (!gate.Ok) { Raise(gate.Code, module, gate.Message); await SafeState(); return gate; }
        var start = startOverride ?? _clock.Elapsed; var seq = Interlocked.Increment(ref _seq); var first = new StepEvent(seq, wafer, site, module, phase, start, null, wait, reason, note);
        lock (_gate) { _log.Add(first); M(module).Enter(wait, phase.ToString(), start); }
        _emoPulse.Release();
        try { StepLogged?.Invoke(first); } catch { } Publish();
        DeviceResult result;
        try { result = await action(ct); }
        catch (OperationCanceledException) { var cancelled = first with { End = _clock.Elapsed }; lock (_gate) { _log.Add(cancelled); M(module).Exit(wait, _clock.Elapsed); } try { StepLogged?.Invoke(cancelled); } catch { } throw; }
        catch (Exception e) { result = DeviceResult.Fail(ClassifyException(e, phase), e.ToString()); }
        var done = first with { End = endOverride ?? _clock.Elapsed }; lock (_gate) { _log.Add(done); if (result.Ok) _phaseTotals[phase] = _phaseTotals.GetValueOrDefault(phase) + done.Duration!.Value; M(module).Exit(wait, _clock.Elapsed); }
        _emoPulse.Release();
        try { StepLogged?.Invoke(done); } catch { } Publish();
        if (result.Code == AlarmCode.AxisStopped) { _runCts?.Cancel(); throw new OperationCanceledException("축이 운영자 정지로 중단됨", ct); }
        if (!result.Ok && !retryable) { Raise(result.Code, module, result.Message); await SafeState(); }
        return result;
    }
    private static AlarmCode ClassifyException(Exception e, PhaseKind phase) => phase is PhaseKind.Save or PhaseKind.HostReport ? AlarmCode.SaveWriteFail : e is TimeoutException or System.IO.IOException ? AlarmCode.CommTimeout : AlarmCode.ConfigError;
    private DeviceResult CommandGate(ModuleId module, PhaseKind phase, string note) => module switch
    {
        ModuleId.Stage when phase == PhaseKind.MoveXY => Interlock.CheckMoveXY(),
        ModuleId.ZHead when phase == PhaseKind.DescendZ => Interlock.CheckDescendZ(),
        ModuleId.Robot when phase is PhaseKind.PlaceOnChuck or PhaseKind.PickFromChuck => Interlock.CheckRobotAccess(),
        ModuleId.Vacuum when note == "진공 해제" => Interlock.CheckVacuumOff(),
        _ => DeviceResult.Success,
    };
    private async Task<DeviceResult> RefreshEmoAsync(CancellationToken ct)
    {
        try
        {
            var reading = await _d.Io.ReadAsync(IoPoints.Emo, ct);
            var result = !reading.Ok ? DeviceResult.Fail(AlarmCode.ConfigError, $"EMO 입력 읽기 실패: {reading.Message}") : reading.Value ? DeviceResult.Fail(AlarmCode.Emo, "비상정지 입력 활성") : DeviceResult.Success;
            Volatile.Write(ref _emoCheck, result);
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { var result = DeviceResult.Fail(AlarmCode.ConfigError, e.ToString()); Volatile.Write(ref _emoCheck, result); return result; }
    }
    private async Task<DeviceResult> RefreshVacuumAsync(CancellationToken ct)
    {
        try
        {
            var reading = await _d.Io.ReadAsync(IoPoints.VacChuckSense, ct);
            _vacuumConfirmed = reading.Ok && reading.Value;
            return !reading.Ok ? DeviceResult.Fail(reading.Code, reading.Message) : _vacuumConfirmed ? DeviceResult.Success : DeviceResult.Fail(AlarmCode.InterlockBlocked, "진공 확인 상실");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { return DeviceResult.Fail(ClassifyException(e, PhaseKind.VacuumCheck), e.ToString()); }
    }
    private async Task WatchEmo(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_recipe?.Clock == ClockMode.Instant) await _emoPulse.WaitAsync(ct);
                else await Task.Delay(TimeSpan.FromMilliseconds(50), _clock, ct);
                var result = await RefreshEmoAsync(ct);
                if (result.Ok) continue;
                if (Interlocked.Exchange(ref _emoAlarmRaised, 1) == 0) Raise(result.Code, ModuleId.Host, result.Message);
                _runCts?.Cancel();
                await SafeState();
                return;
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task PauseGate(CancellationToken ct)
    {
        Task? resume; lock (_controlGate) resume = _pause ? _resume.Task : null;
        if (resume is null) return;
        foreach (var m in _modules.Where(m => m.State == ModuleState.Idle)) m.Set(ModuleState.Paused, "일시정지", _clock.Elapsed);
        Publish(); await resume.WaitAsync(ct);
        foreach (var m in _modules.Where(m => m.State == ModuleState.Paused)) m.Set(ModuleState.Idle, "", _clock.Elapsed);
    }
    private void Raise(AlarmCode code, ModuleId module, string message)
    {
        Alarm alarm;
        lock (_gate)
        {
            // EMO·설정 오류는 발생원(장치·엔진)마다 문구가 달라도 한 번만 띄운다
            if (code is AlarmCode.Emo or AlarmCode.ConfigError && _alarms.Any(x => x.Code == code && x.ClearedAt is null)) return;
            alarm = new Alarm($"A{++_alarmSeq:D4}", code, AlarmSeverity.Error, module, message, _clock.Elapsed, null, false);
            _alarms.Add(alarm); M(module).Set(ModuleState.Alarm, message, _clock.Elapsed);
        }
        try { AlarmRaised?.Invoke(alarm); } catch { } Publish();
    }
    private async Task SafeState()
    {
        await _safetyGate.WaitAsync(CancellationToken.None);
        StepEvent? recovery = null;
        try
        {
            var start = _clock.Elapsed;
            recovery = new StepEvent(Interlocked.Increment(ref _seq), _currentWafer ?? "", _currentSite, ModuleId.ZHead, PhaseKind.AlarmRecovery, start, null, false, null, "안전 상태 복귀");
            lock (_gate) { _log.Add(recovery); M(ModuleId.ZHead).Enter(false, "안전 상태 복귀", start); }
            try { StepLogged?.Invoke(recovery); } catch { }
            await SafeCall(() => _d.Smu.OutputAsync(false, CancellationToken.None), ModuleId.Smu);
            await SafeCall(() => _d.ZAxis.StopAsync(CancellationToken.None), ModuleId.ZHead);
            await SafeCall(() => _d.StageX.StopAsync(CancellationToken.None), ModuleId.Stage);
            await SafeCall(() => _d.StageY.StopAsync(CancellationToken.None), ModuleId.Stage);
            if (!_communicationLost && !_needsReconcile && _recipe is { } r) await SafeCall(() => _d.ZAxis.MoveAbsoluteAsync(r.ZSafeHeightUm, r.TravelSpeedMmPerS, r.AccelMmPerS2, TimeSpan.FromSeconds(30), CancellationToken.None), ModuleId.ZHead);
        }
        finally
        {
            if (recovery is not null)
            {
                var done = recovery with { End = _clock.Elapsed };
                lock (_gate) { _log.Add(done); _phaseTotals[PhaseKind.AlarmRecovery] = _phaseTotals.GetValueOrDefault(PhaseKind.AlarmRecovery) + done.Duration!.Value; M(ModuleId.ZHead).Exit(false, _clock.Elapsed); }
                try { StepLogged?.Invoke(done); } catch { }
            }
            lock (_controlGate) { if (_chuckHeld) { _chuckHeld = false; _chuck.Release(); } }
            _safetyGate.Release(); Publish();
        }
    }
    private async Task SafeCall(Func<Task<DeviceResult>> call, ModuleId module)
    {
        try { var result = await call(); if (!result.Ok && result.Code != AlarmCode.AxisStopped) Raise(result.Code, module, result.Message); }
        catch (Exception e) { Raise(ClassifyException(e, PhaseKind.AlarmRecovery), module, e.ToString()); }
    }
    /// <summary>UI 스레드의 동기화 컨텍스트를 타지 않도록 본체는 스레드풀에서 돈다(화면 스레드 점유·데드락 방지).</summary>
    public Task<DeviceResult> InitializeAsync(CancellationToken ct)
    {
        lock (_controlGate)
        {
            if (_recovering != 0 || _initializing != 0) return Task.FromResult(DeviceResult.Fail(AlarmCode.InterlockBlocked, "안전 회수 또는 초기화 진행 중"));
            _initializing = 1;
        }
        async Task<DeviceResult> Invoke() { try { return await InitializeCoreAsync(ct); } finally { Volatile.Write(ref _initializing, 0); } }
        return _clock.IsDeterministic ? Invoke() : Task.Run(Invoke, CancellationToken.None);
    }
    private async Task<DeviceResult> InitializeCoreAsync(CancellationToken ct)
    {
        if (State != EquipmentState.Uninitialized) return DeviceResult.Fail(AlarmCode.InterlockBlocked, "초기화 상태가 아님");
        foreach (var m in _modules) m.Set(ModuleState.Homing, "원점 복귀", _clock.Elapsed); Publish();
        foreach (var axis in new[] { _d.StageX, _d.StageY, _d.ZAxis })
        {
            var module = axis == _d.ZAxis ? ModuleId.ZHead : ModuleId.Stage;
            var home = await Step("", null, module, PhaseKind.Home, async c => { var on = await axis.ServoOnAsync(c); return on.Ok ? await axis.HomeAsync(TimeSpan.FromSeconds(30), c) : on; }, ct, note: axis.Name);
            if (!home.Ok) return home;
        }
        foreach (var m in _modules) m.Set(ModuleState.Idle, "", _clock.Elapsed); Publish(); return DeviceResult.Success;
    }
    public Task<DeviceResult> StartAsync(RecipeDto recipe, int waferCount, CancellationToken ct)
    {
        lock (_controlGate)
        {
            if (_recovering != 0 || _starting != 0) return Task.FromResult(DeviceResult.Fail(AlarmCode.InterlockBlocked, "안전 회수 또는 실행 진행 중"));
            _starting = 1;
        }
        async Task<DeviceResult> Invoke() { try { return await StartCoreAsync(recipe, waferCount, ct); } finally { Volatile.Write(ref _starting, 0); } }
        return _clock.IsDeterministic ? Invoke() : Task.Run(Invoke, CancellationToken.None);
    }
    private async Task<DeviceResult> StartCoreAsync(RecipeDto recipe, int waferCount, CancellationToken ct)
    {
        if (State != EquipmentState.Idle && State != EquipmentState.Completed) return DeviceResult.Fail(AlarmCode.InterlockBlocked, "시작 상태가 아님");
        if (waferCount < 1 || recipe.ReadingsPerSite < 1 || recipe.SaveQueueCapacity < 1 || recipe.ZLiftAfterMeasureUm < recipe.ZSafeHeightUm) return DeviceResult.Fail(AlarmCode.ConfigError, "레시피 값 오류: 사이트 간 Z 높이는 안전 높이 이상이어야 함");
        if (StorageRecovery is { JournalBlocked: true } blocked) return DeviceResult.Fail(AlarmCode.SaveWriteFail, blocked.JournalStatus);
        if (ControlValidation.Validate(recipe) is { } invalid) return DeviceResult.Fail(AlarmCode.ConfigError, invalid);
        try
        {
            var present = await _d.Io.ReadAsync(IoPoints.ChuckWaferPresent, ct);
            if (!present.Ok) return DeviceResult.Fail(present.Code, "로트 시작 전 척 재석 미확인: " + present.Message);
            if (present.Value || _d.Robot.HoldingWafer || _d.Robot.Extended || (_d.Robot as IRobotTransportSource)?.Transport.AlignerOccupied == true)
                return DeviceResult.Fail(AlarmCode.InterlockBlocked, "미회수 웨이퍼 또는 미수납 로봇이 있음 · 새 로트로 덮어쓸 수 없음");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { return DeviceResult.Fail(AlarmCode.ConfigError, "재석 확인 실패: " + e.Message); }
        _probeDetail = null; _interlockDetail = null;
        _clock.Configure(recipe.Clock, recipe.TimeScale); _recipe = recipe; _random = new Random(recipe.Seed);
        lock (_gate) { _runEndedAt = null; _summaries.Clear(); _log.Clear(); _phaseTotals.Clear(); }
        _pendingSaves.Clear(); lock (_gate) { _saveSchedule.Clear(); _saveAvailableAt = TimeSpan.Zero; } _seq = 0; _queueDepth = 0; _completed = false; _emoAlarmRaised = 0; _queueCapacity = recipe.SaveQueueCapacity;
        _startedAtWall = _wall.GetUtcNow(); var suffix = Guid.NewGuid().ToString("N")[..8]; _runId = $"{_startedAtWall:yyyyMMdd-HHmmss}-{recipe.Seed}-{waferCount}-{suffix}";
        lock (_controlGate) { _pause = false; _resume.TrySetResult(); if (_chuckHeld) { _chuckHeld = false; _chuck.Release(); } }
        _runDone = new(TaskCreationOptions.RunContinuationsAsynchronously); _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct); var token = _runCts.Token; Publish();
        Channel<WaferSummary>? channel = null; Task<DeviceResult>? consumer = null; Task<bool>? prepared = null; Task? emoWatcher = null; using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            if (_d is IDeviceContext context) context.Reseed(recipe.Seed);
            _points = _sites.Build(recipe); _tracker.Reset(waferCount, _points.Count);
            var initialEmo = await RefreshEmoAsync(token);
            if (!initialEmo.Ok) { Raise(initialEmo.Code, ModuleId.Host, initialEmo.Message); await SafeState(); return initialEmo; }
            emoWatcher = _clock.IsDeterministic ? WatchEmo(monitorCts.Token) : Task.Run(() => WatchEmo(monitorCts.Token), CancellationToken.None);
            if (_store is not null) { var init = await Persist(() => _store.InitializeAsync(token)); if (!init.Ok) return init; }
            // 준비: 홈 위치(Z=0)는 접촉 높이이므로 로봇이 들어오기 전에 Z를 안전 높이로 올린다(인터락 전제)
            if (_d.ZAxis.PositionUm < recipe.ZSafeHeightUm)
            {
                ProbeState(ProbePhase.Lift, verdict: "로트 시작 · 안전 높이로 상승");
                var lift = await Step("", null, ModuleId.ZHead, PhaseKind.LiftZ, c => _d.ZAxis.MoveAbsoluteAsync(recipe.ZSafeHeightUm, recipe.TravelSpeedMmPerS, recipe.AccelMmPerS2, TimeSpan.FromSeconds(30), c), token);
                if (!lift.Ok) return lift;
            }
            var map = await Step("", null, ModuleId.LoadPort, PhaseKind.MapSlots, async c => { var open = await _d.LoadPort.OpenAsync(TimeSpan.FromSeconds(10), c); if (!open.Ok) return open; var slots = await _d.LoadPort.MapSlotsAsync(TimeSpan.FromSeconds(1), c); if (!slots.Ok) return DeviceResult.Fail(slots.Code, slots.Message); _slots = slots.Value ?? []; return _slots.Count >= waferCount ? DeviceResult.Success : DeviceResult.Fail(AlarmCode.ConfigError, $"로드포트 웨이퍼 부족: 매핑 {_slots.Count}장, 요청 {waferCount}장"); }, token); if (!map.Ok) return map;
            _tracker.AssignSlots(_slots); // 웨이퍼 i는 i번째 점유 슬롯에서 집는다(이전 로트 중단으로 빈 슬롯은 건너뜀)
            if (recipe.Save == SaveMode.Async) { channel = Channel.CreateBounded<WaferSummary>(new BoundedChannelOptions(recipe.SaveQueueCapacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true }); consumer = Consume(channel.Reader, token); }
            for (int i = 1; i <= waferCount; i++)
            {
                var id = $"W{i:D3}"; _currentWafer = id; var began = _clock.Elapsed;
                if (i == 1 || !recipe.UnloadOverlap) { if (!await Prepare(id, _slots[i - 1], recipe, token)) return LastFailure(); }
                else { if (prepared is null || !await prepared || !await PlacePrepared(id, recipe, token)) return LastFailure(); }
                if (_d is IDeviceContext waferContext) waferContext.SetCurrentWafer(id);
                if (recipe.UnloadOverlap && i < waferCount) prepared = Prefetch($"W{i + 1:D3}", _slots[i], recipe, token);
                if (!await MeasureWafer(id, recipe, token)) { _tracker.Update(id, status: WaferStatus.Failed); return LastFailure(); }
                var summary = _rs.Summarize(id, _measurements, _clock.Elapsed - began); _tracker.Update(id, status: WaferStatus.Measured);
                if (recipe.Save == SaveMode.Async && channel is not null)
                {
                    ScheduleSave(id, recipe.SaveWriteTime);
                    _pendingSaves[id] = summary;
                    Interlocked.Increment(ref _queueDepth);
                    if (!channel.Writer.TryWrite(summary))
                    {
                        var queued = await Step(id, null, ModuleId.Storage, PhaseKind.Wait, async c => { await channel.Writer.WriteAsync(summary, c); return DeviceResult.Success; }, token, true, "저장 큐 가득");
                        if (!queued.Ok) return queued;
                    }
                }
                if (prepared is not null && !await prepared) return LastFailure();
                if (!await Unload(id, _slots[i - 1], recipe, token)) return LastFailure();
                _tracker.Update(id, WaferLocation.Done);
                if (recipe.Save == SaveMode.Sync) { var save = await Save(summary, recipe, token); if (!save.Ok) return save; }
                var finalSummary = summary with { Tact = _clock.Elapsed - began }; lock (_gate) _summaries.Add(finalSummary); try { WaferCompleted?.Invoke(finalSummary); } catch { } Publish();
            }
            if (channel is not null) { channel.Writer.Complete(); if (consumer is not null) { var saved = await consumer; if (!saved.Ok) return saved; } }
            TimeSpan reportAt; lock (_gate) reportAt = _log.Where(x => x.End.HasValue).Select(x => x.End!.Value).DefaultIfEmpty(_clock.Elapsed).Max();
            var reported = await Step("", null, ModuleId.Host, PhaseKind.HostReport, _ => Task.FromResult(DeviceResult.Success), token, note: "로트 완료 보고", startOverride: reportAt, endOverride: reportAt);
            if (!reported.Ok) return reported;
            var avg = TimeSpan.FromTicks((long)_summaries.Average(x => x.Tact.Ticks)); var run = new RunRecord(_runId, _startedAtWall, recipe.Name, JsonSerializer.Serialize(recipe), recipe.Seed, waferCount, avg, avg.Ticks > 0 ? 3600 / avg.TotalSeconds : null, BuildSnapshot().Stats.PhaseTotals, _digest?.Digest(Log.Where(x => x.End.HasValue)) ?? "");
            if (_store is not null) { var savedRun = await Persist(() => FinalizeRun(run, token)); if (!savedRun.Ok) return savedRun; var savedLog = await Persist(() => _store.SaveLogAsync(_runId, Log, token)); if (!savedLog.Ok) return savedLog; }
            _completed = true; _runCts = null; _currentWafer = null; try { RunCompleted?.Invoke(run); } catch { } Publish(); return DeviceResult.Success;
        }
        catch (OperationCanceledException) { await SafeState(); Publish(); return _alarms.Any(x => x.ClearedAt is null) ? LastFailure() : DeviceResult.Fail(AlarmCode.None, "중단됨"); }
        catch (Exception e) { var code = ClassifyException(e, PhaseKind.AlarmRecovery); Raise(code, ModuleId.Host, e.ToString()); await SafeState(); return DeviceResult.Fail(code, e.Message); }
        finally
        {
            lock (_controlGate) { _pause = false; _resume.TrySetResult(); }
            if (!_completed) _runCts?.Cancel();
            monitorCts.Cancel();
            channel?.Writer.TryComplete();
            if (prepared is not null) { try { await prepared; } catch (Exception e) { Raise(ClassifyException(e, PhaseKind.AlarmRecovery), ModuleId.Host, e.ToString()); } }
            if (consumer is not null) { try { await consumer; } catch (OperationCanceledException) { } catch (Exception e) { Raise(ClassifyException(e, PhaseKind.Save), ModuleId.Storage, e.ToString()); } }
            if (emoWatcher is not null) { try { await emoWatcher; } catch (Exception e) { Raise(ClassifyException(e, PhaseKind.AlarmRecovery), ModuleId.Host, e.ToString()); } }
            if (!_completed)
            {
                try { await FlushPending(channel, null); } catch (Exception e) { Raise(ClassifyException(e, PhaseKind.Save), ModuleId.Storage, e.ToString()); }
                try { await PersistFailedRun(recipe, waferCount); } catch (Exception e) { Raise(ClassifyException(e, PhaseKind.Save), ModuleId.Storage, e.ToString()); }
            }
            lock (_controlGate) { _pause = false; _resume.TrySetResult(); if (_chuckHeld) { _chuckHeld = false; _chuck.Release(); } }
            lock (_gate) _runEndedAt = _clock.Elapsed;
            _runCts = null; _runDone.TrySetResult(); Publish();
        }
    }
    private DeviceResult LastFailure() { lock (_gate) { var alarm = _alarms.LastOrDefault(x => x.ClearedAt is null); return alarm is null ? DeviceResult.Fail(AlarmCode.ConfigError, "시퀀스 실패") : DeviceResult.Fail(alarm.Code, alarm.Message); } }
    private void ScheduleSave(string waferId, TimeSpan writeTime)
    {
        lock (_gate)
        {
            var now = _clock.Elapsed;
            var start = now > _saveAvailableAt ? now : _saveAvailableAt;
            var end = start + writeTime;
            _saveSchedule[waferId] = (start, end);
            _saveAvailableAt = end;
        }
    }
    private async Task PersistFailedRun(RecipeDto recipe, int waferCount)
    {
        TimeSpan? average;
        lock (_gate) average = _summaries.Count == 0 ? null : TimeSpan.FromTicks((long)_summaries.Average(x => x.Tact.Ticks));
        bool activeAlarm; lock (_gate) activeAlarm = _alarms.Any(x => x.ClearedAt is null);
        var aborted = _aborting || !activeAlarm;
        var name = recipe.Name + (aborted ? " [중단]" : " [실패]");
        var run = new RunRecord(_runId, _startedAtWall, name, JsonSerializer.Serialize(recipe), recipe.Seed, waferCount, average, average is { Ticks: > 0 } ? 3600d / average.Value.TotalSeconds : null, BuildSnapshot().Stats.PhaseTotals, _digest?.Digest(Log.Where(x => x.End.HasValue)) ?? "");
        if (_store is not null)
        {
            var saved = await Persist(() => FinalizeRun(run, CancellationToken.None));
            if (saved.Ok) await Persist(() => _store.SaveLogAsync(_runId, Log, CancellationToken.None));
        }
        try { RunCompleted?.Invoke(run); } catch { }
    }
    private Task FinalizeRun(RunRecord run, CancellationToken ct) => _store is IRunFinalizer finalizer ? finalizer.FinalizeRunAsync(run, ct) : _store!.SaveRunAsync(run, ct);
    private async Task<DeviceResult> Persist(Func<Task> write)
    {
        try { await write(); return DeviceResult.Success; }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { Raise(AlarmCode.SaveWriteFail, ModuleId.Storage, e.ToString()); await SafeState(); return DeviceResult.Fail(AlarmCode.SaveWriteFail, e.Message); }
    }
    private async Task<DeviceResult> FlushPending(Channel<WaferSummary>? channel, Task<DeviceResult>? consumer)
    {
        channel?.Writer.TryComplete();
        if (consumer is not null) { try { await consumer; } catch (OperationCanceledException) { } }
        var firstFailure = DeviceResult.Success;
        foreach (var wafer in _pendingSaves.Values.OrderBy(x => x.WaferId))
        {
            var saved = await Save(wafer, _recipe!, CancellationToken.None);
            if (!saved.Ok) { if (firstFailure.Ok) firstFailure = saved; continue; }
            if (_pendingSaves.TryRemove(wafer.WaferId, out _)) Interlocked.Decrement(ref _queueDepth);
        }
        return firstFailure;
    }
    private async Task<DeviceResult> MoveStage(double xUm, double yUm, RecipeDto recipe, CancellationToken ct)
    {
        Interlocked.Exchange(ref _stageMoving, 1);
        try
        {
            var x = await _d.StageX.MoveAbsoluteAsync(xUm, recipe.TravelSpeedMmPerS, recipe.AccelMmPerS2, TimeSpan.FromSeconds(30), ct);
            return x.Ok ? await _d.StageY.MoveAbsoluteAsync(yUm, recipe.TravelSpeedMmPerS, recipe.AccelMmPerS2, TimeSpan.FromSeconds(30), ct) : x;
        }
        finally { Interlocked.Exchange(ref _stageMoving, 0); }
    }
    private readonly List<MeasurementResult> _measurements = [];
    private async Task<bool> Prefetch(string id, int slot, RecipeDto r, CancellationToken ct)
    {
        await _robot.WaitAsync(ct);
        try
        {
            var pick = await Step(id, null, ModuleId.Robot, PhaseKind.PickFromSlot, c => _d.Robot.PickFromSlotAsync(slot, r.RobotMoveTime, c), ct);
            if (!pick.Ok) return false;
            _tracker.Update(id, WaferLocation.RobotArm, WaferStatus.Processing);
            var place = await Step(id, null, ModuleId.Robot, PhaseKind.Align, c => _d.Robot.PlaceOnAlignerAsync(r.RobotMoveTime, c), ct, note: "얼라이너에 놓기");
            if (!place.Ok) return false;
            _tracker.Update(id, WaferLocation.Aligner);
        }
        finally { _robot.Release(); }
        await _aligner.WaitAsync(ct);
        try { var align = await Step(id, null, ModuleId.Aligner, PhaseKind.Align, async c => { var x = await _d.Aligner.AlignAsync(r.AlignTime, c); return x.Ok ? DeviceResult.Success : DeviceResult.Fail(x.Code, x.Message); }, ct); return align.Ok; }
        finally { _aligner.Release(); }
    }
    private async Task<bool> PlacePrepared(string id, RecipeDto r, CancellationToken ct)
    {
        _measurements.Clear();
        await _chuck.WaitAsync(ct); lock (_controlGate) _chuckHeld = true;
        await _robot.WaitAsync(ct);
        try
        {
            var pick = await Step(id, null, ModuleId.Robot, PhaseKind.Align, c => _d.Robot.PickFromAlignerAsync(r.RobotMoveTime, c), ct, note: "얼라이너에서 집기");
            if (!pick.Ok) return false;
            _tracker.Update(id, WaferLocation.RobotArm);
            var place = await Step(id, null, ModuleId.Robot, PhaseKind.PlaceOnChuck, c => _d.Robot.PlaceOnChuckAsync(r.RobotMoveTime, c), ct);
            if (!place.Ok) return false;
            _tracker.Update(id, WaferLocation.Chuck);
        }
        finally { _robot.Release(); }
        return await StartVacuum(id, r, ct);
    }
    private async Task<bool> Prepare(string id, int slot, RecipeDto r, CancellationToken ct)
    {
        if (!await Prefetch(id, slot, r, ct)) return false;
        return await PlacePrepared(id, r, ct);
    }
    private async Task<bool> StartVacuum(string id, RecipeDto r, CancellationToken ct)
    {
        var vac = await Step(id, null, ModuleId.Vacuum, PhaseKind.VacuumCheck, c => _d.Io.WriteAsync(IoPoints.VacChuckOn, true, c), ct, note: "진공 켜기"); if (!vac.Ok) return false; _vacuumOn = true;
        for (int n = 0; n < r.VacuumCheckCount; n++) { var v = await Step(id, null, ModuleId.Vacuum, PhaseKind.VacuumCheck, async c => { await Task.Delay(r.VacuumCheckWait, _clock, c); var sense = await _d.Io.ReadAsync(IoPoints.VacChuckSense, c); if (!sense.Ok) return DeviceResult.Fail(sense.Code, sense.Message); _vacuumConfirmed = sense.Value; return _vacuumConfirmed ? DeviceResult.Success : DeviceResult.Fail(AlarmCode.VacuumNotConfirmed, "척 진공 미확인"); }, ct, true, "진공 안정"); if (!v.Ok) return false; }
        var light = await Step(id, null, ModuleId.Vision, PhaseKind.Illumination, async c => { var x = await _d.Io.WriteAsync(IoPoints.LampOn, true, c); if (!x.Ok) return x; _lampOn = true; await Task.Delay(r.IlluminationWait, _clock, c); return DeviceResult.Success; }, ct, true, "조명 안정"); return light.Ok;
    }
    private async Task<bool> MeasureWafer(string id, RecipeDto r, CancellationToken ct)
    {
        for (int i = 0; i < _points.Count; i++)
        {
            _currentSite = i; var point = _points[i];
            if (!(await Step(id, i, ModuleId.Stage, PhaseKind.MoveXY, c => MoveStage(point.XUm, point.YUm, r, c), ct)).Ok) return false;
            if (r.MapInspect == MapInspectMode.AllSites || i == 0)
            {
                if (!(await Step(id, i, ModuleId.Vision, PhaseKind.AutoFocus, c => _d.Vision.AutoFocusAsync(r.AutoFocusWait, TimeSpan.FromSeconds(10), c), ct, true, "초점 안정")).Ok) return false;
                if (!(await Step(id, i, ModuleId.Vision, PhaseKind.MapInspect, async c => { var x = await _d.Vision.InspectAsync(r.MapInspectTime, c); return x.Ok ? DeviceResult.Success : DeviceResult.Fail(x.Code, x.Message); }, ct)).Ok) return false;
            }
            _probeDetail = null;
            var contact = false;
            for (int retry = 0; retry <= r.ContactMaxRetry; retry++)
            {
                ProbeState(ProbePhase.Approach, contactRetry: retry);
                if (!(await Step(id, i, ModuleId.ZHead, PhaseKind.DescendZ, async c => { if (r.ZMode == ZApproachMode.Slow) return await _d.ZAxis.MoveAbsoluteAsync(0, r.ZApproachSpeedMmPerS, r.AccelMmPerS2, TimeSpan.FromSeconds(30), c); while (_d.ZAxis.PositionUm > 0) { var x = await _d.ZAxis.MoveRelativeAsync(-Math.Min(r.ZStepUm, _d.ZAxis.PositionUm), r.ZApproachSpeedMmPerS, r.AccelMmPerS2, TimeSpan.FromSeconds(30), c); if (!x.Ok) return x; var sense = await _d.Io.ReadAsync(IoPoints.ProbeContact, c); if (!sense.Ok) return DeviceResult.Fail(sense.Code, sense.Message); if (sense.Value) break; } return DeviceResult.Success; }, ct, note: retry > 0 ? $"재시도 {retry}회" : "")).Ok) return false;
                ProbeState(ProbePhase.ContactCheck, observed: null);
                var inject = _random.NextDouble() < r.ContactRetryProbability;
                var sensorReady = _clock.Elapsed + TimeSpan.FromSeconds(5);
                var detection = await Step(id, i, ModuleId.ZHead, PhaseKind.ContactDetect, async c => { DeviceResult? readError = null; var waited = await _wait.WaitForAsync(async x => { var s = await _d.Io.ReadAsync(IoPoints.ProbeContact, x); if (!s.Ok) { readError = DeviceResult.Fail(s.Code, s.Message); return false; } var observed = (!_sensorDelay || _clock.Elapsed >= sensorReady) && !inject && s.Value; ProbeState(ProbePhase.ContactCheck, observed: observed); return observed; }, TimeSpan.FromMilliseconds(10), r.ContactDetectTimeout, AlarmCode.ContactTimeout, "프로브 접촉", c); return readError ?? waited; }, ct, true, "접촉 확인", note: retry > 0 ? $"재시도 {retry}회" : "", retryable: true);
                if (detection.Ok) { contact = true; break; }
                if (detection.Code != AlarmCode.ContactTimeout) { Raise(detection.Code, ModuleId.ZHead, detection.Message); await SafeState(); return false; }
                if (retry == r.ContactMaxRetry) { ProbeState(ProbePhase.Rejected, observed: false, verdict: "접촉 확인 실패 · 재접촉 한도 소진"); Raise(AlarmCode.ContactRetryExceeded, ModuleId.ZHead, "접촉 재시도 초과"); await SafeState(); return false; }
                ProbeState(ProbePhase.Lift, verdict: "접촉 실패 · 상승 후 재접촉");
                var lift = await Step(id, i, ModuleId.ZHead, PhaseKind.LiftZ, c => _d.ZAxis.MoveAbsoluteAsync(50, r.ZApproachSpeedMmPerS, r.AccelMmPerS2, TimeSpan.FromSeconds(30), c), ct, note: "접촉 재시도 50 µm 상승"); if (!lift.Ok) return false;
            }
            if (!contact) return false;
            ProbeState(ProbePhase.Settle, observed: true);
            if (!(await Step(id, i, ModuleId.ZHead, PhaseKind.Wait, async c => { await Task.Delay(r.ProbeSettleTime, _clock, c); return DeviceResult.Success; }, ct, true, "접촉 안정화")).Ok) return false;
            MeasurementResult? accepted = null;
            for (int qualityRetry = 0; qualityRetry <= r.QualityMaxRetry; qualityRetry++)
            {
            ProbeState(ProbePhase.Sampling, samples: 0, qualityRetry: qualityRetry, observed: true);
            var volts = new List<double>();
            for (int reading = 0; reading < r.ReadingsPerSite; reading++)
            {
                var index = reading;
                var v = await Step(id, i, ModuleId.Smu, PhaseKind.Measure, async c =>
                {
                    if (index == 0) { var configured = await _d.Smu.ConfigureAsync(r.SourceCurrentA, c); if (!configured.Ok) return configured; var on = await _d.Smu.OutputAsync(true, c); if (!on.Ok) return on; }
                    var sample = await _d.Smu.MeasureVoltageAsync(r.ReadingTime, c);
                    if (!sample.Ok) return DeviceResult.Fail(sample.Code, sample.Message);
                    volts.Add(sample.Value);
                    ProbeState(ProbePhase.Sampling, samples: volts.Count, observed: true);
                    _probeDetail = _probeDetail! with { LastSampleV = sample.Value };
                    return index == r.ReadingsPerSite - 1 ? await _d.Smu.OutputAsync(false, c) : DeviceResult.Success;
                }, ct, note: $"읽기 {reading + 1}");
                if (!v.Ok) return false;
            }
            var candidate = _rs.Build(id, i, point.XUm, point.YUm, volts, r.SourceCurrentA, r.WaferDiameterMm, _clock.Elapsed);
            var deviation = candidate.RsOhmPerSq == 0 ? double.PositiveInfinity : Math.Abs(candidate.RsSigma / candidate.RsOhmPerSq) * 100;
            var good = !r.QualityEnabled || volts.Count(double.IsFinite) >= r.QualityMinSamples && double.IsFinite(deviation) && deviation <= r.QualityTolerancePct;
            ProbeState(ProbePhase.QualityCheck, samples: volts.Count(double.IsFinite), deviation: deviation, observed: true, verdict: !r.QualityEnabled ? "품질 판정 비활성" : good ? "양품" : "반복 샘플 편차 초과");
            var judged = await Step(id, i, ModuleId.Smu, PhaseKind.Wait, _ => Task.FromResult(DeviceResult.Success), ct, note: $"품질 판정 σ/평균 {deviation:F4}% / 허용 {r.QualityTolerancePct:F4}% · 재측정 {qualityRetry} · {(!r.QualityEnabled ? "미판정" : good ? "통과" : "불량")}");
            if (!judged.Ok) return false;
            if (good) { accepted = candidate; break; }
            if (qualityRetry == r.QualityMaxRetry) { ProbeState(ProbePhase.Rejected, verdict: "품질 허용 초과 · 최종 불량"); Raise(AlarmCode.QualityRejected, ModuleId.Smu, "반복 샘플 편차 허용 초과 · 품질 재시도 소진"); await SafeState(); return false; }
            }
            var result = accepted!; _measurements.Add(result); try { MeasurementCompleted?.Invoke(result); } catch { } _tracker.Update(id, sitesDone: i + 1);
            ProbeState(ProbePhase.Lift, observed: true, verdict: _probeDetail?.Verdict ?? "");
            if (!(await Step(id, i, ModuleId.ZHead, PhaseKind.LiftZ, c => _d.ZAxis.MoveAbsoluteAsync(r.ZLiftAfterMeasureUm, r.TravelSpeedMmPerS, r.AccelMmPerS2, TimeSpan.FromSeconds(30), c), ct)).Ok) return false;
        }
        _currentSite = null; return true;
    }
    private async Task<bool> Unload(string id, int slot, RecipeDto r, CancellationToken ct)
    {
        if (!(await Step(id, null, ModuleId.ZHead, PhaseKind.LiftZ, c => _d.ZAxis.MoveAbsoluteAsync(r.ZSafeHeightUm, r.TravelSpeedMmPerS, r.AccelMmPerS2, TimeSpan.FromSeconds(30), c), ct)).Ok) return false;
        if (!(await Step(id, null, ModuleId.Stage, PhaseKind.MoveXY, c => MoveStage(0, 0, r, c), ct)).Ok) return false;
        var off = await Step(id, null, ModuleId.Vacuum, PhaseKind.VacuumCheck, c => _d.Io.WriteAsync(IoPoints.VacChuckOn, false, c), ct, note: "진공 해제"); if (!off.Ok) return false; _vacuumOn = false; _vacuumConfirmed = false;
        await _robot.WaitAsync(ct); try { if (!(await Step(id, null, ModuleId.Robot, PhaseKind.PickFromChuck, c => _d.Robot.PickFromChuckAsync(r.RobotMoveTime, c), ct)).Ok) return false; _tracker.Update(id, WaferLocation.RobotArm); if (!(await Step(id, null, ModuleId.Robot, PhaseKind.ReturnToSlot, c => _d.Robot.ReturnToSlotAsync(slot, r.RobotMoveTime, c), ct)).Ok) return false; lock (_controlGate) { if (_chuckHeld) { _chuckHeld = false; _chuck.Release(); } } return true; } finally { _robot.Release(); }
    }
    private Task<DeviceResult> Save(WaferSummary summary, RecipeDto r, CancellationToken ct)
    {
        (TimeSpan Start, TimeSpan End)? schedule;
        lock (_gate) schedule = r.Save == SaveMode.Async && _saveSchedule.TryGetValue(summary.WaferId, out var planned) ? planned : null;
        return Step(summary.WaferId, null, ModuleId.Storage, PhaseKind.Save, async c => { await Task.Delay(r.SaveWriteTime, _clock, c); if (_store is not null) await _store.SaveWaferAsync(_runId, summary, c); _tracker.Update(summary.WaferId, status: StorageRecovery?.IsWaferPending(_runId, summary.WaferId) == true ? WaferStatus.Journaled : WaferStatus.Saved); return DeviceResult.Success; }, ct, true, "결과 기록", startOverride: schedule?.Start, endOverride: schedule?.End);
    }
    private async Task<DeviceResult> Consume(ChannelReader<WaferSummary> reader, CancellationToken ct) { await foreach (var item in reader.ReadAllAsync(ct)) { var result = await Save(item, _recipe!, ct); if (!result.Ok) { _runCts?.Cancel(); return result; } _pendingSaves.TryRemove(item.WaferId, out _); Interlocked.Decrement(ref _queueDepth); } return DeviceResult.Success; }
    public void Pause() { lock (_controlGate) { if (_runCts is null || _pause) return; _resume = new(TaskCreationOptions.RunContinuationsAsynchronously); _pause = true; } }
    public void Resume() { lock (_controlGate) { _pause = false; _resume.TrySetResult(); } Publish(); }
    public async Task AbortAsync()
    {
        while (Volatile.Read(ref _starting) != 0 && Volatile.Read(ref _runCts) is null) await Task.Yield();
        var run = Volatile.Read(ref _runCts); if (run is null) return;
        var done = _runDone;
        lock (_controlGate) { _aborting = true; _pause = false; _resume.TrySetResult(); }
        run.Cancel(); await done.Task;
        lock (_controlGate) { _aborting = false; _completed = false; }
        Publish();
    }
    public Task<DeviceResult> ResetAsync()
    {
        if (Volatile.Read(ref _recovering) != 0 || _runCts is not null || Volatile.Read(ref _starting) != 0 || Volatile.Read(ref _initializing) != 0) return Task.FromResult(DeviceResult.Fail(AlarmCode.InterlockBlocked, "실행 중에는 초기화할 수 없음"));
        Alarm[] cleared;
        lock (_gate)
        {
            if (_alarms.Any(x => x.ClearedAt is null && x.Severity == AlarmSeverity.Error && !x.Acked)) return Task.FromResult(DeviceResult.Fail(AlarmCode.InterlockBlocked, "확인되지 않은 알람"));
            cleared = _alarms.Where(x => x.ClearedAt is null).Select(x => x with { ClearedAt = _clock.Elapsed }).ToArray();
            foreach (var alarm in cleared) _alarms[_alarms.FindIndex(x => x.Id == alarm.Id)] = alarm;
            foreach (var m in _modules) m.Set(ModuleState.Uninitialized, "", _clock.Elapsed);
            _completed = false;
        }
        foreach (var alarm in cleared) { try { AlarmCleared?.Invoke(alarm); } catch { } }
        Publish(); return Task.FromResult(DeviceResult.Success);
    }
    public void AcknowledgeAlarm(string alarmId) { lock (_gate) { var i = _alarms.FindIndex(x => x.Id == alarmId && x.ClearedAt is null); if (i >= 0) _alarms[i] = _alarms[i] with { Acked = true }; } Publish(); }
}
