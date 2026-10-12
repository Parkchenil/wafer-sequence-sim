using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Contracts;

namespace Core.Measurement;

internal static class JsonSettings
{
    public static readonly JsonSerializerOptions Options = Create();
    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new IsoTimeSpanConverter());
        return options;
    }
}

internal sealed class IsoTimeSpanConverter : JsonConverter<TimeSpan>
{
    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString()!;
        return value.StartsWith('P') ? System.Xml.XmlConvert.ToTimeSpan(value) : TimeSpan.Parse(value, CultureInfo.InvariantCulture);
    }
    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options) =>
        writer.WriteStringValue(System.Xml.XmlConvert.ToString(value));
}

public sealed class RecipeFile : IRecipeFile
{
    public string Serialize(RecipeDto recipe) => JsonSerializer.Serialize(recipe, JsonSettings.Options);
    public RecipeDto Deserialize(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("레시피 본문은 객체여야 합니다.");
            if (document.RootElement.TryGetProperty("SchemaVersion", out var version))
            {
                if (!version.TryGetInt32(out var number)) throw new InvalidDataException("레시피 스키마 버전이 올바르지 않습니다.");
                if (number != 1) throw new InvalidDataException($"지원하지 않는 레시피 스키마 버전: {number}");
            }
            var recipe = JsonSerializer.Deserialize<RecipeDto>(json, JsonSettings.Options)
                ?? throw new InvalidDataException("레시피 내용이 비어 있습니다.");
            if (!double.IsFinite(recipe.SourceCurrentA) || recipe.SourceCurrentA == 0 ||
                !double.IsFinite(recipe.WaferDiameterMm) || recipe.WaferDiameterMm <= 0 ||
                recipe.ReadingsPerSite < 1 || !double.IsFinite(recipe.TimeScale) || recipe.TimeScale <= 0 ||
                recipe.SaveQueueCapacity < 1)
                throw new InvalidDataException("레시피 수치 범위가 올바르지 않습니다.");
            return recipe;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or OverflowException)
        {
            throw new InvalidDataException("레시피 형식이 올바르지 않습니다.", ex);
        }
    }
    public Task SaveAsync(string path, RecipeDto recipe, CancellationToken ct) => File.WriteAllTextAsync(path, Serialize(recipe), ct);
    public async Task<RecipeDto> LoadAsync(string path, CancellationToken ct) => Deserialize(await File.ReadAllTextAsync(path, ct));
}

public sealed class CsvExporter : ICsvExporter
{
    private static string F(double value) => value.ToString("G9", CultureInfo.InvariantCulture);
    private static string E(object? value)
    {
        var s = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        return s.ContainsAny([',', '"', '\n', '\r']) ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    }
    private static string Row(params object?[] values) => string.Join(',', values.Select(E)) + "\n";
    public string WafersToCsv(IEnumerable<WaferSummary> wafers)
    {
        var sb = new StringBuilder("WaferId,SiteCount,MeanRs,SigmaRs,MinRs,MaxRs,UniformitySigmaPct,UniformityRangePct,RepeatabilityPct,TactMs\n");
        foreach (var w in wafers) sb.Append(Row(w.WaferId, w.SiteCount, F(w.MeanRs), F(w.SigmaRs), F(w.MinRs), F(w.MaxRs), F(w.UniformitySigmaPct), F(w.UniformityRangePct), F(w.RepeatabilityPct), F(w.Tact.TotalMilliseconds)));
        return sb.ToString();
    }
    public string SitesToCsv(IEnumerable<MeasurementResult> sites)
    {
        var sb = new StringBuilder("WaferId,SiteIndex,XUm,YUm,VoltageV,CurrentA,RsOhmPerSq,RsSigma,MeasuredAtMs\n");
        foreach (var s in sites) sb.Append(Row(s.WaferId, s.SiteIndex, F(s.XUm), F(s.YUm), string.Join(';', s.VoltageV.Select(F)), F(s.CurrentA), F(s.RsOhmPerSq), F(s.RsSigma), F(s.MeasuredAt.TotalMilliseconds)));
        return sb.ToString();
    }
    public string LogToCsv(IEnumerable<StepEvent> log)
    {
        var sb = new StringBuilder("Seq,WaferId,SiteIndex,Module,Phase,StartMs,EndMs,IsWait,WaitReason,Note\n");
        foreach (var e in log) sb.Append(Row(e.Seq, e.WaferId, e.SiteIndex, e.Module, e.Phase, F(e.Start.TotalMilliseconds), e.End is null ? "" : F(e.End.Value.TotalMilliseconds), e.IsWait, e.WaitReason, e.Note));
        return sb.ToString();
    }
}

public sealed class LogDigest : ILogDigest
{
    public string Digest(IEnumerable<StepEvent> log)
    {
        var sb = new StringBuilder();
        foreach (var e in log)
        {
            if (e.End is null) continue;
            sb.Append(e.Seq).Append('|').Append(e.WaferId).Append('|').Append(e.SiteIndex).Append('|')
                .Append(e.Module).Append('|').Append(e.Phase).Append('|')
                .Append(e.Start.TotalMilliseconds.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(e.End.Value.TotalMilliseconds.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(e.IsWait).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }
}
