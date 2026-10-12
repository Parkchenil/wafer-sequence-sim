using Contracts;

namespace Tests.Sequence.Stubs;

public sealed class StubAxis(string name, VirtualTimeProvider clock) : IAxis
{
    public string Name => name; public double PositionUm { get; private set; } = 300;
    public AlarmCode FailNextMove { get; set; }
    public int FailAtMoveNumber { get; set; }
    public int MoveCount { get; private set; }
    public bool ThrowOnStatus { get; set; }
    public void SetPosition(double positionUm) => PositionUm = positionUm;
    public Task<DeviceResult> ServoOnAsync(CancellationToken ct) => Task.FromResult(DeviceResult.Success);
    public Task<DeviceResult> HomeAsync(TimeSpan timeout, CancellationToken ct) { PositionUm = name == "Z" ? 300 : 0; return Task.FromResult(DeviceResult.Success); }
    public async Task<DeviceResult> MoveAbsoluteAsync(double targetUm, double speedMmPerS, double accelMmPerS2, TimeSpan timeout, CancellationToken ct) { MoveCount++; if (FailNextMove != AlarmCode.None && (FailAtMoveNumber == 0 || MoveCount == FailAtMoveNumber)) { var code = FailNextMove; FailNextMove = AlarmCode.None; return DeviceResult.Fail(code, "가상 축 이동 실패"); } await Task.Delay(TimeSpan.FromMilliseconds(10), clock, ct); PositionUm = targetUm; return DeviceResult.Success; }
    public Task<DeviceResult> MoveRelativeAsync(double deltaUm, double speedMmPerS, double accelMmPerS2, TimeSpan timeout, CancellationToken ct) => MoveAbsoluteAsync(PositionUm + deltaUm, speedMmPerS, accelMmPerS2, timeout, ct);
    public Task<DeviceResult> StopAsync(CancellationToken ct) => Task.FromResult(DeviceResult.Success);
    public Task<DeviceResult<AxisStatus>> GetStatusAsync(CancellationToken ct) => ThrowOnStatus ? throw new InvalidOperationException("동기 상태 조회 금지") : Task.FromResult(DeviceResult<AxisStatus>.Success(new(PositionUm, true, true, true, false, false, false)));
}
public sealed class StubDevices(VirtualTimeProvider clock) : IDeviceSet, IDeviceContext, IDigitalIO, ISourceMeasureUnit, IVision, ILoadPort, IRobot, IAligner, IProbePosition
{
    private readonly Dictionary<string, bool> _io = []; private readonly TaskCompletionSource _measureRelease = new(TaskCreationOptions.RunContinuationsAsynchronously); public readonly TaskCompletionSource MeasureEntered = new(TaskCreationOptions.RunContinuationsAsynchronously); public bool HoldMeasure { get; set; } public void ReleaseMeasure() => _measureRelease.TrySetResult(); public bool ContactSensorFail { get; set; } public bool FailEmoRead { get; set; } public bool EmoActive { get; set; } public bool FailVacuumOff { get; set; } public bool ThrowVacuumOff { get; set; } public bool FailSmuRead { get; set; } public bool FailOutputOff { get; set; } public bool OutputOn { get; private set; } public bool RobotExtendedOverride { get; set; } public int Seed { get; private set; } public string CurrentWafer { get; private set; } = ""; public readonly List<string> ContextCalls = []; public readonly List<string> MeasurementWaferIds = []; public StubAxis X { get; } = new("X", clock); public StubAxis Y { get; } = new("Y", clock); public StubAxis Z { get; } = new("Z", clock);
    public IDigitalIO Io => this; public IAxis StageX => X; public IAxis StageY => Y; public IAxis ZAxis => Z; public ISourceMeasureUnit Smu => this; public IVision Vision => this; public ILoadPort LoadPort => this; public IRobot Robot => this; public IAligner Aligner => this; public IProbePosition Probe => this;
    public IReadOnlyList<string> Points => []; public bool Focused { get; private set; } public bool DoorOpen { get; private set; } public bool HoldingWafer { get; private set; } public bool Extended => RobotExtendedOverride; public double AngleDeg => 0; public double XUm => X.PositionUm; public double YUm => Y.PositionUm; public bool InContact => Z.PositionUm <= 0;
    public void Reseed(int seed) { Seed = seed; lock (ContextCalls) ContextCalls.Add($"seed:{seed}"); }
    public void SetCurrentWafer(string waferId) { CurrentWafer = waferId; lock (ContextCalls) ContextCalls.Add($"wafer:{waferId}"); }
    public Task<DeviceResult<bool>> ReadAsync(string point, CancellationToken ct) => point == IoPoints.Emo && FailEmoRead ? Task.FromResult(DeviceResult<bool>.Fail(AlarmCode.ConfigError, "EMO 입력 읽기 실패")) : Task.FromResult(DeviceResult<bool>.Success(point == IoPoints.Emo ? EmoActive : point == IoPoints.ProbeContact ? InContact && !ContactSensorFail : point == IoPoints.VacChuckSense ? _io.GetValueOrDefault(IoPoints.VacChuckOn) : _io.GetValueOrDefault(point)));
    public Task<DeviceResult> WriteAsync(string point, bool value, CancellationToken ct) { if (point == IoPoints.VacChuckOn && !value && ThrowVacuumOff) throw new IOException("가상 통신 오류"); if (point == IoPoints.VacChuckOn && !value && FailVacuumOff) return Task.FromResult(DeviceResult.Fail(AlarmCode.CommTimeout, "진공 해제 통신 실패")); _io[point] = value; return Task.FromResult(DeviceResult.Success); }
    public Task<DeviceResult> ConfigureAsync(double sourceCurrentA, CancellationToken ct) => Task.FromResult(DeviceResult.Success);
    public async Task<DeviceResult<double>> MeasureVoltageAsync(TimeSpan readingTime, CancellationToken ct) { lock (MeasurementWaferIds) MeasurementWaferIds.Add(CurrentWafer); MeasureEntered.TrySetResult(); if (HoldMeasure) await _measureRelease.Task.WaitAsync(ct); if (FailSmuRead) return DeviceResult<double>.Fail(AlarmCode.SmuTimeout, "측정 실패"); await Task.Delay(readingTime, clock, ct); return DeviceResult<double>.Success(0.01); }
    public Task<DeviceResult> OutputAsync(bool on, CancellationToken ct) { if (!on && FailOutputOff) return Task.FromResult(DeviceResult.Fail(AlarmCode.SmuTimeout, "출력 차단 실패")); OutputOn = on; return Task.FromResult(DeviceResult.Success); }
    public async Task<DeviceResult> AutoFocusAsync(TimeSpan settle, TimeSpan timeout, CancellationToken ct) { await Task.Delay(settle, clock, ct); Focused = true; return DeviceResult.Success; }
    public async Task<DeviceResult<AlignOffset>> InspectAsync(TimeSpan processingTime, CancellationToken ct) { await Task.Delay(processingTime, clock, ct); return DeviceResult<AlignOffset>.Success(new(0, 0, 0, 1)); }
    public Task<DeviceResult> OpenAsync(TimeSpan timeout, CancellationToken ct) { DoorOpen = true; return Task.FromResult(DeviceResult.Success); }
    public Task<DeviceResult> CloseAsync(TimeSpan timeout, CancellationToken ct) { DoorOpen = false; return Task.FromResult(DeviceResult.Success); }
    public Task<DeviceResult<IReadOnlyList<int>>> MapSlotsAsync(TimeSpan mappingTime, CancellationToken ct) => Task.FromResult(DeviceResult<IReadOnlyList<int>>.Success([1, 2, 3, 4]));
    private async Task<DeviceResult> RobotMove(TimeSpan time, bool holding, CancellationToken ct) { await Task.Delay(time, clock, ct); HoldingWafer = holding; return DeviceResult.Success; }
    public Task<DeviceResult> PickFromSlotAsync(int slot, TimeSpan moveTime, CancellationToken ct) => RobotMove(moveTime, true, ct);
    public Task<DeviceResult> PlaceOnAlignerAsync(TimeSpan moveTime, CancellationToken ct) => RobotMove(moveTime, false, ct);
    public Task<DeviceResult> PickFromAlignerAsync(TimeSpan moveTime, CancellationToken ct) => RobotMove(moveTime, true, ct);
    public Task<DeviceResult> PlaceOnChuckAsync(TimeSpan moveTime, CancellationToken ct) => RobotMove(moveTime, false, ct);
    public Task<DeviceResult> PickFromChuckAsync(TimeSpan moveTime, CancellationToken ct) => RobotMove(moveTime, true, ct);
    public Task<DeviceResult> ReturnToSlotAsync(int slot, TimeSpan moveTime, CancellationToken ct) => RobotMove(moveTime, false, ct);
    public async Task<DeviceResult<double>> AlignAsync(TimeSpan alignTime, CancellationToken ct) { await Task.Delay(alignTime, clock, ct); return DeviceResult<double>.Success(0); }
}
public sealed class StubWait(VirtualTimeProvider clock) : IWaitHelper
{
    public async Task<DeviceResult> WaitForAsync(Func<CancellationToken, Task<bool>> condition, TimeSpan pollInterval, TimeSpan timeout, AlarmCode onTimeout, string what, CancellationToken ct) { if (await condition(ct)) return DeviceResult.Success; await Task.Delay(timeout, clock, ct); return DeviceResult.Fail(onTimeout, what); }
}
public sealed class StubSites : ISitePatternProvider
{
    public IReadOnlyList<SitePoint> Build(RecipeDto recipe) => recipe.Pattern == SitePattern.Center1 ? [new(0, 0)] : Enumerable.Range(0, recipe.Pattern == SitePattern.Standard9 ? 9 : 5).Select(i => new SitePoint(i * 100, 0)).ToArray();
}
public sealed class StubRs : IRsCalculator
{
    public double RsFromVI(double voltageV, double currentA, double xUm, double yUm, double waferDiameterMm) => 4.532 * voltageV / currentA;
    public MeasurementResult Build(string waferId, int siteIndex, double xUm, double yUm, IReadOnlyList<double> voltages, double currentA, double waferDiameterMm, TimeSpan at) => new(waferId, siteIndex, xUm, yUm, voltages, currentA, RsFromVI(voltages.Average(), currentA, xUm, yUm, waferDiameterMm), 0, at);
    public WaferSummary Summarize(string waferId, IReadOnlyList<MeasurementResult> sites, TimeSpan tact) => new(waferId, sites.Count, sites.Average(x => x.RsOhmPerSq), 0, sites.Min(x => x.RsOhmPerSq), sites.Max(x => x.RsOhmPerSq), 0, 0, 0, tact, sites.ToArray());
}
public sealed class StubDigest : ILogDigest { public string Digest(IEnumerable<StepEvent> log) => string.Join('|', log.Select(x => x.Phase)); }

public sealed class StubStore : IResultStore
{
    public readonly List<WaferSummary> Saved = [];
    public readonly List<RunRecord> Runs = [];
    public bool HoldWrites { get; set; }
    public bool FailNextWaferSave { get; set; }
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Release() => _release.TrySetResult();
    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
    public async Task SaveWaferAsync(string runId, WaferSummary wafer, CancellationToken ct) { if (FailNextWaferSave) { FailNextWaferSave = false; throw new IOException("저장 쓰기 실패"); } if (HoldWrites) await _release.Task.WaitAsync(ct); lock (Saved) Saved.Add(wafer); }
    public Task SaveRunAsync(RunRecord run, CancellationToken ct) { lock (Runs) Runs.Add(run); return Task.CompletedTask; }
    public Task SaveLogAsync(string runId, IReadOnlyList<StepEvent> log, CancellationToken ct) => Task.CompletedTask;
    public Task<IReadOnlyList<RunRecord>> ListRunsAsync(CancellationToken ct) { lock (Runs) return Task.FromResult<IReadOnlyList<RunRecord>>(Runs.ToArray()); }
    public Task<IReadOnlyList<WaferSummary>> GetWafersAsync(string runId, CancellationToken ct) => Task.FromResult<IReadOnlyList<WaferSummary>>(Saved.ToArray());
    public Task<IReadOnlyList<StepEvent>> GetLogAsync(string runId, CancellationToken ct) => Task.FromResult<IReadOnlyList<StepEvent>>([]);
}
