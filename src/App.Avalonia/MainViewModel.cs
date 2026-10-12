using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Contracts;
using Core.Measurement;
namespace App.Avalonia;
public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ISimulator simulator = Services.Simulator;
    private readonly MainWindow owner;
    private readonly IRecipeFile recipeFile = new RecipeFile();
    private readonly IRunComparer comparer = new RunComparer();
    private readonly ConcurrentQueue<StepEvent> pendingSteps = new();
    private readonly Dictionary<long, int> logIndex = [];
    private readonly DispatcherTimer logTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Action<EquipmentSnapshot> snapshotHandler;
    private readonly Action<StepEvent> stepHandler;
    private readonly Action<WaferSummary> waferHandler;
    private readonly Action<RunRecord> runHandler;
    private bool disposed;
    public ObservableCollection<RecipeGroup> RecipeGroups { get; } = [];
    public ObservableCollection<StepEvent> Logs { get; } = [];
    public ObservableCollection<StepEvent> VisibleLogs { get; } = [];
    public ObservableCollection<WaferSummary> Summaries { get; } = [];
    public ObservableCollection<Alarm> Alarms { get; } = [];
    public ObservableCollection<ModuleStatus> Modules { get; } = [];
    public ObservableCollection<PhaseBar> Phases { get; } = [];
    public ObservableCollection<RunRecord> Runs { get; } = [];
    public ObservableCollection<PhaseDelta> PhaseDeltas { get; } = [];
    [ObservableProperty] private EquipmentSnapshot snapshot;
    [ObservableProperty] private WaferSummary? selectedSummary;
    [ObservableProperty] private RunRecord? selectedRunA;
    [ObservableProperty] private RunRecord? selectedRunB;
    [ObservableProperty] private int waferCount = 5;
    [ObservableProperty] private bool waitsOnly;
    [ObservableProperty] private double windowSeconds = 60;
    [ObservableProperty] private string message = "초기화 버튼을 눌러 시작";
    [ObservableProperty] private string comparisonText = "두 실행을 선택";
    public string StateText => Snapshot.State.ToString();
    public IBrush StateBrush => Snapshot.State switch { EquipmentState.Running => Brushes.MediumSeaGreen, EquipmentState.Paused => Brushes.Gold, EquipmentState.Alarm => Brushes.OrangeRed, EquipmentState.Aborting => Brushes.OrangeRed, _ => Brushes.SteelBlue };
    public string SimTime => Snapshot.SimTime.ToString(@"hh\:mm\:ss\.fff");
    public string AverageTact => Snapshot.Stats.AverageTact is { } t ? $"{t.TotalSeconds:F2} s" : "—";
    public string Uph => Snapshot.Stats.Uph?.ToString("F1") ?? "—";
    public string WaferProgress => $"{Snapshot.Stats.WafersDone}/{Snapshot.Stats.WafersTotal}";
    public string QueueText => $"{Snapshot.Stats.SaveQueueDepth}/{Snapshot.Stats.SaveQueueCapacity}";
    public double QueueFraction => Snapshot.Stats.SaveQueueCapacity == 0 ? 0 : (double)Snapshot.Stats.SaveQueueDepth / Snapshot.Stats.SaveQueueCapacity;
    public int AlarmCount => Snapshot.ActiveAlarms.Count;
    public bool CanInitialize => Snapshot.State == EquipmentState.Uninitialized;
    public bool CanStart => Snapshot.State == EquipmentState.Idle;
    public bool CanPause => Snapshot.State == EquipmentState.Running;
    public bool CanResume => Snapshot.State == EquipmentState.Paused;
    public bool CanAbort => Snapshot.State is EquipmentState.Running or EquipmentState.Paused;
    public bool CanReset => Snapshot.State is EquipmentState.Alarm or EquipmentState.Idle or EquipmentState.Completed;
    public string WaitRatio => $"대기 비율 {Snapshot.Stats.WaitRatio:P1}";
    public MainViewModel(MainWindow owner)
    {
        this.owner = owner;
        snapshot = simulator.Snapshot;
        LoadRecipe(new RecipeDto());
        snapshotHandler = value => Dispatcher.UIThread.Post(() => { if (!disposed) RefreshSnapshot(value); });
        stepHandler = pendingSteps.Enqueue;
        waferHandler = value => Dispatcher.UIThread.Post(() => { if (disposed) return; Summaries.Add(value); SelectedSummary ??= value; });
        runHandler = _ => Dispatcher.UIThread.Post(async () => { if (!disposed) await RefreshRunsAsync(); });
        simulator.SnapshotChanged += snapshotHandler;
        simulator.StepLogged += stepHandler;
        simulator.WaferCompleted += waferHandler;
        simulator.RunCompleted += runHandler;
        foreach (var step in simulator.Log.TakeLast(5000)) AddStep(step);
        RefreshSnapshot(snapshot);
        logTimer.Tick += LogTick; logTimer.Start();
        _ = RefreshRunsAsync();
    }
    private void LoadRecipe(RecipeDto recipe)
    {
        RecipeGroups.Clear(); foreach (var group in RecipeEditor.FieldGroups(recipe)) RecipeGroups.Add(group);
    }
    partial void OnSnapshotChanged(EquipmentSnapshot value)
    {
        foreach (var name in new[] { nameof(StateText), nameof(StateBrush), nameof(SimTime), nameof(AverageTact), nameof(Uph), nameof(WaferProgress), nameof(QueueText), nameof(QueueFraction), nameof(AlarmCount), nameof(CanInitialize), nameof(CanStart), nameof(CanPause), nameof(CanResume), nameof(CanAbort), nameof(CanReset), nameof(WaitRatio) }) OnPropertyChanged(name);
    }
    partial void OnWaitsOnlyChanged(bool value)
    {
        VisibleLogs.Clear(); foreach (var step in Logs.Where(s => !value || s.IsWait)) VisibleLogs.Add(step);
    }
    private static void UpdateByKey<T, TKey>(ObservableCollection<T> target, IEnumerable<T> source, Func<T, TKey> key) where TKey : notnull
    {
        var incoming = source.ToArray(); var keys = incoming.Select(key).ToHashSet();
        for (var i = target.Count - 1; i >= 0; i--) if (!keys.Contains(key(target[i]))) target.RemoveAt(i);
        for (var i = 0; i < incoming.Length; i++)
        {
            var index = -1; for (var j = 0; j < target.Count; j++) if (EqualityComparer<TKey>.Default.Equals(key(target[j]), key(incoming[i]))) { index = j; break; }
            if (index < 0) target.Insert(Math.Min(i, target.Count), incoming[i]);
            else if (!EqualityComparer<T>.Default.Equals(target[index], incoming[i])) target[index] = incoming[i];
        }
    }
    private void RefreshSnapshot(EquipmentSnapshot value)
    {
        if (value.SimTime < Snapshot.SimTime && value.State == Snapshot.State) return;
        Snapshot = value;
        UpdateByKey(Modules, value.Modules, x => x.Module);
        UpdateByKey(Alarms, value.ActiveAlarms, x => x.Id);
        var max = Math.Max(1, value.Stats.PhaseTotals.Values.Select(t => t.TotalSeconds).DefaultIfEmpty(0).Max());
        UpdateByKey(Phases, value.Stats.PhaseTotals.OrderByDescending(x => x.Value).Select(p => new PhaseBar(p.Key, p.Value.TotalSeconds, p.Value.TotalSeconds / max * 100)), x => x.Phase);
    }
    private void LogTick(object? sender, EventArgs args)
    {
        var latest = new SortedDictionary<long, StepEvent>();
        while (pendingSteps.TryDequeue(out var step)) latest[step.Seq] = step;
        foreach (var step in latest.Values) AddStep(step);
    }
    private void AddStep(StepEvent step)
    {
        if (logIndex.TryGetValue(step.Seq, out var index))
        {
            Logs[index] = step;
            if (!WaitsOnly || step.IsWait)
            {
                for (var i = 0; i < VisibleLogs.Count; i++) if (VisibleLogs[i].Seq == step.Seq) { VisibleLogs[i] = step; break; }
            }
            return;
        }
        var lo = 0; var hi = Logs.Count;
        while (lo < hi) { var mid = (lo + hi) / 2; if (Logs[mid].Seq < step.Seq) lo = mid + 1; else hi = mid; }
        Logs.Insert(lo, step); for (var i = lo; i < Logs.Count; i++) logIndex[Logs[i].Seq] = i;
        if (!WaitsOnly || step.IsWait)
        {
            var visibleIndex = 0; while (visibleIndex < VisibleLogs.Count && VisibleLogs[visibleIndex].Seq < step.Seq) visibleIndex++;
            VisibleLogs.Insert(visibleIndex, step);
        }
        if (Logs.Count > 5000)
        {
            var removed = Logs[0]; Logs.RemoveAt(0); logIndex.Remove(removed.Seq);
            if (VisibleLogs.Count > 0 && VisibleLogs[0].Seq == removed.Seq) VisibleLogs.RemoveAt(0);
            for (var i = 0; i < Logs.Count; i++) logIndex[Logs[i].Seq] = i;
        }
    }
    [RelayCommand] private async Task InitializeAsync() => Report(await simulator.InitializeAsync(CancellationToken.None));
    [RelayCommand] private async Task StartAsync()
    {
        if (WaferCount is < 1 or > 25) { Message = "웨이퍼 수는 1~25여야 함"; return; }
        if (!RecipeEditor.TryBuild(RecipeGroups.SelectMany(g => g.Fields), out var recipe, out var error)) { Message = "레시피 입력 오류: " + error; return; }
        Logs.Clear(); VisibleLogs.Clear(); logIndex.Clear(); Summaries.Clear(); SelectedSummary = null;
        Message = "로트 실행 중";
        Report(await simulator.StartAsync(recipe, WaferCount, CancellationToken.None));
    }
    [RelayCommand] private void Pause() => simulator.Pause();
    [RelayCommand] private void Resume() => simulator.Resume();
    [RelayCommand] private async Task AbortAsync() { await simulator.AbortAsync(); Message = "중단 요청 완료"; }
    [RelayCommand] private async Task ResetAsync() => Report(await simulator.ResetAsync());
    [RelayCommand] private void Ack(string id) => simulator.AcknowledgeAlarm(id);
    [RelayCommand] private async Task SaveRecipeAsync()
    {
        if (!RecipeEditor.TryBuild(RecipeGroups.SelectMany(g => g.Fields), out var recipe, out var error)) { Message = "레시피 입력 오류: " + error; return; }
        var path = await owner.PickRecipeSavePathAsync(); if (path is null) return;
        try { await recipeFile.SaveAsync(path, recipe, CancellationToken.None); Message = "레시피 저장 완료"; }
        catch (Exception ex) { Message = "레시피 저장 실패: " + ex.Message; }
    }
    [RelayCommand] private async Task LoadRecipeAsync()
    {
        var path = await owner.PickRecipeOpenPathAsync(); if (path is null) return;
        try
        {
            var recipe = await recipeFile.LoadAsync(path, CancellationToken.None);
            var fields = RecipeEditor.Fields(recipe);
            if (!RecipeEditor.TryBuild(fields, out _, out var error)) { Message = "레시피 입력 오류: " + error; return; }
            LoadRecipe(recipe); Message = "레시피 불러오기 완료";
        }
        catch (Exception ex) { Message = "레시피 불러오기 실패: " + ex.Message; }
    }
    private async Task RefreshRunsAsync()
    {
        try
        {
            var runs = await Services.Store.ListRunsAsync(CancellationToken.None);
            if (disposed) return;
            Runs.Clear(); foreach (var run in runs.Reverse()) Runs.Add(run);
        }
        catch (Exception ex) { Message = "실행 목록 실패: " + ex.Message; }
    }
    [RelayCommand] private void Compare()
    {
        if (SelectedRunA is null || SelectedRunB is null) { ComparisonText = "두 실행을 선택"; return; }
        var result = comparer.Compare(SelectedRunA, SelectedRunB);
        PhaseDeltas.Clear(); foreach (var phase in result.Phases) PhaseDeltas.Add(phase);
        ComparisonText = $"Tact 차이 {result.TactDelta.TotalSeconds:+0.000;-0.000;0} s · UPH 차이 {result.UphDelta:+0.0;-0.0;0}";
    }
    private void Report(DeviceResult result) => Message = result.Ok ? "명령 완료" : $"{result.Code}: {result.Message}";
    public void Dispose()
    {
        if (disposed) return; disposed = true; logTimer.Stop(); logTimer.Tick -= LogTick;
        simulator.SnapshotChanged -= snapshotHandler; simulator.StepLogged -= stepHandler;
        simulator.WaferCompleted -= waferHandler; simulator.RunCompleted -= runHandler;
    }
    public async Task StopAsync() { if (CanAbort) await simulator.AbortAsync(); Dispose(); }
}
public sealed record PhaseBar(PhaseKind Phase, double Seconds, double Percent);
