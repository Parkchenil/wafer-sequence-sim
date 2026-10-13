using Contracts;
using Devices;

namespace Tests.Devices;

public class DeviceTests
{
    private static VirtualTimeProvider Clock()
    { var clock = new VirtualTimeProvider(); clock.Configure(ClockMode.Instant, 1); return clock; }
    [Fact]
    public async Task 축_프로파일과_오류()
    {
        Assert.Equal(1.2, SimulatedAxis.TravelTime(100_000, 100, 500).TotalSeconds, 6);
        Assert.Equal(2 * Math.Sqrt(.001 / 500), SimulatedAxis.TravelTime(1, 100, 500).TotalSeconds, 6);
        var clock = Clock();
        var axis = new SimulatedAxis("X", clock, -100_000, 100_000);
        Assert.Equal(AlarmCode.AxisNotHomed, (await axis.MoveAbsoluteAsync(10, 100, 500, TimeSpan.FromSeconds(2), default)).Code);
        await axis.ServoOnAsync(default); await axis.HomeAsync(TimeSpan.FromSeconds(1), default);
        Assert.Equal(AlarmCode.AxisLimit, (await axis.MoveAbsoluteAsync(100_001, 100, 500, TimeSpan.FromSeconds(2), default)).Code);
        var before = clock.Elapsed;
        Assert.True((await axis.MoveAbsoluteAsync(100_000, 100, 500, TimeSpan.FromSeconds(2), default)).Ok);
        Assert.Equal(TimeSpan.FromSeconds(1.2), clock.Elapsed - before);
        Assert.Equal(100_000, axis.PositionUm, 3);
    }
    [Fact]
    public async Task 대기_시간초과_알람()
    {
        var clock = Clock();
        var result = await new WaitHelper(clock).WaitForAsync(_ => Task.FromResult(false), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(30), AlarmCode.ContactTimeout, "접촉", default);
        Assert.Equal(AlarmCode.ContactTimeout, result.Code);
        Assert.Contains("00:00:00.0300000", result.Message);
        Assert.Equal(TimeSpan.FromMilliseconds(30), clock.Elapsed);
    }
    [Fact]
    public async Task 진공과_접촉_커플링()
    {
        var clock = Clock(); var io = new InMemoryDigitalIO(clock, new(TimeSpan.FromMilliseconds(400)));
        io.Force(IoPoints.ChuckWaferPresent, true);
        await io.WriteAsync(IoPoints.VacChuckOn, true, default);
        Assert.False((await io.ReadAsync(IoPoints.VacChuckSense, default)).Value);
        await Task.Delay(TimeSpan.FromMilliseconds(400), clock);
        Assert.True((await io.ReadAsync(IoPoints.VacChuckSense, default)).Value);
        await io.WriteAsync(IoPoints.VacChuckOn, false, default);
        io.Force(IoPoints.ChuckWaferPresent, false);
        await io.WriteAsync(IoPoints.VacChuckOn, true, default);
        await Task.Delay(TimeSpan.FromMilliseconds(400), clock);
        Assert.False((await io.ReadAsync(IoPoints.VacChuckSense, default)).Value);
        var z = new SimulatedAxis("Z", clock, -1000, 1000); _ = new ContactSensorCoupling(z, io);
        await z.ServoOnAsync(default); await z.HomeAsync(TimeSpan.FromSeconds(1), default);
        Assert.True((await io.ReadAsync(IoPoints.ProbeContact, default)).Value);
        await z.MoveAbsoluteAsync(100, 1, 10, TimeSpan.FromSeconds(2), default);
        Assert.False((await io.ReadAsync(IoPoints.ProbeContact, default)).Value);
    }
    [Fact]
    public async Task 스카피_인메모리_측정()
    {
        await using var bundle = DeviceSetFactory.CreateInMemory(Clock(), new FixedField(), 7);
        var smu = (ScpiSmu)bundle.Smu;
        Assert.Contains("VIRTUAL", (await smu.QueryAsync("*IDN?", default)).Value);
        await smu.ConfigureAsync(.001, default); await smu.OutputAsync(true, default);
        Assert.Equal(.123, (await smu.MeasureVoltageAsync(TimeSpan.FromMilliseconds(10), default)).Value, 6);
    }
    [Fact]
    public async Task 스카피_TCP_측정()
    {
        DeviceBundle bundle;
        try { bundle = await DeviceSetFactory.CreateWithTcp(Clock(), new FixedField(), 7); }
        catch (System.Net.Sockets.SocketException) when (Environment.GetEnvironmentVariable("SIM_SKIP_TCP") == "1") { throw Xunit.Sdk.SkipException.ForSkip("TCP 포트 바인딩 제한"); }
        await using (bundle)
        {
            Assert.Contains("VIRTUAL", (await ((ScpiSmu)bundle.Smu).QueryAsync("*IDN?", default)).Value);
            await bundle.Smu.ConfigureAsync(.001, default);
            await bundle.Smu.OutputAsync(true, default);
            Assert.Equal(.123, (await bundle.Smu.MeasureVoltageAsync(TimeSpan.FromMilliseconds(10), default)).Value, 6);
        }
    }
    [Fact]
    public async Task 모드버스_코일과_레지스터_왕복()
    {
        var pair = InMemoryByteTransport.CreatePair(); var io = new InMemoryDigitalIO(Clock());
        await using var server = new VirtualModbusServer(pair.Server, io, new IoMap(io.Points));
        await using var clientTransport = pair.Client;
        var client = new ModbusTcpClient(clientTransport);
        Assert.Equal(16, (await client.WriteMultipleAsync(10, [123, 456], default))[0]);
        var holding = await client.ReadHoldingAsync(10, 2, default);
        Assert.Equal(new byte[] { 3, 4, 0, 123, 1, 200 }, holding);
        Assert.Equal(5, (await client.WriteCoilAsync(1, true, default))[0]);
        Assert.Equal(1, (await client.ReadCoilsAsync(1, 1, default))[1]);
        Assert.Equal(1, (await client.ReadCoilsAsync(1, 1, default))[2]);
        Assert.Equal(0xE3, (await client.RequestAsync(0x63, [], default))[0]);
    }
    [Fact]
    public async Task 로봇_빈손_배치_거부()
    {
        var robot = new SimulatedRobot(new InMemoryDigitalIO(Clock()), Clock());
        Assert.Equal(AlarmCode.RobotCollisionRisk, (await robot.PlaceOnChuckAsync(TimeSpan.FromSeconds(1), default)).Code);
    }
    [Fact]
    public async Task 장치_실행맥락이_측정에_반영됨()
    {
        await using var bundle = DeviceSetFactory.CreateInMemory(Clock(), new ContextField(), 3);
        var context = (IDeviceContext)bundle;
        await bundle.StageX.ServoOnAsync(default); await bundle.StageX.HomeAsync(TimeSpan.FromSeconds(1), default);
        await bundle.StageX.MoveAbsoluteAsync(1000, 10, 100, TimeSpan.FromSeconds(2), default);
        await bundle.Smu.ConfigureAsync(.002, default); await bundle.Smu.OutputAsync(true, default);
        context.SetCurrentWafer("W123"); context.Reseed(11);
        var first = (await bundle.Smu.MeasureVoltageAsync(TimeSpan.Zero, default)).Value;
        context.Reseed(11);
        var again = (await bundle.Smu.MeasureVoltageAsync(TimeSpan.Zero, default)).Value;
        Assert.Equal(first, again);
        context.Reseed(12);
        var newSeed = (await bundle.Smu.MeasureVoltageAsync(TimeSpan.Zero, default)).Value;
        Assert.NotEqual(first, newSeed);
        context.SetCurrentWafer("W999"); context.Reseed(11);
        Assert.NotEqual(first, (await bundle.Smu.MeasureVoltageAsync(TimeSpan.Zero, default)).Value);
    }
    [Fact]
    public async Task 입력_IO_쓰기와_미등록_포인트_거부()
    {
        var io = new InMemoryDigitalIO(Clock());
        Assert.Equal(AlarmCode.ConfigError, (await io.WriteAsync(IoPoints.Emo, true, default)).Code);
        Assert.Equal(AlarmCode.ConfigError, (await io.ReadAsync("DI_UNKNOWN", default)).Code);
    }
    [Fact]
    public async Task 모드버스_취소뒤_응답_재동기화()
    {
        var pair = InMemoryByteTransport.CreatePair(); var io = new InMemoryDigitalIO(Clock());
        await using var server = new VirtualModbusServer(pair.Server, io, new IoMap(io.Points));
        await using var clientTransport = pair.Client;
        var client = new ModbusTcpClient(clientTransport);
        using var stop = new CancellationTokenSource();
        var first = client.ReadCoilsAsync(1, 1, stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var next = await client.ReadCoilsAsync(1, 1, default);
        Assert.Equal(1, next[0]);
    }
    [Fact]
    public async Task 이동중_정지와_비상정지()
    {
        var clock = new VirtualTimeProvider();
        var io = new InMemoryDigitalIO(clock);
        var axis = new SimulatedAxis("X", clock, -200_000, 200_000, io);
        await axis.ServoOnAsync(default); await axis.HomeAsync(TimeSpan.FromSeconds(1), default);
        var moving = axis.MoveAbsoluteAsync(100_000, 1, 1, TimeSpan.FromMinutes(3), default);
        await axis.StopAsync(default);
        Assert.Equal(AlarmCode.AxisStopped, (await moving).Code);
        io.Force(IoPoints.Emo, true);
        Assert.Equal(AlarmCode.Emo, (await axis.MoveAbsoluteAsync(1000, 10, 100, TimeSpan.FromSeconds(2), default)).Code);
    }
    [Fact]
    public async Task 홈_이동_시간과_타임아웃()
    {
        var clock = Clock();
        var axis = new SimulatedAxis("Z", clock, -1000, 2000, homeUm: 1000);
        await axis.ServoOnAsync(default);
        var expected = SimulatedAxis.TravelTime(1000, 100, 500);
        Assert.Equal(AlarmCode.AxisTimeout, (await axis.HomeAsync(TimeSpan.FromTicks(expected.Ticks / 2), default)).Code);
        Assert.True(axis.PositionUm > 0 && axis.PositionUm < 1000);
        Assert.True((await axis.HomeAsync(TimeSpan.FromSeconds(1), default)).Ok);
        Assert.Equal(1000, axis.PositionUm);
    }
    [Fact]
    public async Task 로봇_인계_순간과_슬롯_높이()
    {
        var clock = Clock(); var io = new InMemoryDigitalIO(clock);
        var port = new SimulatedLoadPort(io, new WaitHelper(clock), clock, [1, 10]);
        var robot = new SimulatedRobot(io, clock, io, port);
        var states = new List<RobotTransportTelemetry>();
        robot.TransportChanged += states.Add;
        Assert.True((await robot.PickFromSlotAsync(10, TimeSpan.FromSeconds(1), default)).Ok);
        Assert.Contains(states, x => x.Subphase == "상승 접촉" && !x.HoldingWafer);
        Assert.Contains(states, x => x.Subphase == "보유 확인" && x.HoldingWafer);
        Assert.Equal(RobotGeometry.SlotBottom(10), robot.Transport.YMetres, 6);
        Assert.Equal(-.07, robot.Transport.ZMetres, 6);
        Assert.True((await robot.PlaceOnAlignerAsync(TimeSpan.FromSeconds(1), default)).Ok);
        Assert.Contains(states, x => x.Subphase == "하강 인계" && x.HoldingWafer);
        Assert.False(robot.HoldingWafer);
        Assert.True(robot.Transport.AlignerOccupied);
    }
    [Fact]
    public async Task 슬롯_점유와_로봇_상태()
    {
        var clock = Clock(); var io = new InMemoryDigitalIO(clock);
        var loadPort = new SimulatedLoadPort(io, new WaitHelper(clock), clock, [1]);
        var robot = new SimulatedRobot(io, clock, io, loadPort);
        Assert.Equal(AlarmCode.RobotCollisionRisk, (await robot.PickFromSlotAsync(2, TimeSpan.Zero, default)).Code);
        Assert.True((await robot.PickFromSlotAsync(1, TimeSpan.Zero, default)).Ok);
        Assert.False(loadPort.HasWafer(1));
        Assert.True((await robot.ReturnToSlotAsync(1, TimeSpan.Zero, default)).Ok);
        Assert.True(loadPort.HasWafer(1));
    }
    [Fact]
    public async Task SMU_출력꺼짐과_서버오류뒤_복구()
    {
        var field = new RecoveringField();
        await using var bundle = DeviceSetFactory.CreateInMemory(Clock(), field, 1);
        Assert.Equal(AlarmCode.SmuOverRange, (await bundle.Smu.MeasureVoltageAsync(TimeSpan.Zero, default)).Code);
        await bundle.Smu.ConfigureAsync(.001, default); await bundle.Smu.OutputAsync(true, default);
        Assert.Equal(AlarmCode.SmuTimeout, (await bundle.Smu.MeasureVoltageAsync(TimeSpan.Zero, default)).Code);
        Assert.True((await bundle.Smu.MeasureVoltageAsync(TimeSpan.Zero, default)).Ok);
        await bundle.DisposeAsync();
    }
    [Fact]
    public async Task 모드버스_무응답_시간초과()
    {
        var pair = InMemoryByteTransport.CreatePair();
        await using var clientTransport = pair.Client;
        await using var serverTransport = pair.Server;
        var io = new ModbusDigitalIO(new ModbusTcpClient(clientTransport, TimeProvider.System, TimeSpan.FromMilliseconds(20)), new IoMap(new InMemoryDigitalIO(Clock()).Points));
        Assert.Equal(AlarmCode.CommTimeout, (await io.ReadAsync(IoPoints.LampOn, default)).Code);
    }
    [Theory]
    [InlineData(1)] [InlineData(13)] [InlineData(25)]
    public async Task 슬롯_인계면과_전체시간이_일치한다(int slot)
    {
        var clock=Clock();var io=new InMemoryDigitalIO(clock);
        var port=new SimulatedLoadPort(io,new WaitHelper(clock),clock,[slot]);
        var robot=new SimulatedRobot(io,clock,io,port);
        var transitions=new List<RobotTransportTelemetry>();var previous=false;
        robot.TransportChanged+=s=>{if(s.HoldingWafer!=previous){transitions.Add(s);previous=s.HoldingWafer;}};
        var before=clock.Elapsed;var duration=TimeSpan.FromTicks(10000003);
        Assert.True((await robot.PickFromSlotAsync(slot,duration,default)).Ok);
        Assert.Equal(duration,clock.Elapsed-before);
        var pickup=Assert.Single(transitions);
        Assert.Equal(-.69,pickup.XMetres,8);Assert.Equal(.28,pickup.ZMetres,8);
        Assert.Equal(RobotGeometry.SlotBottom(slot),pickup.YMetres+RobotGeometry.PadOffset,8);
        Assert.False(port.HasWafer(slot));
        Assert.True((await robot.PlaceOnAlignerAsync(duration,default)).Ok);
        Assert.Equal(2,transitions.Count);var deposit=transitions[1];
        Assert.Equal(-.12,deposit.XMetres,8);Assert.Equal(.34,deposit.ZMetres,8);
        Assert.Equal(RobotGeometry.AlignerBottom,deposit.YMetres+RobotGeometry.PadOffset,8);
        Assert.True(deposit.AlignerOccupied);Assert.False(robot.Extended);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 취소는_마지막위치와_인계전소유권을_보존한다(bool placing)
    {
        var clock=Clock();var io=new InMemoryDigitalIO(clock);
        var port=new SimulatedLoadPort(io,new WaitHelper(clock),clock,[1]);
        var robot=new SimulatedRobot(io,clock,io,port);
        if(placing)await robot.PickFromSlotAsync(1,TimeSpan.FromSeconds(1),default);
        using var stop=new CancellationTokenSource();RobotTransportTelemetry? last=null;
        robot.TransportChanged+=s=>{if(s.Subphase=="블레이드 진입" && s.Progress>.40 && !stop.IsCancellationRequested){last=s;stop.Cancel();}};
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>placing?robot.PlaceOnAlignerAsync(TimeSpan.FromSeconds(1),stop.Token):robot.PickFromSlotAsync(1,TimeSpan.FromSeconds(1),stop.Token));
        Assert.NotNull(last);Assert.Equal(last!.XMetres,robot.Transport.XMetres);Assert.Equal(last.YMetres,robot.Transport.YMetres);Assert.Equal(last.ZMetres,robot.Transport.ZMetres);
        Assert.Equal(placing,robot.HoldingWafer);Assert.Equal(!placing,port.HasWafer(1));Assert.False(robot.Transport.AlignerOccupied);
        Assert.True(robot.Extended);Assert.True((await io.ReadAsync(IoPoints.RobotExtended,default)).Value);
    }
    private sealed class RecoveringField : IResistanceField
    {
        private int calls;
        public double TrueRsAt(string waferId, double xUm, double yUm) => 0;
        public double VoltageSample(string waferId, double xUm, double yUm, double currentA, Random random)
        { if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("가상 측정 오류"); return .25; }
    }
    private sealed class ContextField : IResistanceField
    {
        public double TrueRsAt(string waferId, double xUm, double yUm) => 0;
        public double VoltageSample(string waferId, double xUm, double yUm, double currentA, Random random)
            => (waferId == "W123" ? 1 : 2) + xUm / 1000 + currentA * 1000 + random.NextDouble();
    }
    private sealed class FixedField : IResistanceField
    {
        public double TrueRsAt(string waferId, double xUm, double yUm) => 100;
        public double VoltageSample(string waferId, double xUm, double yUm, double currentA, Random random) => .123;
    }
}
