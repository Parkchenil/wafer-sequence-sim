using Avalonia.Media;
namespace App.Avalonia;
public static class DisplayMath
{
    public static double TimeX(double seconds, double windowStart, double windowSeconds, double width) =>
        (seconds - windowStart) / Math.Max(0.001, windowSeconds) * width;
    public static Color MapColor(double value, double min, double max)
    {
        var t = max <= min ? 0.5 : Math.Clamp((value - min) / (max - min), 0, 1);
        return Color.FromRgb((byte)(48 + 207 * t), (byte)(184 - 130 * t), (byte)(221 - 171 * t));
    }
}
