namespace Contracts;

// ───────────── 측정 데이터 처리·저장·비교 계약 (Core.Measurement가 구현) ─────────────

/// <summary>V/I → Rs 계산과 통계.</summary>
public interface IRsCalculator
{
    /// <summary>얇은 막 근사 Rs = 4.532 · V/I 에 가장자리 보정계수를 곱한다.</summary>
    double RsFromVI(double voltageV, double currentA, double xUm, double yUm, double waferDiameterMm);
    MeasurementResult Build(string waferId, int siteIndex, double xUm, double yUm, IReadOnlyList<double> voltages, double currentA, double waferDiameterMm, TimeSpan at);
    WaferSummary Summarize(string waferId, IReadOnlyList<MeasurementResult> sites, TimeSpan tact);
}

/// <summary>표준 측정 패턴 생성.</summary>
public interface ISitePatternProvider
{
    IReadOnlyList<SitePoint> Build(RecipeDto recipe);
}

/// <summary>실행 이력 저장소. SQLite 구현이 기본. MSSQL·PostgreSQL은 이 인터페이스 뒤에 붙인다(미구현).</summary>
public interface IResultStore
{
    Task InitializeAsync(CancellationToken ct);
    Task SaveRunAsync(RunRecord run, CancellationToken ct);
    Task SaveWaferAsync(string runId, WaferSummary wafer, CancellationToken ct);
    Task SaveLogAsync(string runId, IReadOnlyList<StepEvent> log, CancellationToken ct);
    Task<IReadOnlyList<RunRecord>> ListRunsAsync(CancellationToken ct);
    Task<IReadOnlyList<WaferSummary>> GetWafersAsync(string runId, CancellationToken ct);
    Task<IReadOnlyList<StepEvent>> GetLogAsync(string runId, CancellationToken ct);
}

/// <summary>두 실행의 단계별 시간 차이.</summary>
public interface IRunComparer
{
    RunComparison Compare(RunRecord a, RunRecord b);
}

/// <summary>레시피 JSON 파일 저장·불러오기(스키마 버전 검사 포함).</summary>
public interface IRecipeFile
{
    string Serialize(RecipeDto recipe);
    RecipeDto Deserialize(string json);
    Task SaveAsync(string path, RecipeDto recipe, CancellationToken ct);
    Task<RecipeDto> LoadAsync(string path, CancellationToken ct);
}

/// <summary>CSV 내보내기(웨이퍼 요약·사이트 결과·단계 로그).</summary>
public interface ICsvExporter
{
    string WafersToCsv(IEnumerable<WaferSummary> wafers);
    string SitesToCsv(IEnumerable<MeasurementResult> sites);
    string LogToCsv(IEnumerable<StepEvent> log);
}

/// <summary>로그 지문. 같은 레시피+seed로 다시 돌렸을 때 지문이 같으면 재현된 것이다.</summary>
public interface ILogDigest
{
    string Digest(IEnumerable<StepEvent> log);
}
