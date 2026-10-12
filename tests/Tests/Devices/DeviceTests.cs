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
        var axis = new SimulatedAxis("X", Clock(), -100_000, 100_000);
        Assert.Equal(AlarmCode.AxisNotHomed, (await axis.MoveAbsoluteAsync(10, 100, 500, TimeSpan.FromSeconds(2), default)).Code);
        await axis.ServoOnAsync(default); await axis.HomeAsync(TimeSpan.FromSeconds(1), default);
        Assert.Equal(AlarmCode.AxisLimit, (await axis.MoveAbsoluteAsync(100_001, 100, 500, TimeSpan.FromSeconds(2), default)).Code);
        Assert.True((await axis.MoveAbsoluteAsync(100_000, 100, 500, TimeSpan.FromSeconds(2), default)).Ok);
        Assert.Equal(100_000, axis.PositionUm, 3);
    }
    [Fact]
    public async Task 대기_시간초과_알람()
    {
        var result = await new WaitHelper(Clock()).WaitForAsync(_ => Task.FromResult(false), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(30), AlarmCode.ContactTimeout, "접촉", default);
        Assert.Equal(AlarmCode.ContactTimeout, result.Code);
        Assert.Contains("00:00:00.0300000", result.Message);
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
        catch (System.Net.Sockets.SocketException) { return; }
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
    private sealed class FixedField : IResistanceField
    {
        public double TrueRsAt(string waferId, double xUm, double yUm) => 100;
        public double VoltageSample(string waferId, double xUm, double yUm, double currentA, Random random) => .123;
    }
}
