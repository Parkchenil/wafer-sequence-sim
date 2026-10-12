using Contracts;
using Devices;
using Xunit.Abstractions;

namespace Tests.Integration;

/// <summary>실시간 모드(배속)에서 IO 커플링·대기 헬퍼가 가상 시계와 맞물리는지 확인한다.</summary>
public sealed class RealtimeProbeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task 실시간_배속에서_도어가_열린다()
    {
        var clock = new VirtualTimeProvider();
        clock.Configure(ClockMode.Realtime, 50);
        var io = new InMemoryDigitalIO(clock, null);
        var wait = new WaitHelper(clock);
        var port = new SimulatedLoadPort(io, wait, clock);
        var t0 = clock.Elapsed;
        var sw = System.Diagnostics.Stopwatch.StartNew(); // 테스트 진단용 벽시계
        var result = await port.OpenAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        output.WriteLine($"결과={result.Ok} {result.Message} 가상경과={clock.Elapsed - t0} 벽시계={sw.Elapsed}");
        Assert.True(result.Ok, result.Message);
    }

    [Fact]
    public async Task 실시간_배속에서_Delay가_배속만큼_빨리_끝난다()
    {
        var clock = new VirtualTimeProvider();
        clock.Configure(ClockMode.Realtime, 50);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var before = clock.Elapsed;
        await Task.Delay(TimeSpan.FromMilliseconds(500), clock);
        var after = clock.Elapsed;
        output.WriteLine($"가상 {after - before} 벽시계 {sw.Elapsed}");
        Assert.True(after - before >= TimeSpan.FromMilliseconds(400), $"가상 경과 {after - before}");
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(300), $"벽시계 {sw.Elapsed}");
    }
}
