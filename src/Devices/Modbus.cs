using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Contracts;

namespace Devices;

public interface IByteTransport : IAsyncDisposable
{
    Task SendAsync(byte[] frame, CancellationToken ct);
    Task<byte[]> ReceiveAsync(CancellationToken ct);
}
public sealed class InMemoryByteTransport : IByteTransport
{
    private readonly ChannelReader<byte[]> input;
    private readonly ChannelWriter<byte[]> output;
    private InMemoryByteTransport(ChannelReader<byte[]> input, ChannelWriter<byte[]> output) { this.input = input; this.output = output; }
    public static (InMemoryByteTransport Client, InMemoryByteTransport Server) CreatePair()
    {
        var a = Channel.CreateUnbounded<byte[]>(); var b = Channel.CreateUnbounded<byte[]>();
        return (new(b.Reader, a.Writer), new(a.Reader, b.Writer));
    }
    public Task SendAsync(byte[] frame, CancellationToken ct) => output.WriteAsync(frame, ct).AsTask();
    public Task<byte[]> ReceiveAsync(CancellationToken ct) => input.ReadAsync(ct).AsTask();
    public ValueTask DisposeAsync() { output.TryComplete(); return ValueTask.CompletedTask; }
}
public sealed class TcpByteTransport(TcpClient client) : IByteTransport
{
    private readonly NetworkStream stream = client.GetStream();
    public Task SendAsync(byte[] frame, CancellationToken ct) => stream.WriteAsync(frame, ct).AsTask();
    public async Task<byte[]> ReceiveAsync(CancellationToken ct)
    {
        var head = new byte[6]; await stream.ReadExactlyAsync(head, ct);
        var length = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(4));
        var frame = new byte[6 + length]; head.CopyTo(frame, 0);
        await stream.ReadExactlyAsync(frame.AsMemory(6, length), ct);
        return frame;
    }
    public ValueTask DisposeAsync() { stream.Dispose(); client.Dispose(); return ValueTask.CompletedTask; }
    public static async Task<(TcpByteTransport Client, TcpByteTransport Server, TcpListener Listener)> CreatePairAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        var client = new TcpClient();
        try
        {
            listener.Start();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, ct);
            var server = await listener.AcceptTcpClientAsync(ct);
            listener.Stop();
            return (new(client), new(server), listener);
        }
        catch { client.Dispose(); listener.Stop(); throw; }
    }
}
public sealed class VirtualModbusServer : IAsyncDisposable
{
    private readonly IByteTransport transport;
    private readonly IDigitalIO io;
    private readonly Dictionary<ushort, string> coilMap;
    private readonly Dictionary<ushort, ushort> holding = [];
    private readonly CancellationTokenSource stop = new();
    private readonly Task run;
    public VirtualModbusServer(IByteTransport transport, IDigitalIO io, IReadOnlyDictionary<string, ushort> map)
    { this.transport = transport; this.io = io; coilMap = map.ToDictionary(x => x.Value, x => x.Key); run = RunAsync(); }
    private async Task RunAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            byte[] request;
            try { request = await transport.ReceiveAsync(stop.Token); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) { Console.Error.WriteLine($"Modbus 수신 실패: {ex.Message}"); break; }
            byte[] response;
            try { response = await HandleAsync(request, stop.Token); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Modbus 명령 실패: {ex.Message}");
                response = request.Length >= 8 ? [request[0], request[1], 0, 0, 0, 3, request[6], (byte)(request[7] | 0x80), 4] : [];
            }
            if (response.Length == 0) continue;
            try { await transport.SendAsync(response, stop.Token); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) { Console.Error.WriteLine($"Modbus 응답 실패: {ex.Message}"); break; }
        }
    }
    public async Task<byte[]> HandleAsync(byte[] frame, CancellationToken ct)
    {
        if (frame.Length < 8) return [];
        var fc = frame[7]; var pdu = frame.AsSpan(8).ToArray();
        byte[] result;
        ushort U(int i) => BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(i, 2));
        static byte[] B(ushort value) => [(byte)(value >> 8), (byte)value];
        if ((fc is 1 or 3 or 5 or 6 or 16) && pdu.Length < 4) result = [2];
        else if (fc == 1)
        {
            var start = U(0); var count = U(2);
            if (count == 0 || count > 2000) result = [3];
            else
            {
                var data = new byte[(count + 7) / 8];
                var missing = false;
                for (var i = 0; i < count; i++)
                {
                    if (!coilMap.TryGetValue((ushort)(start + i), out var point)) { missing = true; break; }
                    if ((await io.ReadAsync(point, ct)).Value) data[i / 8] |= (byte)(1 << (i % 8));
                }
                result = missing ? [2] : [(byte)data.Length, ..data];
            }
        }
        else if (fc == 5)
        {
            if (!coilMap.TryGetValue(U(0), out var point) || point.StartsWith("DI_", StringComparison.Ordinal) || U(2) is not (0 or 0xFF00)) result = [2];
            else { var write = await io.WriteAsync(point, U(2) == 0xFF00, ct); result = write.Ok ? pdu[..4] : [4]; }
        }
        else if (fc == 3)
        {
            var start = U(0); var count = U(2);
            if (count == 0 || count > 125) result = [3];
            else { var bytes = new List<byte> { (byte)(count * 2) }; for (var i = 0; i < count; i++) bytes.AddRange(B(holding.GetValueOrDefault((ushort)(start + i)))); result = bytes.ToArray(); }
        }
        else if (fc == 6) { holding[U(0)] = U(2); result = pdu[..4]; }
        else if (fc == 16)
        {
            var start = U(0); var count = U(2);
            if (count == 0 || count > 123 || pdu.Length != 5 + count * 2 || pdu[4] != count * 2) result = [3];
            else { for (var i = 0; i < count; i++) holding[(ushort)(start + i)] = U(5 + i * 2); result = [..B(start), ..B(count)]; }
        }
        else result = [1];
        var error = result.Length == 1 && (fc != 1 || result[0] != 0);
        var responseFc = error ? (byte)(fc | 0x80) : fc;
        var length = 2 + result.Length;
        return [frame[0], frame[1], 0, 0, (byte)(length >> 8), (byte)length, frame[6], responseFc, ..result];
    }
    private int disposed;
    public async ValueTask DisposeAsync() { if (Interlocked.Exchange(ref disposed, 1) != 0) return; stop.Cancel(); await transport.DisposeAsync(); await run; stop.Dispose(); }
}
public sealed class ModbusTcpClient(IByteTransport transport, TimeProvider? clock = null, TimeSpan? timeout = null)
{
    private ushort transaction;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly TimeSpan responseTimeout = timeout ?? TimeSpan.FromSeconds(2);
    private Task<byte[]>? pending;
    public async Task<byte[]> RequestAsync(byte function, byte[] pdu, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var id = ++transaction; var len = pdu.Length + 2;
            await transport.SendAsync([(byte)(id >> 8), (byte)id, 0, 0, (byte)(len >> 8), (byte)len, 1, function, ..pdu], ct);
            while (true)
            {
                pending ??= transport.ReceiveAsync(CancellationToken.None);
                var response = await pending.WaitAsync(responseTimeout, time is VirtualTimeProvider { Mode: ClockMode.Instant } ? TimeProvider.System : time, ct);
                pending = null;
                if (response.Length < 9) throw new IOException("Modbus 응답 길이 오류");
                if (BinaryPrimitives.ReadUInt16BigEndian(response) != id) continue;
                return response[7..];
            }
        }
        finally { gate.Release(); }
    }
    public Task<byte[]> ReadCoilsAsync(ushort start, ushort count, CancellationToken ct) => RequestAsync(1, [..Word(start), ..Word(count)], ct);
    public Task<byte[]> WriteCoilAsync(ushort address, bool value, CancellationToken ct) => RequestAsync(5, [..Word(address), ..Word(value ? (ushort)0xFF00 : (ushort)0)], ct);
    public Task<byte[]> ReadHoldingAsync(ushort start, ushort count, CancellationToken ct) => RequestAsync(3, [..Word(start), ..Word(count)], ct);
    public Task<byte[]> WriteHoldingAsync(ushort address, ushort value, CancellationToken ct) => RequestAsync(6, [..Word(address), ..Word(value)], ct);
    public Task<byte[]> WriteMultipleAsync(ushort start, IReadOnlyList<ushort> values, CancellationToken ct) => RequestAsync(16, [..Word(start), ..Word((ushort)values.Count), (byte)(values.Count * 2), ..values.SelectMany(Word)], ct);
    private static byte[] Word(ushort v) => [(byte)(v >> 8), (byte)v];
}
public sealed class IoMap : Dictionary<string, ushort>
{
    public IoMap(IEnumerable<string> points) { ushort i = 0; foreach (var point in points) this[point] = i++; }
}
public sealed class ModbusDigitalIO(ModbusTcpClient client, IoMap map) : IDigitalIO
{
    public IReadOnlyList<string> Points => map.Keys.ToArray();
    public async Task<DeviceResult<bool>> ReadAsync(string point, CancellationToken ct)
    {
        if (!map.TryGetValue(point, out var address)) return DeviceResult<bool>.Fail(AlarmCode.ConfigError, "IO 주소 없음");
        try { var response = await client.ReadCoilsAsync(address, 1, ct); return response[0] == 1 ? DeviceResult<bool>.Success((response[2] & 1) != 0) : DeviceResult<bool>.Fail(AlarmCode.CommTimeout, "Modbus 예외 응답"); }
        catch (Exception ex) when (ex is IOException or TimeoutException or ChannelClosedException or ObjectDisposedException) { return DeviceResult<bool>.Fail(AlarmCode.CommTimeout, "Modbus 읽기 실패"); }
    }
    public async Task<DeviceResult> WriteAsync(string point, bool value, CancellationToken ct)
    {
        if (!map.TryGetValue(point, out var address)) return DeviceResult.Fail(AlarmCode.ConfigError, "IO 주소 없음");
        if (point.StartsWith("DI_", StringComparison.Ordinal)) return DeviceResult.Fail(AlarmCode.ConfigError, "입력 IO 쓰기 금지");
        try { var response = await client.WriteCoilAsync(address, value, ct); return response[0] == 5 ? DeviceResult.Success : DeviceResult.Fail(AlarmCode.CommTimeout, "Modbus 예외 응답"); }
        catch (Exception ex) when (ex is IOException or TimeoutException or ChannelClosedException or ObjectDisposedException) { return DeviceResult.Fail(AlarmCode.CommTimeout, "Modbus 쓰기 실패"); }
    }
}
