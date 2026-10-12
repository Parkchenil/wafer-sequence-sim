using System.Net.Sockets;
using Contracts;

namespace Devices;

public sealed record DeviceOptions(IoCouplingOptions? IoCoupling = null, double ContactHeightUm = 0, double VisionFailureProbability = 0, TimeSpan? SmuRoundTrip = null);
public sealed class DeviceBundle : IDeviceSet, IAsyncDisposable
{
    private readonly List<IAsyncDisposable> resources = [];
    internal void Add(IAsyncDisposable resource) => resources.Add(resource);
    public required IDigitalIO Io { get; init; }
    public required IAxis StageX { get; init; }
    public required IAxis StageY { get; init; }
    public required IAxis ZAxis { get; init; }
    public required ISourceMeasureUnit Smu { get; init; }
    public required IVision Vision { get; init; }
    public required ILoadPort LoadPort { get; init; }
    public required IRobot Robot { get; init; }
    public required IAligner Aligner { get; init; }
    public required IProbePosition Probe { get; init; }
    public VirtualSmuServer? SmuServer { get; internal set; }
    public VirtualModbusServer? ModbusServer { get; internal set; }
    public async ValueTask DisposeAsync() { foreach (var resource in resources) await resource.DisposeAsync(); }
}
public static class DeviceSetFactory
{
    public static DeviceBundle CreateInMemory(VirtualTimeProvider clock, IResistanceField field, int seed, DeviceOptions? o = null) => Build(clock, field, seed, o, false, false).GetAwaiter().GetResult();
    public static DeviceBundle CreateWithProtocols(VirtualTimeProvider clock, IResistanceField field, int seed, DeviceOptions? o = null) => Build(clock, field, seed, o, true, false).GetAwaiter().GetResult();
    public static Task<DeviceBundle> CreateWithTcp(VirtualTimeProvider clock, IResistanceField field, int seed, DeviceOptions? o = null, CancellationToken ct = default) => Build(clock, field, seed, o, true, true, ct);
    private static async Task<DeviceBundle> Build(VirtualTimeProvider clock, IResistanceField field, int seed, DeviceOptions? o, bool modbus, bool tcp, CancellationToken ct = default)
    {
        o ??= new();
        var physical = new InMemoryDigitalIO(clock, o.IoCoupling);
        var x = new SimulatedAxis("X", clock, -200_000, 200_000);
        var y = new SimulatedAxis("Y", clock, -200_000, 200_000);
        var z = new SimulatedAxis("Z", clock, -10_000, 100_000);
        _ = new ContactSensorCoupling(z, physical, o.ContactHeightUm);
        IDigitalIO io = physical;
        VirtualModbusServer? modbusServer = null;
        var resources = new List<IAsyncDisposable>();
        if (modbus)
        {
            IByteTransport client, server;
            if (tcp)
            {
                var pair = await TcpByteTransport.CreatePairAsync(ct);
                client = pair.Client; server = pair.Server; resources.Add(new ListenerResource(pair.Listener));
            }
            else { var pair = InMemoryByteTransport.CreatePair(); client = pair.Client; server = pair.Server; }
            var map = new IoMap(physical.Points);
            modbusServer = new VirtualModbusServer(server, physical, map);
            io = new ModbusDigitalIO(new ModbusTcpClient(client), map);
            resources.Add(client); resources.Add(modbusServer);
        }
        IDuplexTransport smuClient, smuServerTransport;
        if (tcp)
        {
            var pair = await TcpTransport.CreatePairAsync(ct);
            smuClient = pair.Client; smuServerTransport = pair.Server; resources.Add(new ListenerResource(pair.Listener));
        }
        else { var pair = InMemoryTransport.CreatePair(); smuClient = pair.Client; smuServerTransport = pair.Server; }
        var probe = new ProbePosition(x, y, io);
        var smuServer = new VirtualSmuServer(smuServerTransport, field, probe, clock, new Random(seed), o.SmuRoundTrip);
        var bundle = new DeviceBundle
        {
            Io = io, StageX = x, StageY = y, ZAxis = z, Probe = probe,
            Smu = new ScpiSmu(smuClient, smuServer), SmuServer = smuServer, ModbusServer = modbusServer,
            Vision = new SimulatedVision(clock, new Random(unchecked(seed + 1)), o.VisionFailureProbability),
            LoadPort = new SimulatedLoadPort(io, new WaitHelper(clock), clock),
            Robot = new SimulatedRobot(io, clock), Aligner = new SimulatedAligner(clock)
        };
        bundle.Add(smuClient); bundle.Add(smuServer);
        foreach (var resource in resources) bundle.Add(resource);
        return bundle;
    }
    private sealed class ListenerResource(TcpListener listener) : IAsyncDisposable
    { public ValueTask DisposeAsync() { listener.Stop(); return ValueTask.CompletedTask; } }
}
