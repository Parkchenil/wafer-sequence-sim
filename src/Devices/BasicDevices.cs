using Contracts;

namespace Devices;

public sealed class WaitHelper(TimeProvider clock) : IWaitHelper
{
    public async Task<DeviceResult> WaitForAsync(Func<CancellationToken, Task<bool>> condition, TimeSpan pollInterval, TimeSpan timeout, AlarmCode onTimeout, string what, CancellationToken ct)
    {
        // 경과는 명목 폴링 간격의 합이 아니라 시계로 잰다 — 배속·타이머 해상도 때문에 Delay가 요청보다 짧게 돌아올 수 있다
        var start = clock.GetTimestamp();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (await condition(ct)) return DeviceResult.Success;
            var elapsed = clock.GetElapsedTime(start);
            if (elapsed >= timeout) return DeviceResult.Fail(onTimeout, $"{what} 대기 시간 초과 ({timeout})");
            var step = pollInterval > TimeSpan.Zero ? pollInterval : TimeSpan.FromMilliseconds(1);
            step = TimeSpan.FromTicks(Math.Min(step.Ticks, (timeout - elapsed).Ticks));
            await Task.Delay(step, clock, ct);
        }
    }
}

public sealed record IoCouplingOptions(TimeSpan? VacuumSettle = null, TimeSpan? DoorSettle = null)
{
    public TimeSpan VacuumDelay => VacuumSettle ?? TimeSpan.FromMilliseconds(400);
    public TimeSpan DoorDelay => DoorSettle ?? TimeSpan.FromMilliseconds(800);
}

public sealed class InMemoryDigitalIO : IDigitalIO
{
    private readonly Dictionary<string, bool> values;
    private readonly TimeProvider clock;
    private readonly IoCouplingOptions options;
    private readonly object gate = new();
    private long vacuumDue, doorDue;
    public IReadOnlyList<string> Points { get; } = [IoPoints.VacChuckOn, IoPoints.LampOn, IoPoints.LoadPortDoorOpen, IoPoints.LoadPortClamp, IoPoints.VacChuckSense, IoPoints.ProbeContact, IoPoints.LoadPortWaferPresent, IoPoints.LoadPortDoorOpened, IoPoints.RobotExtended, IoPoints.ChuckWaferPresent, IoPoints.Emo];
    public InMemoryDigitalIO(TimeProvider clock, IoCouplingOptions? options = null)
    {
        this.clock = clock; this.options = options ?? new();
        values = Points.ToDictionary(p => p, _ => false);
    }
    public Task<DeviceResult<bool>> ReadAsync(string point, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (values[IoPoints.VacChuckOn] && clock.GetTimestamp() >= vacuumDue) values[IoPoints.VacChuckSense] = values[IoPoints.ChuckWaferPresent];
            if (values[IoPoints.LoadPortDoorOpen] && clock.GetTimestamp() >= doorDue) values[IoPoints.LoadPortDoorOpened] = true;
            return Task.FromResult(values.TryGetValue(point, out var value) ? DeviceResult<bool>.Success(value) : DeviceResult<bool>.Fail(AlarmCode.ConfigError, $"알 수 없는 IO 포인트: {point}"));
        }
    }
    public Task<DeviceResult> WriteAsync(string point, bool value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!values.ContainsKey(point)) return Task.FromResult(DeviceResult.Fail(AlarmCode.ConfigError, $"알 수 없는 IO 포인트: {point}"));
            if (point.StartsWith("DI_", StringComparison.Ordinal)) return Task.FromResult(DeviceResult.Fail(AlarmCode.ConfigError, $"입력 IO 쓰기 금지: {point}"));
            values[point] = value;
            if (point == IoPoints.VacChuckOn)
            {
                vacuumDue = clock.GetTimestamp() + options.VacuumDelay.Ticks;
                if (!value) values[IoPoints.VacChuckSense] = false;
            }
            if (point == IoPoints.LoadPortDoorOpen)
            {
                doorDue = clock.GetTimestamp() + options.DoorDelay.Ticks;
                if (!value) values[IoPoints.LoadPortDoorOpened] = false;
            }
        }
        return Task.FromResult(DeviceResult.Success);
    }
    public void Force(string point, bool value)
    {
        lock (gate)
        {
            if (!values.ContainsKey(point)) throw new ArgumentException("알 수 없는 IO 포인트", nameof(point));
            values[point] = value;
            if (point == IoPoints.ChuckWaferPresent && !value) values[IoPoints.VacChuckSense] = false;
        }
    }
}

public sealed class SimulatedAxis : IAxis
{
    private readonly TimeProvider clock;
    private readonly double minUm, maxUm;
    private readonly IDigitalIO? io;
    private readonly double homeUm;
    private readonly object gate = new();
    private CancellationTokenSource? motion;
    private double position;
    private bool servo, homed, moving;
    public string Name { get; }
    public double PositionUm { get { lock (gate) return position; } }
    public event Action<double>? PositionChanged;
    public SimulatedAxis(string name, TimeProvider clock, double minUm, double maxUm, IDigitalIO? io = null, double homeUm = 0)
    { Name = name; this.clock = clock; this.minUm = minUm; this.maxUm = maxUm; this.io = io; this.homeUm = Math.Clamp(homeUm, minUm, maxUm); }
    public Task<DeviceResult> ServoOnAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); lock (gate) servo = true; return Task.FromResult(DeviceResult.Success); }
    public async Task<DeviceResult> HomeAsync(TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!servo) return DeviceResult.Fail(AlarmCode.ServoOff, "서보 꺼짐 상태");
        var start = PositionUm;
        var total = TravelTime(homeUm - start, 100, 500);
        var allowed = TimeSpan.FromTicks(Math.Min(Math.Max(0, timeout.Ticks), total.Ticks));
        CancellationTokenSource own;
        lock (gate) { motion?.Cancel(); motion = own = CancellationTokenSource.CreateLinkedTokenSource(ct); moving = true; homed = false; }
        var started = clock.GetTimestamp();
        try
        {
            var steps = Math.Clamp((int)Math.Ceiling(allowed.TotalMilliseconds / 33), 2, 200);
            for (var i = 1; i <= steps; i++)
            {
                var previous = allowed.Ticks * (i - 1) / steps;
                var next = allowed.Ticks * i / steps;
                await Task.Delay(TimeSpan.FromTicks(next - previous), clock, own.Token);
                if (io is not null && (await io.ReadAsync(IoPoints.Emo, own.Token)).Value) return DeviceResult.Fail(AlarmCode.Emo, "비상 정지 입력");
                lock (gate) position = start + (homeUm - start) * (total == TimeSpan.Zero ? 1 : ProfileDistance(next / (double)TimeSpan.TicksPerSecond, total.TotalSeconds, Math.Abs(homeUm - start) / 1000, 100, 500));
                PositionChanged?.Invoke(PositionUm);
            }
            var remaining = allowed - clock.GetElapsedTime(started);
            if (remaining > TimeSpan.Zero) await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(remaining.TotalMilliseconds)), clock, own.Token);
            if (allowed < total) return DeviceResult.Fail(AlarmCode.AxisTimeout, "홈 이동 시간 초과");
            lock (gate) homed = true;
            return DeviceResult.Success;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return DeviceResult.Fail(AlarmCode.AxisStopped, "홈 이동 정지"); }
        finally { lock (gate) { if (ReferenceEquals(motion, own)) { moving = false; motion = null; } } own.Dispose(); }
    }
    public static TimeSpan TravelTime(double distanceUm, double speedMmPerS, double accelMmPerS2)
    {
        if (speedMmPerS <= 0 || accelMmPerS2 <= 0 || !double.IsFinite(distanceUm)) return TimeSpan.Zero;
        var d = Math.Abs(distanceUm) / 1000;
        var ramp = speedMmPerS * speedMmPerS / accelMmPerS2;
        return TimeSpan.FromSeconds(d <= ramp ? 2 * Math.Sqrt(d / accelMmPerS2) : d / speedMmPerS + speedMmPerS / accelMmPerS2);
    }
    public Task<DeviceResult> MoveRelativeAsync(double deltaUm, double speedMmPerS, double accelMmPerS2, TimeSpan timeout, CancellationToken ct) => MoveAbsoluteAsync(PositionUm + deltaUm, speedMmPerS, accelMmPerS2, timeout, ct);
    public async Task<DeviceResult> MoveAbsoluteAsync(double targetUm, double speedMmPerS, double accelMmPerS2, TimeSpan timeout, CancellationToken ct)
    {
        if (targetUm < minUm || targetUm > maxUm || !double.IsFinite(targetUm)) return DeviceResult.Fail(AlarmCode.AxisLimit, "소프트 리밋 초과");
        if (!homed) return DeviceResult.Fail(AlarmCode.AxisNotHomed, "홈 미완료");
        if (!servo) return DeviceResult.Fail(AlarmCode.ServoOff, "서보 꺼짐");
        if (speedMmPerS <= 0 || accelMmPerS2 <= 0) return DeviceResult.Fail(AlarmCode.AxisLimit, "속도 또는 가속도 오류");
        var start = PositionUm;
        var total = TravelTime(targetUm - start, speedMmPerS, accelMmPerS2);
        var allowed = timeout < total ? TimeSpan.FromTicks(Math.Max(0, timeout.Ticks)) : total;
        CancellationTokenSource own;
        lock (gate) { motion?.Cancel(); motion = own = CancellationTokenSource.CreateLinkedTokenSource(ct); moving = true; }
        var started = clock.GetTimestamp();
        try
        {
            var steps = Math.Clamp((int)Math.Ceiling(allowed.TotalMilliseconds / 33), 2, 200);
            for (var i = 1; i <= steps; i++)
            {
                var next = TimeSpan.FromTicks(allowed.Ticks * i / steps);
                var prev = TimeSpan.FromTicks(allowed.Ticks * (i - 1) / steps);
                await Task.Delay(next - prev, clock, own.Token);
                if (io is not null && (await io.ReadAsync(IoPoints.Emo, own.Token)).Value) return DeviceResult.Fail(AlarmCode.Emo, "비상 정지 입력");
                lock (gate) position = start + (targetUm - start) * (total == TimeSpan.Zero ? 1 : ProfileDistance(next.TotalSeconds, total.TotalSeconds, Math.Abs(targetUm - start) / 1000, speedMmPerS, accelMmPerS2));
                PositionChanged?.Invoke(PositionUm);
            }
            var remaining = allowed - clock.GetElapsedTime(started);
            if (remaining > TimeSpan.Zero) await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(remaining.TotalMilliseconds)), clock, own.Token);
            return allowed < total ? DeviceResult.Fail(AlarmCode.AxisTimeout, "축 이동 시간 초과") : DeviceResult.Success;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return DeviceResult.Fail(AlarmCode.AxisStopped, "축 이동 정지"); }
        finally { lock (gate) { if (ReferenceEquals(motion, own)) { moving = false; motion = null; } } own.Dispose(); }
    }
    private static double ProfileDistance(double t, double total, double d, double v, double a)
    {
        if (d == 0) return 1;
        var ramp = Math.Min(v / a, total / 2);
        var peak = a * ramp;
        var travelled = t <= ramp ? a * t * t / 2 : t < total - ramp ? a * ramp * ramp / 2 + peak * (t - ramp) : d - a * (total - t) * (total - t) / 2;
        return Math.Clamp(travelled / d, 0, 1);
    }
    public Task<DeviceResult> StopAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); lock (gate) motion?.Cancel(); return Task.FromResult(DeviceResult.Success); }
    public Task<DeviceResult<AxisStatus>> GetStatusAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate) return Task.FromResult(DeviceResult<AxisStatus>.Success(new(position, servo, homed, !moving, position >= maxUm, position <= minUm, moving)));
    }
}

public sealed class ContactSensorCoupling
{
    private volatile bool inContact;
    /// <summary>마지막으로 계산된 접촉 상태. 스냅샷·애니메이션은 IO를 다시 읽지 않고 이 값을 본다(프로토콜 경로에서 동기 읽기 금지).</summary>
    public bool InContact => inContact;
    public ContactSensorCoupling(SimulatedAxis z, InMemoryDigitalIO io, double contactHeightUm = 0)
    {
        z.PositionChanged += p => { inContact = p <= contactHeightUm; io.Force(IoPoints.ProbeContact, inContact); };
        inContact = z.PositionUm <= contactHeightUm;
        io.Force(IoPoints.ProbeContact, inContact);
    }
}
/// <summary>프로브 위치 공급원. 접촉 상태는 커플링 캐시에서 읽는다 — IO(특히 Modbus) 동기 읽기는 스냅샷 경로를 막는다.</summary>
public sealed class ProbePosition(IAxis x, IAxis y, ContactSensorCoupling contact) : IProbePosition
{
    public double XUm => x.PositionUm;
    public double YUm => y.PositionUm;
    public bool InContact => contact.InContact;
}
