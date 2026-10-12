using Contracts;
using Core.Measurement;

namespace Tests.Measurement;

public sealed class MeasurementTests
{
    [Fact]
    public void 입력목록을바꿔도측정결과가유지됨()
    {
        var calculator = new RsCalculator();
        var voltages = new List<double> { 0.011 };
        var site = calculator.Build("W", 0, 0, 0, voltages, 0.001, 300, TimeSpan.Zero);
        var sites = new List<MeasurementResult> { site };
        var summary = calculator.Summarize("W", sites, TimeSpan.Zero);
        voltages.Clear(); sites.Clear();
        Assert.Single(site.VoltageV);
        Assert.Single(summary.Sites);
        Assert.Equal(1, summary.SiteCount);
    }

    [Fact]
    public async Task 같은실행아이디를다시저장하면이전자식행이사라짐()
    {
        using var store = new SqliteResultStore(":memory:");
        await store.InitializeAsync(CancellationToken.None);
        RunRecord Run(string name) => new("R", DateTimeOffset.UnixEpoch, name, "{}", 1, 1, null, null,
            new Dictionary<PhaseKind, TimeSpan>(), "");
        var calculator = new RsCalculator();
        var site = calculator.Build("W", 0, 0, 0, [0.011], 0.001, 300, TimeSpan.Zero);
        await store.SaveRunAsync(Run("처음"), CancellationToken.None);
        await store.SaveWaferAsync("R", calculator.Summarize("W", [site], TimeSpan.Zero), CancellationToken.None);
        await store.SaveLogAsync("R", [new StepEvent(1, "W", 0, ModuleId.Smu, PhaseKind.Measure,
            TimeSpan.Zero, TimeSpan.FromSeconds(1), false, null, "")], CancellationToken.None);
        await store.SaveRunAsync(Run("다음"), CancellationToken.None);
        Assert.Equal("다음", Assert.Single(await store.ListRunsAsync(CancellationToken.None)).RecipeName);
        Assert.Empty(await store.GetWafersAsync("R", CancellationToken.None));
        Assert.Empty(await store.GetLogAsync("R", CancellationToken.None));
    }

    [Fact]
    public void 가장자리보정이연속이고참값을복원함()
    {
        var calculator = new RsCalculator();
        const double current = 0.001;
        var boundary = 150000 - 4 * 1.59 * 1000;
        var inside = calculator.RsFromVI(0.011, current, boundary + 0.001, 0, 300);
        var outside = calculator.RsFromVI(0.011, current, boundary - 0.001, 0, 300);
        Assert.True(Math.Abs(inside - outside) < 0.00001);
        var field = new ResistanceField(1, noisePct: 0);
        var voltage = field.VoltageSample("W", 148000, 0, current, new Random(1));
        Assert.Equal(field.TrueRsAt("W", 148000, 0), calculator.RsFromVI(voltage, current, 148000, 0, 300), 9);
    }

    [Fact]
    public void 비유한측정값은통계에서제외됨()
    {
        var calculator = new RsCalculator();
        Assert.True(double.IsNaN(calculator.RsFromVI(0.011, 0, 0, 0, 300)));
        Assert.True(double.IsNaN(calculator.RsFromVI(double.PositiveInfinity, 0.001, 0, 0, 300)));
        var valid = calculator.Build("W", 0, 0, 0, [0.011, double.NaN], 0.001, 300, TimeSpan.Zero);
        Assert.Single(valid.VoltageV);
        Assert.Equal(49.852, valid.RsOhmPerSq, 3);
        var invalid = valid with { SiteIndex = 1, RsOhmPerSq = double.NaN };
        var summary = calculator.Summarize("W", [valid, invalid], TimeSpan.Zero);
        Assert.Equal(2, summary.SiteCount);
        Assert.Single(summary.Sites);
        Assert.Equal(valid.RsOhmPerSq, summary.MeanRs);
    }

    [Fact]
    public void 레시피범위를검증함()
    {
        var file = new RecipeFile();
        var invalid = new RecipeDto[]
        {
            new() { SourceCurrentA = 0 },
            new() { WaferDiameterMm = 0 }, new() { ReadingsPerSite = 0 },
            new() { TimeScale = 0 }, new() { SaveQueueCapacity = 0 }
        };
        foreach (var recipe in invalid)
            Assert.Throws<InvalidDataException>(() => file.Deserialize(file.Serialize(recipe)));
        Assert.Throws<InvalidDataException>(() => file.Deserialize("{\"SourceCurrentA\":1e999}"));
    }

    [Fact]
    public void 레시피형식오류와선행버전검사()
    {
        var file = new RecipeFile();
        foreach (var json in new[] { "{bad", "[]", "{\"ReadingTime\":5}", "{\"ReadingTime\":\"abc\"}" })
            Assert.Throws<InvalidDataException>(() => file.Deserialize(json));
        var error = Assert.Throws<InvalidDataException>(() => file.Deserialize("{\"SchemaVersion\":2,\"ReadingTime\":5}"));
        Assert.Contains("버전", error.Message);
    }

    [Fact]
    public void Csv는작은숫자의유효자리수를보존함()
    {
        var site = new MeasurementResult("W", 0, 0, 0, [0.0000112345678], 0.0000012345678,
            50.1234567, 0, TimeSpan.Zero);
        var csv = new CsvExporter().SitesToCsv([site]);
        Assert.Contains("1.12345678E-05", csv);
        Assert.Contains("1.2345678E-06", csv);
    }

    [Fact]
    public void 면저항과가장자리보정()
    {
        var calculator = new RsCalculator();
        Assert.Equal(49.852, calculator.RsFromVI(0.011, 0.001, 0, 0, 300), 3);
        Assert.True(calculator.RsFromVI(0.011, 0.001, 148000, 0, 300) < 49.852);
        var site = calculator.Build("W", 0, 0, 0, [0.010, 0.011, 0.012], 0.001, 300, TimeSpan.Zero);
        Assert.True(site.RsSigma > 0);
        Assert.Equal(1, calculator.Summarize("W", [site], TimeSpan.FromSeconds(1)).SiteCount);
    }

    [Fact]
    public void 저항분포와시드재현()
    {
        var a = new ResistanceField(4);
        var b = new ResistanceField(4);
        var c = new ResistanceField(5);
        Assert.True(a.TrueRsAt("W", 0, 0) < a.TrueRsAt("W", 140000, 0));
        var r1 = new Random(10); var r2 = new Random(10); var r3 = new Random(10);
        var va = Enumerable.Range(0, 5).Select(_ => a.VoltageSample("W", 1000, 0, 0.001, r1)).ToArray();
        var vb = Enumerable.Range(0, 5).Select(_ => b.VoltageSample("W", 1000, 0, 0.001, r2)).ToArray();
        var vc = Enumerable.Range(0, 5).Select(_ => c.VoltageSample("W", 1000, 0, 0.001, r3)).ToArray();
        Assert.Equal(va, vb); Assert.NotEqual(va, vc);
    }

    [Fact]
    public void 표준패턴좌표()
    {
        var provider = new SitePatternProvider();
        var recipe = new RecipeDto();
        var nine = provider.Build(recipe);
        Assert.Equal(9, nine.Count);
        Assert.Equal(new SitePoint(73500, 0), nine[1]);
        Assert.Equal(new SitePoint(0, -132300), nine[8]);
        var fortyNine = provider.Build(recipe with { Pattern = SitePattern.Standard49 });
        Assert.Equal(49, fortyNine.Count);
        Assert.All(fortyNine, p => Assert.True(Math.Sqrt(p.XUm * p.XUm + p.YUm * p.YUm) < 147000));
    }

    [Fact]
    public async Task 메모리저장소왕복()
    {
        using var store = new SqliteResultStore(":memory:");
        await store.InitializeAsync(CancellationToken.None);
        var run = new RunRecord("R", DateTimeOffset.UnixEpoch, "가상", "{}", 1, 2, TimeSpan.FromSeconds(2), 10,
            new Dictionary<PhaseKind, TimeSpan> { [PhaseKind.Measure] = TimeSpan.FromSeconds(1) }, "hash");
        await store.SaveRunAsync(run, CancellationToken.None);
        var calculator = new RsCalculator();
        foreach (var id in new[] { "W1", "W2" })
        {
            var site = calculator.Build(id, 0, 0, 0, [0.011], 0.001, 300, TimeSpan.Zero);
            await store.SaveWaferAsync("R", calculator.Summarize(id, [site], TimeSpan.FromSeconds(1)), CancellationToken.None);
        }
        var log = Enumerable.Range(1, 10).Select(i => new StepEvent(i, "W1", 0, ModuleId.Smu, PhaseKind.Measure,
            TimeSpan.FromMilliseconds(i), TimeSpan.FromMilliseconds(i + 1), false, null, "기록")).ToArray();
        await store.SaveLogAsync("R", log, CancellationToken.None);
        var restoredRun = Assert.Single(await store.ListRunsAsync(CancellationToken.None));
        Assert.Equal(run.RunId, restoredRun.RunId);
        Assert.Equal(run.PhaseTotals[PhaseKind.Measure], restoredRun.PhaseTotals[PhaseKind.Measure]);
        var wafers = await store.GetWafersAsync("R", CancellationToken.None);
        Assert.Equal(2, wafers.Count); Assert.All(wafers, w => Assert.Single(w.Sites));
        Assert.Equal(log, await store.GetLogAsync("R", CancellationToken.None));
    }

    [Fact]
    public void 실행비교부호와정렬()
    {
        RunRecord Run(string id, int measure, int wait) => new(id, DateTimeOffset.UnixEpoch, "", "{}", 1, 1,
            TimeSpan.FromSeconds(measure), measure, new Dictionary<PhaseKind, TimeSpan>
            { [PhaseKind.Measure] = TimeSpan.FromSeconds(measure), [PhaseKind.Wait] = TimeSpan.FromSeconds(wait) }, "");
        var result = new RunComparer().Compare(Run("A", 10, 9), Run("B", 5, 10));
        Assert.Equal(PhaseKind.Measure, result.Phases[0].Phase);
        Assert.Equal(TimeSpan.FromSeconds(-5), result.Phases[0].Delta);
        Assert.Equal(-5, result.UphDelta);
    }

    [Fact]
    public void 레시피직렬화와버전검사()
    {
        var file = new RecipeFile();
        var recipe = new RecipeDto { ReadingTime = TimeSpan.FromMilliseconds(250), Pattern = SitePattern.Standard5 };
        var json = file.Serialize(recipe);
        Assert.Contains("PT0.25S", json);
        var restored = file.Deserialize(json);
        Assert.Equal(recipe.Pattern, restored.Pattern);
        Assert.Equal(recipe.ReadingTime, restored.ReadingTime);
        Assert.Equal(recipe.Seed, restored.Seed);
        Assert.Equal(recipe.ReadingTime, file.Deserialize(json.Replace("PT0.25S", "00:00:00.250")).ReadingTime);
        Assert.Throws<InvalidDataException>(() => file.Deserialize(file.Serialize(recipe with { SchemaVersion = 2 })));
    }

    [Fact]
    public void Csv이스케이프와로그지문()
    {
        var item = new StepEvent(1, "W", 0, ModuleId.Smu, PhaseKind.Measure, TimeSpan.Zero,
            TimeSpan.FromMilliseconds(1), false, null, "메모, \"확인\"");
        Assert.Contains("\"메모, \"\"확인\"\"\"", new CsvExporter().LogToCsv([item]));
        var digest = new LogDigest();
        Assert.Equal(digest.Digest([item]), digest.Digest([item]));
        Assert.NotEqual(digest.Digest([item]), digest.Digest([item with { End = TimeSpan.FromMilliseconds(2) }]));
    }
}
