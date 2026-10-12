using System.Globalization;
using System.Reflection;
using Contracts;
namespace App.Avalonia;
public sealed class RecipeField
{
    public required string Group { get; init; }
    public required string Label { get; init; }
    public required PropertyInfo Property { get; init; }
    public string Value { get; set; } = "";
    public bool BoolValue { get => bool.TryParse(Value, out var value) && value; set => Value = value.ToString(); }
    public bool IsChoice => Property.PropertyType.IsEnum;
    public bool IsBoolean => Property.PropertyType == typeof(bool);
    public string[] Choices => IsChoice ? Enum.GetNames(Property.PropertyType) : [];
}
public sealed record RecipeGroup(string Name, IReadOnlyList<RecipeField> Fields)
{
    public IEnumerable<RecipeField> TextFields => Fields.Where(f => !f.IsChoice && !f.IsBoolean);
    public IEnumerable<RecipeField> ChoiceFields => Fields.Where(f => f.IsChoice);
    public IEnumerable<RecipeField> BooleanFields => Fields.Where(f => f.IsBoolean);
}
public static class RecipeEditor
{
    private static readonly (string Name, string[] Properties)[] Groups =
    [
        ("기본", ["SchemaVersion", "Name", "Seed"]),
        ("측정 위치", ["Pattern", "CustomSites", "WaferDiameterMm", "EdgeExclusionMm"]),
        ("전기 측정", ["ReadingsPerSite", "ReadingTime", "SourceCurrentA"]),
        ("모션", ["TravelSpeedMmPerS", "AccelMmPerS2", "ZMode", "ZApproachSpeedMmPerS", "ZStepUm", "ZSafeHeightUm", "ZLiftAfterMeasureUm", "ContactDetectTimeout", "ContactRetryProbability", "ContactMaxRetry"]),
        ("고정 대기", ["VacuumCheckWait", "VacuumCheckCount", "IlluminationWait", "AutoFocusWait", "MapInspect", "MapInspectTime"]),
        ("핸들링", ["UnloadOverlap", "RobotMoveTime", "AlignTime"]),
        ("저장", ["Save", "SaveQueueCapacity", "SaveWriteTime"]),
        ("시계", ["Clock", "TimeScale"])
    ];
    public static IReadOnlyList<RecipeGroup> FieldGroups(RecipeDto recipe) => Groups.Select(g => new RecipeGroup(g.Name, g.Properties.Select(name =>
    {
        var property = typeof(RecipeDto).GetProperty(name)!;
        return new RecipeField { Group = g.Name, Label = name + (property.PropertyType == typeof(TimeSpan) ? " (ms)" : ""), Property = property, Value = Format(property.GetValue(recipe)) };
    }).ToArray())).ToArray();
    public static List<RecipeField> Fields(RecipeDto recipe) => FieldGroups(recipe).SelectMany(g => g.Fields).ToList();
    public static string Format(object? value) => value switch
    {
        TimeSpan t => t.TotalMilliseconds.ToString(CultureInfo.InvariantCulture),
        IReadOnlyList<SitePoint> sites => string.Join("; ", sites.Select(s => $"{s.XUm.ToString(CultureInfo.InvariantCulture)},{s.YUm.ToString(CultureInfo.InvariantCulture)}")),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value?.ToString() ?? ""
    };
    private static object Parse(RecipeField field)
    {
        var type = field.Property.PropertyType;
        if (type == typeof(string)) return field.Value;
        if (type == typeof(int)) return int.Parse(field.Value, CultureInfo.InvariantCulture);
        if (type == typeof(double)) { var n = double.Parse(field.Value, CultureInfo.InvariantCulture); if (!double.IsFinite(n)) throw new FormatException("유한한 숫자여야 함"); return n; }
        if (type == typeof(TimeSpan)) { var n = double.Parse(field.Value, CultureInfo.InvariantCulture); if (!double.IsFinite(n) || n < 0) throw new FormatException("0 이상의 유한한 ms여야 함"); return TimeSpan.FromMilliseconds(n); }
        if (type == typeof(bool)) return bool.Parse(field.Value);
        if (type.IsEnum) { var value = Enum.Parse(type, field.Value); if (!Enum.IsDefined(type, value)) throw new FormatException("선택 범위 밖임"); return value; }
        return field.Value.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p =>
        {
            var xy = p.Split(','); if (xy.Length != 2) throw new FormatException("XUm,YUm 형식이 아님");
            var x = double.Parse(xy[0], CultureInfo.InvariantCulture); var y = double.Parse(xy[1], CultureInfo.InvariantCulture);
            if (!double.IsFinite(x) || !double.IsFinite(y)) throw new FormatException("좌표가 유한하지 않음");
            return new SitePoint(x, y);
        }).ToArray();
    }
    public static bool TryBuild(IEnumerable<RecipeField> fields, out RecipeDto recipe, out string error)
    {
        recipe = new RecipeDto(); error = "";
        var parsed = new Dictionary<string, object>();
        foreach (var field in fields)
        {
            try { parsed[field.Property.Name] = Parse(field); }
            catch (Exception ex) { error = $"{field.Label}: {ex.Message}"; return false; }
        }
        foreach (var (key, value) in parsed) typeof(RecipeDto).GetProperty(key)!.SetValue(recipe, value);
        string? Invalid(string name, bool condition) => condition ? name + ": 값 범위가 올바르지 않음" : null;
        var checkedRecipe = recipe;
        error = Invalid(nameof(recipe.SchemaVersion), recipe.SchemaVersion != 1)
            ?? Invalid(nameof(recipe.Name), string.IsNullOrWhiteSpace(recipe.Name))
            ?? Invalid(nameof(recipe.WaferDiameterMm), !(recipe.WaferDiameterMm > 0))
            ?? Invalid(nameof(recipe.EdgeExclusionMm), recipe.EdgeExclusionMm < 0 || recipe.EdgeExclusionMm >= recipe.WaferDiameterMm / 2)
            ?? Invalid(nameof(recipe.ReadingsPerSite), recipe.ReadingsPerSite < 1)
            ?? Invalid(nameof(recipe.ReadingTime), recipe.ReadingTime <= TimeSpan.Zero)
            ?? Invalid(nameof(recipe.SourceCurrentA), recipe.SourceCurrentA == 0)
            ?? Invalid(nameof(recipe.TravelSpeedMmPerS), !(recipe.TravelSpeedMmPerS > 0))
            ?? Invalid(nameof(recipe.AccelMmPerS2), !(recipe.AccelMmPerS2 > 0))
            ?? Invalid(nameof(recipe.ZApproachSpeedMmPerS), !(recipe.ZApproachSpeedMmPerS > 0))
            ?? Invalid(nameof(recipe.ZStepUm), !(recipe.ZStepUm > 0))
            ?? Invalid(nameof(recipe.ZSafeHeightUm), !(recipe.ZSafeHeightUm > 0))
            ?? Invalid(nameof(recipe.ZLiftAfterMeasureUm), !(recipe.ZLiftAfterMeasureUm > 0))
            ?? Invalid(nameof(recipe.ContactDetectTimeout), recipe.ContactDetectTimeout <= TimeSpan.Zero)
            ?? Invalid(nameof(recipe.ContactRetryProbability), recipe.ContactRetryProbability is < 0 or > 1)
            ?? Invalid(nameof(recipe.ContactMaxRetry), recipe.ContactMaxRetry < 0)
            ?? Invalid(nameof(recipe.VacuumCheckCount), recipe.VacuumCheckCount < 1)
            ?? Invalid(nameof(recipe.RobotMoveTime), recipe.RobotMoveTime <= TimeSpan.Zero)
            ?? Invalid(nameof(recipe.AlignTime), recipe.AlignTime <= TimeSpan.Zero)
            ?? Invalid(nameof(recipe.SaveQueueCapacity), recipe.SaveQueueCapacity < 1)
            ?? Invalid(nameof(recipe.SaveWriteTime), recipe.SaveWriteTime <= TimeSpan.Zero)
            ?? Invalid(nameof(recipe.TimeScale), !(recipe.TimeScale > 0))
            ?? Invalid(nameof(recipe.CustomSites), recipe.Pattern == SitePattern.Custom && recipe.CustomSites.Count == 0)
            ?? Invalid(nameof(recipe.CustomSites), checkedRecipe.CustomSites.Any(s => Math.Sqrt(s.XUm * s.XUm + s.YUm * s.YUm) > (checkedRecipe.WaferDiameterMm / 2 - checkedRecipe.EdgeExclusionMm) * 1000))
            ?? "";
        return error.Length == 0;
    }
}
