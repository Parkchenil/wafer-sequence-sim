using Contracts;

namespace Core.Measurement;

internal static class EdgeCorrection
{
    public static double Factor(double xUm, double yUm, double waferDiameterMm)
    {
        if (!double.IsFinite(xUm) || !double.IsFinite(yUm) || !double.IsFinite(waferDiameterMm) || waferDiameterMm <= 0)
            return double.NaN;
        const double spacingMm = 1.59;
        var distanceMm = Math.Max(0, waferDiameterMm / 2 - Math.Sqrt(xUm * xUm + yUm * yUm) / 1000);
        if (distanceMm >= 4 * spacingMm) return 1;
        // 경계에서 1이 되는 단조 함수로 가장자리 측정 기하 효과를 근사한다.
        return 1 / (1 + 0.5 * (Math.Exp(-distanceMm / spacingMm) - Math.Exp(-4)));
    }
}

public sealed class RsCalculator : IRsCalculator
{
    public double RsFromVI(double voltageV, double currentA, double xUm, double yUm, double waferDiameterMm)
    {
        if (!double.IsFinite(voltageV) || !double.IsFinite(currentA) || currentA == 0)
            return double.NaN;
        return 4.532 * voltageV / currentA * EdgeCorrection.Factor(xUm, yUm, waferDiameterMm);
    }

    public MeasurementResult Build(string waferId, int siteIndex, double xUm, double yUm, IReadOnlyList<double> voltages, double currentA, double waferDiameterMm, TimeSpan at)
    {
        var copiedVoltages = voltages.Where(double.IsFinite).ToArray();
        var values = copiedVoltages.Select(v => RsFromVI(v, currentA, xUm, yUm, waferDiameterMm))
            .Where(double.IsFinite).ToArray();
        var mean = values.Length == 0 ? 0 : values.Average();
        var sigma = values.Length < 2 ? 0 : Math.Sqrt(values.Sum(v => Math.Pow(v - mean, 2)) / (values.Length - 1));
        return new(waferId, siteIndex, xUm, yUm, copiedVoltages, double.IsFinite(currentA) ? currentA : 0, mean, sigma, at);
    }

    public WaferSummary Summarize(string waferId, IReadOnlyList<MeasurementResult> sites, TimeSpan tact)
    {
        var copiedSites = sites.ToArray();
        var validSites = copiedSites.Where(s => double.IsFinite(s.RsOhmPerSq)).ToArray();
        var values = validSites.Select(s => s.RsOhmPerSq).ToArray();
        var mean = values.Length == 0 ? 0 : values.Average();
        var sigma = values.Length < 2 ? 0 : Math.Sqrt(values.Sum(v => Math.Pow(v - mean, 2)) / (values.Length - 1));
        var min = values.Length == 0 ? 0 : values.Min();
        var max = values.Length == 0 ? 0 : values.Max();
        return new(waferId, sites.Count, mean, sigma, min, max,
            mean == 0 ? 0 : sigma / mean * 100,
            mean == 0 ? 0 : (max - min) / (2 * mean) * 100,
            mean == 0 ? 0 : validSites.Select(s => s.RsSigma).Where(double.IsFinite).DefaultIfEmpty(0).Average() / mean * 100,
            tact, validSites);
    }
}

public sealed class ResistanceField(int seed, double centerRsOhmPerSq = 50, double edgeRisePct = 8,
    double noisePct = 0.2, double waferDiameterMm = 300) : IResistanceField
{
    private static int WaferHash(string waferId)
    {
        unchecked { var hash = 2166136261u; foreach (var c in waferId) hash = (hash ^ c) * 16777619; return (int)hash; }
    }

    public double TrueRsAt(string waferId, double xUm, double yUm)
    {
        var random = new Random(unchecked(seed + WaferHash(waferId)));
        var rise = edgeRisePct / 100 * (0.8 + 0.4 * random.NextDouble());
        var wave = edgeRisePct / 100 * 0.06 * (2 * random.NextDouble() - 1);
        var r = Math.Sqrt(xUm * xUm + yUm * yUm) / (waferDiameterMm * 500);
        var angle = Math.Atan2(yUm, xUm);
        return centerRsOhmPerSq * (1 + rise * r * r + wave * r * Math.Cos(angle));
    }

    public double VoltageSample(string waferId, double xUm, double yUm, double currentA, Random random)
    {
        var u1 = 1 - random.NextDouble();
        var u2 = random.NextDouble();
        var gaussian = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        return TrueRsAt(waferId, xUm, yUm) * (1 + noisePct / 100 * gaussian) * currentA
            / (4.532 * EdgeCorrection.Factor(xUm, yUm, waferDiameterMm));
    }
}

public sealed class SitePatternProvider : ISitePatternProvider
{
    public IReadOnlyList<SitePoint> Build(RecipeDto recipe)
    {
        if (recipe.Pattern == SitePattern.Custom) return recipe.CustomSites;
        var radiusUm = (recipe.WaferDiameterMm / 2 - recipe.EdgeExclusionMm) * 1000;
        var points = new List<SitePoint> { new(0, 0) };
        if (recipe.Pattern == SitePattern.Center1) return points;
        if (recipe.Pattern == SitePattern.Standard49)
        {
            for (var ring = 1; ring <= 6; ring++)
                for (var i = 0; i < 8; i++)
                {
                    var angle = i * Math.PI / 4;
                    points.Add(new(radiusUm * ring / 7 * Math.Cos(angle), radiusUm * ring / 7 * Math.Sin(angle)));
                }
            return points;
        }
        var rings = recipe.Pattern == SitePattern.Standard5 ? new[] { 0.7 } : new[] { 0.5, 0.9 };
        foreach (var fraction in rings)
        {
            var r = radiusUm * fraction;
            points.AddRange([new(r, 0), new(0, r), new(-r, 0), new(0, -r)]);
        }
        return points;
    }
}

public sealed class RunComparer : IRunComparer
{
    public RunComparison Compare(RunRecord a, RunRecord b)
    {
        var phases = a.PhaseTotals.Keys.Union(b.PhaseTotals.Keys)
            .Select(p => new PhaseDelta(p, a.PhaseTotals.GetValueOrDefault(p), b.PhaseTotals.GetValueOrDefault(p),
                b.PhaseTotals.GetValueOrDefault(p) - a.PhaseTotals.GetValueOrDefault(p)))
            .OrderByDescending(p => Math.Abs(p.Delta.Ticks)).ThenBy(p => p.Phase).ToArray();
        return new(a, b, phases, (b.AverageTact ?? TimeSpan.Zero) - (a.AverageTact ?? TimeSpan.Zero), (b.Uph ?? 0) - (a.Uph ?? 0));
    }
}
