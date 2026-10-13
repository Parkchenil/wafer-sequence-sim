using Contracts;
using Core.Measurement;
using Core.Sequence;
using Core.Visualization;
using Devices;
using System.Text.Json;
namespace Tests.Integration;
public sealed partial class ControlUpgradeTests
{
    [Fact]
    public async Task QualityFailureRecoveryThenNextLot()
    {
        var (engine, devices) = Create(); await using var dispose = devices;
        await engine.InitializeAsync(default);
        Assert.False((await engine.StartAsync(Recipe with { Pattern = SitePattern.Center1, QualityTolerancePct = 0, QualityMaxRetry = 0 }, 1, default)).Ok);
        Assert.False((await engine.StartAsync(Recipe, 1, default)).Ok);
        var recovered = await engine.RecoverWafersAsync(default); Assert.True(recovered.Ok, recovered.Message);
        Assert.Equal(WaferStatus.Failed, engine.Snapshot.Wafers[0].Status);
        Assert.Equal(WaferLocation.LoadPort, engine.Snapshot.Wafers[0].Location);
        Assert.False(devices.Robot.HoldingWafer); Assert.False(devices.Robot.Extended);
        Assert.Contains(engine.Log, e => e.Phase == PhaseKind.AlarmRecovery && e.Note.Contains("원래 슬롯"));
        var next = await engine.StartAsync(Recipe with { Pattern = SitePattern.Center1 }, 1, default); Assert.True(next.Ok, next.Message);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task CancelPickBeforeAndAfterHandoffCanRecover(bool after)
    {
        var (engine, devices) = Create(); await using var dispose = devices;
        await engine.InitializeAsync(default);
        using var cancel = new CancellationTokenSource();
        var cancelled = false;
        ((IRobotTransportSource)devices.Robot).TransportChanged += t =>
        {
            if (!cancelled && (after ? t.HandoffLocation == WaferLocation.RobotArm : t.Subphase == "블레이드 진입")) { cancelled = true; cancel.Cancel(); }
        };
        Assert.False((await engine.StartAsync(Recipe, 1, cancel.Token)).Ok);
        Assert.True(cancelled); Assert.True(devices.Robot.Extended); Assert.Equal(after, devices.Robot.HoldingWafer);
        Assert.False((await engine.StartAsync(Recipe, 1, default)).Ok);
        var recovery = await engine.RecoverWafersAsync(default); Assert.True(recovery.Ok, recovery.Message);
        Assert.Equal(WaferLocation.LoadPort, engine.Snapshot.Wafers[0].Location);
        Assert.True((await engine.StartAsync(Recipe with { Pattern = SitePattern.Center1 }, 1, default)).Ok);
    }
    [Fact]
    public async Task RecoveryMismatchRefusesMotion()
    {
        var (engine, devices) = Create(); await using var dispose = devices; await engine.InitializeAsync(default);
        ((InMemoryDigitalIO)devices.Io).Force(IoPoints.ChuckWaferPresent, true);
        var before = engine.Log.Count;
        var result = await engine.RecoverWafersAsync(default);
        Assert.False(result.Ok); Assert.Contains("불일치", result.Message); Assert.Equal(before, engine.Log.Count);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Standard5EightWafersEightExactRepeats(bool overlap)
    {
        var recipe = Recipe with { UnloadOverlap = overlap };
        var experiment = new RecipeExperiment(); experiment.Capture(recipe, 8);
        ExperimentResult? reference = null;
        for (var i = 0; i < 4; i++)
        {
            var (a,b) = await experiment.CompareAsync(recipe, default);
            foreach (var result in new[] { a,b })
            {
                Assert.True(result.Completed, result.Outcome); reference ??= result;
                Assert.Equal(reference.Seconds, result.Seconds); Assert.Equal(reference.Wph, result.Wph); Assert.Equal(reference.Digest, result.Digest);
            }
        }
    }
    private static RunRecord JournalRun(string id) => new(id, DateTimeOffset.UnixEpoch, "검증", "{}", 1, 0, null, null, new Dictionary<PhaseKind,TimeSpan>(), "검증");
    [Fact]
    public async Task JournalTwelveRotationsPreserveOriginalBytesAndRestart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wafer-rotation-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        using var db = new SqliteResultStore(Path.Combine(dir,"runs.db")); await db.InitializeAsync(default);
        var path = Path.Combine(dir,"active.jsonl"); var store = new RecoverableResultStore(db,path,1);
        byte[]? previous = null;
        for (var i = 0; i < 12; i++)
        {
            await store.SaveRunAsync(JournalRun("R"+i), default);
            if (previous is not null) Assert.Contains(Directory.GetFiles(dir,"*.acked"), p => File.ReadAllBytes(p).SequenceEqual(previous));
            previous = File.ReadAllBytes(path);
            store = new RecoverableResultStore(db,path,1); Assert.False(store.JournalBlocked); Assert.Equal(0,store.PendingCount);
        }
        Assert.Equal(11, Directory.GetFiles(dir,"*.acked").Length); Assert.Equal(12,(await db.ListRunsAsync(default)).Count);
    }
    [Theory][InlineData("tail")][InlineData("middle")][InlineData("ack")]
    public async Task JournalCorruptionPreservesBytesAndDoesNotAcknowledgePending(string fault)
    {
        var dir = Path.Combine(Path.GetTempPath(), "wafer-corrupt-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        using var db = new SqliteResultStore(Path.Combine(dir,"runs.db")); await db.InitializeAsync(default);
        var path=Path.Combine(dir,"active.jsonl"); var store=new RecoverableResultStore(db,path) { InjectWriteFailure=true };
        await store.SaveRunAsync(JournalRun("R"),default);
        var prefix=File.ReadAllText(path);
        // 시험 전용 파일에 중단 쓰기를 추가한다. 기존 기록의 바이트를 덮어쓰지 않는다.
        await File.AppendAllTextAsync(path, fault=="ack" ? "{\"Id\":\"" : "{잘린 JSON");
        if(fault=="middle") await File.AppendAllTextAsync(path,"\n"+prefix);
        var original=File.ReadAllBytes(path);
        store=new RecoverableResultStore(db,path);
        Assert.Equal(1,store.PendingCount);
        if(fault=="middle") { Assert.True(store.JournalBlocked); Assert.Equal(original,File.ReadAllBytes(path)); await Assert.ThrowsAsync<IOException>(()=>store.RecoverAsync(default)); }
        else
        {
            Assert.False(store.JournalBlocked); Assert.Contains("손상",store.JournalStatus);
            Assert.Equal(original,File.ReadAllBytes(Assert.Single(Directory.GetFiles(dir,"*.tail-original"))));
            await store.RecoverAsync(default); await store.RecoverAsync(default);
            Assert.Single(await db.ListRunsAsync(default)); Assert.Equal(0,store.PendingCount);
        }
    }
    [Fact]
    public async Task JournalAckCrashReplaysIdempotently()
    {
        var dir=Path.Combine(Path.GetTempPath(),"wafer-ack-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        using var db=new SqliteResultStore(Path.Combine(dir,"runs.db")); await db.InitializeAsync(default);
        var path=Path.Combine(dir,"active.jsonl"); var store=new RecoverableResultStore(db,path) { InjectWriteFailure=true };
        await store.SaveRunAsync(JournalRun("R"),default);
        db.InjectWriteFailure=false; await db.FinalizeRunAsync(JournalRun("R"),default);
        store=new RecoverableResultStore(db,path); await store.RecoverAsync(default); await store.RecoverAsync(default);
        Assert.Single(await db.ListRunsAsync(default)); Assert.Equal(0,store.PendingCount);
    }
    [Fact]
    public async Task InstantCommunicationGateAllowsHumanRestoreAndEmo()
    {
        var (engine, devices)=Create(); await using var dispose=devices; await engine.InitializeAsync(default);
        engine.SetCommunicationFailure(true);
        var run=engine.StartAsync(Recipe,1,default);
        await Task.Delay(200); Assert.False(run.IsCompleted);
        ((InMemoryDigitalIO)devices.Io).Force(IoPoints.Emo,true);
        var result=await run.WaitAsync(TimeSpan.FromSeconds(3)); Assert.False(result.Ok);
        Assert.DoesNotContain(engine.Log,e=>e.Phase==PhaseKind.PickFromSlot);
    }
    [Fact]
    public async Task Standard49TwentyFiveWafersTwelveLotsStorageLoad()
    {
        var dir=Path.Combine(Path.GetTempPath(),"wafer-standard49-load-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        using var db=new SqliteResultStore(Path.Combine(dir,"runs.db"));
        var store=new RecoverableResultStore(db,Path.Combine(dir,"active.jsonl"),1024*1024);
        using var cancel=new CancellationTokenSource(TimeSpan.FromMinutes(5));
        for(var lot=0;lot<12;lot++)
        {
            var clock=new VirtualTimeProvider(); clock.Configure(ClockMode.Instant,1);
            await Task.Run(()=>clock.RunDeterministic(async ()=>
            {
                await using var devices=DeviceSetFactory.CreateInMemory(clock,new ResistanceField(12345),12345);
                var engine=new SequenceEngine(devices,new WaitHelper(clock),clock,new RsCalculator(),new SitePatternProvider(),store,new LogDigest());
                await engine.InitializeAsync(cancel.Token);
                var result=await engine.StartAsync(Recipe with { Pattern=SitePattern.Standard49, UnloadOverlap=true },25,cancel.Token);
                Assert.True(result.Ok,result.Message); Assert.Equal(25,engine.Snapshot.Stats.WafersDone); return true;
            }));
        }
        var runs=await db.ListRunsAsync(default); Assert.Equal(12,runs.Count);
        foreach(var run in runs) { var wafers=await db.GetWafersAsync(run.RunId,default); Assert.Equal(25,wafers.Count); Assert.All(wafers,w=>Assert.Equal(49,w.Sites.Count)); }
        Assert.Equal(0,store.PendingCount); Assert.NotEmpty(Directory.GetFiles(dir,"*.acked"));
        File.WriteAllText(Path.Combine(dir,"verified.txt"),"12 lots × 25 wafers × 49 sites = 14700 sites; pending 0; archive preserved");
    }
    private sealed class SamplesSmu(double[] samples) : ISourceMeasureUnit
    {
        private int index;
        public Task<DeviceResult> ConfigureAsync(double current,CancellationToken ct)=>Task.FromResult(DeviceResult.Success);
        public Task<DeviceResult> OutputAsync(bool on,CancellationToken ct)=>Task.FromResult(DeviceResult.Success);
        public Task<DeviceResult<double>> MeasureVoltageAsync(TimeSpan duration,CancellationToken ct)=>Task.FromResult(DeviceResult<double>.Success(samples[index++ % samples.Length]));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task QualityUsesFiniteSamplesAndCanAcceptSecondAttempt(bool validRetry)
    {
        var clock=new VirtualTimeProvider(); clock.Configure(ClockMode.Instant,1);
        await using var devices=DeviceSetFactory.CreateInMemory(clock,new ResistanceField(12345),12345);
        var samples=validRetry ? new double[] { 1,2,1,2,3,3,3,3 } : new double[] { double.NaN,double.PositiveInfinity,1,double.NaN };
        var replacement=new DeviceBundle { Io=devices.Io,StageX=devices.StageX,StageY=devices.StageY,ZAxis=devices.ZAxis,Probe=devices.Probe,Vision=devices.Vision,LoadPort=devices.LoadPort,Robot=devices.Robot,Aligner=devices.Aligner,Smu=new SamplesSmu(samples) };
        var engine=new SequenceEngine(replacement,new WaitHelper(clock),clock,new RsCalculator(),new SitePatternProvider(),null,new LogDigest());
        MeasurementResult? accepted=null; engine.MeasurementCompleted+=m=>accepted=m;
        await engine.InitializeAsync(default);
        var result=await engine.StartAsync(Recipe with { Pattern=SitePattern.Center1,QualityMaxRetry=1 },1,default);
        Assert.Equal(validRetry,result.Ok);
        if(validRetry) { Assert.NotNull(accepted); Assert.All(accepted.VoltageV,v=>Assert.Equal(3,v)); Assert.Equal(1,engine.Snapshot.ProbeDetail!.QualityRetry); }
        else { Assert.Equal(AlarmCode.QualityRejected,result.Code); Assert.Null(accepted); Assert.Equal(1,engine.Snapshot.ProbeDetail!.SampleCount); }
    }
    [Fact]
    public async Task CommunicationWallClockCanBeInjectedWithoutWaiting120Seconds()
    {
        var (engine,devices)=Create(); await using var dispose=devices; await engine.InitializeAsync(default);
        var fake=new Microsoft.Extensions.Time.Testing.FakeTimeProvider(); engine.CommunicationTimeProvider=fake;
        engine.SetCommunicationFailure(true); var run=engine.StartAsync(Recipe,1,default);
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while(engine.Snapshot.Interlock?.Request!="LiftZ" && engine.Snapshot.Interlock?.Request!="MapSlots") await Task.Delay(5,deadline.Token);
        for(var i=0;i<3&&!run.IsCompleted;i++) { fake.Advance(TimeSpan.FromSeconds(121)); await Task.Delay(20,deadline.Token); }
        Assert.Equal(AlarmCode.CommTimeout,(await run.WaitAsync(deadline.Token)).Code);
        Assert.DoesNotContain(engine.Log,e=>e.Phase==PhaseKind.PickFromSlot);
    }
    [Fact]
    public async Task InstantCommunicationWaitCanBeCancelled()
    {
        var (engine,devices)=Create(); await using var dispose=devices; await engine.InitializeAsync(default);
        engine.SetCommunicationFailure(true); var run=engine.StartAsync(Recipe,1,default);
        await Task.Delay(80); Assert.False(run.IsCompleted);
        await engine.AbortAsync().WaitAsync(TimeSpan.FromSeconds(2)); Assert.False((await run).Ok);
    }
    [Fact]
    public async Task PositiveOverlapHasActualConcurrentIntervals()
    {
        var experiment=new RecipeExperiment(); var recipe=Recipe with { UnloadOverlap=true,RobotMoveTime=TimeSpan.FromSeconds(1) }; experiment.Capture(recipe,3);
        var (a,_)=await experiment.CompareAsync(recipe,default);
        Assert.Contains(a.Steps,p=>p.Phase==PhaseKind.PickFromSlot && p.WaferId=="W002" && a.Steps.Any(m=>m.WaferId=="W001" && m.Module is ModuleId.ZHead or ModuleId.Smu && m.Start<p.End && m.End>p.Start));
    }
    [Fact]
    public async Task RobotZoneGateAllowsIsolatedAndRejectsChuckAndUnknown()
    {
        var (engine,devices)=Create(); await using var dispose=devices; await engine.InitializeAsync(default);
        var interlock=new Interlock(devices,()=>true,()=>0,()=>DeviceResult.Success,()=>false);
        using var cancel=new CancellationTokenSource(); bool sawIsolated=false;
        ((IRobotTransportSource)devices.Robot).TransportChanged+=t=> { if(!sawIsolated && t.Subphase=="블레이드 진입") { sawIsolated=true; Assert.True(interlock.CheckMoveXY().Ok); cancel.Cancel(); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>devices.Robot.PickFromSlotAsync(1,TimeSpan.FromSeconds(1),cancel.Token));
        Assert.True(sawIsolated); Assert.Equal(RobotZone.Unknown,((IRobotTransportSource)devices.Robot).Transport.Zone); Assert.False(interlock.CheckMoveXY().Ok);
        Assert.True((await ((IRobotRecovery)devices.Robot).WithdrawAsync(TimeSpan.FromMilliseconds(100),default)).Ok);
        await devices.Robot.PickFromSlotAsync(1,TimeSpan.FromMilliseconds(100),default);
        bool sawChuck=false;
        ((IRobotTransportSource)devices.Robot).TransportChanged+=t=> { if(t.Zone==RobotZone.ChuckAccess && devices.Robot.Extended) { sawChuck=true; Assert.False(interlock.CheckMoveXY().Ok); } };
        await devices.Robot.PlaceOnChuckAsync(TimeSpan.FromMilliseconds(100),default); Assert.True(sawChuck);
    }
    [Fact]
    public async Task PerWaferPendingStatusDoesNotUseGlobalQueueCount()
    {
        var dir=Path.Combine(Path.GetTempPath(),"wafer-status-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        using var db=new SqliteResultStore(Path.Combine(dir,"runs.db")); await db.InitializeAsync(default);
        var store=new RecoverableResultStore(db,Path.Combine(dir,"active.jsonl")) { InjectWriteFailure=true };
        WaferSummary Wafer(string id)=>new(id,0,0,0,0,0,0,0,0,TimeSpan.Zero,[]);
        await store.SaveWaferAsync("R",Wafer("W001"),default); store.InjectWriteFailure=false;
        await store.SaveWaferAsync("R",Wafer("W002"),default);
        Assert.True(store.IsWaferPending("R","W001")); Assert.False(store.IsWaferPending("R","W002")); Assert.Equal(1,store.PendingCount);
    }
    [Fact]
    public async Task IncompleteComparisonIsExplicitlyUnavailable()
    {
        var experiment=new RecipeExperiment(); experiment.Capture(Recipe with { Pattern=SitePattern.Center1 },1);
        var (a,b)=await experiment.CompareAsync(Recipe with { QualityTolerancePct=0,QualityMaxRetry=1 },default);
        Assert.Contains("— (불완료)",RecipeExperiment.Describe(a,b)); Assert.Contains("고정 항목",RecipeExperiment.Describe(a,b)); Assert.InRange(b.RetryRate,0,1);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task PreparedTailRepairSurvivesCrashBeforeOrAfterOriginalRename(bool renamed)
    {
        var dir=Path.Combine(Path.GetTempPath(),"wafer-repair-crash-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        using var db=new SqliteResultStore(Path.Combine(dir,"runs.db")); await db.InitializeAsync(default);
        var path=Path.Combine(dir,"active.jsonl"); var store=new RecoverableResultStore(db,path) { InjectWriteFailure=true };
        await store.SaveRunAsync(JournalRun("R"),default); var prefix=File.ReadAllBytes(path);
        await File.AppendAllTextAsync(path,"{중단"); var original=File.ReadAllBytes(path);
        await File.WriteAllBytesAsync(path+".repair-ready",prefix);
        await File.WriteAllTextAsync(path+".repair-ready.commit",Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(prefix)));
        if(renamed) File.Move(path,path+".archive-test.tail-original");
        store=new RecoverableResultStore(db,path); Assert.False(store.JournalBlocked); Assert.Equal(1,store.PendingCount);
        Assert.Equal(original,File.ReadAllBytes(Assert.Single(Directory.GetFiles(dir,"*.tail-original"))));
        await store.RecoverAsync(default); Assert.Single(await db.ListRunsAsync(default));
    }
    [Fact]
    public async Task IncompleteRepairPreparationCannotReplaceOriginalPrefix()
    {
        var dir=Path.Combine(Path.GetTempPath(),"wafer-repair-incomplete-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        using var db=new SqliteResultStore(Path.Combine(dir,"runs.db")); await db.InitializeAsync(default);
        var path=Path.Combine(dir,"active.jsonl"); var store=new RecoverableResultStore(db,path) { InjectWriteFailure=true };
        await store.SaveRunAsync(JournalRun("R1"),default); var first=File.ReadAllBytes(path); await store.SaveRunAsync(JournalRun("R2"),default);
        await File.AppendAllTextAsync(path,"{중단"); await File.WriteAllBytesAsync(path+".repair-ready",first);
        store=new RecoverableResultStore(db,path); Assert.Equal(2,store.PendingCount); Assert.False(store.JournalBlocked);
        await store.RecoverAsync(default); Assert.Equal(2,(await db.ListRunsAsync(default)).Count);
    }
    [Fact]
    public async Task PendingDataPreventsRotationAndLatestWriteRemainsLatest()
    {
        var dir=Path.Combine(Path.GetTempPath(),"wafer-pending-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        using var db=new SqliteResultStore(Path.Combine(dir,"runs.db")); await db.InitializeAsync(default);
        var path=Path.Combine(dir,"active.jsonl"); var store=new RecoverableResultStore(db,path,1) { InjectWriteFailure=true };
        await store.SaveRunAsync(JournalRun("R") with { LogDigest="시작" },default);
        await store.SaveRunAsync(JournalRun("R2"),default); Assert.Empty(Directory.GetFiles(dir,"*.acked"));
        store.InjectWriteFailure=false; await store.SaveRunAsync(JournalRun("R") with { LogDigest="완료" },default);
        store=new RecoverableResultStore(db,path,1); await store.RecoverAsync(default);
        Assert.Equal("완료",(await db.ListRunsAsync(default)).Single(r=>r.RunId=="R").LogDigest);
    }
    [Fact]
    public async Task RecoveryCancellationPreservesWaferAndCanResume()
    {
        var (engine,devices)=Create(); await using var dispose=devices; await engine.InitializeAsync(default);
        await engine.StartAsync(Recipe with { Pattern=SitePattern.Center1,QualityTolerancePct=0,QualityMaxRetry=0 },1,default);
        var cancelled=false;
        engine.StepLogged+=e=> { if(!cancelled && e.Phase==PhaseKind.AlarmRecovery && e.Note.Contains("원래 슬롯") && !e.End.HasValue) { cancelled=true; engine.CancelRecovery(); } };
        Assert.False((await engine.RecoverWafersAsync(default).WaitAsync(TimeSpan.FromSeconds(3))).Ok);
        Assert.True(cancelled); Assert.Equal(WaferLocation.RobotArm,engine.Snapshot.Wafers[0].Location); Assert.Equal(WaferStatus.Failed,engine.Snapshot.Wafers[0].Status);
        Assert.False((await engine.StartAsync(Recipe,1,default)).Ok);
        var recovered=await engine.RecoverWafersAsync(default); Assert.True(recovered.Ok,recovered.Message);
        Assert.True((await engine.StartAsync(Recipe with { Pattern=SitePattern.Center1 },1,default)).Ok);
    }
}
