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
        var listener = new TcpListener(IPAddress.Loopback, 0);
        var client = new TcpClient();
        try
        {
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            await client.ConnectAsync(IPAddress.Loopback, port, ct);
            var server = await listener.AcceptTcpClientAsync(ct);
            listener.Stop();
            return (new(client), new(server), listener);
        }
        catch { client.Dispose(); listener.Stop(); throw; }
    }
}
public sealed class VirtualSmuServer : IAsyncDisposable
{
    private readonly IDuplexTransport transport;
    private readonly IResistanceField field;
    private readonly IProbePosition probe;
    private readonly TimeProvider clock;
    private Random random;
    private readonly TimeSpan roundTrip;
    private readonly CancellationTokenSource stop = new();
    private readonly Task run;
    private double current;
    private bool outputOn;
    private int disposed;
    public string CurrentWaferId { get; set; } = "W1";
    public TimeSpan ReadingTime { get; private set; }
    public void Reseed(int seed) => random = new Random(seed);
    public VirtualSmuServer(IDuplexTransport transport, IResistanceField field, IProbePosition probe, TimeProvider clock, Random random, TimeSpan? roundTrip = null)
    { this.transport = transport; this.field = field; this.probe = probe; this.clock = clock; this.random = random; this.roundTrip = roundTrip ?? TimeSpan.FromMilliseconds(5); run = RunAsync(); }
    private async Task RunAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            string line;
            try { line = await transport.ReadLineAsync(stop.Token); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) { Console.Error.WriteLine($"SMU 수신 실패: {ex.Message}"); break; }
            string response;
            try
            {
                response = "OK";
                if (line == "*IDN?") response = "VIRTUAL,SMU,1,1";
                else if (line == "*RST") { current = 0; outputOn = false; }
                else if (line.StartsWith(":SOUR:CURR ", StringComparison.Ordinal) && double.TryParse(line[11..], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) current = parsed;
                else if (line.StartsWith(":SENS:TIME ", StringComparison.Ordinal) && double.TryParse(line[11..], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0) ReadingTime = TimeSpan.FromSeconds(seconds);
                else if (line == ":OUTP ON") outputOn = true;
                else if (line == ":OUTP OFF") outputOn = false;
                else if (line == ":MEAS:VOLT?")
                {
                    if (!outputOn) response = "ERR:OUTPUT_OFF";
                    else { await Task.Delay(ReadingTime, clock, stop.Token); response = field.VoltageSample(CurrentWaferId, probe.XUm, probe.YUm, current, random).ToString("R", CultureInfo.InvariantCulture); }
                }
                else response = "ERR:COMMAND";
                await Task.Delay(roundTrip, clock, stop.Token);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) { Console.Error.WriteLine($"SMU 명령 실패: {ex.Message}"); response = "ERR:SERVER"; }
            try { await transport.SendLineAsync(response, stop.Token); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) { Console.Error.WriteLine($"SMU 응답 실패: {ex.Message}"); break; }
        }
    }
    public async ValueTask DisposeAsync()
    { if (Interlocked.Exchange(ref disposed, 1) != 0) return; stop.Cancel(); await transport.DisposeAsync(); await run; stop.Dispose(); }
}
public sealed class ScpiSmu(IDuplexTransport transport, TimeProvider? clock = null, TimeSpan? timeout = null) : ISourceMeasureUnit
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly TimeSpan responseTimeout = timeout ?? TimeSpan.FromSeconds(2);
    private Task<string>? pending;
    private int staleResponses;
    public Task<DeviceResult<string>> QueryAsync(string command, CancellationToken ct) => QueryAsync(command, TimeSpan.Zero, ct);
    /// <summary>extraWait: 서버가 응답 전에 쓰는 시간(측정 시간 등). 응답 타임아웃에 더한다 — 측정 시간이 타임아웃보다 길어도 가짜 타임아웃이 나지 않게.</summary>
    public async Task<DeviceResult<string>> QueryAsync(string command, TimeSpan extraWait, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        var sent = false;
        try
        {
            await transport.SendLineAsync(command, ct);
            sent = true;
            while (true)
            {
                pending ??= transport.ReadLineAsync(CancellationToken.None);
                var line = await pending.WaitAsync(responseTimeout + extraWait, time is VirtualTimeProvider { Mode: ClockMode.Instant } ? TimeProvider.System : time, ct);
                pending = null;
                if (staleResponses > 0) { staleResponses--; continue; }
                if (line == "ERR:SERVER") return DeviceResult<string>.Fail(AlarmCode.SmuTimeout, "SMU 서버 측정 실패");
                if (line == "ERR:OUTPUT_OFF") return DeviceResult<string>.Fail(AlarmCode.SmuOverRange, "SMU 출력이 꺼져 있어 측정할 수 없음");
                if (line.StartsWith("ERR", StringComparison.Ordinal)) return DeviceResult<string>.Fail(AlarmCode.SmuOverRange, "SCPI 명령 오류: " + line);
                return DeviceResult<string>.Success(line);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { if (sent && pending is not null) staleResponses++; throw; } // 전송 전 취소면 응답도 없다
        catch (TimeoutException) { if (pending is not null) staleResponses++; return DeviceResult<string>.Fail(AlarmCode.SmuTimeout, "SMU 응답 시간 초과"); }
        catch (Exception ex) when (ex is IOException or ChannelClosedException or ObjectDisposedException) { return DeviceResult<string>.Fail(AlarmCode.SmuTimeout, "SMU 통신 실패"); }
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
        var setting = await QueryAsync(":SENS:TIME " + readingTime.TotalSeconds.ToString("R", CultureInfo.InvariantCulture), ct);
        if (!setting.Ok) return DeviceResult<double>.Fail(setting.Code, setting.Message);
        var result = await QueryAsync(":MEAS:VOLT?", readingTime, ct);
        if (!result.Ok) return DeviceResult<double>.Fail(result.Code, result.Message);
        return double.TryParse(result.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? DeviceResult<double>.Success(value) : DeviceResult<double>.Fail(AlarmCode.SmuOverRange, "전압 응답 범위 초과");
    }
}
