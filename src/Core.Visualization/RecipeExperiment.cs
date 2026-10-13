using Contracts;
using Core.Sequence;
using Core.Measurement;
using Devices;
namespace Core.Visualization;

public sealed record ExperimentResult(string Name, bool Completed, string Outcome, int Good, int Failed, int Total, double Seconds, double? Wph, double TransferSeconds, double WaitSeconds, double MeasureSeconds, double RetryRate, string Digest, bool QualityEnabled)
{
    public IReadOnlyList<StepEvent> Steps { get; init; } = [];
}
public sealed class RecipeExperiment
{
    private int running;
    public RecipeDto? Baseline { get; private set; }
    public int WaferCount { get; private set; }
    public void Capture(RecipeDto recipe, int count)
    {
        if (Volatile.Read(ref running) != 0) throw new InvalidOperationException("비교 진행 중");
        if (ControlValidation.Validate(recipe) is { } error) throw new ArgumentException(error);
        Baseline = recipe with { CustomSites = Array.AsReadOnly(recipe.CustomSites.ToArray()) }; WaferCount = count;
    }
    public async Task<(ExperimentResult A, ExperimentResult B)> CompareAsync(RecipeDto candidate, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0) throw new InvalidOperationException("비교 진행 중");
        try
        {
            var baseline = Baseline ?? throw new InvalidOperationException("기준 레시피를 먼저 캡처해야 함");
            // 작업량·난수 조건을 고정하고 모션·대기·재시도·중첩 조건만 비교한다.
            candidate = candidate with { Seed = baseline.Seed, Pattern = baseline.Pattern, CustomSites = baseline.CustomSites, WaferDiameterMm = baseline.WaferDiameterMm, EdgeExclusionMm = baseline.EdgeExclusionMm };
            return (await Run(baseline, WaferCount, ct), await Run(candidate, WaferCount, ct));
        }
        finally { Volatile.Write(ref running, 0); }
    }
    private static Task<ExperimentResult> Run(RecipeDto recipe, int count, CancellationToken ct) => Task.Run(() =>
    {
        var clock = new VirtualTimeProvider(); clock.Configure(ClockMode.Instant, 1);
        return clock.RunDeterministic(() => RunCore(clock, recipe, count, ct));
    }, ct);
    private static async Task<ExperimentResult> RunCore(VirtualTimeProvider clock, RecipeDto recipe, int count, CancellationToken ct)
    {
        await using var devices = DeviceSetFactory.CreateInMemory(clock, new ResistanceField(recipe.Seed), recipe.Seed);
        var engine = new SequenceEngine(devices, new WaitHelper(clock), clock, new RsCalculator(), new SitePatternProvider(), null, new LogDigest());
        var init = await engine.InitializeAsync(ct);
        var result = init.Ok ? await engine.StartAsync(recipe with { Clock = ClockMode.Instant }, count, ct) : init;
        ct.ThrowIfCancellationRequested();
        var snapshot = engine.Snapshot;
        var steps = engine.Log.Where(e => e.End.HasValue && !e.IsDetail).GroupBy(e => e.Seq).Select(g => g.Last()).ToArray();
        double Sum(Func<StepEvent, bool> predicate) => steps.Where(predicate).Sum(e => e.Duration!.Value.TotalSeconds);
        var sites = steps.Where(e => e.SiteIndex.HasValue && e.Phase == PhaseKind.DescendZ).Select(e => (e.WaferId,e.SiteIndex)).Distinct().Count();
        var retried = steps.Where(e => e.Phase == PhaseKind.DescendZ && e.Note.StartsWith("재시도") || e.Phase == PhaseKind.Measure && e.Note == "읽기 1" && steps.Count(x => x.WaferId == e.WaferId && x.SiteIndex == e.SiteIndex && x.Phase == PhaseKind.Measure && x.Note == "읽기 1") > 1).Select(e => (e.WaferId,e.SiteIndex)).Distinct().Count();
        var seconds = snapshot.SimTime.TotalSeconds;
        return new(recipe.Name, result.Ok, result.Ok ? "완료" : result.Message, snapshot.Stats.WafersDone, snapshot.Wafers.Count(w => w.Status == WaferStatus.Failed), count, seconds,
            result.Ok && seconds > 0 ? snapshot.Stats.WafersDone * 3600 / seconds : null,
            Sum(e => e.Module is ModuleId.Robot or ModuleId.Stage or ModuleId.ZHead && !e.IsWait), Sum(e => e.IsWait), Sum(e => e.Phase == PhaseKind.Measure), sites == 0 ? 0 : (double)retried / sites, new LogDigest().Digest(steps), recipe.QualityEnabled) { Steps = Array.AsReadOnly(steps) };
    }
    public static string Describe(ExperimentResult a, ExperimentResult b) => $"고정 항목: wafer 수 / site 패턴·좌표 / seed / wafer 직경 / edge 제외 폭\n동일 wafer/site/seed · 독립 가상 엔진\nWPH = 완료 장수 × 3600 / 실제 로트 경과(초)\n시간은 모듈별 합계이며 중첩 시 경과시간보다 클 수 있음\n\n{One("기준", a)}\n\n{One("변경", b)}";
    private static string One(string title, ExperimentResult r) => $"{title} {r.Name}: {r.Outcome}\n완료 {r.Good}/{r.Total} · {(r.QualityEnabled ? "양품 " + r.Good : "품질 미판정")} · 실패 {r.Failed}\n로트 {r.Seconds:F2}s · 실적 WPH {(r.Wph.HasValue ? r.Wph.Value.ToString("F2") : "— (불완료)")}\n이송 {r.TransferSeconds:F2}s · 대기 {r.WaitSeconds:F2}s · 측정 {r.MeasureSeconds:F2}s\n재시도 사이트 비율 {r.RetryRate:P1}";
}
