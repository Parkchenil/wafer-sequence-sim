using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Contracts;

namespace Devices;

public interface IDuplexTransport : IAsyncDisposable
{
    Task SendLineAsync(string line, CancellationToken ct);
    Task<string> ReadLineAsync(CancellationToken ct);
}
public sealed class InMemoryTransport : IDuplexTransport
{
    private readonly ChannelReader<string> input;
    private readonly ChannelWriter<string> output;
    private InMemoryTransport(ChannelReader<string> input, ChannelWriter<string> output) { this.input = input; this.output = output; }
    public static (InMemoryTransport Client, InMemoryTransport Server) CreatePair()
    {
        var a = Channel.CreateUnbounded<string>(); var b = Channel.CreateUnbounded<string>();
        return (new(b.Reader, a.Writer), new(a.Reader, b.Writer));
    }
    public Task SendLineAsync(string line, CancellationToken ct) => output.WriteAsync(line, ct).AsTask();
    public Task<string> ReadLineAsync(CancellationToken ct) => input.ReadAsync(ct).AsTask();
    public ValueTask DisposeAsync() { output.TryComplete(); return ValueTask.CompletedTask; }
}
public sealed class TcpTransport(TcpClient client) : IDuplexTransport
{
    private readonly StreamReader reader = new(client.GetStream(), Encoding.ASCII, leaveOpen: true);
    private readonly StreamWriter writer = new(client.GetStream(), Encoding.ASCII, leaveOpen: true) { AutoFlush = true };
    public Task SendLineAsync(string line, CancellationToken ct) => writer.WriteLineAsync(line.AsMemory(), ct);
    public async Task<string> ReadLineAsync(CancellationToken ct) => await reader.ReadLineAsync(ct) ?? throw new IOException("연결 종료");
    public ValueTask DisposeAsync() { reader.Dispose(); writer.Dispose(); client.Dispose(); return ValueTask.CompletedTask; }
    public static async Task<(TcpTransport Client, TcpTransport Server, TcpListener Listener)> CreatePairAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, ct);
        var server = await listener.AcceptTcpClientAsync(ct);
        return (new(client), new(server), listener);
    }
}
public sealed class VirtualSmuServer : IAsyncDisposable
{
    private readonly IDuplexTransport transport;
    private readonly IResistanceField field;
    private readonly IProbePosition probe;
    private readonly TimeProvider clock;
    private readonly Random random;
    private readonly TimeSpan roundTrip;
    private readonly CancellationTokenSource stop = new();
    private readonly Task run;
    private double current;
    private bool outputOn;
    public string CurrentWaferId { get; set; } = "W1";
    public TimeSpan ReadingTime { get; set; }
    public VirtualSmuServer(IDuplexTransport transport, IResistanceField field, IProbePosition probe, TimeProvider clock, Random random, TimeSpan? roundTrip = null)
    { this.transport = transport; this.field = field; this.probe = probe; this.clock = clock; this.random = random; this.roundTrip = roundTrip ?? TimeSpan.FromMilliseconds(5); run = RunAsync(); }
    private async Task RunAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var line = await transport.ReadLineAsync(stop.Token);
                string response = "OK";
                if (line == "*IDN?") response = "VIRTUAL,SMU,1,1";
                else if (line == "*RST") { current = 0; outputOn = false; }
                else if (line.StartsWith(":SOUR:CURR ", StringComparison.Ordinal) && double.TryParse(line[11..], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) current = parsed;
                else if (line == ":OUTP ON") outputOn = true;
                else if (line == ":OUTP OFF") outputOn = false;
                else if (line == ":MEAS:VOLT?")
                {
                    await Task.Delay(ReadingTime, clock, stop.Token);
                    response = outputOn ? field.VoltageSample(CurrentWaferId, probe.XUm, probe.YUm, current, random).ToString("R", CultureInfo.InvariantCulture) : "0";
                }
                else response = "ERR";
                await Task.Delay(roundTrip, clock, stop.Token);
                await transport.SendLineAsync(response, stop.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }
        catch (IOException) { }
    }
    public async ValueTask DisposeAsync() { stop.Cancel(); await transport.DisposeAsync(); try { await run; } catch (OperationCanceledException) { } stop.Dispose(); }
}
public sealed class ScpiSmu(IDuplexTransport transport, VirtualSmuServer? server = null) : ISourceMeasureUnit
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<DeviceResult<string>> QueryAsync(string command, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await transport.SendLineAsync(command, ct);
            var line = await transport.ReadLineAsync(ct);
            return line == "ERR" ? DeviceResult<string>.Fail(AlarmCode.SmuOverRange, "SCPI 명령 오류") : DeviceResult<string>.Success(line);
        }
        catch (IOException) { return DeviceResult<string>.Fail(AlarmCode.SmuTimeout, "SMU 통신 실패"); }
        finally { gate.Release(); }
    }
    public async Task<DeviceResult> ConfigureAsync(double sourceCurrentA, CancellationToken ct)
    {
        if (!double.IsFinite(sourceCurrentA) || Math.Abs(sourceCurrentA) > 1) return DeviceResult.Fail(AlarmCode.SmuOverRange, "전류 설정 범위 초과");
        var result = await QueryAsync(":SOUR:CURR " + sourceCurrentA.ToString("R", CultureInfo.InvariantCulture), ct);
        return result.Ok ? DeviceResult.Success : DeviceResult.Fail(result.Code, result.Message);
    }
    public async Task<DeviceResult> OutputAsync(bool on, CancellationToken ct)
    {
        var result = await QueryAsync(on ? ":OUTP ON" : ":OUTP OFF", ct);
        return result.Ok ? DeviceResult.Success : DeviceResult.Fail(result.Code, result.Message);
    }
    public async Task<DeviceResult<double>> MeasureVoltageAsync(TimeSpan readingTime, CancellationToken ct)
    {
        if (server is not null) server.ReadingTime = readingTime;
        var result = await QueryAsync(":MEAS:VOLT?", ct);
        if (!result.Ok) return DeviceResult<double>.Fail(result.Code, result.Message);
        return double.TryParse(result.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? DeviceResult<double>.Success(value) : DeviceResult<double>.Fail(AlarmCode.SmuOverRange, "전압 응답 범위 초과");
    }
}
