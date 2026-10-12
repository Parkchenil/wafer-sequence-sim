using Contracts;
using System.Collections.Concurrent;
using System.Text;

namespace App.WinForms;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => Console.Error.WriteLine($"UI 오류: {e.Exception}");
            ApplicationConfiguration.Initialize();
            Services.Configure(args.Contains("--fake"));
            if (args.Contains("--screenshot")) return Screenshot(args);
            Application.Run(new MainForm(Services.Simulator));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"실행 실패: {ex}");
            return 1;
        }
    }

    private static int Screenshot(string[] args)
    {
        var pathIndex = Array.IndexOf(args, "--screenshot") + 1;
        if (pathIndex >= args.Length || args[pathIndex].StartsWith("--")) throw new ArgumentException("PNG 경로가 필요함");
        var count = ReadInt(args, "--wafers", 1);
        var seed = ReadInt(args, "--seed", 12345);
        if (count < 1 || count > 25) throw new ArgumentException("웨이퍼 수는 1~25여야 함");
        using var watchdog = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = watchdog.Token;
        ISimulator simulator;
        try { simulator = Task.Run(() => Services.Simulator, token).WaitAsync(token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (watchdog.IsCancellationRequested)
        {
            Console.Error.WriteLine("스크린샷 실행 제한 시간 3분 초과");
            return 2;
        }
        var recipe = new RecipeDto { Clock = ClockMode.Instant, Seed = seed };
        var measurements = new ConcurrentQueue<MeasurementResult>();
        var summaries = new ConcurrentQueue<WaferSummary>();
        simulator.MeasurementCompleted += measurements.Enqueue;
        simulator.WaferCompleted += summaries.Enqueue;
        DeviceResult run;
        try
        {
            run = Task.Run(async () =>
            {
                var init = await simulator.InitializeAsync(token);
                if (!init.Ok) throw new InvalidOperationException(init.Message);
                return await simulator.StartAsync(recipe, count, token);
            }, token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (watchdog.IsCancellationRequested)
        {
            Console.Error.WriteLine("스크린샷 실행 제한 시간 3분 초과");
            return 2;
        }
        if (!run.Ok) throw new InvalidOperationException(run.Message);
        if (token.IsCancellationRequested) { Console.Error.WriteLine("스크린샷 실행 제한 시간 3분 초과"); return 2; }
        using var form = new MainForm(simulator);
        form.ApplyCompletedRun();
        form.LoadCompletedData(measurements.ToArray(), summaries.ToArray());
        form.FormBorderStyle = FormBorderStyle.None;
        form.ClientSize = new Size(1280, 800);
        form.PerformLayout();
        form.CreateControl();
        _ = form.Handle;
        form.CreateChildren();
        form.PerformLayout();
        form.ConfigureLayout();
        form.PerformLayout();
        Capture(form, args[pathIndex], 0);
        if (token.IsCancellationRequested) { Console.Error.WriteLine("스크린샷 실행 제한 시간 3분 초과"); return 2; }
        var folder = Path.GetDirectoryName(args[pathIndex]) ?? "";
        var name = Path.GetFileNameWithoutExtension(args[pathIndex]);
        Capture(form, Path.Combine(folder, $"{name}-map.png"), 1);
        if (token.IsCancellationRequested) { Console.Error.WriteLine("스크린샷 실행 제한 시간 3분 초과"); return 2; }
        Capture(form, Path.Combine(folder, $"{name}-stats.png"), 2);
        return 0;
    }

    private static void Capture(MainForm form, string path, int tab)
    {
        form.SelectScreenshotTab(tab);
        form.PerformLayout();
        form.Refresh();
        using var bitmap = new Bitmap(1280, 800);
        form.DrawToBitmap(bitmap, new Rectangle(0, 0, 1280, 800));
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static int ReadInt(string[] args, string key, int fallback)
    {
        var index = Array.IndexOf(args, key);
        if (index < 0) return fallback;
        if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out var value)) throw new ArgumentException($"{key} 정수가 필요함");
        return value;
    }
}
