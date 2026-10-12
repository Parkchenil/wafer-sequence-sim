using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Contracts;
namespace App.Avalonia;
public sealed class WaferMapView : Control
{
    public static readonly StyledProperty<WaferSummary?> SummaryProperty = AvaloniaProperty.Register<WaferMapView, WaferSummary?>(nameof(Summary));
    static WaferMapView() => AffectsRender<WaferMapView>(SummaryProperty);
    public WaferSummary? Summary { get => GetValue(SummaryProperty); set => SetValue(SummaryProperty, value); }
    public override void Render(DrawingContext c)
    {
        base.Render(c); var summary = Summary; if (summary is null) return;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2 - 12); var radius = Math.Min(Bounds.Width / 2 - 20, Bounds.Height / 2 - 30);
        c.DrawEllipse(new SolidColorBrush(Color.Parse("#20384D")), new Pen(Brushes.SteelBlue, 2), center, radius, radius);
        var maxR = Math.Max(1, summary.Sites.Max(x => Math.Sqrt(x.XUm * x.XUm + x.YUm * x.YUm)) * 1.1);
        foreach (var site in summary.Sites)
        {
            var p = new Point(center.X + site.XUm / maxR * radius, center.Y - site.YUm / maxR * radius);
            c.DrawEllipse(new SolidColorBrush(DisplayMath.MapColor(site.RsOhmPerSq, summary.MinRs, summary.MaxRs)), new Pen(Brushes.White, 1), p, 7, 7);
        }
        for (var i = 0; i < 50; i++) c.FillRectangle(new SolidColorBrush(DisplayMath.MapColor(i, 0, 49)), new Rect(20 + i * (Bounds.Width - 40) / 50, Bounds.Height - 16, (Bounds.Width - 40) / 50 + 1, 7));
    }
}
