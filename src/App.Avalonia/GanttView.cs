using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using System.Collections.Specialized;
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
    private static readonly IBrush[] Colors = Enum.GetValues<PhaseKind>().Select(x => (IBrush)new SolidColorBrush(DisplayMath.PhaseColor(x))).ToArray();
    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.Parse("#121D2A"));
    private static readonly Pen GridPen = new(Brushes.DarkSlateGray, 0.5);
    private static readonly Pen BoundaryPen = new(Brushes.Gold, 1);
    private static readonly Pen WaitPen = new(Brushes.White, 0.5);
    private static readonly Dictionary<string, FormattedText> TextCache = [];
    private INotifyCollectionChanged? subscribed;
    private static FormattedText Text(string text, double size, IBrush brush)
    {
        var key = $"{text}|{size}|{brush}";
        if (!TextCache.TryGetValue(key, out var value)) TextCache[key] = value = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);
        return value;
    }
    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != StepsProperty) return;
        if (subscribed is not null) subscribed.CollectionChanged -= CollectionChanged;
        subscribed = Steps as INotifyCollectionChanged;
        if (subscribed is not null) subscribed.CollectionChanged += CollectionChanged;
    }
    public override void Render(DrawingContext c)
    {
        base.Render(c); hit.Clear();
        c.FillRectangle(BackgroundBrush, new Rect(Bounds.Size));
        var now = Snapshot?.SimTime.TotalSeconds ?? 0; var span = Math.Max(5, WindowSeconds); var start = Math.Max(0, now - span); var plotX = 90.0; var width = Math.Max(1, Bounds.Width - plotX - 10); var rowH = (Bounds.Height - 50) / Enum.GetValues<ModuleId>().Length;
        foreach (var module in Enum.GetValues<ModuleId>())
        {
            var row = (int)module; var y = 25 + row * rowH;
            c.DrawText(Text(module.ToString(), 11, Brushes.LightGray), new Point(6, y));
            c.DrawLine(GridPen, new Point(plotX, y + rowH - 2), new Point(Bounds.Width, y + rowH - 2));
        }
        for (var i = 0; i <= 6; i++)
        {
            var x = plotX + i * width / 6; c.DrawLine(GridPen, new Point(x, 17), new Point(x, Bounds.Height));
            c.DrawText(Text($"{start + span * i / 6:F0}s", 10, Brushes.LightGray), new Point(x, 2));
        }
        var steps = Steps?.ToArray() ?? [];
        foreach (var boundary in steps.Where(x => x.Phase == PhaseKind.PickFromSlot && x.End is null))
        {
            var x = plotX + DisplayMath.TimeX(boundary.Start.TotalSeconds, start, span, width); c.DrawLine(BoundaryPen, new Point(x, 17), new Point(x, Bounds.Height));
        }
        foreach (var step in steps)
        {
            var a = step.Start.TotalSeconds; var b = (step.End ?? Snapshot?.SimTime ?? step.Start).TotalSeconds;
            if (b < start || a > start + span) continue;
            var x1 = plotX + DisplayMath.TimeX(Math.Max(a, start), start, span, width);
            var x2 = plotX + DisplayMath.TimeX(Math.Min(b, start + span), start, span, width);
            var rect = new Rect(x1, 25 + (int)step.Module * rowH + 2, Math.Max(2, x2 - x1), Math.Max(3, rowH - 6));
            c.FillRectangle(Colors[(int)step.Phase], rect);
            if (step.IsWait) for (var x = rect.X; x < rect.Right; x += 7) c.DrawLine(WaitPen, new Point(x, rect.Bottom), new Point(Math.Min(x + 7, rect.Right), rect.Top));
            hit.Add((rect, step));
        }
        for (var i = 0; i < Colors.Length; i++)
        {
            var x = 6 + (i % 10) * Math.Max(54, (Bounds.Width - 12) / 10);
            var y = Bounds.Height - 24 + (i / 10) * 12;
            c.FillRectangle(Colors[i], new Rect(x, y, 6, 6));
            c.DrawText(Text(((PhaseKind)i).ToString(), 8, Brushes.LightGray), new Point(x + 8, y - 2));
        }
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e); var p = e.GetPosition(this); var found = hit.LastOrDefault(x => x.Rect.Contains(p));
        ToolTip.SetTip(this, found.Step is null ? null : $"{found.Step.Phase} · {(found.Step.Duration?.TotalMilliseconds.ToString("F0") ?? "진행 중")} ms\n{found.Step.WaitReason} {found.Step.Note}");
    }
}
