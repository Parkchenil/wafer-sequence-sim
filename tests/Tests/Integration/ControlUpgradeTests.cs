using Contracts;
using Core.Measurement;
using Core.Sequence;
using Core.Visualization;
using Devices;
namespace Tests.Integration;
public sealed partial class ControlUpgradeTests
{
    private static RecipeDto Recipe => new() { Clock = ClockMode.Instant, Pattern = SitePattern.Standard5, ContactRetryProbability = 0, RobotMoveTime = TimeSpan.FromMilliseconds(100), AlignTime = TimeSpan.FromMilliseconds(100), ReadingTime = TimeSpan.FromMilliseconds(10), ProbeSettleTime = TimeSpan.FromMilliseconds(10), QualityEnabled = true };
    private static (SequenceEngine Engine, DeviceBundle Devices) Create(IResultStore? store = null)
    {
        var clock = new VirtualTimeProvider(); clock.Configure(ClockMode.Instant, 1);
        var devices = DeviceSetFactory.CreateInMemory(clock, new ResistanceField(12345), 12345);
        return (new(devices, new WaitHelper(clock), clock, new RsCalculator(), new SitePatternProvider(), store, new LogDigest()), devices);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task PositiveTransportFullLot(bool overlap)
    {
        var (engine, devices) = Create(); await using var dispose = devices;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Assert.True((await engine.InitializeAsync(stop.Token)).Ok);
        var result = await engine.StartAsync(Recipe with { UnloadOverlap = overlap }, 25, stop.Token);
        Assert.True(result.Ok, result.Message); Assert.Equal(25, engine.Snapshot.Stats.WafersDone);
        Assert.All(engine.Snapshot.Wafers, w => Assert.Equal(WaferLocation.Done, w.Location));
        Assert.Equal(500, engine.Log.Count(e => e.Phase == PhaseKind.Measure && e.End.HasValue));
    }
    [Theory][InlineData(true)][InlineData(false)]
    public async Task ContactAndQualityFailureAreDistinct(bool contact)
    {
        var (engine, devices) = Create(); await using var dispose = devices;
        await engine.InitializeAsync(default);
        var result = await engine.StartAsync(Recipe with { Pattern = SitePattern.Center1, ContactRetryProbability = contact ? 1 : 0, ContactMaxRetry = 1, ContactDetectTimeout = TimeSpan.FromMilliseconds(100), QualityTolerancePct = 0, QualityMaxRetry = 1 }, 1, default);
        Assert.Equal(contact ? AlarmCode.ContactRetryExceeded : AlarmCode.QualityRejected, result.Code);
        Assert.Equal(0, engine.Snapshot.Stats.WafersDone);
        Assert.Equal(WaferStatus.Failed, engine.Snapshot.Wafers[0].Status);
        Assert.Equal(contact ? 2 : 1, engine.Log.Count(e => e.Phase == PhaseKind.DescendZ && e.End.HasValue));
        Assert.Equal(contact ? 0 : 8, engine.Log.Count(e => e.Phase == PhaseKind.Measure && e.End.HasValue));
    }
    [Fact]
    public async Task SensorTimeoutUsesVirtualTimeAndPreservesTruth()
    {
        var (engine, devices) = Create(); await using var dispose = devices;
        await engine.InitializeAsync(default); engine.SetSensorDelay(true);
        ProbeSnapshot? sensed = null;
        engine.StepLogged += e => { if (e.Phase == PhaseKind.ContactDetect && e.End.HasValue) sensed = engine.Snapshot.ProbeDetail; };
        var result = await engine.StartAsync(Recipe with { ContactMaxRetry = 0, ContactDetectTimeout = TimeSpan.FromMilliseconds(100) }, 1, default);
        Assert.Equal(AlarmCode.ContactRetryExceeded, result.Code);
        Assert.NotNull(sensed); Assert.True(sensed.TrueContact); Assert.False(sensed.ObservedContact);
        Assert.Contains(engine.Log, e => e.Phase == PhaseKind.ContactDetect && e.Duration >= TimeSpan.FromMilliseconds(100));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task CommunicationBlocksNextMotionAndCanRecoverOrCancel(bool cancel)
    {
        var (engine, devices) = Create(); await using var dispose = devices;
        await engine.InitializeAsync(default);
        var injected = false;
        engine.StepLogged += e => { if (!injected && e.Phase == PhaseKind.Measure && e.End.HasValue) { injected = true; engine.SetCommunicationFailure(true); } };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run = engine.StartAsync(Recipe with { Clock = ClockMode.Realtime, TimeScale = 20, Pattern = SitePattern.Center1 }, 1, stop.Token);
        while (!injected) await Task.Delay(10, stop.Token);
        await Task.Delay(80, stop.Token);
        var motions = engine.Log.Count(e => e.Phase == PhaseKind.LiftZ);
        Assert.True(engine.Snapshot.Faults!.CommunicationLost);
        await Task.Delay(80, stop.Token); Assert.Equal(motions, engine.Log.Count(e => e.Phase == PhaseKind.LiftZ));
        if (cancel) { await engine.AbortAsync(); Assert.False((await run).Ok); Assert.NotEqual(WaferLocation.Done, engine.Snapshot.Wafers[0].Location); }
        else { engine.SetCommunicationFailure(false); var result = await run; Assert.True(result.Ok, result.Message); Assert.Contains("확인 완료", engine.Snapshot.Faults!.Recovery); }
    }
    [Fact]
    public async Task DurableDatabaseFailureContinuesAndRecoversWithoutDuplicates()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wafer-control-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        using var sqlite = new SqliteResultStore(Path.Combine(dir, "runs.db"));
        var store = new RecoverableResultStore(sqlite, Path.Combine(dir, "recovery.jsonl")) { InjectWriteFailure = true };
        var (engine, devices) = Create(store); await using var dispose = devices;
        await engine.InitializeAsync(default);
        var result = await engine.StartAsync(Recipe with { Pattern = SitePattern.Center1, Save = SaveMode.Async, SaveQueueCapacity = 1 }, 3, default);
        Assert.True(result.Ok, result.Message); Assert.Equal(3, engine.Snapshot.Stats.WafersDone);
        Assert.Equal(5, store.PendingCount); Assert.True(new FileInfo(store.JournalPath).Length > 0); Assert.Empty(await sqlite.ListRunsAsync(default));
        // 프로세스 재시작을 흉내 내어 영속 저널에서 다시 읽는다.
        var recovery = new RecoverableResultStore(sqlite, store.JournalPath);
        await recovery.RecoverAsync(default); await recovery.RecoverAsync(default);
        Assert.Equal(0, recovery.PendingCount);
        var run = Assert.Single(await sqlite.ListRunsAsync(default));
        var wafers = await sqlite.GetWafersAsync(run.RunId, default); Assert.Equal(3, wafers.Count); Assert.All(wafers, w => Assert.Single(w.Sites));
        Assert.NotEmpty(await sqlite.GetLogAsync(run.RunId, default));
    }
    [Fact]
    public async Task ExperimentUsesIndependentEnginesAndSameWorkload()
    {
        var experiment = new RecipeExperiment(); experiment.Capture(Recipe with { Pattern = SitePattern.Center1 }, 2);
        var (a,b) = await experiment.CompareAsync(Recipe with { Pattern = SitePattern.Standard49, Seed = 99, ProbeSettleTime = TimeSpan.FromSeconds(1) }, default);
        Assert.True(a.Completed, a.Outcome); Assert.True(b.Completed, b.Outcome); Assert.Equal(2,a.Good); Assert.Equal(2,b.Good);
        Assert.True(b.WaitSeconds > a.WaitSeconds); Assert.Equal(a.MeasureSeconds,b.MeasureSeconds,6); Assert.Equal(2*3600/a.Seconds,a.Wph!.Value,6);
    }
    [Fact]
    public void QualityInputsAreValidated()
    {
        Assert.NotNull(ControlValidation.Validate(Recipe with { QualityTolerancePct = double.NaN }));
        Assert.NotNull(ControlValidation.Validate(Recipe with { ReadingsPerSite = 2 }));
        Assert.NotNull(ControlValidation.Validate(Recipe with { QualityMaxRetry = 99 }));
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task SameSeedProducesSameIndependentResult(bool overlap)
    {
        var experiment = new RecipeExperiment(); experiment.Capture(Recipe with { Pattern = SitePattern.Center1, UnloadOverlap = overlap }, 2);
        var (a,b) = await experiment.CompareAsync(Recipe with { Pattern = SitePattern.Center1, UnloadOverlap = overlap }, default);
        if(a.Digest != b.Digest) File.WriteAllText(Path.Combine(Path.GetTempPath(),"wafer-comparison-difference.json"),System.Text.Json.JsonSerializer.Serialize(new { a,b }));
        Assert.Equal(a.Seconds,b.Seconds,6); Assert.Equal(a.Wph,b.Wph); Assert.Equal(a.Digest,b.Digest);
    }
    [Theory][InlineData("Z")][InlineData("Vacuum")][InlineData("Robot")][InlineData("Emo")]
    public void IndividualInterlockConditionsBlock(string fault)
    {
        var devices = new Tests.Sequence.Stubs.StubDevices(new VirtualTimeProvider());
        if (fault == "Z") devices.Z.SetPosition(0);
        if (fault == "Robot") devices.RobotExtendedOverride = true;
        var gate = new Interlock(devices, () => fault != "Vacuum", () => 300, () => fault == "Emo" ? DeviceResult.Fail(AlarmCode.Emo, "비상 정지") : DeviceResult.Success, () => false);
        Assert.False(gate.CheckMoveXY().Ok);
        if (fault == "Robot") Assert.False(gate.CheckDescendZ().Ok);
    }
    [Fact]
    public async Task ReplayKeepsControlSnapshots()
    {
        var (engine, devices) = Create(); await using var dispose = devices; await engine.InitializeAsync(default);
        var replay = new RunReplay();
        engine.StepLogged += e => replay.Record(engine.Snapshot);
        Assert.True((await engine.StartAsync(Recipe with { Pattern = SitePattern.Center1 }, 1, default)).Ok);
        var sampling = replay.Frames.First(f => f.ProbeDetail?.Phase == ProbePhase.Sampling);
        engine.SetCommunicationFailure(true);
        Assert.False(sampling.Faults!.CommunicationLost);
        Assert.Equal(ProbePhase.Sampling, replay.Seek(sampling.SimTime)!.ProbeDetail!.Phase);
        Assert.NotNull(replay.Frames.First(f => f.Interlock?.Request == "MoveXY").Interlock);
    }
    [Fact]
    public async Task JournalFailureStopsBeforeNextWafer()
    {
        using var sqlite = new SqliteResultStore(":memory:");
        var dir = Path.Combine(Path.GetTempPath(), "wafer-journal-stop-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var path = Path.Combine(dir,"journal"); var store = new RecoverableResultStore(sqlite,path) { InjectWriteFailure = true };
        Directory.CreateDirectory(path);
        var (engine, devices) = Create(store); await using var dispose = devices; await engine.InitializeAsync(default);
        var result = await engine.StartAsync(Recipe with { Pattern = SitePattern.Center1 }, 2, default);
        Assert.Equal(AlarmCode.SaveWriteFail,result.Code); Assert.Equal(WaferStatus.Pending,engine.Snapshot.Wafers[1].Status);
    }

    [Fact]
    public async Task CommunicationTimeoutDoesNotIssueMotion()
    {
        var (engine, devices) = Create(); await using var dispose = devices; await engine.InitializeAsync(default);
        engine.CommunicationTimeout = TimeSpan.FromMilliseconds(100);
        engine.SetCommunicationFailure(true);
        var z = devices.ZAxis.PositionUm;
        var result = await engine.StartAsync(Recipe, 1, default);
        Assert.Equal(AlarmCode.CommTimeout, result.Code); Assert.Equal(z,devices.ZAxis.PositionUm);
        Assert.DoesNotContain(engine.Log, e => e.Phase == PhaseKind.PickFromSlot);
        Assert.True(engine.Snapshot.Interlock!.Elapsed >= TimeSpan.FromMilliseconds(100));
    }
    [Fact]
    public async Task CommunicationRecoveryRejectsOwnershipMismatch()
    {
        var (engine, devices) = Create(); await using var dispose = devices; await engine.InitializeAsync(default);
        var injected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.StepLogged += e => { if(e.Phase == PhaseKind.Measure && e.End.HasValue && !injected.Task.IsCompleted) { engine.SetCommunicationFailure(true); injected.TrySetResult(); } };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run = engine.StartAsync(Recipe with { Clock=ClockMode.Realtime,TimeScale=30,Pattern=SitePattern.Center1 },1,stop.Token);
        await injected.Task.WaitAsync(stop.Token); await Task.Delay(50,stop.Token);
        ((InMemoryDigitalIO)devices.Io).Force(IoPoints.ChuckWaferPresent,false);
        engine.SetCommunicationFailure(false);
        var result = await run; Assert.Equal(AlarmCode.InterlockBlocked,result.Code); Assert.Contains("불일치",result.Message);
        Assert.DoesNotContain(engine.Log,e=>e.Phase==PhaseKind.PickFromChuck);
    }
    [Fact]
    public async Task ComparisonCancellationLeavesNoCompletedResult()
    {
        var experiment = new RecipeExperiment(); experiment.Capture(Recipe,25);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>experiment.CompareAsync(Recipe,stop.Token));
    }

    [Fact]
    public async Task FailedChuckWaferCannotBeOverwrittenByNewLot()
    {
        var (engine,devices)=Create(); await using var dispose=devices; await engine.InitializeAsync(default);
        Assert.False((await engine.StartAsync(Recipe with { ContactRetryProbability=1,ContactMaxRetry=0,ContactDetectTimeout=TimeSpan.FromMilliseconds(20) },1,default)).Ok);
        foreach(var alarm in engine.Snapshot.ActiveAlarms) engine.AcknowledgeAlarm(alarm.Id);
        await engine.ResetAsync(); await engine.InitializeAsync(default);
        var result=await engine.StartAsync(Recipe,1,default);
        Assert.Equal(AlarmCode.InterlockBlocked,result.Code); Assert.Contains("미회수",result.Message);
    }

    [Fact]
    public async Task RejectedComparisonReportsRetryAndZeroThroughput()
    {
        var experiment=new RecipeExperiment(); experiment.Capture(Recipe with { Pattern=SitePattern.Center1 },1);
        var (_,b)=await experiment.CompareAsync(Recipe with { QualityTolerancePct=0,QualityMaxRetry=1 },default);
        Assert.False(b.Completed); Assert.Null(b.Wph); Assert.Equal(1,b.Failed); Assert.Equal(1,b.RetryRate);
    }
    [Fact]
    public async Task RunningComparisonCanBeCancelledAndRejectsDuplicate()
    {
        var experiment=new RecipeExperiment(); experiment.Capture(Recipe with { Pattern=SitePattern.Standard49, ReadingsPerSite=1000 },25);
        using var stop=new CancellationTokenSource();
        var run=experiment.CompareAsync(Recipe with { Pattern=SitePattern.Standard49,ReadingsPerSite=1000 },stop.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>experiment.CompareAsync(Recipe,default));
        await Task.Delay(10); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async ()=>await run.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task FinishedRunTimeDoesNotAdvanceDuringInspection()
    {
        var (engine,devices)=Create(); await using var dispose=devices; await engine.InitializeAsync(default);
        Assert.True((await engine.StartAsync(Recipe with { Clock=ClockMode.Realtime,TimeScale=100,Pattern=SitePattern.Center1 },1,default)).Ok);
        var end=engine.Snapshot.SimTime; await Task.Delay(30); engine.SetSensorDelay(true);
        Assert.Equal(end,engine.Snapshot.SimTime);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task SensorDelayCanRecoverBeforeTimeoutOrCancel(bool cancel)
    {
        var (engine,devices)=Create(); await using var dispose=devices; await engine.InitializeAsync(default); engine.SetSensorDelay(true);
        var contact=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.StepLogged+=e=> { if(e.Phase==PhaseKind.ContactDetect && e.End is null) contact.TrySetResult(); };
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run=engine.StartAsync(Recipe with { Clock=ClockMode.Realtime, TimeScale=20, Pattern=SitePattern.Center1 },1,stop.Token);
        await contact.Task.WaitAsync(stop.Token);
        if(cancel) { await engine.AbortAsync(); Assert.False((await run).Ok); Assert.NotEqual(WaferLocation.Done,engine.Snapshot.Wafers[0].Location); }
        else { engine.SetSensorDelay(false); var result=await run; Assert.True(result.Ok,result.Message); Assert.Equal(1,engine.Snapshot.Stats.WafersDone); }
    }

    [Fact]
    public async Task CommunicationRecoveryWaitsForOverlappingZMotion()
    {
        var (engine,devices)=Create(); await using var dispose=devices; await engine.InitializeAsync(default);
        var descending=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.StepLogged+=e=> { if(e.Phase==PhaseKind.DescendZ && e.End is null && !descending.Task.IsCompleted) { engine.SetCommunicationFailure(true); descending.TrySetResult(); } };
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run=engine.StartAsync(Recipe with { Clock=ClockMode.Realtime,TimeScale=50,Pattern=SitePattern.Center1,UnloadOverlap=true,RobotMoveTime=TimeSpan.FromSeconds(2.5),ZApproachSpeedMmPerS=.1 },2,stop.Token);
        await descending.Task.WaitAsync(stop.Token); await Task.Delay(5,stop.Token); engine.SetCommunicationFailure(false);
        var result=await run; Assert.True(result.Ok,result.Message); Assert.Equal(2,engine.Snapshot.Stats.WafersDone);
    }
}
