using System.Reflection;
using Contracts;
namespace App.Avalonia;
public sealed class RecipeField
{
    public required string Group { get; init; }
    public required string Label { get; init; }
    public required PropertyInfo Property { get; init; }
    public string Value { get; set; } = "";
    public bool IsChoice => Property.PropertyType.IsEnum;
    public bool IsBoolean => Property.PropertyType == typeof(bool);
    public string[] Choices => IsChoice ? Enum.GetNames(Property.PropertyType) : [];
}
public static class RecipeEditor
{
    public static List<RecipeField> Fields(RecipeDto recipe)
    {
        var groups = new Dictionary<string, string[]>
        {
            ["기본"] = ["SchemaVersion", "Name", "Seed"],
            ["측정 위치"] = ["Pattern", "CustomSites", "WaferDiameterMm", "EdgeExclusionMm"],
            ["전기 측정"] = ["ReadingsPerSite", "ReadingTime", "SourceCurrentA"],
            ["모션"] = ["TravelSpeedMmPerS", "AccelMmPerS2", "ZMode", "ZApproachSpeedMmPerS", "ZStepUm", "ZSafeHeightUm", "ZLiftAfterMeasureUm", "ContactDetectTimeout", "ContactRetryProbability", "ContactMaxRetry"],
            ["고정 대기"] = ["VacuumCheckWait", "VacuumCheckCount", "IlluminationWait", "AutoFocusWait", "MapInspect", "MapInspectTime"],
            ["핸들링"] = ["UnloadOverlap", "RobotMoveTime", "AlignTime"],
            ["저장"] = ["Save", "SaveQueueCapacity", "SaveWriteTime"],
            ["시계"] = ["Clock", "TimeScale"]
        };
        return groups.SelectMany(g => g.Value.Select(name => new RecipeField
        {
            Group = g.Key, Label = name + (typeof(TimeSpan) == typeof(RecipeDto).GetProperty(name)!.PropertyType ? " (ms)" : ""),
            Property = typeof(RecipeDto).GetProperty(name)!,
            Value = Format(typeof(RecipeDto).GetProperty(name)!.GetValue(recipe))
        })).ToList();
    }
    private static string Format(object? value) => value switch
    {
        TimeSpan t => t.TotalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
        IReadOnlyList<SitePoint> sites => string.Join("; ", sites.Select(s => $"{s.XUm},{s.YUm}")),
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value?.ToString() ?? ""
    };
    public static bool TryBuild(IEnumerable<RecipeField> fields, out RecipeDto recipe, out string error)
    {
        recipe = new RecipeDto(); error = "";
        try
        {
            foreach (var field in fields)
            {
                var type = field.Property.PropertyType;
                object value = type == typeof(TimeSpan) ? TimeSpan.FromMilliseconds(double.Parse(field.Value, System.Globalization.CultureInfo.InvariantCulture)) :
                    type == typeof(int) ? int.Parse(field.Value, System.Globalization.CultureInfo.InvariantCulture) :
                    type == typeof(double) ? double.Parse(field.Value, System.Globalization.CultureInfo.InvariantCulture) :
                    type == typeof(bool) ? bool.Parse(field.Value) :
                    type.IsEnum ? Enum.Parse(type, field.Value) :
                    type == typeof(string) ? field.Value :
                    field.Value.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => { var xy = p.Split(','); return new SitePoint(double.Parse(xy[0], System.Globalization.CultureInfo.InvariantCulture), double.Parse(xy[1], System.Globalization.CultureInfo.InvariantCulture)); }).ToArray();
                field.Property.SetValue(recipe, value);
            }
            if (recipe.ReadingsPerSite < 1 || recipe.TimeScale <= 0 || recipe.WaferDiameterMm <= 0 || recipe.TravelSpeedMmPerS <= 0 || recipe.AccelMmPerS2 <= 0 || recipe.ZApproachSpeedMmPerS <= 0) throw new FormatException("양수 입력값을 확인해 주세요");
            return true;
        }
        catch (Exception ex) { error = ex.InnerException?.Message ?? ex.Message; return false; }
    }
}
