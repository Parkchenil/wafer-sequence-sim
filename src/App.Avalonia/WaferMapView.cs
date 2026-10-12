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
        base.Render(c); var summary = Summary; if (summary is null || summary.Sites.Count == 0) return;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2 - 12); var radius = Math.Min(Bounds.Width / 2 - 20, Bounds.Height / 2 - 30);
        c.DrawEllipse(new SolidColorBrush(Color.Parse("#20384D")), new Pen(Brushes.SteelBlue, 2), center, radius, radius);
        var maxR = Math.Max(1, (Services.Simulator.CurrentRecipe?.WaferDiameterMm ?? 300) * 500);
        foreach (var site in summary.Sites)
        {
            var p = new Point(center.X + site.XUm / maxR * radius, center.Y - site.YUm / maxR * radius);
            c.DrawEllipse(new SolidColorBrush(DisplayMath.MapColor(site.RsOhmPerSq, summary.MinRs, summary.MaxRs)), new Pen(Brushes.White, 1), p, 7, 7);
        }
        c.DrawLine(new Pen(Brushes.Cyan, 3), new Point(center.X - 5, center.Y + radius), new Point(center.X + 5, center.Y + radius));
        for (var i = 0; i < 50; i++) c.FillRectangle(new SolidColorBrush(DisplayMath.MapColor(i, 0, 49)), new Rect(20 + i * (Bounds.Width - 40) / 50, Bounds.Height - 16, (Bounds.Width - 40) / 50 + 1, 7));
        c.DrawText(new FormattedText(summary.MinRs.ToString("F3"), System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 10, Brushes.White), new Point(20, Bounds.Height - 30));
        c.DrawText(new FormattedText(summary.MaxRs.ToString("F3"), System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 10, Brushes.White), new Point(Bounds.Width - 58, Bounds.Height - 30));
    }
}
