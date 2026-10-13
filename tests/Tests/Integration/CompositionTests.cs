using Core.Visualization;
using Contracts;
using Core.Measurement;
using Core.Sequence;
using Devices;

namespace Tests.Integration;

/// <summary>화면이 쓰는 조립 그대로(진짜 장치·프로토콜·저장소·엔진) 로트 1회를 돌린다. 어느 단계에서 멈추는지도 드러난다.</summary>
public sealed class CompositionTests
{
    private static async Task<(VirtualTimeProvider, DeviceBundle, SqliteResultStore, SequenceEngine)> BuildAsync(bool protocols)
    {
        var clock = new VirtualTimeProvider();
        var field = new ResistanceField(777);
        var devices = protocols ? DeviceSetFactory.CreateWithProtocols(clock, field, 777) : DeviceSetFactory.CreateInMemory(clock, field, 777);
        var store = new SqliteResultStore(":memory:");
        await store.InitializeAsync(CancellationToken.None);
        var engine = new SequenceEngine(devices, new WaitHelper(clock), clock, new RsCalculator(), new SitePatternProvider(), store, new LogDigest());
        engine.SnapshotChanged += s => { if (devices.SmuServer is { } smu && s.CurrentWaferId is { } w) smu.CurrentWaferId = w; };
        return (clock, devices, store, engine);
    }

    [Fact]
    public async Task 선행픽업은_계측중인_웨이퍼의_소유권을_바꾸지_않는다()
    {
        var (_,devices,_,engine)=await BuildAsync(false);await using var disposal=devices;
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Assert.True((await engine.InitializeAsync(stop.Token)).Ok);
        EquipmentSnapshot? prefetch=null;
        ((IRobotTransportSource)devices.Robot).TransportChanged+=s=>
        {
            if(s.Slot==2 && s.HandoffLocation==WaferLocation.RobotArm && engine.Snapshot.CurrentWaferId=="W001")prefetch=engine.Snapshot;
        };
        var recipe=ConceptPresets.ImprovementExample() with {Clock=ClockMode.Instant,Pattern=SitePattern.Center1,ReadingsPerSite=1,UnloadOverlap=true,RobotMoveTime=TimeSpan.Zero,VacuumCheckWait=TimeSpan.FromSeconds(10)};
        var result=await engine.StartAsync(recipe,2,stop.Token);
        Assert.True(result.Ok,result.Message);Assert.NotNull(prefetch);
        Assert.Equal(WaferLocation.Chuck,prefetch!.Wafers.Single(w=>w.WaferId=="W001").Location);
        Assert.Equal(WaferLocation.RobotArm,prefetch.Wafers.Single(w=>w.WaferId=="W002").Location);
    }
    [Fact]
    public async Task 전로트_반송_소유권과_상세통계가_일치한다()
    {
        var (_,devices,_,engine)=await BuildAsync(false);
        await using var disposal=devices;
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Assert.True((await engine.InitializeAsync(stop.Token)).Ok);
        var errors=new List<string>();var handoffs=0;
        ((IRobotTransportSource)devices.Robot).TransportChanged+=state=>
        {
            var wafers=engine.Snapshot.Wafers;
            if(wafers.Count(w=>w.Location==WaferLocation.RobotArm)>1)errors.Add("로봇 중복 소유");
            if(state.HandoffLocation is { } location)
            {
                handoffs++;
                var parent=engine.Log.LastOrDefault(e=>e.Module==ModuleId.Robot && !e.IsDetail && e.End is null);
                // Log는 정렬된 뷰이므로 시간과 sequence로 현재 명령을 찾는다.
                parent=engine.Log.Where(e=>e.Module==ModuleId.Robot && !e.IsDetail && e.End is null).MaxBy(e=>e.Seq);
                if(parent is null || wafers.Single(w=>w.WaferId==parent.WaferId).Location!=location)errors.Add("인계 소유자 불일치");
            }
        };
        var recipe=ConceptPresets.ImprovementExample() with {Clock=ClockMode.Instant,Pattern=SitePattern.Center1,ReadingsPerSite=1,UnloadOverlap=false};
        var result=await engine.StartAsync(recipe,25,stop.Token);
        Assert.True(result.Ok,result.Message);Assert.Empty(errors);Assert.Equal(150,handoffs);
        Assert.Equal(25,engine.Snapshot.Stats.WafersDone);Assert.All(engine.Snapshot.Wafers,w=>Assert.Equal(WaferLocation.Done,w.Location));
        Assert.Contains(engine.Log,e=>e.IsDetail);
        foreach(var total in engine.Snapshot.Stats.PhaseTotals.Where(t=>t.Key is PhaseKind.PickFromSlot or PhaseKind.Align or PhaseKind.PlaceOnChuck or PhaseKind.PickFromChuck or PhaseKind.ReturnToSlot))
            Assert.Equal(total.Value,TimeSpan.FromTicks(engine.Log.Where(e=>!e.IsDetail && e.End.HasValue && e.Phase==total.Key).Sum(e=>e.Duration!.Value.Ticks)));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 조립_인메모리와_프로토콜_둘다_로트가_끝난다(bool protocols)
    {
        var (clock, devices, store, engine) = await BuildAsync(protocols);
        await using var _ = devices;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)); // 벽시계 워치독
        var init = await engine.InitializeAsync(cts.Token);
        Assert.True(init.Ok, init.Message);
        var recipe = new RecipeDto { Clock = ClockMode.Instant, Pattern = SitePattern.Standard9, Name = "조립" };
        WaferSummary? done = null;
        engine.WaferCompleted += w => done = w;
        var result = await engine.StartAsync(recipe, 1, cts.Token);
        Assert.True(result.Ok, result.Message);
        Assert.NotNull(done);
        Assert.Equal(9, done!.SiteCount);
        Assert.True(done.MeanRs > 0 && double.IsFinite(done.MeanRs));
        var runs = await store.ListRunsAsync(CancellationToken.None);
        Assert.Single(runs);
        Assert.NotEmpty(await store.GetLogAsync(runs[0].RunId, CancellationToken.None));
    }

    [Fact]
    public async Task 조립_실시간모드_짧은_레시피도_끝난다()
    {
        var (clock, devices, store, engine) = await BuildAsync(true);
        await using var _ = devices;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Assert.True((await engine.InitializeAsync(cts.Token)).Ok);
        var recipe = new RecipeDto { Clock = ClockMode.Realtime, TimeScale = 50, Pattern = SitePattern.Center1, ReadingsPerSite = 1, Name = "실시간" };
        var result = await engine.StartAsync(recipe, 1, cts.Token);
        Assert.True(result.Ok, result.Message);
        Assert.Equal(1, engine.Snapshot.Stats.WafersDone);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 가상_13점_프리셋이_실제_엔진에서_동작한다(bool improved)
    {
        var (_, devices, _, engine) = await BuildAsync(false);
        await using var _ = devices;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Assert.True((await engine.InitializeAsync(cts.Token)).Ok);
        var recipe = (improved ? ConceptPresets.ImprovementExample() : ConceptPresets.WaitingExample()) with { Clock = ClockMode.Instant };
        WaferSummary? summary = null;
        var replay = new RunReplay();
        engine.SnapshotChanged += replay.Record;
        engine.StepLogged += step => { replay.Record(step); replay.Record(engine.Snapshot); };
        engine.WaferCompleted += value => summary = value;
        var result = await engine.StartAsync(recipe, 1, cts.Token);
        Assert.True(result.Ok, result.Message);
        Assert.NotNull(summary);
        Assert.Equal(13, summary!.SiteCount);
        Assert.Equal(13, engine.Snapshot.Sites.Count);
        replay.Record(engine.Snapshot);
        Assert.Equal(EquipmentState.Completed,replay.Seek(replay.Duration)!.State);
        Assert.NotEmpty(replay.VisibleEvents(replay.Duration));
        Assert.Null(replay.Seek(replay.StartTime-TimeSpan.FromTicks(1)));
    }
    [Fact]
    public async Task 척으로_반송하는_동안_웨이퍼가_로봇에_있다()
    {
        var (_,devices,_,engine)=await BuildAsync(false);
        await using var disposal=devices;
        using var cts=new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Assert.True((await engine.InitializeAsync(cts.Token)).Ok);
        var locations=new List<WaferLocation>();
        engine.StepLogged+=step=>
        {
            if(step.Phase==PhaseKind.PlaceOnChuck && step.End is null)
                locations.Add(engine.Snapshot.Wafers.Single(w=>w.WaferId==step.WaferId).Location);
        };
        var recipe=ConceptPresets.ImprovementExample() with { Clock=ClockMode.Instant,Pattern=SitePattern.Center1 };
        var result=await engine.StartAsync(recipe,2,cts.Token);
        Assert.True(result.Ok,result.Message);
        Assert.Equal(2,locations.Count);
        Assert.All(locations,location=>Assert.Equal(WaferLocation.RobotArm,location));
        Assert.Equal(2,engine.Snapshot.Stats.WafersDone);
    }
}
