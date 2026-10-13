using Contracts;
using Core.Measurement;

namespace Tests.Integration;

public sealed class JournalAcceptanceTests
{
    [Fact]
    public async Task 저장완료표식_유실후_전체결과와_최신실행상태를_복원한다()
    {
        var folder = Path.Combine(Path.GetTempPath(), "wafer-lead-ack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        using var db = new SqliteResultStore(Path.Combine(folder, "runs.db"));
        await db.InitializeAsync(default);
        var path = Path.Combine(folder, "active.jsonl");
        var journal = new RecoverableResultStore(db, path) { InjectWriteFailure = true };
        var calculator = new RsCalculator();
        // 시작/종료 업데이트가 여러 개 섞여도 마지막 영속 기록이 우선되어야 한다.
        for (var n = 0; n < 24; n++)
        {
            var id = "R" + n;
            var initial = new RunRecord(id, DateTimeOffset.UnixEpoch, "복구 검증", "{}", 1, 0, null, null, new Dictionary<PhaseKind, TimeSpan>(), "시작");
            var completed = initial with { WaferCount = 1, LogDigest = "완료" };
            var samples = new[] { calculator.Build("W001", 0, 0, 0, new[] { .01, .011, .012 }, .001, 300, TimeSpan.FromSeconds(1)), calculator.Build("W001", 1, 100, 100, new[] { .02, .021, .022 }, .001, 300, TimeSpan.FromSeconds(2)) };
            var wafer = calculator.Summarize("W001", samples, TimeSpan.FromSeconds(5));
            var started = new StepEvent(1, "W001", 0, ModuleId.Smu, PhaseKind.Measure, TimeSpan.Zero, null, false, null, "측정");
            var ended = started with { End = TimeSpan.FromSeconds(2) };
            await journal.SaveRunAsync(initial, default);
            await journal.SaveLogAsync(id, new[] { started }, default);
            await journal.SaveWaferAsync(id, wafer, default);
            await journal.SaveLogAsync(id, new[] { ended }, default);
            await journal.SaveRunAsync(completed, default);
            // DB 반영은 끝났지만 저널 ACK만 남지 않은 중단 시점을 구성한다.
            db.InjectWriteFailure = false;
            await db.FinalizeRunAsync(completed, default);
            await db.SaveWaferAsync(id, wafer, default);
            await db.SaveLogAsync(id, new[] { ended }, default);
        }
        journal = new RecoverableResultStore(db, path);
        await journal.RecoverAsync(default);
        await journal.RecoverAsync(default);
        Assert.Equal(0, journal.PendingCount);
        var runs = await db.ListRunsAsync(default);
        Assert.Equal(24, runs.Count);
        foreach (var run in runs)
        {
            Assert.Equal("완료", run.LogDigest);
            Assert.Equal(1, run.WaferCount);
            var wafer = Assert.Single(await db.GetWafersAsync(run.RunId, default));
            Assert.Equal(2, wafer.Sites.Count);
            Assert.Equal(new[] { .01, .011, .012 }, wafer.Sites.Single(s => s.SiteIndex == 0).VoltageV);
            Assert.Equal(new[] { .02, .021, .022 }, wafer.Sites.Single(s => s.SiteIndex == 1).VoltageV);
            Assert.Equal(TimeSpan.FromSeconds(2), Assert.Single(await db.GetLogAsync(run.RunId, default)).End);
        }
    }
}
