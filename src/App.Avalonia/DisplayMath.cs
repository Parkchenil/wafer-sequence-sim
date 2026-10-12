using Avalonia.Media;
using Contracts;
namespace App.Avalonia;
public static class DisplayMath
{
    private static readonly Color[] PhaseColors = new string[] { "#5288D1", "#39A7A0", "#A57CDB", "#DF8953", "#6FB96F", "#CB6FA5", "#6DAAD5", "#CDAF55", "#758DDD", "#C77372", "#55B5B1", "#AD88C8", "#91AE58", "#D07D63", "#7C9ECC", "#A39B6E", "#B574A5", "#5BA993", "#CB9A71", "#8799A3" }.Select(Color.Parse).ToArray();
    public static Color PhaseColor(PhaseKind phase) => PhaseColors[(int)phase];
    public static double TimeX(double seconds, double windowStart, double windowSeconds, double width) =>
        (seconds - windowStart) / Math.Max(0.001, windowSeconds) * width;
    public static Color MapColor(double value, double min, double max)
    {
        var t = max <= min ? 0.5 : Math.Clamp((value - min) / (max - min), 0, 1);
        return Color.FromRgb((byte)(48 + 207 * t), (byte)(184 - 130 * t), (byte)(221 - 171 * t));
    }
}
