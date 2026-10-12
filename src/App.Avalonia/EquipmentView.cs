using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Contracts;
namespace App.Avalonia;
public sealed class EquipmentView : Control
{
    public static readonly StyledProperty<EquipmentSnapshot?> SnapshotProperty = AvaloniaProperty.Register<EquipmentView, EquipmentSnapshot?>(nameof(Snapshot));
    static EquipmentView() => AffectsRender<EquipmentView>(SnapshotProperty);
    public EquipmentSnapshot? Snapshot { get => GetValue(SnapshotProperty); set => SetValue(SnapshotProperty, value); }
    private static readonly IBrush TextBrush = Brushes.White;
    private static void Label(DrawingContext c, string text, double x, double y, double size = 12) => c.DrawText(new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, size, TextBrush), new Point(x, y));
    public override void Render(DrawingContext c)
    {
        base.Render(c);
        var s = Snapshot; if (s is null) return;
        var w = Bounds.Width; var h = Bounds.Height;
        c.FillRectangle(new SolidColorBrush(Color.Parse("#121D2A")), new Rect(Bounds.Size));
        var scale = Math.Min(w / 750, h / 310); var port = new Rect(24 * scale, 48 * scale, 85 * scale, 225 * scale);
        c.DrawRectangle(new SolidColorBrush(Color.Parse("#22354A")), new Pen(Brushes.SlateGray, 1), port);
        Label(c, "LOAD PORT", port.X, 18 * scale);
        foreach (var wafer in s.Wafers.Take(25))
        {
            var y = port.Y + (wafer.Slot - 0.5) * port.Height / Math.Max(1, Math.Min(25, s.Wafers.Count));
            var brush = wafer.Status switch { WaferStatus.Saved => Brushes.MediumSeaGreen, WaferStatus.Processing => Brushes.Gold, WaferStatus.Failed => Brushes.OrangeRed, _ => Brushes.SlateGray };
            c.DrawLine(new Pen(brush, 5), new Point(port.X + 10, y), new Point(port.Right - 10, y));
        }
        var robotX = 165 * scale; var cy = 160 * scale;
        c.DrawEllipse(Brushes.SlateGray, null, new Point(robotX, cy), 17 * scale, 17 * scale);
        c.DrawLine(new Pen(Brushes.Cyan, 8 * scale), new Point(robotX, cy), new Point((s.Motion.RobotExtended ? 252 : 204) * scale, cy));
        Label(c, "ROBOT", 140 * scale, 18 * scale);
        var alignX = 277 * scale;
        c.DrawEllipse(null, new Pen(Brushes.SteelBlue, 2), new Point(alignX, cy), 40 * scale, 40 * scale);
        var angle = (s.Motion.AlignerAngleDeg + 270) * Math.PI / 180;
        c.DrawLine(new Pen(Brushes.Cyan, 3), new Point(alignX + 32 * scale * Math.Cos(angle), cy + 32 * scale * Math.Sin(angle)), new Point(alignX + 40 * scale * Math.Cos(angle), cy + 40 * scale * Math.Sin(angle)));
        Label(c, "ALIGNER", 248 * scale, 18 * scale);
        var cx = 455 * scale; var radius = 105 * scale;
        c.DrawEllipse(new SolidColorBrush(Color.Parse("#20384D")), new Pen(Brushes.SteelBlue, 2), new Point(cx, cy), radius, radius);
        Label(c, "CHUCK / STAGE", 401 * scale, 18 * scale);
        var diameter = Services.Simulator.CurrentRecipe?.WaferDiameterMm ?? 300.0;
        var current = s.Wafers.FirstOrDefault(x => x.WaferId == s.CurrentWaferId);
        for (var i = 0; i < s.Sites.Count; i++)
        {
            var site = s.Sites[i]; var p = new Point(cx + site.XUm / (diameter * 500) * radius, cy - site.YUm / (diameter * 500) * radius);
            var active = s.CurrentSiteIndex == i;
            c.DrawEllipse(i < (current?.SitesDone ?? 0) ? Brushes.MediumSeaGreen : active ? Brushes.Gold : null, new Pen(active ? Brushes.Gold : Brushes.LightSteelBlue, active ? 2 : 1), p, 5 * scale, 5 * scale);
        }
        var cross = new Point(cx + s.Motion.StageXUm / (diameter * 500) * radius, cy - s.Motion.StageYUm / (diameter * 500) * radius);
        c.DrawLine(new Pen(Brushes.Orange, 1), new Point(cross.X - 9 * scale, cross.Y), new Point(cross.X + 9 * scale, cross.Y));
        c.DrawLine(new Pen(Brushes.Orange, 1), new Point(cross.X, cross.Y - 9 * scale), new Point(cross.X, cross.Y + 9 * scale));
        var zx = 645 * scale; Label(c, "Z HEAD", zx - 30 * scale, 18 * scale);
        c.DrawLine(new Pen(Brushes.SlateGray, 2), new Point(zx, 65 * scale), new Point(zx, 260 * scale));
        var zy = 235 * scale - Math.Clamp(s.Motion.ZUm / 500, 0, 1) * 145 * scale;
        c.DrawRectangle(Brushes.SteelBlue, null, new Rect(zx - 24 * scale, zy - 9 * scale, 48 * scale, 18 * scale));
        c.DrawEllipse(s.Motion.ProbeInContact ? Brushes.Red : Brushes.Gray, null, new Point(zx, zy + 13 * scale), 4 * scale, 4 * scale);
        Label(c, $"{s.Motion.ZUm:F0} µm", zx - 27 * scale, 270 * scale);
        var lights = new[] { ("VAC", s.Motion.VacuumOn), ("CONF", s.Motion.VacuumConfirmed), ("LAMP", s.Motion.LampOn), ("FOCUS", s.Motion.CameraFocused), ("DOOR", s.Motion.LoadPortDoorOpen), ("EMO", s.ActiveAlarms.Any(a => a.Code == AlarmCode.Emo)) };
        for (var i = 0; i < lights.Length; i++) { var x = 15 * scale + i * 90 * scale; c.DrawEllipse(lights[i].Item2 ? (i == 5 ? Brushes.Orange : Brushes.MediumSeaGreen) : Brushes.DimGray, null, new Point(x + 6 * scale, h - 18 * scale), 5 * scale, 5 * scale); Label(c, lights[i].Item1, x + 17 * scale, h - 27 * scale, 11); }
    }
}
