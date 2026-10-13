using Contracts;
namespace Core.Visualization;
public static class ControlText
{
    public static string Probe(EquipmentSnapshot s) => s.ProbeDetail is not { } p ? $"4PP 대기 · Z {s.Motion.ZUm:F1} µm" : $"4PP {Phase(p.Phase)} · Z {p.HeightUm:F1} µm · 접촉 참값 {p.TrueContact} / 마지막 관측 {p.ObservedContact?.ToString() ?? "미확인"}\n샘플 {p.SampleCount}/{p.MinimumSamples} · V {p.LastSampleV?.ToString("F5") ?? "—"} · 편차 {p.DeviationPct?.ToString("F3") ?? "—"}% ≤ {p.LimitPct:F3}%\n접촉 재시도 {p.ContactRetry}/{p.ContactMaxRetry} · 품질 재측정 {p.QualityRetry}/{p.QualityMaxRetry} · {p.Verdict}";
    public static string Phase(ProbePhase p) => p switch { ProbePhase.Approach => "접근", ProbePhase.ContactCheck => "접촉 확인", ProbePhase.Settle => "안정화", ProbePhase.Sampling => "측정", ProbePhase.QualityCheck => "품질 판정", ProbePhase.Lift => "상승", ProbePhase.Rejected => "최종 불량", _ => "대기" };
    public static string Inspector(EquipmentSnapshot s, bool historical = false)
    {
        var interlock = s.Interlock is not { } i ? "아직 요청 없음" : $"요청 {i.Request} · {i.Outcome} · 대기 {i.Elapsed.TotalSeconds:F2}s\n" + string.Join("\n", i.Conditions.Select(c => $"{(c.Passed ? "통과" : "대기/차단")} {c.Name}: {c.Current} / 기준 {c.Required}"));
        var f = s.Faults;
        return $"{(historical ? "재생 기록 · 당시 상태 (현재 장치 아님)\n" : "실시간 상태\n")}완료 {s.Stats.WafersDone}/{s.Stats.WafersTotal} · 실패 {s.Wafers.Count(w => w.Status == WaferStatus.Failed)} · 저널 보존 {s.Wafers.Count(w => w.Status == WaferStatus.Journaled)}\n{Probe(s)}\n\n인터락\n{interlock}\n\n통신 단절 {f?.CommunicationLost} · 접촉 센서 5 s 지연 {f?.SensorDelay}\nDB 쓰기 실패 {f?.DatabaseFailure} · 복구 대기 {f?.JournalPending}건\n{f?.Recovery}\n{f?.JournalStatus}\n저널: {f?.JournalPath}\n보관: 같은 경로.archive-* · 자동 삭제 없음\n\n가상 장애 연습. 분리 반송 영역은 설계 가정이며 물리 충돌 검증이 아님. 품질은 반복 샘플 σ/평균 기준이며 Z 높이로 품질을 예측하지 않음.";
    }
}
