using Contracts;
using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace App.WinForms;

internal static class DrawingMath
{
    public static PointF SiteToPixel(SitePoint site, RectangleF wafer, double diameterMm)
    {
        var scale = wafer.Width / (diameterMm * 1000);
        return new PointF(wafer.Left + wafer.Width / 2 + (float)(site.XUm * scale), wafer.Top + wafer.Height / 2 - (float)(site.YUm * scale));
    }
    public static float TimeToPixel(TimeSpan time, TimeSpan start, TimeSpan window, float left, float width) => left + (float)((time - start).TotalSeconds / window.TotalSeconds * width);
    public static Color Interpolate(Color low, Color high, double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        return Color.FromArgb((int)(low.R + (high.R - low.R) * fraction), (int)(low.G + (high.G - low.G) * fraction), (int)(low.B + (high.B - low.B) * fraction));
    }
    public static Color PhaseColor(PhaseKind phase) => phase switch
    {
        PhaseKind.Measure => Color.FromArgb(55, 185, 156),
        PhaseKind.MoveXY or PhaseKind.DescendZ or PhaseKind.LiftZ => Color.FromArgb(85, 154, 223),
        PhaseKind.VacuumCheck or PhaseKind.Illumination or PhaseKind.AutoFocus => Color.FromArgb(220, 168, 77),
        PhaseKind.Save or PhaseKind.HostReport => Color.FromArgb(170, 129, 220),
        _ => Color.FromArgb(111, 132, 155)
    };
}

internal abstract class BufferedPanel : Panel
{
    protected BufferedPanel() { DoubleBuffered = true; BackColor = Color.FromArgb(25, 32, 43); ForeColor = Color.WhiteSmoke; }
    protected static void Label(Graphics g, string text, Font font, Brush brush, float x, float y) => g.DrawString(text, font, brush, x, y);
}

internal sealed class SchematicPanel : BufferedPanel
{
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public EquipmentSnapshot? Snapshot { get; set; }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double WaferDiameterMm { get; set; } = 300;
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        var s = Snapshot; if (s is null) return;
        Label(g, "평면도  /  TOP VIEW", Font, Brushes.LightGray, 16, 10);
        var h = Height - 70;
        var waferSize = Math.Min(210, Math.Max(100, h - 35));
        var wafer = new RectangleF(Width * .47f - waferSize / 2, 36, waferSize, waferSize);
        using var surface = new SolidBrush(Color.FromArgb(49, 62, 77));
        g.FillEllipse(surface, wafer); g.DrawEllipse(Pens.SlateGray, wafer);
        g.DrawLine(Pens.DimGray, wafer.Left + wafer.Width / 2, wafer.Top, wafer.Left + wafer.Width / 2, wafer.Bottom);
        g.DrawLine(Pens.DimGray, wafer.Left, wafer.Top + wafer.Height / 2, wafer.Right, wafer.Top + wafer.Height / 2);
        var diameter = WaferDiameterMm;
        for (var i = 0; i < s.Sites.Count; i++)
        {
            var p = DrawingMath.SiteToPixel(s.Sites[i], wafer, diameter);
            var done = s.Wafers.FirstOrDefault(w => w.WaferId == s.CurrentWaferId)?.SitesDone ?? 0;
            using var b = new SolidBrush(i < done ? Color.MediumAquamarine : i == s.CurrentSiteIndex ? Color.Gold : Color.SlateGray);
            g.FillEllipse(b, p.X - 4, p.Y - 4, 8, 8);
        }
        var stage = DrawingMath.SiteToPixel(new SitePoint(s.Motion.StageXUm, s.Motion.StageYUm), wafer, diameter);
        g.DrawLine(Pens.Orange, stage.X - 9, stage.Y, stage.X + 9, stage.Y);
        g.DrawLine(Pens.Orange, stage.X, stage.Y - 9, stage.X, stage.Y + 9);
        Label(g, "LOAD PORT", Font, Brushes.LightGray, 16, 48);
        for (var i = 0; i < Math.Min(8, Math.Max(5, s.Wafers.Count)); i++)
        {
            var y = 75 + i * 19;
            g.DrawRectangle(Pens.SlateGray, 17, y, 76, 12);
            if (i < s.Wafers.Count && s.Wafers[i].Location == WaferLocation.LoadPort) g.FillRectangle(Brushes.LightSkyBlue, 20, y + 3, 70, 6);
        }
        var armY = wafer.Top + wafer.Height / 2;
        using var armPen = new Pen(Color.CornflowerBlue, 8);
        g.DrawLine(armPen, 110, armY, s.Motion.RobotExtended ? wafer.Left - 5 : 155, armY);
        Label(g, "ROBOT", Font, Brushes.LightGray, 111, armY + 12);
        var ax = wafer.Right + 33;
        g.DrawEllipse(Pens.CornflowerBlue, ax, 75, 60, 60);
        g.DrawLine(Pens.Gold, ax + 30, 75, ax + 30, 85);
        Label(g, "ALIGNER", Font, Brushes.LightGray, ax, 145);
        var sideY = Math.Min(Height - 85, wafer.Bottom + 12);
        Label(g, "측면도  /  SIDE VIEW", Font, Brushes.LightGray, 16, sideY);
        var zX = Width * .55f;
        using var head = new SolidBrush(s.Motion.ProbeInContact ? Color.OrangeRed : Color.SteelBlue);
        g.FillRectangle(head, zX, sideY + 20 + (float)Math.Clamp(300 - s.Motion.ZUm, 0, 300) / 15, 78, 12);
        g.DrawLine(Pens.SlateGray, zX - 12, sideY + 60, zX + 95, sideY + 60);
        var flags = new[] { ("진공", s.Motion.VacuumOn), ("확인", s.Motion.VacuumConfirmed), ("조명", s.Motion.LampOn), ("초점", s.Motion.CameraFocused), ("도어", s.Motion.LoadPortDoorOpen), ("EMO", s.ActiveAlarms.Any(a => a.Code == AlarmCode.Emo)) };
        var flagY = Height - 26;
        for (var i = 0; i < flags.Length; i++)
        {
            var x = 14 + i * 68;
            using var light = new SolidBrush(flags[i].Item2 ? (i == 5 ? Color.OrangeRed : Color.MediumAquamarine) : Color.DimGray);
            g.FillEllipse(light, x, flagY + 3, 9, 9);
            Label(g, flags[i].Item1, Font, Brushes.LightGray, x + 12, flagY);
        }
    }
}

internal sealed class GanttPanel : BufferedPanel
{
    private readonly ToolTip _tip = new();
    private readonly List<(RectangleF Rect, StepEvent Event)> _hits = new();
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<StepEvent> Steps { get; set; } = Array.Empty<StepEvent>();
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public TimeSpan Now { get; set; }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int WindowSeconds { get; set; } = 60;
    public GanttPanel() { MouseMove += Hover; }
    private void Hover(object? sender, MouseEventArgs e)
    {
        var hit = _hits.FirstOrDefault(h => h.Rect.Contains(e.Location));
        _tip.SetToolTip(this, hit.Event is null ? "" : $"{hit.Event.WaferId} · {hit.Event.Phase} · {hit.Event.Duration?.TotalMilliseconds:0} ms\n{hit.Event.WaitReason}");
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics; var modules = Enum.GetValues<ModuleId>();
        var top = 27f; var row = Math.Max(15, (Height - 35f) / modules.Length); var left = 82f; var width = Math.Max(1, Width - left - 12);
        var window = TimeSpan.FromSeconds(WindowSeconds); var start = Now > window ? Now - window : TimeSpan.Zero;
        Label(g, $"모듈 간트  /  최근 {WindowSeconds}초", Font, Brushes.LightGray, 9, 4);
        _hits.Clear();
        for (var i = 0; i < modules.Length; i++)
        {
            var y = top + i * row;
            Label(g, modules[i].ToString(), Font, Brushes.Silver, 5, y);
            g.DrawLine(Pens.DimGray, left, y + row - 2, Width - 10, y + row - 2);
        }
        string? previous = null;
        foreach (var ev in Steps.OrderBy(x => x.Start))
        {
            if (ev.End is null || ev.End < start || ev.Start > Now) continue;
            var x1 = DrawingMath.TimeToPixel(ev.Start < start ? start : ev.Start, start, window, left, width);
            var x2 = DrawingMath.TimeToPixel(ev.End.Value > Now ? Now : ev.End.Value, start, window, left, width);
            var y = top + Array.IndexOf(modules, ev.Module) * row + 2;
            var rect = new RectangleF(x1, y, Math.Max(2, x2 - x1), Math.Max(6, row - 5));
            using Brush fill = ev.IsWait ? new HatchBrush(HatchStyle.ForwardDiagonal, DrawingMath.PhaseColor(ev.Phase), Color.FromArgb(48, 52, 60)) : new SolidBrush(DrawingMath.PhaseColor(ev.Phase));
            g.FillRectangle(fill, rect); _hits.Add((rect, ev));
            if (previous != ev.WaferId)
            {
                g.DrawLine(Pens.Gold, x1, top, x1, Height - 5);
                previous = ev.WaferId;
            }
        }
    }
}

internal sealed class WaferMapPanel : BufferedPanel
{
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public EquipmentSnapshot? Snapshot { get; set; }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double WaferDiameterMm { get; set; } = 300;
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<MeasurementResult> Measurements { get; set; } = Array.Empty<MeasurementResult>();
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public WaferSummary? Summary { get; set; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        var size = Math.Min(Width - 45, Height - 110);
        if (size <= 0) return;
        var wafer = new RectangleF((Width - size) / 2f, 20, size, size);
        g.FillEllipse(Brushes.DimGray, wafer); g.DrawEllipse(Pens.LightGray, wafer);
        var values = Measurements.Select(m => m.RsOhmPerSq).ToArray();
        var min = values.Length == 0 ? 0 : values.Min(); var max = values.Length == 0 ? 1 : values.Max();
        foreach (var (site, index) in (Snapshot?.Sites ?? Array.Empty<SitePoint>()).Select((p, i) => (p, i)))
        {
            var p = DrawingMath.SiteToPixel(site, wafer, WaferDiameterMm);
            var measurement = Measurements.LastOrDefault(m => m.SiteIndex == index && m.WaferId == (Summary?.WaferId ?? Snapshot?.CurrentWaferId));
            var color = measurement is null ? Color.SlateGray : DrawingMath.Interpolate(Color.RoyalBlue, Color.OrangeRed, (measurement.RsOhmPerSq - min) / Math.Max(.000001, max - min));
            using var brush = new SolidBrush(color); g.FillEllipse(brush, p.X - 7, p.Y - 7, 14, 14);
        }
        var y = wafer.Bottom + 15;
        for (var i = 0; i < 80; i++)
        {
            using var brush = new SolidBrush(DrawingMath.Interpolate(Color.RoyalBlue, Color.OrangeRed, i / 79d));
            g.FillRectangle(brush, 20 + i * (Width - 40f) / 80, y, (Width - 40f) / 80 + 1, 9);
        }
        Label(g, $"{min:0.00} Ω/sq", Font, Brushes.LightGray, 20, y + 13);
        Label(g, $"{max:0.00} Ω/sq", Font, Brushes.LightGray, Width - 105, y + 13);
        Label(g, Summary is null ? "측정 대기" : $"{Summary.WaferId}  평균 {Summary.MeanRs:0.00}  균일도 {Summary.UniformitySigmaPct:0.00}%", Font, Brushes.WhiteSmoke, 20, y + 37);
    }
}

internal sealed class StatsPanel : BufferedPanel
{
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyDictionary<PhaseKind, TimeSpan> Totals { get; set; } = new Dictionary<PhaseKind, TimeSpan>();
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var entries = Totals.OrderByDescending(x => x.Value).ToArray();
        var max = entries.Length == 0 ? 1 : Math.Max(1, entries[0].Value.TotalSeconds);
        for (var i = 0; i < entries.Length && i < 18; i++)
        {
            var y = 15 + i * 26;
            Label(e.Graphics, entries[i].Key.ToString(), Font, Brushes.LightGray, 10, y);
            using var b = new SolidBrush(DrawingMath.PhaseColor(entries[i].Key));
            e.Graphics.FillRectangle(b, 120, y + 2, (float)((Width - 185) * entries[i].Value.TotalSeconds / max), 15);
            Label(e.Graphics, $"{entries[i].Value.TotalSeconds:0.0}s", Font, Brushes.WhiteSmoke, Width - 57, y);
        }
    }
}
