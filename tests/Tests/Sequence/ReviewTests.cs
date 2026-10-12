using Contracts;
using Core.Sequence;
using Tests.Sequence.Stubs;

namespace Tests.Sequence;

public sealed class ReviewTests
{
    private static RecipeDto Recipe => new() { Clock = ClockMode.Instant, Pattern = SitePattern.Center1, ContactRetryProbability = 0, RobotMoveTime = TimeSpan.FromMilliseconds(10), AlignTime = TimeSpan.FromMilliseconds(10) };
    private static (SequenceEngine Engine, StubDevices Devices, StubStore Store) Create()
    {
        var clock = new VirtualTimeProvider(); var devices = new StubDevices(clock); var store = new StubStore();
        return (new SequenceEngine(devices, new StubWait(clock), clock, new StubRs(), new StubSites(), store, new StubDigest()), devices, store);
    }
    private static async Task Reset(SequenceEngine engine)
    {
        foreach (var alarm in engine.Snapshot.ActiveAlarms) engine.AcknowledgeAlarm(alarm.Id);
        Assert.True((await engine.ResetAsync()).Ok); Assert.True((await engine.InitializeAsync(default)).Ok);
    }

    [Fact(DisplayName = "장치 시드와 웨이퍼 ID는 측정 전에 설정되고 실행 ID는 매번 유일함")]
    public async Task ContextAndRunId()
    {
        var (engine, devices, store) = Create(); await engine.InitializeAsync(default);
        var recipe = Recipe with { Seed = 712, UnloadOverlap = true };
        Assert.True((await engine.StartAsync(recipe, 2, default)).Ok);
        Assert.True((await engine.StartAsync(recipe, 2, default)).Ok);
        Assert.Equal(new[] { "W001", "W002", "W001", "W002" }, devices.MeasurementWaferIds.Where((_, i) => i % recipe.ReadingsPerSite == 0));
        Assert.Equal("seed:712", devices.ContextCalls[0]);
        Assert.Equal(2, devices.ContextCalls.Count(x => x == "seed:712"));
        Assert.Equal(2, store.Runs.Count); Assert.NotEqual(store.Runs[0].RunId, store.Runs[1].RunId);
        Assert.All(store.Runs, x => Assert.InRange(x.StartedAtWall, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.Contains(engine.Log, x => x.Module == ModuleId.Robot && x.Note == "얼라이너에 놓기");
        Assert.Contains(engine.Log, x => x.Module == ModuleId.Robot && x.Note == "얼라이너에서 집기");
    }

    [Fact(DisplayName = "저장 소비를 모두 마친 뒤 실행 완료 이벤트를 보냄")]
    public async Task RunCompletedAfterQueueDrain()
    {
        var (engine, _, store) = Create(); await engine.InitializeAsync(default);
        var savedAtCompletion = -1; engine.RunCompleted += _ => savedAtCompletion = store.Saved.Count;
        Assert.True((await engine.StartAsync(Recipe with { Save = SaveMode.Async }, 3, default)).Ok);
        Assert.Equal(3, savedAtCompletion); Assert.Equal(3, store.Saved.Count);
    }

    [Fact(DisplayName = "EMO 활성과 입력 읽기 실패는 안전하게 차단함")]
    public async Task EmoFailClosed()
    {
        foreach (var failedRead in new[] { false, true })
        {
            var (engine, devices, _) = Create(); await engine.InitializeAsync(default);
            devices.EmoActive = !failedRead; devices.FailEmoRead = failedRead;
            var result = await engine.StartAsync(Recipe, 1, default);
            Assert.False(result.Ok); Assert.Equal(failedRead ? AlarmCode.ConfigError : AlarmCode.Emo, result.Code);
            Assert.Equal(EquipmentState.Alarm, engine.Snapshot.State);
            Assert.Equal(Recipe.ZSafeHeightUm, devices.Z.PositionUm);
            Assert.Empty(devices.MeasurementWaferIds);
        }
    }

    [Fact(DisplayName = "일시정지 뒤 진공 상실을 다시 검사하고 XY 명령을 막음")]
    public async Task InterlockAfterResume()
    {
        var (engine, devices, _) = Create(); await engine.InitializeAsync(default);
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = true;
        engine.StepLogged += x => { if (armed && x.End is null && x.Phase == PhaseKind.Measure && x.SiteIndex == 0) { armed = false; engine.Pause(); } };
        engine.SnapshotChanged += x => { if (x.State == EquipmentState.Paused) paused.TrySetResult(); };
        var run = engine.StartAsync(Recipe with { Pattern = SitePattern.Standard5 }, 1, default);
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await devices.WriteAsync(IoPoints.VacChuckOn, false, default);
        engine.Resume(); var result = await run;
        Assert.False(result.Ok); Assert.Equal(AlarmCode.InterlockBlocked, result.Code);
        Assert.DoesNotContain(engine.Log, x => x.SiteIndex == 1 && x.Phase == PhaseKind.MoveXY);
    }

    [Fact(DisplayName = "축 상태를 동기로 조회하지 않고 낮은 사이트 리프트를 거부함")]
    public async Task StageStatusAndLiftValidation()
    {
        var (engine, devices, _) = Create(); devices.X.ThrowOnStatus = true; devices.Y.ThrowOnStatus = true;
        await engine.InitializeAsync(default);
        Assert.Equal(AlarmCode.ConfigError, (await engine.StartAsync(Recipe with { ZLiftAfterMeasureUm = 50 }, 1, default)).Code);
        Assert.True((await engine.StartAsync(Recipe, 1, default)).Ok);
    }

    [Fact(DisplayName = "축 운영자 정지는 알람 없이 중단 이력으로 남음")]
    public async Task AxisStoppedIsAbort()
    {
        var (engine, devices, store) = Create(); await engine.InitializeAsync(default); devices.X.FailNextMove = AlarmCode.AxisStopped;
        var result = await engine.StartAsync(Recipe, 1, default);
        Assert.False(result.Ok); Assert.Empty(engine.Snapshot.ActiveAlarms);
        Assert.Equal(EquipmentState.Idle, engine.Snapshot.State);
        Assert.Single(store.Runs); Assert.Contains("[중단]", store.Runs[0].RecipeName);
    }

    [Fact(DisplayName = "접촉 재시도 상승 실패 뒤 알람과 안전 상태를 거쳐 다시 시작함")]
    public async Task RetryLiftFailureReleasesChuck()
    {
        var (engine, devices, _) = Create(); await engine.InitializeAsync(default);
        devices.ContactSensorFail = true; devices.Z.FailNextMove = AlarmCode.AxisTimeout; devices.Z.FailAtMoveNumber = 2;
        var failed = await engine.StartAsync(Recipe with { ContactMaxRetry = 1 }, 1, default);
        Assert.Equal(AlarmCode.AxisTimeout, failed.Code); Assert.Equal(Recipe.ZSafeHeightUm, devices.Z.PositionUm);
        devices.ContactSensorFail = false; await Reset(engine);
        Assert.True((await engine.StartAsync(Recipe, 1, default)).Ok);
    }

    [Fact(DisplayName = "진공 해제 실패는 알람을 남기고 척 점유를 해제함")]
    public async Task VacuumOffFailureReleasesChuck()
    {
        var (engine, devices, _) = Create(); await engine.InitializeAsync(default); devices.FailVacuumOff = true;
        var failed = await engine.StartAsync(Recipe, 1, default);
        Assert.Equal(AlarmCode.CommTimeout, failed.Code); Assert.Equal(EquipmentState.Alarm, engine.Snapshot.State);
        devices.FailVacuumOff = false; await Reset(engine);
        Assert.True((await engine.StartAsync(Recipe, 1, default)).Ok);
    }

    [Fact(DisplayName = "SMU 읽기 실패 시 출력을 먼저 끄고 안전 높이로 복귀함")]
    public async Task SmuFailureTurnsOutputOff()
    {
        var (engine, devices, _) = Create(); await engine.InitializeAsync(default); devices.FailSmuRead = true;
        var result = await engine.StartAsync(Recipe, 1, default);
        Assert.Equal(AlarmCode.SmuTimeout, result.Code); Assert.False(devices.OutputOn);
        Assert.Equal(Recipe.ZSafeHeightUm, devices.Z.PositionUm);
    }

    [Fact(DisplayName = "비동기 저장 실패는 SaveWriteFail 알람과 실패 이력을 남김")]
    public async Task SaveFailurePreservesResult()
    {
        var (engine, _, store) = Create(); await engine.InitializeAsync(default); store.FailNextWaferSave = true;
        var result = await engine.StartAsync(Recipe with { Save = SaveMode.Async }, 1, default);
        Assert.Equal(AlarmCode.SaveWriteFail, result.Code);
        Assert.Contains(engine.Snapshot.ActiveAlarms, x => x.Code == AlarmCode.SaveWriteFail);
        Assert.Single(store.Saved); Assert.Single(store.Runs); Assert.Contains("[실패]", store.Runs[0].RecipeName);
    }

    [Fact(DisplayName = "중단 시 저장 큐를 비우고 중단 이력을 남김")]
    public async Task AbortFlushesQueue()
    {
        var (engine, _, store) = Create(); await engine.InitializeAsync(default); store.HoldWrites = true;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.WaferCompleted += _ => completed.TrySetResult();
        var run = engine.StartAsync(Recipe with { Save = SaveMode.Async }, 4, default);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var abort = engine.AbortAsync(); store.Release();
        await abort.WaitAsync(TimeSpan.FromSeconds(10)); Assert.False((await run).Ok);
        Assert.NotEmpty(store.Saved); Assert.Single(store.Runs); Assert.Contains("[중단]", store.Runs[0].RecipeName);
    }

    [Fact(DisplayName = "알람 시 앞서 측정된 큐 결과를 비우고 실패 이력을 남김")]
    public async Task AlarmFlushesQueue()
    {
        var (engine, devices, store) = Create(); await engine.InitializeAsync(default); store.HoldWrites = true;
        var alarm = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.StepLogged += x => { if (x.End is null && x.WaferId == "W003" && x.Phase == PhaseKind.PlaceOnChuck) devices.ContactSensorFail = true; };
        engine.AlarmRaised += _ => { store.Release(); alarm.TrySetResult(); };
        var run = engine.StartAsync(Recipe with { Save = SaveMode.Async, ContactMaxRetry = 0 }, 3, default);
        await alarm.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(AlarmCode.ContactRetryExceeded, result.Code);
        Assert.Equal(2, store.Saved.Count); Assert.Single(store.Runs); Assert.Contains("[실패]", store.Runs[0].RecipeName);
    }

    [Fact(DisplayName = "중복 시작과 중복 일시정지 뒤에도 재개되며 실행 중 Reset은 거부함")]
    public async Task ConcurrentControl()
    {
        var (engine, _, _) = Create(); await engine.InitializeAsync(default);
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = true;
        engine.StepLogged += x => { if (armed && x.End is null && x.Phase == PhaseKind.Measure) { armed = false; engine.Pause(); engine.Pause(); } };
        engine.SnapshotChanged += x => { if (x.State == EquipmentState.Paused) paused.TrySetResult(); };
        var run = engine.StartAsync(Recipe, 1, default);
        Assert.False((await engine.StartAsync(Recipe, 1, default)).Ok);
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False((await engine.ResetAsync()).Ok);
        engine.Resume(); Assert.True((await run.WaitAsync(TimeSpan.FromSeconds(10))).Ok);
    }

    [Fact(DisplayName = "긴 측정 도중 EMO를 감시해 출력을 끄고 취소함")]
    public async Task EmoDuringMeasurement()
    {
        var (engine, devices, store) = Create(); devices.HoldMeasure = true; await engine.InitializeAsync(default);
        var run = engine.StartAsync(Recipe with { Clock = ClockMode.Realtime, TimeScale = 100 }, 1, default);
        await devices.MeasureEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        devices.EmoActive = true;
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(AlarmCode.Emo, result.Code); Assert.False(devices.OutputOn);
        Assert.Equal(Recipe.ZSafeHeightUm, devices.Z.PositionUm);
        Assert.Single(store.Runs); Assert.Contains("[실패]", store.Runs[0].RecipeName);
    }

    [Fact(DisplayName = "측정 중 Abort는 현재 동작을 취소하고 안전 상태로 감")]
    public async Task AbortDuringMeasurement()
    {
        var (engine, devices, store) = Create(); devices.HoldMeasure = true; await engine.InitializeAsync(default);
        var run = engine.StartAsync(Recipe, 1, default);
        await devices.MeasureEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await engine.AbortAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False((await run).Ok); Assert.False(devices.OutputOn);
        Assert.Equal(EquipmentState.Idle, engine.Snapshot.State);
        Assert.Single(store.Runs); Assert.Contains("[중단]", store.Runs[0].RecipeName);
    }

    [Fact(DisplayName = "로봇 진입과 Z 안전 높이 인터락을 단계 직전에 검사함")]
    public async Task SymmetricInterlocks()
    {
        var (robotEngine, robot, _) = Create(); await robotEngine.InitializeAsync(default); robot.RobotExtendedOverride = true;
        var robotResult = await robotEngine.StartAsync(Recipe, 1, default);
        Assert.Equal(AlarmCode.InterlockBlocked, robotResult.Code);

        var (zEngine, zDevices, _) = Create(); await zEngine.InitializeAsync(default);
        zEngine.StepLogged += x => { if (x.End.HasValue && x.Phase == PhaseKind.LiftZ && x.SiteIndex == 0) zDevices.Z.SetPosition(0); };
        var zResult = await zEngine.StartAsync(Recipe with { Pattern = SitePattern.Standard5 }, 1, default);
        Assert.Equal(AlarmCode.InterlockBlocked, zResult.Code);
        Assert.DoesNotContain(zEngine.Log, x => x.SiteIndex == 1 && x.Phase == PhaseKind.MoveXY);
    }

    [Fact(DisplayName = "진공 해제 전에도 Z 안전 높이를 재검사함")]
    public async Task VacuumOffInterlock()
    {
        var (engine, devices, _) = Create(); await engine.InitializeAsync(default);
        engine.StepLogged += x => { if (x.End.HasValue && x.Phase == PhaseKind.MoveXY && x.SiteIndex is null && x.WaferId == "W001") devices.Z.SetPosition(0); };
        var result = await engine.StartAsync(Recipe, 1, default);
        Assert.Equal(AlarmCode.InterlockBlocked, result.Code);
        Assert.Contains(engine.Snapshot.ActiveAlarms, x => x.Code == AlarmCode.InterlockBlocked && x.Module == ModuleId.Vacuum);
    }

    [Fact(DisplayName = "중첩 비동기 저장도 같은 시드의 단계 시각을 재현함")]
    public async Task OverlapAsyncDeterminism()
    {
        async Task<string[]> Signature()
        {
            var (engine, _, _) = Create(); await engine.InitializeAsync(default);
            Assert.True((await engine.StartAsync(Recipe with { Pattern = SitePattern.Standard9, Save = SaveMode.Async, UnloadOverlap = true, Seed = 9182 }, 3, default)).Ok);
            return engine.Log.Where(x => x.End.HasValue).Select(x => $"{x.WaferId}:{x.Phase}:{x.Start.Ticks}:{x.End!.Value.Ticks}").ToArray();
        }
        var first = await Signature(); var second = await Signature();
        Assert.Equal(first, second);
    }

    [Fact(DisplayName = "중첩 저장 역압 중에도 모듈은 Idle로 보이지 않고 자원 교착 없이 끝남")]
    public async Task OverlapBackpressureNoDeadlock()
    {
        var (engine, _, store) = Create(); await engine.InitializeAsync(default); store.HoldWrites = true;
        ModuleState? duringWait = null;
        engine.StepLogged += x =>
        {
            if (x.End is not null || x.Phase != PhaseKind.Wait || x.WaitReason != "저장 큐 가득") return;
            duringWait = engine.Snapshot.Modules.Single(m => m.Module == ModuleId.Storage).State;
            store.Release();
        };
        var run = engine.StartAsync(Recipe with { Save = SaveMode.Async, UnloadOverlap = true, SaveQueueCapacity = 1, SaveWriteTime = TimeSpan.FromSeconds(3) }, 8, default);
        Assert.True((await run.WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        Assert.NotNull(duringWait);
        Assert.NotEqual(ModuleState.Idle, duringWait);
        Assert.Equal(8, store.Saved.Count);
    }

    [Fact(DisplayName = "비동기 택트는 저장과 겹친 실제 처리 시간을 이중 차감하지 않음")]
    public async Task AsyncTactMatchesHandlingSpan()
    {
        var (engine, _, _) = Create(); await engine.InitializeAsync(default);
        WaferSummary? wafer = null; engine.WaferCompleted += x => wafer = x;
        Assert.True((await engine.StartAsync(Recipe with { Save = SaveMode.Async, SaveWriteTime = TimeSpan.FromSeconds(5) }, 1, default)).Ok);
        Assert.NotNull(wafer);
        var first = engine.Log.First(x => x.End.HasValue && x.Phase == PhaseKind.PickFromSlot).Start;
        var last = engine.Log.Last(x => x.End.HasValue && x.Phase == PhaseKind.ReturnToSlot).End!.Value;
        Assert.True(wafer!.Tact >= last - first, $"택트 {wafer.Tact}, 처리 {last - first}");
    }

    [Fact(DisplayName = "SMU 출력 OFF 실패도 알람을 남기고 Z를 안전 높이로 올림")]
    public async Task OutputOffFailureIsReported()
    {
        var (engine, devices, _) = Create(); await engine.InitializeAsync(default); devices.FailOutputOff = true;
        var result = await engine.StartAsync(Recipe, 1, default);
        Assert.Equal(AlarmCode.SmuTimeout, result.Code);
        Assert.Equal(Recipe.ZSafeHeightUm, devices.Z.PositionUm);
        Assert.Contains(engine.Log, x => x.Phase == PhaseKind.AlarmRecovery && x.End.HasValue);
        var alarms = engine.Snapshot.ActiveAlarms;
        Assert.Equal(alarms.Count, alarms.Select(x => x.Id).Distinct().Count());
        foreach (var alarm in alarms) engine.AcknowledgeAlarm(alarm.Id);
        Assert.True((await engine.ResetAsync()).Ok);
    }

    [Fact(DisplayName = "진공 해제 통신 예외는 CommTimeout으로 분류함")]
    public async Task CommunicationExceptionClassification()
    {
        var (engine, devices, _) = Create(); await engine.InitializeAsync(default); devices.ThrowVacuumOff = true;
        var result = await engine.StartAsync(Recipe, 1, default);
        Assert.Equal(AlarmCode.CommTimeout, result.Code);
        Assert.Contains(engine.Snapshot.ActiveAlarms, x => x.Code == AlarmCode.CommTimeout);
    }

    [Fact(DisplayName = "모듈은 잘못된 전이를 거부하고 알람 상태를 단계 종료로 덮지 않음")]
    public void ModuleTransitionRules()
    {
        var module = new StorageModule();
        Assert.False(module.Set(ModuleState.Paused, "잘못된 전이", TimeSpan.Zero));
        Assert.True(module.Set(ModuleState.Homing, "원점", TimeSpan.Zero));
        Assert.True(module.Set(ModuleState.Idle, "", TimeSpan.Zero));
        module.Enter(false, "저장", TimeSpan.Zero);
        Assert.False(module.Set(ModuleState.Paused, "잘못된 전이", TimeSpan.Zero));
        Assert.True(module.Set(ModuleState.Alarm, "오류", TimeSpan.Zero));
        module.Exit(false, TimeSpan.Zero);
        Assert.Equal(ModuleState.Alarm, module.Status.State);
        Assert.True(module.Set(ModuleState.Uninitialized, "", TimeSpan.Zero));
    }
}
