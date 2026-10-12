using Contracts;

namespace App.WinForms;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            ApplicationConfiguration.Initialize();
            if (args.Contains("--screenshot")) return ScreenshotAsync(args).GetAwaiter().GetResult();
            Application.Run(new MainForm(Services.Simulator));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"실행 실패: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> ScreenshotAsync(string[] args)
    {
        var pathIndex = Array.IndexOf(args, "--screenshot") + 1;
        if (pathIndex >= args.Length || args[pathIndex].StartsWith("--")) throw new ArgumentException("PNG 경로가 필요함");
        var count = ReadInt(args, "--wafers", 1);
        var seed = ReadInt(args, "--seed", 12345);
        if (count < 1 || count > 25) throw new ArgumentException("웨이퍼 수는 1~25여야 함");
        var simulator = Services.Simulator;
        var recipe = new RecipeDto { Clock = ClockMode.Instant, Seed = seed };
        var measurements = new List<MeasurementResult>();
        var summaries = new List<WaferSummary>();
        simulator.MeasurementCompleted += measurements.Add;
        simulator.WaferCompleted += summaries.Add;
        var init = await simulator.InitializeAsync(CancellationToken.None);
        if (!init.Ok) throw new InvalidOperationException(init.Message);
        var run = await simulator.StartAsync(recipe, count, CancellationToken.None);
        if (!run.Ok) throw new InvalidOperationException(run.Message);
        using var form = new MainForm(simulator);
        form.ApplyCompletedRun();
        form.LoadCompletedData(measurements, summaries);
        form.CreateControl();
        _ = form.Handle;
        form.CreateChildren();
        using var bitmap = new Bitmap(1280, 800);
        form.DrawToBitmap(bitmap, new Rectangle(0, 0, 1280, 800));
        bitmap.Save(args[pathIndex], System.Drawing.Imaging.ImageFormat.Png);
        return 0;
    }

    private static int ReadInt(string[] args, string key, int fallback)
    {
        var index = Array.IndexOf(args, key);
        if (index < 0) return fallback;
        if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out var value)) throw new ArgumentException($"{key} 정수가 필요함");
        return value;
    }
}
