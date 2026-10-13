namespace Contracts;

public enum RobotZone { Retracted, IsolatedTransfer, ChuckAccess, Unknown }
public enum ProbePhase { Idle, Approach, ContactCheck, Settle, Sampling, QualityCheck, Lift, Rejected }
public sealed record InterlockCondition(string Name, string Current, string Required, bool Passed);
public sealed record InterlockSnapshot(string Request, IReadOnlyList<InterlockCondition> Conditions, TimeSpan Since, TimeSpan Elapsed, string Outcome);
public sealed record ProbeSnapshot(ProbePhase Phase, double HeightUm, bool TrueContact, bool? ObservedContact, int SampleCount, double? DeviationPct, int ContactRetry, int QualityRetry, double LimitPct, int MinimumSamples, string Verdict)
{
    public int ContactMaxRetry { get; init; }
    public int QualityMaxRetry { get; init; }
    public double? LastSampleV { get; init; }
}
public sealed record FaultSnapshot(bool CommunicationLost, bool SensorDelay, bool DatabaseFailure, string Recovery, int JournalPending, string JournalPath) { public string JournalStatus { get; init; } = ""; }
public interface IStorageRecovery
{
    bool InjectWriteFailure { get; set; }
    int PendingCount { get; }
    string JournalPath { get; }
    string JournalStatus => "저널 정상";
    bool JournalBlocked => false;
    bool IsWaferPending(string runId, string waferId) => PendingCount > 0;
    Task RecoverAsync(CancellationToken ct);
}
public interface IRunFinalizer { Task FinalizeRunAsync(RunRecord run, CancellationToken ct); }
public static class ControlValidation
{
    public static string? Validate(RecipeDto r)
    {
        if (!double.IsFinite(r.ZSafeHeightUm) || r.ZSafeHeightUm <= 0 || !double.IsFinite(r.ZApproachSpeedMmPerS) || r.ZApproachSpeedMmPerS <= 0 || !double.IsFinite(r.AccelMmPerS2) || r.AccelMmPerS2 <= 0) return "모션 높이·속도·가속도는 유한한 양수여야 함";
        if (!double.IsFinite(r.SourceCurrentA) || r.SourceCurrentA == 0 || r.ReadingTime < TimeSpan.Zero) return "측정 전류(A)는 유한한 0 이외 값, 읽기 시간은 0 이상이어야 함";
        if (!double.IsFinite(r.ContactRetryProbability) || r.ContactRetryProbability is < 0 or > 1 || r.ContactDetectTimeout <= TimeSpan.Zero) return "접촉 실패 확률은 0~1, 확인 시간은 양수여야 함";
        if (!double.IsFinite(r.QualityTolerancePct) || r.QualityTolerancePct < 0) return "품질 허용 편차(%)는 0 이상의 유한수여야 함";
        if (r.QualityMinSamples < 2 || r.QualityMinSamples > 1000) return "품질 최소 샘플은 2~1000개여야 함";
        if (r.ReadingsPerSite < 1 || r.ReadingsPerSite > 1000 || r.QualityEnabled && r.ReadingsPerSite < r.QualityMinSamples) return "반복 샘플 수가 품질 최소 샘플보다 적음";
        if (r.ContactMaxRetry is < 0 or > 20 || r.QualityMaxRetry is < 0 or > 20) return "재시도 한도는 0~20회임";
        if (r.ProbeSettleTime < TimeSpan.Zero || r.ProbeSettleTime > TimeSpan.FromMinutes(1)) return "접촉 안정화는 0~60000 ms임";
        if (!double.IsFinite(r.TravelSpeedMmPerS) || r.TravelSpeedMmPerS <= 0 || !double.IsFinite(r.ZLiftAfterMeasureUm) || r.ZLiftAfterMeasureUm < r.ZSafeHeightUm) return "이송 속도는 양수, Z 상승은 안전 높이 이상이어야 함";
        return null;
    }
}
