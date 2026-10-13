namespace Contracts;

/// <summary>
/// 시뮬레이션의 유일한 시간원.
///  - Realtime: 벽시계 × TimeScale. Task.Delay(ts, this)는 실제로 ts/TimeScale 만큼 기다린다.
///  - Instant: 벽시계를 쓰지 않는다. 타이머를 "가상 시각 due"로 큐에 넣고, 스레드풀이 조용해질 때마다
///    가장 이른 타이머로 가상 시계를 점프시켜 콜백을 실행한다. 그래서 동시에 진행되는 동작(언로딩 중첩 등)은
///    겹친 만큼만 시간이 흐르고, 같은 입력이면 항상 같은 시각이 찍힌다(결정성).
/// 모든 장치·엔진은 이 인스턴스 하나를 공유한다. DateTime.Now·Stopwatch 사용 금지.
/// </summary>
public sealed class VirtualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly TimeProvider _wall = TimeProvider.System;
    private readonly DateTimeOffset _epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private long _wallStartTicks;
    private TimeSpan _instantNow = TimeSpan.Zero;

    // Instant 모드 타이머 큐: (due, seq)로 정렬 — 같은 시각이면 등록 순서
    private readonly PriorityQueue<(InstantTimer Timer, int Gen), (TimeSpan Due, long Seq)> _queue = new();
    private long _timerSeq;
    private int _pumpRunning; // 0/1

    public ClockMode Mode { get; private set; } = ClockMode.Realtime;
    public double TimeScale { get; private set; } = 1.0;

    public VirtualTimeProvider()
    {
        _wallStartTicks = _wall.GetTimestamp();
    }

    /// <summary>모드·배속 설정. 실행 시작 전에만 호출한다. 가상 시계를 0으로 되돌린다.</summary>
    public void Configure(ClockMode mode, double timeScale)
    {
        lock (_gate)
        {
            Mode = mode;
            TimeScale = timeScale <= 0 ? 1.0 : timeScale;
            _wallStartTicks = _wall.GetTimestamp();
            _instantNow = TimeSpan.Zero;
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
                return TimeSpan.FromTicks((long)(wall.Ticks * TimeScale));
            }
        }
    }

    public override DateTimeOffset GetUtcNow() => _epoch + Elapsed;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Elapsed.Ticks;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (Mode != ClockMode.Instant)
        {
            var scaled = dueTime == Timeout.InfiniteTimeSpan ? dueTime : TimeSpan.FromTicks((long)(dueTime.Ticks / TimeScale));
            var scaledPeriod = period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero ? period : TimeSpan.FromTicks((long)(period.Ticks / TimeScale));
            return _wall.CreateTimer(callback, state, scaled, scaledPeriod);
        }
        var t = new InstantTimer(this, callback, state, period);
        t.Change(dueTime, period);
        return t;
    }

    // 비교 실행은 단일 실행 큐를 먼저 비운 뒤 다음 가상 타이머를 발화한다.
    // 스레드풀의 순간 작업 수를 시간 진행의 근거로 쓰지 않는다.
    public bool IsDeterministic { get; private set; }
    public T RunDeterministic<T>(Func<Task<T>> work)
    {
        if (Mode != ClockMode.Instant) throw new InvalidOperationException("결정적 실행은 Instant 모드 전용");
        if (IsDeterministic) throw new InvalidOperationException("결정적 실행 중복");
        var previous = SynchronizationContext.Current;
        using var context = new SimulationContext();
        IsDeterministic = true;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var task = work();
            while (!task.IsCompleted)
            {
                if (context.TryRun()) continue;
                InstantTimer? timer = null;
                lock (_gate)
                {
                    while (_queue.TryDequeue(out var entry, out var key))
                    {
                        if (entry.Timer.Disposed || entry.Gen != entry.Timer.Generation) continue;
                        if (key.Due > _instantNow) _instantNow = key.Due;
                        timer = entry.Timer; break;
                    }
                }
                if (timer is not null)
                {
                    timer.Fire();
                    if (timer.Period > TimeSpan.Zero && !timer.Disposed) Enqueue(timer, timer.Period);
                }
                else context.Wait();
            }
            return task.GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); IsDeterministic = false; }
    }
    private sealed class SimulationContext : SynchronizationContext, IDisposable
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback Callback, object? State)> work = new();
        private readonly AutoResetEvent signal = new(false);
        public override void Post(SendOrPostCallback callback, object? state) { work.Enqueue((callback,state)); signal.Set(); }
        public bool TryRun() { if (!work.TryDequeue(out var item)) return false; item.Callback(item.State); return true; }
        public void Wait() => signal.WaitOne(10);
        public void Dispose() => signal.Dispose();
    }

    // ───── Instant 모드 스케줄러 ─────

    private void Enqueue(InstantTimer t, TimeSpan dueTime)
    {
        lock (_gate)
        {
            var due = dueTime <= TimeSpan.Zero ? _instantNow : _instantNow + dueTime;
            _queue.Enqueue((t, t.Generation), (due, ++_timerSeq));
        }
        if (IsDeterministic) return;
        if (Interlocked.CompareExchange(ref _pumpRunning, 1, 0) == 0)
            ThreadPool.UnsafeQueueUserWorkItem(_ => Pump(), null);
    }

    /// <summary>스레드풀에 남은 일이 없을 때(= 모든 비동기 흐름이 다음 타이머를 기다리는 상태) 가장 이른 타이머를 발화한다.</summary>
    private void Pump()
    {
        try
        {
            while (true)
            {
                WaitQuiescent();
                InstantTimer t; int gen; TimeSpan due;
                lock (_gate)
                {
                    if (!_queue.TryDequeue(out var entry, out var key))
                    {
                        Volatile.Write(ref _pumpRunning, 0);
                        // 큐가 비는 사이에 새 타이머가 들어왔으면 펌프를 다시 돈다
                        if (_queue.Count > 0 && Interlocked.CompareExchange(ref _pumpRunning, 1, 0) == 0) continue;
                        return;
                    }
                    (t, gen) = entry;
                    due = key.Due;
                    if (t.Disposed || gen != t.Generation) continue; // Change/Dispose로 무효화된 항목
                    if (due > _instantNow) _instantNow = due;
                }
                t.Fire();
                if (t.Period > TimeSpan.Zero && !t.Disposed) Enqueue(t, t.Period);
            }
        }
        catch
        {
            Volatile.Write(ref _pumpRunning, 0);
            throw;
        }
    }

    /// <summary>스레드풀 대기 작업이 두 번 연속 0이면 조용한 것으로 본다. 타이머 콜백 뒤의 await 연속 작업이 끝나길 기다리는 휴리스틱.</summary>
    private static void WaitQuiescent()
    {
        var calm = 0;
        var spins = 0;
        while (calm < 3)
        {
            if (ThreadPool.PendingWorkItemCount == 0) calm++; else calm = 0;
            if (++spins > 200) { Thread.Sleep(0); spins = 0; } else Thread.Yield();
        }
    }

    private sealed class InstantTimer : ITimer
    {
        private readonly VirtualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        public TimeSpan Period { get; private set; }
        public bool Disposed { get; private set; }
        /// <summary>Change가 호출될 때마다 증가. 큐에 남은 옛 항목을 무효화한다.</summary>
        public int Generation;

        public InstantTimer(VirtualTimeProvider owner, TimerCallback callback, object? state, TimeSpan period)
        {
            _owner = owner; _callback = callback; _state = state; Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
        }

        public void Fire() => _callback(_state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (Disposed) return false;
            Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
            lock (_owner._gate) Generation++; // 큐에 남은 옛 항목 무효화
            if (dueTime == Timeout.InfiniteTimeSpan) return true; // 정지
            _owner.Enqueue(this, dueTime);
            return true;
        }

        public void Dispose() { Disposed = true; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
