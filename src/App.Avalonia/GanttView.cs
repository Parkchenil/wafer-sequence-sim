using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Contracts;
namespace App.Avalonia;
public sealed class GanttView : Control
{
    public static readonly StyledProperty<EquipmentSnapshot?> SnapshotProperty = AvaloniaProperty.Register<GanttView, EquipmentSnapshot?>(nameof(Snapshot));
    public static readonly StyledProperty<IEnumerable<StepEvent>?> StepsProperty = AvaloniaProperty.Register<GanttView, IEnumerable<StepEvent>?>(nameof(Steps));
    public static readonly StyledProperty<double> WindowSecondsProperty = AvaloniaProperty.Register<GanttView, double>(nameof(WindowSeconds), 60);
    static GanttView() => AffectsRender<GanttView>(SnapshotProperty, StepsProperty, WindowSecondsProperty);
    public EquipmentSnapshot? Snapshot { get => GetValue(SnapshotProperty); set => SetValue(SnapshotProperty, value); }
    public IEnumerable<StepEvent>? Steps { get => GetValue(StepsProperty); set => SetValue(StepsProperty, value); }
    public double WindowSeconds { get => GetValue(WindowSecondsProperty); set => SetValue(WindowSecondsProperty, value); }
    private readonly List<(Rect Rect, StepEvent Step)> hit = [];
    private static readonly IBrush[] Colors = [Brushes.SteelBlue, Brushes.MediumSeaGreen, Brushes.MediumPurple, Brushes.DarkOrange, Brushes.Teal, Brushes.CornflowerBlue];
    public override void Render(DrawingContext c)
    {
        base.Render(c); hit.Clear();
        c.FillRectangle(new SolidColorBrush(Color.Parse("#121D2A")), new Rect(Bounds.Size));
        var now = Snapshot?.SimTime.TotalSeconds ?? 0; var span = Math.Max(5, WindowSeconds); var start = Math.Max(0, now - span); var plotX = 90.0; var width = Math.Max(1, Bounds.Width - plotX - 10); var rowH = (Bounds.Height - 30) / 10;
        foreach (var module in Enum.GetValues<ModuleId>())
        {
            var row = (int)module; var y = 25 + row * rowH;
            c.DrawText(new FormattedText(module.ToString(), System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 11, Brushes.LightGray), new Point(6, y));
            c.DrawLine(new Pen(Brushes.DarkSlateGray, 0.5), new Point(plotX, y + rowH - 2), new Point(Bounds.Width, y + rowH - 2));
        }
        for (var i = 0; i <= 6; i++)
        {
            var x = plotX + i * width / 6; c.DrawLine(new Pen(Brushes.DarkSlateGray, 0.5), new Point(x, 17), new Point(x, Bounds.Height));
            c.DrawText(new FormattedText($"{start + span * i / 6:F0}s", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 10, Brushes.LightGray), new Point(x, 2));
        }
        var steps = Steps?.ToArray() ?? [];
        foreach (var boundary in steps.Where(x => x.Phase == PhaseKind.PickFromSlot && x.End is null))
        {
            var x = plotX + DisplayMath.TimeX(boundary.Start.TotalSeconds, start, span, width); c.DrawLine(new Pen(Brushes.Gold, 1), new Point(x, 17), new Point(x, Bounds.Height));
        }
        foreach (var step in steps)
        {
            var a = step.Start.TotalSeconds; var b = (step.End ?? Snapshot?.SimTime ?? step.Start).TotalSeconds;
            if (b < start || a > start + span) continue;
            var x1 = plotX + DisplayMath.TimeX(Math.Max(a, start), start, span, width);
            var x2 = plotX + DisplayMath.TimeX(Math.Min(b, start + span), start, span, width);
            var rect = new Rect(x1, 25 + (int)step.Module * rowH + 2, Math.Max(2, x2 - x1), Math.Max(3, rowH - 6));
            c.FillRectangle(Colors[(int)step.Phase % Colors.Length], rect);
            if (step.IsWait) for (var x = rect.X; x < rect.Right; x += 7) c.DrawLine(new Pen(Brushes.White, 0.5), new Point(x, rect.Bottom), new Point(Math.Min(x + 7, rect.Right), rect.Top));
            hit.Add((rect, step));
        }
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e); var p = e.GetPosition(this); var found = hit.LastOrDefault(x => x.Rect.Contains(p));
        ToolTip.SetTip(this, found.Step is null ? null : $"{found.Step.Phase} · {(found.Step.Duration?.TotalMilliseconds.ToString("F0") ?? "진행 중")} ms\n{found.Step.WaitReason} {found.Step.Note}");
    }
}
