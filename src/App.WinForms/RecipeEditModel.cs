using System.ComponentModel;
using System.Globalization;
using Contracts;

namespace App.WinForms;

public sealed class RecipeEditModel
{
    public RecipeDto Recipe { get; private set; } = new();
    public RecipeDto Build() => Recipe;
    public void Load(RecipeDto recipe) => Recipe = recipe;
    public void Reset() => Recipe = new RecipeDto();
    private void Set(Func<RecipeDto, RecipeDto> change) => Recipe = change(Recipe);
    public string? Validate()
    {
        var r = Recipe;
        if (r.SchemaVersion != 1) return "SchemaVersion: 지원 버전은 1임";
        if (string.IsNullOrWhiteSpace(r.Name)) return "Name: 이름이 필요함";
        if (!Positive(r.WaferDiameterMm)) return "WaferDiameterMm: 양수여야 함";
        if (!double.IsFinite(r.EdgeExclusionMm) || r.EdgeExclusionMm < 0 || r.EdgeExclusionMm >= r.WaferDiameterMm / 2) return "EdgeExclusionMm: 0 이상, 반지름 미만이어야 함";
        if (r.ReadingsPerSite < 1) return "ReadingsPerSite: 1 이상이어야 함";
        if (!NonNegative(r.ReadingTime)) return "ReadingTimeMs: 0 이상이어야 함";
        if (!Positive(r.SourceCurrentA)) return "SourceCurrentA: 양수여야 함";
        if (!Positive(r.TravelSpeedMmPerS)) return "TravelSpeedMmPerS: 양수여야 함";
        if (!Positive(r.AccelMmPerS2)) return "AccelMmPerS2: 양수여야 함";
        if (!Positive(r.ZApproachSpeedMmPerS)) return "ZApproachSpeedMmPerS: 양수여야 함";
        if (!Positive(r.ZStepUm)) return "ZStepUm: 양수여야 함";
        if (!Positive(r.ZSafeHeightUm)) return "ZSafeHeightUm: 양수여야 함";
        if (!Positive(r.ZLiftAfterMeasureUm)) return "ZLiftAfterMeasureUm: 양수여야 함";
        if (!NonNegative(r.ContactDetectTimeout)) return "ContactDetectTimeoutMs: 0 이상이어야 함";
        if (!double.IsFinite(r.ContactRetryProbability) || r.ContactRetryProbability is < 0 or > 1) return "ContactRetryProbability: 0~1이어야 함";
        if (r.ContactMaxRetry < 0) return "ContactMaxRetry: 0 이상이어야 함";
        if (!NonNegative(r.VacuumCheckWait)) return "VacuumCheckWaitMs: 0 이상이어야 함";
        if (r.VacuumCheckCount < 1) return "VacuumCheckCount: 1 이상이어야 함";
        if (!NonNegative(r.IlluminationWait)) return "IlluminationWaitMs: 0 이상이어야 함";
        if (!NonNegative(r.AutoFocusWait)) return "AutoFocusWaitMs: 0 이상이어야 함";
        if (!NonNegative(r.MapInspectTime)) return "MapInspectTimeMs: 0 이상이어야 함";
        if (!NonNegative(r.RobotMoveTime)) return "RobotMoveTimeMs: 0 이상이어야 함";
        if (!NonNegative(r.AlignTime)) return "AlignTimeMs: 0 이상이어야 함";
        if (r.SaveQueueCapacity < 1) return "SaveQueueCapacity: 1 이상이어야 함";
        if (!NonNegative(r.SaveWriteTime)) return "SaveWriteTimeMs: 0 이상이어야 함";
        if (!Positive(r.TimeScale)) return "TimeScale: 양수여야 함";
        if (r.Pattern == SitePattern.Custom && r.CustomSites.Count == 0) return "CustomSitesText: 사이트가 필요함";
        var radiusUm = (r.WaferDiameterMm / 2 - r.EdgeExclusionMm) * 1000;
        if (r.CustomSites.Any(p => !double.IsFinite(p.XUm) || !double.IsFinite(p.YUm) || Math.Sqrt(p.XUm * p.XUm + p.YUm * p.YUm) > radiusUm)) return "CustomSitesText: 웨이퍼 유효 반경 안이어야 함";
        return null;
    }
    private static bool Positive(double value) => double.IsFinite(value) && value > 0;
    private static bool NonNegative(TimeSpan value) => value >= TimeSpan.Zero;

    [Category("기본"), Description("레시피 형식 버전")]
    public int SchemaVersion { get => Recipe.SchemaVersion; set => Set(r => r with { SchemaVersion = value }); }
    [Category("기본"), Description("레시피 이름")]
    public string Name { get => Recipe.Name; set => Set(r => r with { Name = value }); }
    [Category("기본"), Description("재현 가능한 난수 시드")]
    public int Seed { get => Recipe.Seed; set => Set(r => r with { Seed = value }); }
    [Category("측정 위치"), Description("사이트 배열")]
    public SitePattern Pattern { get => Recipe.Pattern; set => Set(r => r with { Pattern = value }); }
    [Category("측정 위치"), Description("사용자 지정 사이트 좌표 (µm)")]
    public string CustomSitesText
    {
        get => string.Join(";", Recipe.CustomSites.Select(p => $"{p.XUm.ToString(CultureInfo.InvariantCulture)},{p.YUm.ToString(CultureInfo.InvariantCulture)}"));
        set
        {
            var points = new List<SitePoint>();
            foreach (var entry in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var values = entry.Split(',', StringSplitOptions.TrimEntries);
                if (values.Length != 2 || !double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                    !double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
                    !double.IsFinite(x) || !double.IsFinite(y))
                    throw new FormatException("CustomSitesText: X,Y;X,Y 형식이 필요함");
                points.Add(new SitePoint(x, y));
            }
            Set(r => r with { CustomSites = points });
        }
    }
    [Category("측정 위치"), Description("웨이퍼 지름 (mm)")]
    public double WaferDiameterMm { get => Recipe.WaferDiameterMm; set => Set(r => r with { WaferDiameterMm = value }); }
    [Category("측정 위치"), Description("가장자리 제외 폭 (mm)")]
    public double EdgeExclusionMm { get => Recipe.EdgeExclusionMm; set => Set(r => r with { EdgeExclusionMm = value }); }
    [Category("전기 측정"), Description("사이트당 반복 측정 수")]
    public int ReadingsPerSite { get => Recipe.ReadingsPerSite; set => Set(r => r with { ReadingsPerSite = value }); }
    [Category("전기 측정"), Description("1회 측정 시간 (ms)")]
    public double ReadingTimeMs { get => Recipe.ReadingTime.TotalMilliseconds; set => Set(r => r with { ReadingTime = TimeSpan.FromMilliseconds(value) }); }
    [Category("전기 측정"), Description("공급 전류 (A)")]
    public double SourceCurrentA { get => Recipe.SourceCurrentA; set => Set(r => r with { SourceCurrentA = value }); }
    [Category("모션"), Description("이동 속도 (mm/s)")]
    public double TravelSpeedMmPerS { get => Recipe.TravelSpeedMmPerS; set => Set(r => r with { TravelSpeedMmPerS = value }); }
    [Category("모션"), Description("가속도 (mm/s²)")]
    public double AccelMmPerS2 { get => Recipe.AccelMmPerS2; set => Set(r => r with { AccelMmPerS2 = value }); }
    [Category("모션"), Description("Z 접근 방식")]
    public ZApproachMode ZMode { get => Recipe.ZMode; set => Set(r => r with { ZMode = value }); }
    [Category("모션"), Description("Z 접근 속도 (mm/s)")]
    public double ZApproachSpeedMmPerS { get => Recipe.ZApproachSpeedMmPerS; set => Set(r => r with { ZApproachSpeedMmPerS = value }); }
    [Category("모션"), Description("Z 스텝 (µm)")]
    public double ZStepUm { get => Recipe.ZStepUm; set => Set(r => r with { ZStepUm = value }); }
    [Category("모션"), Description("Z 안전 높이 (µm)")]
    public double ZSafeHeightUm { get => Recipe.ZSafeHeightUm; set => Set(r => r with { ZSafeHeightUm = value }); }
    [Category("모션"), Description("측정 후 상승 높이 (µm)")]
    public double ZLiftAfterMeasureUm { get => Recipe.ZLiftAfterMeasureUm; set => Set(r => r with { ZLiftAfterMeasureUm = value }); }
    [Category("모션"), Description("접촉 감지 제한 시간 (ms)")]
    public double ContactDetectTimeoutMs { get => Recipe.ContactDetectTimeout.TotalMilliseconds; set => Set(r => r with { ContactDetectTimeout = TimeSpan.FromMilliseconds(value) }); }
    [Category("모션"), Description("접촉 재시도 확률")]
    public double ContactRetryProbability { get => Recipe.ContactRetryProbability; set => Set(r => r with { ContactRetryProbability = value }); }
    [Category("모션"), Description("최대 접촉 재시도")]
    public int ContactMaxRetry { get => Recipe.ContactMaxRetry; set => Set(r => r with { ContactMaxRetry = value }); }
    [Category("고정 대기"), Description("진공 확인 대기 (ms)")]
    public double VacuumCheckWaitMs { get => Recipe.VacuumCheckWait.TotalMilliseconds; set => Set(r => r with { VacuumCheckWait = TimeSpan.FromMilliseconds(value) }); }
    [Category("고정 대기"), Description("진공 확인 횟수")]
    public int VacuumCheckCount { get => Recipe.VacuumCheckCount; set => Set(r => r with { VacuumCheckCount = value }); }
    [Category("고정 대기"), Description("조명 안정 대기 (ms)")]
    public double IlluminationWaitMs { get => Recipe.IlluminationWait.TotalMilliseconds; set => Set(r => r with { IlluminationWait = TimeSpan.FromMilliseconds(value) }); }
    [Category("고정 대기"), Description("자동 초점 대기 (ms)")]
    public double AutoFocusWaitMs { get => Recipe.AutoFocusWait.TotalMilliseconds; set => Set(r => r with { AutoFocusWait = TimeSpan.FromMilliseconds(value) }); }
    [Category("고정 대기"), Description("맵 검사 방식")]
    public MapInspectMode MapInspect { get => Recipe.MapInspect; set => Set(r => r with { MapInspect = value }); }
    [Category("고정 대기"), Description("맵 검사 시간 (ms)")]
    public double MapInspectTimeMs { get => Recipe.MapInspectTime.TotalMilliseconds; set => Set(r => r with { MapInspectTime = TimeSpan.FromMilliseconds(value) }); }
    [Category("핸들링"), Description("언로딩 중첩")]
    public bool UnloadOverlap { get => Recipe.UnloadOverlap; set => Set(r => r with { UnloadOverlap = value }); }
    [Category("핸들링"), Description("로봇 이동 시간 (ms)")]
    public double RobotMoveTimeMs { get => Recipe.RobotMoveTime.TotalMilliseconds; set => Set(r => r with { RobotMoveTime = TimeSpan.FromMilliseconds(value) }); }
    [Category("핸들링"), Description("정렬 시간 (ms)")]
    public double AlignTimeMs { get => Recipe.AlignTime.TotalMilliseconds; set => Set(r => r with { AlignTime = TimeSpan.FromMilliseconds(value) }); }
    [Category("저장"), Description("동기 또는 비동기 저장")]
    public SaveMode Save { get => Recipe.Save; set => Set(r => r with { Save = value }); }
    [Category("저장"), Description("저장 큐 용량")]
    public int SaveQueueCapacity { get => Recipe.SaveQueueCapacity; set => Set(r => r with { SaveQueueCapacity = value }); }
    [Category("저장"), Description("저장 쓰기 시간 (ms)")]
    public double SaveWriteTimeMs { get => Recipe.SaveWriteTime.TotalMilliseconds; set => Set(r => r with { SaveWriteTime = TimeSpan.FromMilliseconds(value) }); }
    [Category("시계"), Description("실시간 또는 즉시 실행")]
    public ClockMode Clock { get => Recipe.Clock; set => Set(r => r with { Clock = value }); }
    [Category("시계"), Description("실시간 배속")]
    public double TimeScale { get => Recipe.TimeScale; set => Set(r => r with { TimeScale = value }); }
}
