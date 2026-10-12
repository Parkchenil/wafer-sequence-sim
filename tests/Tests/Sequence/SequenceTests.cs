using Contracts;
using Core.Sequence;
using Tests.Sequence.Stubs;

namespace Tests.Sequence;

public sealed class SequenceTests
{
    private static (SequenceEngine Engine, StubDevices Devices, VirtualTimeProvider Clock) Create(StubStore? store = null)
    {
        var clock = new VirtualTimeProvider(); var devices = new StubDevices(clock);
        return (new SequenceEngine(devices, new StubWait(clock), clock, new StubRs(), new StubSites(), store, new StubDigest()), devices, clock);
    }
    private static RecipeDto Recipe => new() { Clock = ClockMode.Instant, Pattern = SitePattern.Standard9, ContactRetryProbability = 0, RobotMoveTime = TimeSpan.FromMilliseconds(10), AlignTime = TimeSpan.FromMilliseconds(10) };
    private static StepEvent[] Finished(SequenceEngine e) => e.Log.Where(x => x.End.HasValue).ToArray();
    private static async Task<SequenceEngine> Run(RecipeDto recipe, int wafers = 2, StubStore? store = null)
    { var (engine, _, _) = Create(store); Assert.True((await engine.InitializeAsync(default)).Ok); var result = await engine.StartAsync(recipe, wafers, default); Assert.True(result.Ok, result.Message); return engine; }

    [Fact(DisplayName = "표준 9점 두 장의 단계 순서와 사이트별 읽기 횟수")]
    public async Task StandardNine()
    {
        var e = await Run(Recipe); var log = Finished(e);
        Assert.Equal(EquipmentState.Completed, e.Snapshot.State);
        Assert.Equal(2, e.Snapshot.Stats.WafersDone);
        Assert.Equal(2 * 9 * Recipe.ReadingsPerSite, log.Count(x => x.Phase == PhaseKind.Measure));
        foreach (var id in new[] { "W001", "W002" })
        {
            var phases = log.Where(x => x.WaferId == id).Select(x => x.Phase).ToArray();
            Assert.True(Array.IndexOf(phases, PhaseKind.PickFromSlot) < Array.IndexOf(phases, PhaseKind.Align));
            Assert.True(Array.IndexOf(phases, PhaseKind.Align) < Array.IndexOf(phases, PhaseKind.PlaceOnChuck));
            Assert.True(Array.IndexOf(phases, PhaseKind.PlaceOnChuck) < Array.IndexOf(phases, PhaseKind.VacuumCheck));
            Assert.True(Array.IndexOf(phases, PhaseKind.VacuumCheck) < Array.IndexOf(phases, PhaseKind.Illumination));
            Assert.True(Array.IndexOf(phases, PhaseKind.Illumination) < Array.IndexOf(phases, PhaseKind.ContactDetect));
            Assert.True(Array.IndexOf(phases, PhaseKind.ContactDetect) < Array.IndexOf(phases, PhaseKind.Measure));
            Assert.True(Array.IndexOf(phases, PhaseKind.Measure) < Array.IndexOf(phases, PhaseKind.PickFromChuck));
            Assert.True(Array.IndexOf(phases, PhaseKind.PickFromChuck) < Array.IndexOf(phases, PhaseKind.ReturnToSlot));
            for (var site = 0; site < 9; site++) Assert.Equal(Recipe.ReadingsPerSite, log.Count(x => x.WaferId == id && x.SiteIndex == site && x.Phase == PhaseKind.Measure));
        }
    }

    [Fact(DisplayName = "같은 시드는 단계와 시간을 재현하고 다른 시드는 재시도 경로를 바꿈")]
    public async Task Deterministic()
    {
        async Task<string[]> Signature(int seed)
        { var (e, _, _) = Create(); await e.InitializeAsync(default); await e.StartAsync(Recipe with { Pattern = SitePattern.Standard9, ContactRetryProbability = 0.5, Seed = seed }, 1, default); return Finished(e).Select(x => $"{x.Phase}:{x.Start.Ticks}:{x.End!.Value.Ticks}").ToArray(); }
        var a = await Signature(12345); var again = await Signature(12345); Assert.Equal(a, again);
        var different = false; for (var seed = 1; seed <= 20; seed++) { var other = await Signature(seed); if (!a.SequenceEqual(other)) { different = true; break; } }
        Assert.True(different);
    }

    [Fact(DisplayName = "비동기 저장의 평균 택트가 동기 저장보다 짧음")]
    public async Task AsyncTact()
    {
        var r = Recipe with { Pattern = SitePattern.Center1, SaveWriteTime = TimeSpan.FromSeconds(3) };
        var sync = await Run(r with { Save = SaveMode.Sync }, 3);
        var asyncRun = await Run(r with { Save = SaveMode.Async }, 3);
        Assert.True(asyncRun.Snapshot.Stats.AverageTact < sync.Snapshot.Stats.AverageTact, $"동기 {sync.Snapshot.Stats.AverageTact}, 비동기 {asyncRun.Snapshot.Stats.AverageTact}");
    }

    [Fact(DisplayName = "저장 큐가 가득 차면 대기하고 웨이퍼를 버리지 않음")]
    public async Task QueueBackpressure()
    {
        var store = new StubStore { HoldWrites = true }; var (e, _, _) = Create(store); await e.InitializeAsync(default);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        e.StepLogged += x => { if (x.End is null && x.Phase == PhaseKind.Wait && x.WaitReason == "저장 큐 가득") waiting.TrySetResult(); };
        var run = e.StartAsync(Recipe with { Pattern = SitePattern.Center1, Save = SaveMode.Async, SaveQueueCapacity = 1, SaveWriteTime = TimeSpan.FromSeconds(3) }, 4, default);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(e.Log, x => x.End is null && x.Phase == PhaseKind.Wait && x.WaitReason == "저장 큐 가득");
        store.Release(); Assert.True((await run).Ok);
        Assert.Equal(4, store.Saved.Count); Assert.Equal(4, e.Snapshot.Stats.WafersDone);
    }

    [Fact(DisplayName = "중첩 반송은 측정 중 다음 웨이퍼를 정렬하고 총 시간을 줄임")]
    public async Task Overlap()
    {
        var r = Recipe with { Pattern = SitePattern.Standard9, Save = SaveMode.Async };
        var serial = await Run(r with { UnloadOverlap = false }, 3);
        var overlap = await Run(r with { UnloadOverlap = true }, 3);
        var log = Finished(overlap); var firstLastMeasure = log.Where(x => x.WaferId == "W001" && x.Phase == PhaseKind.Measure).Max(x => x.End!.Value);
        Assert.Contains(log, x => x.WaferId == "W002" && x.Phase == PhaseKind.PickFromSlot && x.Start < firstLastMeasure);
        var firstSave = log.First(x => x.WaferId == "W001" && x.Phase == PhaseKind.Save);
        var firstUnload = log.First(x => x.WaferId == "W001" && x.Phase == PhaseKind.PickFromChuck);
        var nextPlace = log.First(x => x.WaferId == "W002" && x.Phase == PhaseKind.PlaceOnChuck);
        var firstReturn = log.First(x => x.WaferId == "W001" && x.Phase == PhaseKind.ReturnToSlot);
        Assert.True(firstSave.Start <= firstUnload.End);
        Assert.True(firstReturn.End <= nextPlace.Start);
        Assert.True(overlap.Snapshot.SimTime < serial.Snapshot.SimTime, $"직렬 {serial.Snapshot.SimTime}, 중첩 {overlap.Snapshot.SimTime}");
    }

    [Fact(DisplayName = "접촉 실패 뒤 안전 상태와 알람 확인 복구")]
    public async Task ContactFailureRecovery()
    {
        var (e, devices, _) = Create(); devices.ContactSensorFail = true; await e.InitializeAsync(default);
        var r = Recipe with { Pattern = SitePattern.Center1, ContactMaxRetry = 2, ContactRetryProbability = 0 };
        var result = await e.StartAsync(r, 1, default);
        Assert.False(result.Ok); Assert.Equal(AlarmCode.ContactRetryExceeded, result.Code);
        Assert.Equal(EquipmentState.Alarm, e.Snapshot.State); Assert.Equal(r.ZSafeHeightUm, devices.Z.PositionUm);
        Assert.Equal(3, Finished(e).Count(x => x.Phase == PhaseKind.ContactDetect));
        Assert.Contains(Finished(e), x => x.Note == "재시도 2회");
        Assert.False((await e.ResetAsync()).Ok);
        foreach (var alarm in e.Snapshot.ActiveAlarms) e.AcknowledgeAlarm(alarm.Id);
        Assert.True((await e.ResetAsync()).Ok); Assert.Equal(EquipmentState.Uninitialized, e.Snapshot.State);
        Assert.True((await e.InitializeAsync(default)).Ok); Assert.Equal(EquipmentState.Idle, e.Snapshot.State);
    }

    [Fact(DisplayName = "진공 미확인 XY 명령은 인터락 알람을 냄")]
    public async Task Interlock()
    {
        var (e, _, _) = Create(); await e.InitializeAsync(default);
        var result = await e.StartAsync(Recipe with { Pattern = SitePattern.Center1, VacuumCheckCount = 0 }, 1, default);
        Assert.False(result.Ok); Assert.Equal(AlarmCode.InterlockBlocked, result.Code);
        Assert.Contains(e.Snapshot.ActiveAlarms, x => x.Code == AlarmCode.InterlockBlocked);
    }

    [Fact(DisplayName = "일시정지는 단계 경계에서 멈추고 재개되며 중단 뒤 Idle")]
    public async Task PauseResumeAbort()
    {
        var (e, devices, clock) = Create(); await e.InitializeAsync(default);
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = false;
        e.StepLogged += x => { if (!armed && x.End is null && x.Phase == PhaseKind.Measure) { armed = true; e.Pause(); } };
        e.SnapshotChanged += x => { if (x.State == EquipmentState.Paused) paused.TrySetResult(); };
        var run = e.StartAsync(Recipe with { Pattern = SitePattern.Standard9 }, 2, default);
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var count = e.Log.Count(x => x.End is null);
        await Task.Delay(TimeSpan.FromMilliseconds(50), clock);
        Assert.Equal(count, e.Log.Count(x => x.End is null));
        e.Resume(); Assert.True((await run).Ok);
        armed = false; paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = e.StartAsync(Recipe with { Pattern = SitePattern.Standard9 }, 3, default);
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await e.AbortAsync(); var stopped = await second;
        Assert.False(stopped.Ok); Assert.Equal(EquipmentState.Idle, e.Snapshot.State); Assert.Equal(Recipe.ZSafeHeightUm, devices.Z.PositionUm);
    }
}
