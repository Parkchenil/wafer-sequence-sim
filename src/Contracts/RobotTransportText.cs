namespace Contracts;

public static class RobotTransportText
{
    public static string Describe(RobotTransportTelemetry? state)
    {
        if (state is null) return "반송 상태 없음";
        return $"반송 {state.Subphase} {state.Progress:P0} · 위치 ({state.XMetres * 1000:F1}, {state.YMetres * 1000:F1}, {state.ZMetres * 1000:F1}) mm · 슬롯 {(state.Slot?.ToString() ?? "—")} · 웨이퍼 {(state.HoldingWafer ? "보유" : "미보유")} · 얼라이너 {(state.AlignerOccupied ? "재석" : "비어 있음")}" + (state.WaitingFor is null ? "" : $" · 대기 {state.WaitingFor}");
    }
}
