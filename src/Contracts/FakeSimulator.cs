namespace Contracts;

/// <summary>
/// UI 개발용 가짜 시뮬레이터. 진짜 엔진(Core.Sequence)이 오기 전에 화면을 만들 수 있게
/// 레시피와 무관하게 정해진 각본으로 단계를 밟고 스냅샷·로그·측정값을 발행한다.
/// 통합 시점에 DI 한 줄만 바꿔 진짜 ISimulator로 교체한다. UI 프로젝트는 이 클래스를 직접 참조하지 않는 편이 좋다(인터페이스만).
/// </summary>
public sealed class FakeSimulator : ISimulator
{
    private readonly VirtualTimeProvider _clock = new();
    private readonly List<StepEvent> _log = new();
    private readonly List<Alarm> _alarms = new();
    private readonly List<WaferTrack> _wafers = new();
    private readonly Dictionary<PhaseKind, TimeSpan> _phaseTotals = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private volatile bool _paused;
    private long _seq;
    private EquipmentState _state = EquipmentState.Uninitialized;
    private MotionSnapshot _motion = new(0, 0, 300, false, false, false, false, false, false, false, 0);
    private IReadOnlyList<SitePoint> _sites = Array.Empty<SitePoint>();
    private string? _currentWafer;
    private int? _currentSite;
    private int _wafersDone;
    private int _wafersTotal;
    private readonly List<TimeSpan> _tacts = new();
    private int _queueDepth;

    public EquipmentSnapshot Snapshot { get; private set; }
    public IReadOnlyList<StepEvent> Log { get { lock (_gate) return _log.ToArray(); } }
    public RecipeDto? CurrentRecipe { get; private set; }

    public event Action<EquipmentSnapshot>? SnapshotChanged;
    public event Action<StepEvent>? StepLogged;
    public event Action<Alarm>? AlarmRaised;
    public event Action<Alarm>? AlarmCleared;
    public event Action<MeasurementResult>? MeasurementCompleted;
    public event Action<WaferSummary>? WaferCompleted;
    public event Action<RunRecord>? RunCompleted;

    public FakeSimulator()
    {
        Snapshot = BuildSnapshot();
    }

    public async Task<DeviceResult> InitializeAsync(CancellationToken ct)
    {
        _state = EquipmentState.Initializing;
        Publish();
        var ev = Begin("-", null, ModuleId.Stage, PhaseKind.Home, false, null, "전 축 홈잉");
        await Task.Delay(TimeSpan.FromSeconds(1.5), _clock, ct);
        End(ev);
        _motion = _motion with { StageXUm = 0, StageYUm = 0, ZUm = 300 };
        _state = EquipmentState.Idle;
        Publish();
        return DeviceResult.Success;
    }

    public async Task<DeviceResult> StartAsync(RecipeDto recipe, int waferCount, CancellationToken ct)
    {
        if (_state != EquipmentState.Idle) return DeviceResult.Fail(AlarmCode.InterlockBlocked, $"Idle에서만 시작할 수 있다(현재 {_state})");
        CurrentRecipe = recipe;
        _clock.Configure(recipe.Clock, recipe.TimeScale);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        _state = EquipmentState.Running;
        _wafersDone = 0; _wafersTotal = waferCount; _tacts.Clear();
        _sites = BuildSites(recipe);
        lock (_gate) { _log.Clear(); _wafers.Clear(); _phaseTotals.Clear(); _seq = 0; }
        for (var i = 1; i <= waferCount; i++) _wafers.Add(new WaferTrack($"W{i:00}", i, WaferLocation.LoadPort, WaferStatus.Pending, 0, _sites.Count));
        var rnd = new Random(recipe.Seed);
        Publish();

        try
        {
            for (var w = 0; w < waferCount; w++)
            {
                var id = $"W{w + 1:00}";
                _currentWafer = id;
                var waferStart = _clock.Elapsed;
                var results = new List<MeasurementResult>();

                await Step(id, null, ModuleId.Robot, PhaseKind.PickFromSlot, recipe.RobotMoveTime, token, m => m with { RobotExtended = true, LoadPortDoorOpen = true });
                SetWafer(id, WaferLocation.RobotArm, WaferStatus.Processing);
                await Step(id, null, ModuleId.Aligner, PhaseKind.Align, recipe.AlignTime, token, m => m with { RobotExtended = false, AlignerAngleDeg = rnd.NextDouble() * 3 - 1.5 });
                SetWafer(id, WaferLocation.Aligner, WaferStatus.Processing);
                await Step(id, null, ModuleId.Robot, PhaseKind.PlaceOnChuck, recipe.RobotMoveTime, token, m => m with { RobotExtended = true });
                SetWafer(id, WaferLocation.Chuck, WaferStatus.Processing);
                _motion = _motion with { RobotExtended = false, VacuumOn = true };
                for (var k = 0; k < recipe.VacuumCheckCount; k++)
                    await Step(id, null, ModuleId.Vacuum, PhaseKind.VacuumCheck, recipe.VacuumCheckWait, token, m => m, isWait: true, waitReason: "진공 확인 고정 대기");
                _motion = _motion with { VacuumConfirmed = true };
                await Step(id, null, ModuleId.Vision, PhaseKind.Illumination, recipe.IlluminationWait, token, m => m with { LampOn = true }, isWait: true, waitReason: "조명 안정");

                for (var s = 0; s < _sites.Count; s++)
                {
                    _currentSite = s;
                    var site = _sites[s];
                    var dist = Math.Sqrt(Math.Pow(site.XUm - _motion.StageXUm, 2) + Math.Pow(site.YUm - _motion.StageYUm, 2)) / 1000.0;
                    var moveTime = TimeSpan.FromSeconds(dist / recipe.TravelSpeedMmPerS + recipe.TravelSpeedMmPerS / recipe.AccelMmPerS2);
                    await Step(id, s, ModuleId.Stage, PhaseKind.MoveXY, moveTime, token, m => m with { StageXUm = site.XUm, StageYUm = site.YUm });
                    if (recipe.MapInspect == MapInspectMode.AllSites || s == 0)
                    {
                        await Step(id, s, ModuleId.Vision, PhaseKind.AutoFocus, recipe.AutoFocusWait, token, m => m with { CameraFocused = true }, isWait: true, waitReason: "오토포커스 대기");
                        await Step(id, s, ModuleId.Vision, PhaseKind.MapInspect, recipe.MapInspectTime, token, m => m);
                    }
                    var zTime = TimeSpan.FromSeconds(_motion.ZUm / 1000.0 / recipe.ZApproachSpeedMmPerS);
                    await Step(id, s, ModuleId.ZHead, PhaseKind.DescendZ, zTime, token, m => m with { ZUm = 0 });
                    var retry = rnd.NextDouble() < recipe.ContactRetryProbability;
                    await Step(id, s, ModuleId.ZHead, PhaseKind.ContactDetect, retry ? TimeSpan.FromSeconds(1.2) : TimeSpan.FromMilliseconds(150), token, m => m with { ProbeInContact = true }, note: retry ? "접촉 감지 지연·재시도 1회" : "");
                    var volts = new List<double>();
                    for (var r = 0; r < recipe.ReadingsPerSite; r++)
                    {
                        await Step(id, s, ModuleId.Smu, PhaseKind.Measure, recipe.ReadingTime, token, m => m);
                        var r0 = Math.Sqrt(site.XUm * site.XUm + site.YUm * site.YUm) / 150000.0;
                        var rs = 50 * (1 + 0.08 * r0 * r0) * (1 + (rnd.NextDouble() - 0.5) * 0.004);
                        volts.Add(rs * recipe.SourceCurrentA / 4.532);
                    }
                    var rsList = volts.Select(v => 4.532 * v / recipe.SourceCurrentA).ToList();
                    var mean = rsList.Average();
                    var sigma = Math.Sqrt(rsList.Sum(x => (x - mean) * (x - mean)) / Math.Max(1, rsList.Count - 1));
                    var mr = new MeasurementResult(id, s, site.XUm, site.YUm, volts, recipe.SourceCurrentA, mean, sigma, _clock.Elapsed);
                    results.Add(mr);
                    MeasurementCompleted?.Invoke(mr);
                    await Step(id, s, ModuleId.ZHead, PhaseKind.LiftZ, TimeSpan.FromSeconds(recipe.ZLiftAfterMeasureUm / 1000.0 / 2.0), token, m => m with { ZUm = recipe.ZLiftAfterMeasureUm, ProbeInContact = false });
                    SetWafer(id, WaferLocation.Chuck, WaferStatus.Processing, s + 1);
                }
                _currentSite = null;
                await Step(id, null, ModuleId.ZHead, PhaseKind.LiftZ, TimeSpan.FromMilliseconds(200), token, m => m with { ZUm = recipe.ZSafeHeightUm });
                await Step(id, null, ModuleId.Stage, PhaseKind.MoveXY, TimeSpan.FromSeconds(0.8), token, m => m with { StageXUm = 0, StageYUm = 0, LampOn = false, CameraFocused = false });
                _motion = _motion with { VacuumOn = false, VacuumConfirmed = false };
                SetWafer(id, WaferLocation.Chuck, WaferStatus.Measured);
                var unload = Step(id, null, ModuleId.Robot, PhaseKind.PickFromChuck, recipe.RobotMoveTime, token, m => m with { RobotExtended = true })
                    .ContinueWith(_ => Step(id, null, ModuleId.Robot, PhaseKind.ReturnToSlot, recipe.RobotMoveTime, token, m => m with { RobotExtended = false }), token).Unwrap();
                var save = recipe.Save == SaveMode.Sync
                    ? Step(id, null, ModuleId.Storage, PhaseKind.Save, recipe.SaveWriteTime * _sites.Count / 4, token, m => m)
                    : Task.Run(async () => { Interlocked.Increment(ref _queueDepth); await Task.Delay(recipe.SaveWriteTime * _sites.Count / 4, _clock, token); Interlocked.Decrement(ref _queueDepth); }, token);
                if (recipe.UnloadOverlap) await Task.WhenAll(unload, save); else { await unload; await save; }
                SetWafer(id, WaferLocation.Done, WaferStatus.Saved);
                var tact = _clock.Elapsed - waferStart;
                _tacts.Add(tact);
                _wafersDone++;
                var rsAll = results.Select(r => r.RsOhmPerSq).ToList();
                var m2 = rsAll.Average();
                var s2 = Math.Sqrt(rsAll.Sum(x => (x - m2) * (x - m2)) / Math.Max(1, rsAll.Count - 1));
                var summary = new WaferSummary(id, results.Count, m2, s2, rsAll.Min(), rsAll.Max(), s2 / m2 * 100, (rsAll.Max() - rsAll.Min()) / (2 * m2) * 100, results.Average(r => r.RsSigma) / m2 * 100, tact, results);
                WaferCompleted?.Invoke(summary);
                Publish();
            }
            _state = EquipmentState.Completed;
            var run = new RunRecord(Guid.NewGuid().ToString("N")[..8], DateTimeOffset.Now, recipe.Name, "{}", recipe.Seed, waferCount, _tacts.Count > 0 ? TimeSpan.FromTicks((long)_tacts.Average(t => t.Ticks)) : null, _tacts.Count > 0 ? 3600.0 / _tacts.Average(t => t.TotalSeconds) : null, new Dictionary<PhaseKind, TimeSpan>(_phaseTotals), "fake");
            RunCompleted?.Invoke(run);
            _state = EquipmentState.Idle;
            Publish();
            return DeviceResult.Success;
        }
        catch (OperationCanceledException)
        {
            _state = EquipmentState.Idle;
            _motion = _motion with { ZUm = 300, ProbeInContact = false };
            Publish();
            return DeviceResult.Fail(AlarmCode.None, "중단됨");
        }
    }

    public void Pause() { if (_state == EquipmentState.Running) { _paused = true; _state = EquipmentState.Paused; Publish(); } }
    public void Resume() { if (_state == EquipmentState.Paused) { _paused = false; _state = EquipmentState.Running; Publish(); } }
    public async Task AbortAsync() { _state = EquipmentState.Aborting; Publish(); _cts?.Cancel(); await Task.Yield(); }
    public Task<DeviceResult> ResetAsync()
    {
        if (_alarms.Any(a => a.ClearedAt is null && a.Severity == AlarmSeverity.Error && !a.Acked))
            return Task.FromResult(DeviceResult.Fail(AlarmCode.InterlockBlocked, "Ack되지 않은 Error 알람이 있다"));
        _alarms.Clear(); _state = EquipmentState.Uninitialized; Publish();
        return Task.FromResult(DeviceResult.Success);
    }
    public void AcknowledgeAlarm(string alarmId)
    {
        var i = _alarms.FindIndex(a => a.Id == alarmId);
        if (i >= 0) _alarms[i] = _alarms[i] with { Acked = true };
        Publish();
    }

    // ───── 내부 ─────

    private async Task Step(string wafer, int? site, ModuleId module, PhaseKind phase, TimeSpan duration, CancellationToken ct, Func<MotionSnapshot, MotionSnapshot> after, bool isWait = false, string? waitReason = null, string note = "")
    {
        while (_paused) await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System, ct);
        var ev = Begin(wafer, site, module, phase, isWait, waitReason, note);
        if (duration > TimeSpan.Zero) await Task.Delay(duration, _clock, ct);
        _motion = after(_motion);
        End(ev);
    }

    private StepEvent Begin(string wafer, int? site, ModuleId module, PhaseKind phase, bool isWait, string? waitReason, string note)
    {
        StepEvent ev;
        lock (_gate)
        {
            ev = new StepEvent(++_seq, wafer, site, module, phase, _clock.Elapsed, null, isWait, waitReason, note);
            _log.Add(ev);
        }
        StepLogged?.Invoke(ev);
        Publish();
        return ev;
    }

    private void End(StepEvent ev)
    {
        StepEvent done;
        lock (_gate)
        {
            done = ev with { End = _clock.Elapsed };
            var i = _log.FindIndex(e => e.Seq == ev.Seq);
            if (i >= 0) _log[i] = done;
            _phaseTotals[ev.Phase] = _phaseTotals.GetValueOrDefault(ev.Phase) + (done.Duration ?? TimeSpan.Zero);
        }
        StepLogged?.Invoke(done);
        Publish();
    }

    private void SetWafer(string id, WaferLocation loc, WaferStatus status, int? sitesDone = null)
    {
        var i = _wafers.FindIndex(w => w.WaferId == id);
        if (i >= 0) _wafers[i] = _wafers[i] with { Location = loc, Status = status, SitesDone = sitesDone ?? _wafers[i].SitesDone };
    }

    private long _lastPublishTicks;
    private void Publish()
    {
        // 30 Hz 스로틀(벽시계 기준)
        var now = Environment.TickCount64;
        if (now - _lastPublishTicks < 33 && _state is EquipmentState.Running) return;
        _lastPublishTicks = now;
        Snapshot = BuildSnapshot();
        SnapshotChanged?.Invoke(Snapshot);
    }

    private EquipmentSnapshot BuildSnapshot()
    {
        IReadOnlyDictionary<PhaseKind, TimeSpan> totals;
        lock (_gate) totals = new Dictionary<PhaseKind, TimeSpan>(_phaseTotals);
        var total = totals.Values.Aggregate(TimeSpan.Zero, (a, b) => a + b);
        TimeSpan waitTotal;
        lock (_gate) waitTotal = _log.Where(e => e.IsWait && e.Duration is { }).Aggregate(TimeSpan.Zero, (a, e) => a + e.Duration!.Value);
        var modules = Enum.GetValues<ModuleId>().Select(m => new ModuleStatus(m, _state == EquipmentState.Running ? ModuleState.Busy : ModuleState.Idle, "", TimeSpan.Zero)).ToList();
        var stats = new RunStats(_wafersDone, _wafersTotal, _clock.Elapsed,
            _tacts.Count > 0 ? TimeSpan.FromTicks((long)_tacts.Average(t => t.Ticks)) : null,
            _tacts.Count > 0 ? 3600.0 / _tacts.Average(t => t.TotalSeconds) : null,
            totals, total > TimeSpan.Zero ? waitTotal / total : 0, _queueDepth, CurrentRecipe?.SaveQueueCapacity ?? 0);
        return new EquipmentSnapshot(_clock.Elapsed, _state, modules, _wafers.ToArray(), _motion, stats, _alarms.Where(a => a.ClearedAt is null).ToArray(), _currentWafer, _currentSite, _sites);
    }

    private static IReadOnlyList<SitePoint> BuildSites(RecipeDto r)
    {
        var radius = (r.WaferDiameterMm / 2 - r.EdgeExclusionMm) * 1000;
        return r.Pattern switch
        {
            SitePattern.Center1 => new[] { new SitePoint(0, 0) },
            SitePattern.Standard5 => new[] { new SitePoint(0, 0), new(radius * 0.7, 0), new(-radius * 0.7, 0), new(0, radius * 0.7), new(0, -radius * 0.7) },
            SitePattern.Standard9 => new[] { new SitePoint(0, 0), new(radius * 0.5, 0), new(-radius * 0.5, 0), new(0, radius * 0.5), new(0, -radius * 0.5), new(radius * 0.9, 0), new(-radius * 0.9, 0), new(0, radius * 0.9), new(0, -radius * 0.9) },
            SitePattern.Custom => r.CustomSites,
            _ => Enumerable.Range(0, 49).Select(i => { var ring = i == 0 ? 0 : (i - 1) / 8 + 1; var ang = (i - 1) % 8 * Math.PI / 4; var rr = radius * ring / 6.0; return new SitePoint(rr * Math.Cos(ang), rr * Math.Sin(ang)); }).ToList(),
        };
    }
}
