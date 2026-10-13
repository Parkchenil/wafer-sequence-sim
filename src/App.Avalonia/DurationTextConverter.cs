using System.Globalization;
using Avalonia.Data.Converters;
namespace App.Avalonia;
// 로그의 시간은 밀리초 단위까지 일정한 폭으로 표시한다.
public sealed class DurationTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is TimeSpan time ? $"{(int)time.TotalMinutes:00}:{time.Seconds:00}.{time.Milliseconds:000}" : "—";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
