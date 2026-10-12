namespace Contracts;

/// <summary>
/// 시뮬레이션의 유일한 시간원.
///  - Realtime: 벽시계 × TimeScale. Task.Delay(ts, this)는 실제로 ts/TimeScale 만큼 기다린다.
///  - Instant: 기다리지 않는다. 타이머가 등록되는 즉시 가상 시간을 그만큼 앞으로 보내고 콜백을 실행한다.
///    여러 동작이 겹칠 때(언로딩 중첩) 가상 시간은 "가장 늦게 끝나는 타이머"까지 단조 증가한다.
/// 모든 장치·엔진은 이 인스턴스 하나를 공유한다. DateTime.Now·Stopwatch 사용 금지.
/// </summary>
public sealed class VirtualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly TimeProvider _wall = TimeProvider.System;
    private readonly DateTimeOffset _epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private long _wallStartTicks;
    private TimeSpan _instantNow = TimeSpan.Zero;
    private TimeSpan _realtimeOffset = TimeSpan.Zero; // Realtime에서 일시정지 보정용

    public ClockMode Mode { get; private set; } = ClockMode.Realtime;
    public double TimeScale { get; private set; } = 1.0;

    public VirtualTimeProvider()
    {
        _wallStartTicks = _wall.GetTimestamp();
    }

    /// <summary>모드·배속 설정. 실행 시작 전에만 호출한다.</summary>
    public void Configure(ClockMode mode, double timeScale)
    {
        lock (_gate)
        {
            Mode = mode;
            TimeScale = timeScale <= 0 ? 1.0 : timeScale;
            _wallStartTicks = _wall.GetTimestamp();
            _instantNow = TimeSpan.Zero;
            _realtimeOffset = TimeSpan.Zero;
        }
    }

    /// <summary>시작 이후 경과한 시뮬레이션 시간.</summary>
    public TimeSpan Elapsed
    {
        get
        {
            lock (_gate)
            {
                if (Mode == ClockMode.Instant) return _instantNow;
                var wall = _wall.GetElapsedTime(_wallStartTicks);
                return TimeSpan.FromTicks((long)(wall.Ticks * TimeScale)) + _realtimeOffset;
            }
        }
    }

    public override DateTimeOffset GetUtcNow() => _epoch + Elapsed;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Elapsed.Ticks;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (Mode == ClockMode.Instant)
        {
            lock (_gate)
            {
                if (dueTime > TimeSpan.Zero) _instantNow += dueTime; // 가상 시간을 앞으로
            }
            return new InstantTimer(callback, state, dueTime);
        }
        var scaled = dueTime == Timeout.InfiniteTimeSpan ? dueTime : TimeSpan.FromTicks((long)(dueTime.Ticks / TimeScale));
        var scaledPeriod = period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero ? period : TimeSpan.FromTicks((long)(period.Ticks / TimeScale));
        return _wall.CreateTimer(callback, state, scaled, scaledPeriod);
    }

    /// <summary>Instant 모드에서 콜백을 즉시(스레드풀에서) 실행하는 타이머.</summary>
    private sealed class InstantTimer : ITimer
    {
        public InstantTimer(TimerCallback callback, object? state, TimeSpan dueTime)
        {
            if (dueTime != Timeout.InfiniteTimeSpan)
                ThreadPool.UnsafeQueueUserWorkItem(_ => callback(state), null);
        }
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
