using System.Collections.ObjectModel;
using Avalonia.Threading;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Contracts;
namespace App.Avalonia;
public partial class MainViewModel : ObservableObject
{
    private readonly ISimulator simulator = Services.Simulator;
    private readonly Dictionary<string, List<MeasurementResult>> measurements = new();
    public ObservableCollection<RecipeField> RecipeFields { get; } = new(RecipeEditor.Fields(new RecipeDto()));
    public ObservableCollection<StepEvent> Logs { get; } = [];
    public ObservableCollection<WaferSummary> Summaries { get; } = [];
    public ObservableCollection<Alarm> Alarms { get; } = [];
    public ObservableCollection<ModuleStatus> Modules { get; } = [];
    public ObservableCollection<PhaseBar> Phases { get; } = [];
    [ObservableProperty] private EquipmentSnapshot snapshot;
    [ObservableProperty] private WaferSummary? selectedSummary;
    [ObservableProperty] private int waferCount = 5;
    [ObservableProperty] private bool waitsOnly;
    [ObservableProperty] private double windowSeconds = 60;
    [ObservableProperty] private string message = "초기화 버튼을 눌러 시작";
    public string StateText => Snapshot.State.ToString();
    public IBrush StateBrush => Snapshot.State switch { EquipmentState.Running => Brushes.MediumSeaGreen, EquipmentState.Paused => Brushes.Gold, EquipmentState.Alarm => Brushes.OrangeRed, EquipmentState.Aborting => Brushes.OrangeRed, _ => Brushes.SteelBlue };
    public string SimTime => Snapshot.SimTime.ToString(@"hh\:mm\:ss\.fff");
    public string AverageTact => Snapshot.Stats.AverageTact?.TotalSeconds.ToString("F2") + " s";
    public string Uph => Snapshot.Stats.Uph?.ToString("F1") ?? "—";
    public string WaferProgress => $"{Snapshot.Stats.WafersDone}/{Snapshot.Stats.WafersTotal}";
    public string QueueText => $"{Snapshot.Stats.SaveQueueDepth}/{Snapshot.Stats.SaveQueueCapacity}";
    public double QueueFraction => Snapshot.Stats.SaveQueueCapacity == 0 ? 0 : (double)Snapshot.Stats.SaveQueueDepth / Snapshot.Stats.SaveQueueCapacity;
    public int AlarmCount => Snapshot.ActiveAlarms.Count;
    public bool CanInitialize => Snapshot.State is EquipmentState.Uninitialized or EquipmentState.Alarm;
    public bool CanStart => Snapshot.State == EquipmentState.Idle;
    public bool CanPause => Snapshot.State == EquipmentState.Running;
    public bool CanResume => Snapshot.State == EquipmentState.Paused;
    public bool CanAbort => Snapshot.State is EquipmentState.Running or EquipmentState.Paused;
    public bool CanReset => Snapshot.State is EquipmentState.Alarm or EquipmentState.Idle or EquipmentState.Completed;
    public IEnumerable<StepEvent> VisibleLogs => WaitsOnly ? Logs.Where(x => x.IsWait) : Logs;
    public MainViewModel()
    {
        snapshot = simulator.Snapshot;
        foreach (var step in simulator.Log.TakeLast(5000)) Logs.Add(step);
        RefreshSnapshot(snapshot);
        simulator.SnapshotChanged += value => Dispatcher.UIThread.Post(() => RefreshSnapshot(value));
        simulator.StepLogged += value => Dispatcher.UIThread.Post(() => AddStep(value));
        simulator.MeasurementCompleted += value => Dispatcher.UIThread.Post(() => { if (!measurements.TryGetValue(value.WaferId, out var list)) measurements[value.WaferId] = list = []; list.Add(value); OnPropertyChanged(nameof(Snapshot)); });
        simulator.WaferCompleted += value => Dispatcher.UIThread.Post(() => { Summaries.Add(value); SelectedSummary = value; });
        simulator.AlarmRaised += _ => Dispatcher.UIThread.Post(() => RefreshSnapshot(simulator.Snapshot));
        simulator.AlarmCleared += _ => Dispatcher.UIThread.Post(() => RefreshSnapshot(simulator.Snapshot));
    }
    partial void OnSnapshotChanged(EquipmentSnapshot value)
    {
        foreach (var name in new[] { nameof(StateText), nameof(StateBrush), nameof(SimTime), nameof(AverageTact), nameof(Uph), nameof(WaferProgress), nameof(QueueText), nameof(QueueFraction), nameof(AlarmCount), nameof(CanInitialize), nameof(CanStart), nameof(CanPause), nameof(CanResume), nameof(CanAbort), nameof(CanReset) }) OnPropertyChanged(name);
    }
    partial void OnWaitsOnlyChanged(bool value) => OnPropertyChanged(nameof(VisibleLogs));
    private void RefreshSnapshot(EquipmentSnapshot value)
    {
        Snapshot = value;
        Modules.Clear(); foreach (var module in value.Modules) Modules.Add(module);
        Alarms.Clear(); foreach (var alarm in value.ActiveAlarms) Alarms.Add(alarm);
        var max = Math.Max(1, value.Stats.PhaseTotals.Values.Select(t => t.TotalSeconds).DefaultIfEmpty(0).Max());
        Phases.Clear(); foreach (var phase in value.Stats.PhaseTotals.OrderByDescending(x => x.Value)) Phases.Add(new PhaseBar(phase.Key, phase.Value.TotalSeconds, phase.Value.TotalSeconds / max * 100));
        OnPropertyChanged(nameof(WaitRatio));
    }
    public string WaitRatio => $"대기 비율 {Snapshot.Stats.WaitRatio:P1}";
    private void AddStep(StepEvent step)
    {
        var existing = Logs.FirstOrDefault(x => x.Seq == step.Seq);
        if (existing is not null) Logs.Remove(existing);
        Logs.Add(step);
        while (Logs.Count > 5000) Logs.RemoveAt(0);
        OnPropertyChanged(nameof(VisibleLogs));
    }
    [RelayCommand] private async Task InitializeAsync() => Report(await simulator.InitializeAsync(CancellationToken.None));
    [RelayCommand] private async Task StartAsync()
    {
        if (WaferCount < 1) { Message = "웨이퍼 수는 1 이상이어야 함"; return; }
        if (!RecipeEditor.TryBuild(RecipeFields, out var recipe, out var error)) { Message = "레시피 입력 오류: " + error; return; }
        Logs.Clear(); Summaries.Clear(); measurements.Clear(); SelectedSummary = null;
        Report(await simulator.StartAsync(recipe, WaferCount, CancellationToken.None));
    }
    [RelayCommand] private void Pause() => simulator.Pause();
    [RelayCommand] private void Resume() => simulator.Resume();
    [RelayCommand] private async Task AbortAsync() => await simulator.AbortAsync();
    [RelayCommand] private async Task ResetAsync() => Report(await simulator.ResetAsync());
    [RelayCommand] private void Ack(string id) => simulator.AcknowledgeAlarm(id);
    [RelayCommand] private void RecipeFileTodo() => Message = "TODO: IRecipeFile 계약 연결 후 사용 가능";
    [RelayCommand] private void CompareTodo() => Message = "TODO: IRunComparer 계약 연결 후 사용 가능";
    private void Report(DeviceResult result) => Message = result.Ok ? "명령 완료" : $"{result.Code}: {result.Message}";
    public IReadOnlyList<MeasurementResult> CurrentMeasurements => Snapshot.CurrentWaferId is { } id && measurements.TryGetValue(id, out var list) ? list : [];
}
public sealed record PhaseBar(PhaseKind Phase, double Seconds, double Percent);
