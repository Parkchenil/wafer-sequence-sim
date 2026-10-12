using System.Text.Json;
using System.Threading.Channels;
using Contracts;

namespace Core.Sequence;

public abstract class ModuleMachine
{
    protected ModuleMachine(ModuleId id) { Id = id; }
    public ModuleId Id { get; }
    public ModuleState State { get; private set; } = ModuleState.Uninitialized;
    public string Detail { get; private set; } = "";
    public TimeSpan Since { get; private set; }
    public void Set(ModuleState state, string detail, TimeSpan at) { State = state; Detail = detail; Since = at; }
    public ModuleStatus Status => new(Id, State, Detail, Since);
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
    private readonly List<WaferTrack> _items = [];
    public IReadOnlyList<WaferTrack> Items => _items.ToArray();
    public void Reset(int count, int sites) { _items.Clear(); for (int i = 1; i <= count; i++) _items.Add(new($"W{i:D3}", i, WaferLocation.LoadPort, WaferStatus.Pending, 0, sites)); }
    public void Update(string id, WaferLocation? location = null, WaferStatus? status = null, int? sitesDone = null)
    { var i = _items.FindIndex(x => x.WaferId == id); if (i >= 0) { var w = _items[i]; _items[i] = w with { Location = location ?? w.Location, Status = status ?? w.Status, SitesDone = sitesDone ?? w.SitesDone }; } }
}

public sealed class Interlock(IDeviceSet devices, Func<bool> vacuumConfirmed, Func<double> safeHeight, Func<bool> emo) : IInterlock
{
    private static DeviceResult Block(string reason) => DeviceResult.Fail(AlarmCode.InterlockBlocked, reason);
    public DeviceResult CheckMoveXY() => emo() ? Block("비상정지") : devices.ZAxis.PositionUm < safeHeight() ? Block("Z 안전 높이 미달") : !vacuumConfirmed() ? Block("진공 미확인") : devices.Robot.Extended ? Block("로봇 진입") : DeviceResult.Success;
    public DeviceResult CheckDescendZ() => emo() ? Block("비상정지") : !vacuumConfirmed() ? Block("진공 미확인") : !StageStopped() ? Block("XY 이동 중") : DeviceResult.Success;
    public DeviceResult CheckRobotAccess() => emo() ? Block("비상정지") : devices.ZAxis.PositionUm < safeHeight() ? Block("Z 안전 높이 미달") : Math.Abs(devices.StageX.PositionUm) > 0.01 || Math.Abs(devices.StageY.PositionUm) > 0.01 ? Block("스테이지 원점 아님") : DeviceResult.Success;
    public DeviceResult CheckVacuumOff() => emo() ? Block("비상정지") : devices.ZAxis.PositionUm < safeHeight() ? Block("Z 안전 높이 미달") : DeviceResult.Success;
    private bool StageStopped() => !devices.StageX.GetStatusAsync(CancellationToken.None).GetAwaiter().GetResult().Value!.Moving && !devices.StageY.GetStatusAsync(CancellationToken.None).GetAwaiter().GetResult().Value!.Moving;
}

public sealed class SequenceEngine : ISimulator
{
    private readonly IDeviceSet _d; private readonly IWaitHelper _wait; private readonly VirtualTimeProvider _clock; private readonly IRsCalculator _rs; private readonly ISitePatternProvider _sites; private readonly IResultStore? _store; private readonly ILogDigest? _digest;
    private readonly ModuleMachine[] _modules = [new LoadPortModule(), new RobotModule(), new AlignerModule(), new StageModule(), new ZHeadModule(), new SmuModule(), new VisionModule(), new VacuumModule(), new StorageModule(), new HostModule()];
    private readonly TimeProvider _wall = TimeProvider.System; private readonly object _publishGate = new(); private long _lastWallSnapshot; private ITimer? _snapshotTimer;
    private readonly WaferTracker _tracker = new(); private readonly List<StepEvent> _log = []; private readonly List<Alarm> _alarms = []; private readonly List<WaferSummary> _summaries = []; private readonly object _gate = new();
    private readonly SemaphoreSlim _robot = new(1), _aligner = new(1), _chuck = new(1);
    private CancellationTokenSource? _runCts; private TaskCompletionSource _runDone = new(TaskCreationOptions.RunContinuationsAsynchronously); private TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RecipeDto? _recipe; private IReadOnlyList<SitePoint> _points = []; private Random _random = new(1); private bool _pause, _aborting, _completed, _vacuumOn, _vacuumConfirmed, _lampOn; private string? _currentWafer; private int? _currentSite; private long _seq; private bool _chuckHeld; private int _queueDepth, _queueCapacity; private long _asyncSaveTicks; private string _runId = "";
    public SequenceEngine(IDeviceSet devices, IWaitHelper wait, VirtualTimeProvider clock, IRsCalculator rs, ISitePatternProvider sites, IResultStore? store, ILogDigest? digest)
    { _d = devices; _wait = wait; _clock = clock; _rs = rs; _sites = sites; _store = store; _digest = digest; Interlock = new Interlock(devices, () => _vacuumConfirmed, () => _recipe?.ZSafeHeightUm ?? 300, () => _d.Io.ReadAsync(IoPoints.Emo, CancellationToken.None).GetAwaiter().GetResult().Value); }
    public IInterlock Interlock { get; }
    public RecipeDto? CurrentRecipe => _recipe;
    public IReadOnlyList<StepEvent> Log { get { lock (_gate) return _log.ToArray(); } }
    public EquipmentSnapshot Snapshot => BuildSnapshot();
    public event Action<EquipmentSnapshot>? SnapshotChanged; public event Action<StepEvent>? StepLogged; public event Action<Alarm>? AlarmRaised; public event Action<Alarm>? AlarmCleared; public event Action<MeasurementResult>? MeasurementCompleted; public event Action<WaferSummary>? WaferCompleted; public event Action<RunRecord>? RunCompleted;
    private ModuleMachine M(ModuleId id) => _modules[(int)id];
    private EquipmentState State => _alarms.Any(a => a.ClearedAt is null && a.Severity == AlarmSeverity.Error) ? EquipmentState.Alarm : _aborting ? EquipmentState.Aborting : _pause && _runCts is not null ? EquipmentState.Paused : _runCts is not null ? EquipmentState.Running : _modules.Any(m => m.State == ModuleState.Homing) ? EquipmentState.Initializing : _modules.Any(m => m.State == ModuleState.Uninitialized) ? EquipmentState.Uninitialized : _completed ? EquipmentState.Completed : EquipmentState.Idle;
    private EquipmentSnapshot BuildSnapshot()
    {
        lock (_gate)
        {
            var totals = _log.Where(x => x.End.HasValue).GroupBy(x => x.Phase).ToDictionary(x => x.Key, x => TimeSpan.FromTicks(x.Sum(y => y.Duration!.Value.Ticks)));
            var elapsed = _clock.Elapsed; var avg = _summaries.Count == 0 ? (TimeSpan?)null : TimeSpan.FromTicks((long)_summaries.Average(x => x.Tact.Ticks));
            var stats = new RunStats(_summaries.Count, _tracker.Items.Count, elapsed, avg, avg is { Ticks: > 0 } ? 3600d / avg.Value.TotalSeconds : null, totals, elapsed.Ticks == 0 ? 0 : (double)totals.Where(x => x.Key == PhaseKind.Wait).Sum(x => x.Value.Ticks) / elapsed.Ticks, _queueDepth, _queueCapacity);
            var motion = new MotionSnapshot(_d.StageX.PositionUm, _d.StageY.PositionUm, _d.ZAxis.PositionUm, _d.Probe.InContact, _vacuumOn, _vacuumConfirmed, _lampOn, _d.Vision.Focused, _d.Robot.Extended, _d.LoadPort.DoorOpen, _d.Aligner.AngleDeg);
            return new(elapsed, State, _modules.Select(x => x.Status).ToArray(), _tracker.Items, motion, stats, _alarms.Where(x => x.ClearedAt is null).ToArray(), _currentWafer, _currentSite, _points);
        }
    }
    private void Publish(bool force = false)
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
        SnapshotChanged?.Invoke(BuildSnapshot());
    }
    private async Task<DeviceResult> Step(string wafer, int? site, ModuleId module, PhaseKind phase, Func<CancellationToken, Task<DeviceResult>> action, CancellationToken ct, bool wait = false, string? reason = null, string note = "", bool retryable = false)
    {
        await PauseGate(ct); ct.ThrowIfCancellationRequested(); var start = _clock.Elapsed; var seq = Interlocked.Increment(ref _seq); var first = new StepEvent(seq, wafer, site, module, phase, start, null, wait, reason, note);
        lock (_gate) { _log.Add(first); M(module).Set(wait ? ModuleState.Waiting : ModuleState.Busy, phase.ToString(), start); }
        StepLogged?.Invoke(first); Publish();
        DeviceResult result;
        try { result = await action(ct); }
        catch (OperationCanceledException) { var cancelled = first with { End = _clock.Elapsed }; lock (_gate) { _log.Add(cancelled); M(module).Set(ModuleState.Idle, "", _clock.Elapsed); } StepLogged?.Invoke(cancelled); throw; }
        catch (Exception e) { result = DeviceResult.Fail(phase == PhaseKind.Save ? AlarmCode.SaveWriteFail : AlarmCode.CommTimeout, e.Message); }
        var done = first with { End = _clock.Elapsed }; lock (_gate) { _log.Add(done); M(module).Set(ModuleState.Idle, "", _clock.Elapsed); }
        StepLogged?.Invoke(done); Publish();
        if (!result.Ok && !retryable) { Raise(result.Code, module, result.Message); await SafeState(); }
        return result;
    }
    private async Task PauseGate(CancellationToken ct) { if (_pause) { foreach (var m in _modules.Where(m => m.State == ModuleState.Idle)) m.Set(ModuleState.Paused, "일시정지", _clock.Elapsed); Publish(true); await _resume.Task.WaitAsync(ct); foreach (var m in _modules.Where(m => m.State == ModuleState.Paused)) m.Set(ModuleState.Idle, "", _clock.Elapsed); } }
    private void Raise(AlarmCode code, ModuleId module, string message) { var alarm = new Alarm($"A{_alarms.Count + 1:D4}", code, AlarmSeverity.Error, module, message, _clock.Elapsed, null, false); lock (_gate) { _alarms.Add(alarm); M(module).Set(ModuleState.Alarm, message, _clock.Elapsed); } AlarmRaised?.Invoke(alarm); Publish(true); }
    private async Task SafeState() { var ct = CancellationToken.None; await _d.ZAxis.StopAsync(ct); await _d.StageX.StopAsync(ct); await _d.StageY.StopAsync(ct); if (_recipe is { } r) await _d.ZAxis.MoveAbsoluteAsync(r.ZSafeHeightUm, r.TravelSpeedMmPerS, r.AccelMmPerS2, TimeSpan.FromSeconds(30), ct); if (_chuckHeld) { _chuckHeld = false; _chuck.Release(); } Publish(true); }
    /// <summary>UI 스레드의 동기화 컨텍스트를 타지 않도록 본체는 스레드풀에서 돈다(화면 스레드 점유·데드락 방지).</summary>
    public Task<DeviceResult> InitializeAsync(CancellationToken ct) => Task.Run(() => InitializeCoreAsync(ct), ct);
    private async Task<DeviceResult> InitializeCoreAsync(CancellationToken ct)
    {
        if (State != EquipmentState.Uninitialized) return DeviceResult.Fail(AlarmCode.InterlockBlocked, "초기화 상태가 아님");
        foreach (var m in _modules) m.Set(ModuleState.Homing, "원점 복귀", _clock.Elapsed); Publish(true);
        foreach (var axis in new[] { _d.StageX, _d.StageY, _d.ZAxis }) { var on = await axis.ServoOnAsync(ct); if (!on.Ok) { Raise(on.Code, ModuleId.Stage, on.Message); return on; } var home = await axis.HomeAsync(TimeSpan.FromSeconds(30), ct); if (!home.Ok) { Raise(home.Code, ModuleId.Stage, home.Message); return home; } }
        foreach (var m in _modules) m.Set(ModuleState.Idle, "", _clock.Elapsed); Publish(true); return DeviceResult.Success;
    }
    public Task<DeviceResult> StartAsync(RecipeDto recipe, int waferCount, CancellationToken ct) => Task.Run(() => StartCoreAsync(recipe, waferCount, ct), ct);
    private async Task<DeviceResult> StartCoreAsync(RecipeDto recipe, int waferCount, CancellationToken ct)
    {
        if (State != EquipmentState.Idle && State != EquipmentState.Completed) return DeviceResult.Fail(AlarmCode.InterlockBlocked, "시작 상태가 아님");
        if (waferCount < 1 || recipe.ReadingsPerSite < 1 || recipe.SaveQueueCapacity < 1) return DeviceResult.Fail(AlarmCode.InterlockBlocked, "레시피 값 오류");
        _clock.Configure(recipe.Clock, recipe.TimeScale); _recipe = recipe; _random = new Random(recipe.Seed); _points = _sites.Build(recipe); _tracker.Reset(waferCount, _points.Count); _summaries.Clear(); _log.Clear(); _seq = 0; _asyncSaveTicks = 0; _completed = false; _queueCapacity = recipe.SaveQueueCapacity; _runId = $"R{recipe.Seed}-{waferCount}"; _runDone = new(TaskCreationOptions.RunContinuationsAsynchronously); _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct); var token = _runCts.Token; Publish(true);
        Channel<WaferSummary>? channel = null; Task<DeviceResult>? consumer = null; Task<bool>? prepared = null;
        try
        {
            if (_store is not null) await _store.InitializeAsync(token);
            // 준비: 홈 위치(Z=0)는 접촉 높이이므로 로봇이 들어오기 전에 Z를 안전 높이로 올린다(인터락 전제)
            if (_d.ZAxis.PositionUm < recipe.ZSafeHeightUm)
            {
                var lift = await Step("", null, ModuleId.ZHead, PhaseKind.LiftZ, c => _d.ZAxis.MoveAbsoluteAsync(recipe.ZSafeHeightUm, recipe.TravelSpeedMmPerS, recipe.AccelMmPerS2, TimeSpan.FromSeconds(30), c), token);
                if (!lift.Ok) return lift;
            }
            var map = await Step("", null, ModuleId.LoadPort, PhaseKind.MapSlots, async c => { var open = await _d.LoadPort.OpenAsync(TimeSpan.FromSeconds(10), c); if (!open.Ok) return open; var slots = await _d.LoadPort.MapSlotsAsync(TimeSpan.FromSeconds(1), c); return slots.Ok ? DeviceResult.Success : DeviceResult.Fail(slots.Code, slots.Message); }, token); if (!map.Ok) return map;
            if (recipe.Save == SaveMode.Async) { channel = Channel.CreateBounded<WaferSummary>(new BoundedChannelOptions(recipe.SaveQueueCapacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true }); consumer = Consume(channel.Reader, token); }
            for (int i = 1; i <= waferCount; i++)
            {
                var id = $"W{i:D3}"; _currentWafer = id; var began = _clock.Elapsed; var savedAtBegin = Interlocked.Read(ref _asyncSaveTicks);
                if (i == 1 || !recipe.UnloadOverlap) { if (!await Prepare(id, i, recipe, token)) return LastFailure(); }
                else { if (prepared is null || !await prepared || !await PlacePrepared(id, recipe, token)) return LastFailure(); }
                if (recipe.UnloadOverlap && i < waferCount) prepared = Prefetch($"W{i + 1:D3}", i + 1, recipe, token);
                if (!await MeasureWafer(id, recipe, token)) return LastFailure();
                var summary = _rs.Summarize(id, _measurements, _clock.Elapsed - began); _tracker.Update(id, status: WaferStatus.Measured);
                if (recipe.Save == SaveMode.Async && channel is not null)
                {
                    if (!channel.Writer.TryWrite(summary))
                    {
                        var queued = await Step(id, null, ModuleId.Storage, PhaseKind.Wait, async c => { await channel.Writer.WriteAsync(summary, c); return DeviceResult.Success; }, token, true, "저장 큐 가득");
                        if (!queued.Ok) return queued;
                    }
                    Interlocked.Increment(ref _queueDepth);
                }
                if (prepared is not null && !await prepared) return LastFailure();
                if (!await Unload(id, i, recipe, token)) return LastFailure();
                _tracker.Update(id, WaferLocation.Done);
                if (recipe.Save == SaveMode.Sync) { var save = await Save(summary, recipe, token); if (!save.Ok) return save; }
                var tactTicks = (_clock.Elapsed - began).Ticks - (recipe.Save == SaveMode.Async ? Interlocked.Read(ref _asyncSaveTicks) - savedAtBegin : 0); var finalSummary = summary with { Tact = TimeSpan.FromTicks(Math.Max(0, tactTicks)) }; _summaries.Add(finalSummary); WaferCompleted?.Invoke(finalSummary); Publish(true);
            }
            if (channel is not null) { channel.Writer.Complete(); if (consumer is not null) { var saved = await consumer; if (!saved.Ok) return saved; } }
            var avg = TimeSpan.FromTicks((long)_summaries.Average(x => x.Tact.Ticks)); var run = new RunRecord(_runId, _clock.GetUtcNow(), recipe.Name, JsonSerializer.Serialize(recipe), recipe.Seed, waferCount, avg, avg.Ticks > 0 ? 3600 / avg.TotalSeconds : null, BuildSnapshot().Stats.PhaseTotals, _digest?.Digest(Log.Where(x => x.End.HasValue)) ?? "");
            if (_store is not null) { await _store.SaveRunAsync(run, token); await _store.SaveLogAsync(_runId, Log, token); }
            _completed = true; _runCts = null; _currentWafer = null; RunCompleted?.Invoke(run); Publish(true); return DeviceResult.Success;
        }
        catch (OperationCanceledException) { await SafeState(); _pause = false; Publish(true); return _alarms.Any(x => x.ClearedAt is null) ? LastFailure() : DeviceResult.Fail(AlarmCode.None, "중단됨"); }
        catch (Exception e) { Raise(AlarmCode.CommTimeout, ModuleId.Host, e.Message); await SafeState(); return DeviceResult.Fail(AlarmCode.CommTimeout, e.Message); }
        finally
        {
            if (!_completed) _runCts?.Cancel();
            channel?.Writer.TryComplete();
            if (prepared is not null) { try { await prepared; } catch (OperationCanceledException) { } }
            if (consumer is not null) { try { await consumer; } catch (OperationCanceledException) { } }
            _runCts = null; _runDone.TrySetResult(); Publish(true);
        }
    }
    private DeviceResult LastFailure() { var alarm = _alarms.LastOrDefault(x => x.ClearedAt is null); return alarm is null ? DeviceResult.Fail(AlarmCode.CommTimeout, "시퀀스 실패") : DeviceResult.Fail(alarm.Code, alarm.Message); }
    private readonly List<MeasurementResult> _measurements = [];
    private async Task<bool> Prefetch(string id, int slot, RecipeDto r, CancellationToken ct)
    {
        await _robot.WaitAsync(ct);
        try
        {
            var pick = await Step(id, null, ModuleId.Robot, PhaseKind.PickFromSlot, c => _d.Robot.PickFromSlotAsync(slot, r.RobotMoveTime, c), ct);
            if (!pick.Ok) return false;
            _tracker.Update(id, WaferLocation.RobotArm, WaferStatus.Processing);
            var place = await Step(id, null, ModuleId.Robot, PhaseKind.Align, c => _d.Robot.PlaceOnAlignerAsync(r.RobotMoveTime, c), ct);
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
        await _chuck.WaitAsync(ct); _chuckHeld = true;
        await _robot.WaitAsync(ct);
        try
        {
            var pick = await Step(id, null, ModuleId.Robot, PhaseKind.Align, c => _d.Robot.PickFromAlignerAsync(r.RobotMoveTime, c), ct);
            if (!pick.Ok) return false;
            var access = Interlock.CheckRobotAccess();
            if (!access.Ok) { Raise(access.Code, ModuleId.Robot, access.Message); await SafeState(); return false; }
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
        var vac = await _d.Io.WriteAsync(IoPoints.VacChuckOn, true, ct); if (!vac.Ok) { Raise(vac.Code, ModuleId.Vacuum, vac.Message); await SafeState(); return false; } _vacuumOn = true;
        for (int n = 0; n < r.VacuumCheckCount; n++) { var v = await Step(id, null, ModuleId.Vacuum, PhaseKind.VacuumCheck, async c => { await Task.Delay(r.VacuumCheckWait, _clock, c); var sense = await _d.Io.ReadAsync(IoPoints.VacChuckSense, c); _vacuumConfirmed = sense.Ok && sense.Value; return _vacuumConfirmed ? DeviceResult.Success : DeviceResult.Fail(AlarmCode.VacuumNotConfirmed, "척 진공 미확인"); }, ct, true, "진공 안정"); if (!v.Ok) return false; }
        var light = await Step(id, null, ModuleId.Vision, PhaseKind.Illumination, async c => { var x = await _d.Io.WriteAsync(IoPoints.LampOn, true, c); if (!x.Ok) return x; _lampOn = true; await Task.Delay(r.IlluminationWait, _clock, c); return DeviceResult.Success; }, ct, true, "조명 안정"); return light.Ok;
    }
    private async Task<bool> MeasureWafer(string id, RecipeDto r, CancellationToken ct)
    {
        for (int i = 0; i < _points.Count; i++)
        {
            _currentSite = i; var point = _points[i]; var check = Interlock.CheckMoveXY(); if (!check.Ok) { Raise(check.Code, ModuleId.Stage, check.Message); await SafeState(); return false; }
            if (!(await Step(id, i, ModuleId.Stage, PhaseKind.MoveXY, async c => { var x = await _d.StageX.MoveAbsoluteAsync(point.XUm, r.TravelSpeedMmPerS, r.AccelMmPerS2, TimeSpan.FromSeconds(30), c); return x.Ok ? await _d.StageY.MoveAbsoluteAsync(point.YUm, r.TravelSpeedMmPerS, r.AccelMmPerS2, TimeSpan.FromSeconds(30), c) : x; }, ct)).Ok) return false;
            if (r.MapInspect == MapInspectMode.AllSites || i == 0)
            {
                if (!(await Step(id, i, ModuleId.Vision, PhaseKind.AutoFocus, c => _d.Vision.AutoFocusAsync(r.AutoFocusWait, TimeSpan.FromSeconds(10), c), ct, true, "초점 안정")).Ok) return false;
                if (!(await Step(id, i, ModuleId.Vision, PhaseKind.MapInspect, async c => { var x = await _d.Vision.InspectAsync(r.MapInspectTime, c); return x.Ok ? DeviceResult.Success : DeviceResult.Fail(x.Code, x.Message); }, ct)).Ok) return false;
            }
            var contact = false;
            for (int retry = 0; retry <= r.ContactMaxRetry; retry++)
            {
                check = Interlock.CheckDescendZ(); if (!check.Ok) { Raise(check.Code, ModuleId.ZHead, check.Message); await SafeState(); return false; }
                if (!(await Step(id, i, ModuleId.ZHead, PhaseKind.DescendZ, async c => { if (r.ZMode == ZApproachMode.Slow) return await _d.ZAxis.MoveAbsoluteAsync(0, r.ZApproachSpeedMmPerS, r.AccelMmPerS2, TimeSpan.FromSeconds(30), c); while (_d.ZAxis.PositionUm > 0) { var x = await _d.ZAxis.MoveRelativeAsync(-Math.Min(r.ZStepUm, _d.ZAxis.PositionUm), r.ZApproachSpeedMmPerS, r.AccelMmPerS2, TimeSpan.FromSeconds(30), c); if (!x.Ok) return x; var sense = await _d.Io.ReadAsync(IoPoints.ProbeContact, c); if (sense.Ok && sense.Value) break; } return DeviceResult.Success; }, ct, note: retry > 0 ? $"재시도 {retry}회" : "")).Ok) return false;
                var inject = _random.NextDouble() < r.ContactRetryProbability;
                var detection = await Step(id, i, ModuleId.ZHead, PhaseKind.ContactDetect, c => _wait.WaitForAsync(async x => { var s = await _d.Io.ReadAsync(IoPoints.ProbeContact, x); return !inject && s.Ok && s.Value; }, TimeSpan.FromMilliseconds(10), r.ContactDetectTimeout, AlarmCode.ContactTimeout, "프로브 접촉", c), ct, note: retry > 0 ? $"재시도 {retry}회" : "", retryable: true);
                if (detection.Ok) { contact = true; break; }
                if (detection.Code != AlarmCode.ContactTimeout) { Raise(detection.Code, ModuleId.ZHead, detection.Message); await SafeState(); return false; }
                if (retry == r.ContactMaxRetry) { Raise(AlarmCode.ContactRetryExceeded, ModuleId.ZHead, "접촉 재시도 초과"); await SafeState(); return false; }
                var lift = await _d.ZAxis.MoveAbsoluteAsync(50, r.ZApproachSpeedMmPerS, r.AccelMmPerS2, TimeSpan.FromSeconds(30), ct); if (!lift.Ok) return false;
            }
            if (!contact) return false;
            var volts = new List<double>(); var configured = await _d.Smu.ConfigureAsync(r.SourceCurrentA, ct); if (!configured.Ok) { Raise(configured.Code, ModuleId.Smu, configured.Message); await SafeState(); return false; } var output = await _d.Smu.OutputAsync(true, ct); if (!output.Ok) { Raise(output.Code, ModuleId.Smu, output.Message); await SafeState(); return false; }
            for (int reading = 0; reading < r.ReadingsPerSite; reading++) { var v = await Step(id, i, ModuleId.Smu, PhaseKind.Measure, async c => { var x = await _d.Smu.MeasureVoltageAsync(r.ReadingTime, c); if (x.Ok) volts.Add(x.Value); return x.Ok ? DeviceResult.Success : DeviceResult.Fail(x.Code, x.Message); }, ct, note: $"읽기 {reading + 1}"); if (!v.Ok) return false; }
            await _d.Smu.OutputAsync(false, ct); var result = _rs.Build(id, i, point.XUm, point.YUm, volts, r.SourceCurrentA, r.WaferDiameterMm, _clock.Elapsed); _measurements.Add(result); MeasurementCompleted?.Invoke(result); _tracker.Update(id, sitesDone: i + 1);
            if (!(await Step(id, i, ModuleId.ZHead, PhaseKind.LiftZ, c => _d.ZAxis.MoveAbsoluteAsync(r.ZLiftAfterMeasureUm, r.TravelSpeedMmPerS, r.AccelMmPerS2, TimeSpan.FromSeconds(30), c), ct)).Ok) return false;
        }
        _currentSite = null; return true;
    }
    private async Task<bool> Unload(string id, int slot, RecipeDto r, CancellationToken ct)
    {
        if (!(await Step(id, null, ModuleId.ZHead, PhaseKind.LiftZ, c => _d.ZAxis.MoveAbsoluteAsync(r.ZSafeHeightUm, r.TravelSpeedMmPerS, r.AccelMmPerS2, TimeSpan.FromSeconds(30), c), ct)).Ok) return false;
        var moveCheck = Interlock.CheckMoveXY(); if (!moveCheck.Ok) { Raise(moveCheck.Code, ModuleId.Stage, moveCheck.Message); await SafeState(); return false; }
        if (!(await Step(id, null, ModuleId.Stage, PhaseKind.MoveXY, async c => { var x = await _d.StageX.MoveAbsoluteAsync(0, r.TravelSpeedMmPerS, r.AccelMmPerS2, TimeSpan.FromSeconds(30), c); return x.Ok ? await _d.StageY.MoveAbsoluteAsync(0, r.TravelSpeedMmPerS, r.AccelMmPerS2, TimeSpan.FromSeconds(30), c) : x; }, ct)).Ok) return false;
        var check = Interlock.CheckVacuumOff(); if (!check.Ok) { Raise(check.Code, ModuleId.Vacuum, check.Message); await SafeState(); return false; } var off = await _d.Io.WriteAsync(IoPoints.VacChuckOn, false, ct); if (!off.Ok) return false; _vacuumOn = false; _vacuumConfirmed = false;
        await _robot.WaitAsync(ct); try { if (!(await Step(id, null, ModuleId.Robot, PhaseKind.PickFromChuck, c => _d.Robot.PickFromChuckAsync(r.RobotMoveTime, c), ct)).Ok) return false; _tracker.Update(id, WaferLocation.RobotArm); if (!(await Step(id, null, ModuleId.Robot, PhaseKind.ReturnToSlot, c => _d.Robot.ReturnToSlotAsync(slot, r.RobotMoveTime, c), ct)).Ok) return false; _chuckHeld = false; _chuck.Release(); return true; } finally { _robot.Release(); }
    }
    private Task<DeviceResult> Save(WaferSummary summary, RecipeDto r, CancellationToken ct) => Step(summary.WaferId, null, ModuleId.Storage, PhaseKind.Save, async c => { if (r.Save == SaveMode.Async) Interlocked.Add(ref _asyncSaveTicks, r.SaveWriteTime.Ticks); await Task.Delay(r.SaveWriteTime, _clock, c); if (_store is not null) await _store.SaveWaferAsync(_runId, summary, c); _tracker.Update(summary.WaferId, status: WaferStatus.Saved); return DeviceResult.Success; }, ct, true, "결과 기록");
    private async Task<DeviceResult> Consume(ChannelReader<WaferSummary> reader, CancellationToken ct) { await foreach (var item in reader.ReadAllAsync(ct)) { var result = await Save(item, _recipe!, ct); Interlocked.Decrement(ref _queueDepth); if (!result.Ok) { _runCts?.Cancel(); return result; } } return DeviceResult.Success; }
    public void Pause() { if (_runCts is null) return; _pause = true; _resume = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    public void Resume() { _pause = false; _resume.TrySetResult(); Publish(true); }
    public async Task AbortAsync() { if (_runCts is null) return; _aborting = true; _pause = false; _resume.TrySetResult(); _runCts.Cancel(); await _runDone.Task; _aborting = false; _completed = false; Publish(true); }
    public Task<DeviceResult> ResetAsync() { if (_alarms.Any(x => x.ClearedAt is null && x.Severity == AlarmSeverity.Error && !x.Acked)) return Task.FromResult(DeviceResult.Fail(AlarmCode.InterlockBlocked, "확인되지 않은 알람")); foreach (var a in _alarms.Where(x => x.ClearedAt is null).ToArray()) { var cleared = a with { ClearedAt = _clock.Elapsed }; _alarms[_alarms.IndexOf(a)] = cleared; AlarmCleared?.Invoke(cleared); } foreach (var m in _modules) m.Set(ModuleState.Uninitialized, "", _clock.Elapsed); _completed = false; Publish(true); return Task.FromResult(DeviceResult.Success); }
    public void AcknowledgeAlarm(string alarmId) { var i = _alarms.FindIndex(x => x.Id == alarmId && x.ClearedAt is null); if (i >= 0) _alarms[i] = _alarms[i] with { Acked = true }; Publish(true); }
}
