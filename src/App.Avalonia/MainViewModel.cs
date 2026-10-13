using Core.Visualization;
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
    private readonly RunReplay replay = new();
    private readonly DispatcherTimer replayTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private volatile bool recordingRun;
    private bool replayPlaying;
    private bool isReplayView;
    private EquipmentSnapshot liveSnapshot;
    public double ReplayMinimum => replay.StartTime.TotalSeconds;
    public bool IsReplayView => isReplayView;
    public StepEvent? ActiveStep => replay.ActiveEvent(Snapshot.SimTime);
    public StepEvent? RobotStep => replay.ActiveEvent(Snapshot.SimTime, ModuleId.Robot);
    public double WaferDiameterMm => simulator.CurrentRecipe?.WaferDiameterMm ?? 300;
    public TimeSpan RobotMoveTime => simulator.CurrentRecipe?.RobotMoveTime ?? TimeSpan.FromSeconds(2.5);
    public string ReplayCoverage => replay.FramesTruncated || replay.EventsTruncated ? "기록 앞부분 잘림" : "기록 전체 보관";
    public double[] ReplaySpeeds { get; } = [0.5, 1, 2, 4];
    [ObservableProperty] private double replaySpeed = 1;
    [ObservableProperty] private double replayPosition;
    [ObservableProperty] private double replayMaximum = 1;
    public IEnumerable<StepEvent> TimelineSteps => isReplayView ? replay.VisibleEvents(Snapshot.SimTime) : Logs;
    public string ReplayMode => isReplayView ? replayPlaying ? "기록 재생 중 · 엔진 제어와 분리" : "기록 정지 · 엔진 제어와 분리" : "실시간 · 종료 후 기록 보기";
    public string ReplayTime => Snapshot.SimTime.ToString(@"hh\:mm\:ss\.fff");
    public bool CanReplay => replay.FrameCount > 0 && liveSnapshot.State is not (EquipmentState.Running or EquipmentState.Paused or EquipmentState.Aborting);
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
    public string StateText => Snapshot.State switch { EquipmentState.Uninitialized => "초기화 전", EquipmentState.Idle => "준비", EquipmentState.Running => "실행 중", EquipmentState.Paused => "일시정지", EquipmentState.Completed => "완료", EquipmentState.Alarm => "알람", EquipmentState.Aborting => "중단 중", _ => Snapshot.State.ToString() };
    public IBrush StateBrush => Snapshot.State switch { EquipmentState.Running => Brushes.MediumSeaGreen, EquipmentState.Paused => Brushes.Gold, EquipmentState.Alarm => Brushes.OrangeRed, EquipmentState.Aborting => Brushes.OrangeRed, _ => Brushes.SteelBlue };
    public string SimTime => Snapshot.SimTime.ToString(@"hh\:mm\:ss\.fff");
    public string AverageTact => Snapshot.Stats.AverageTact is { } t ? $"{t.TotalSeconds:F2} s" : "—";
    public string Uph => Snapshot.Stats.Uph?.ToString("F1") ?? "—";
    public string WaferProgress => $"{Snapshot.Stats.WafersDone}/{Snapshot.Stats.WafersTotal}";
    public string QueueText => $"{Snapshot.Stats.SaveQueueDepth}/{Snapshot.Stats.SaveQueueCapacity}";
    public double QueueFraction => Snapshot.Stats.SaveQueueCapacity == 0 ? 0 : (double)Snapshot.Stats.SaveQueueDepth / Snapshot.Stats.SaveQueueCapacity;
    public int AlarmCount => Snapshot.ActiveAlarms.Count;
    public bool CanInitialize => liveSnapshot.State == EquipmentState.Uninitialized;
    public bool CanStart => liveSnapshot.State is EquipmentState.Idle or EquipmentState.Completed;
    public bool CanPause => liveSnapshot.State == EquipmentState.Running;
    public bool CanResume => liveSnapshot.State == EquipmentState.Paused;
    public bool CanAbort => liveSnapshot.State is EquipmentState.Running or EquipmentState.Paused;
    public bool CanReset => liveSnapshot.State is EquipmentState.Alarm or EquipmentState.Idle or EquipmentState.Completed;
    public string WaitRatio => $"대기 비율 {Snapshot.Stats.WaitRatio:P1}";
    public MainViewModel(MainWindow owner)
    {
        this.owner = owner;
        snapshot = simulator.Snapshot;liveSnapshot=snapshot;
        LoadRecipe(new RecipeDto());
        snapshotHandler = value => { if(recordingRun) replay.Record(value); Dispatcher.UIThread.Post(() => { if (!disposed) RefreshSnapshot(value); }); };
        stepHandler = value => { if(recordingRun){replay.Record(value);replay.Record(simulator.Snapshot);} pendingSteps.Enqueue(value); };
        waferHandler = value => Dispatcher.UIThread.Post(() => { if (disposed) return; Summaries.Add(value); SelectedSummary ??= value; });
        runHandler = _ => Dispatcher.UIThread.Post(async () => { if (!disposed) await RefreshRunsAsync(); });
        simulator.SnapshotChanged += snapshotHandler;
        simulator.StepLogged += stepHandler;
        simulator.WaferCompleted += waferHandler;
        simulator.RunCompleted += runHandler;
        foreach (var step in simulator.Log.TakeLast(5000)) AddStep(step);
        RefreshSnapshot(snapshot);
        logTimer.Tick += LogTick; logTimer.Start();
        replayTimer.Tick += (_, _) => { if(!replayPlaying || !CanReplay || !isReplayView)return; ReplayPosition=Math.Min(ReplayMaximum,ReplayPosition+replayTimer.Interval.TotalSeconds*ReplaySpeed);if(ReplayPosition>=ReplayMaximum){replayPlaying=false;OnPropertyChanged(nameof(ReplayMode));} }; replayTimer.Start();
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
        RefreshVisibleLogs();
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
        var current=simulator.Snapshot;
        if(value.State is EquipmentState.Completed or EquipmentState.Idle or EquipmentState.Alarm && current.State==EquipmentState.Running)return;
        if(value.State==EquipmentState.Running && liveSnapshot.State==EquipmentState.Running && value.SimTime<liveSnapshot.SimTime)return;
        liveSnapshot=value;
        ReplayMaximum=Math.Max(1,replay.Duration.TotalSeconds);
        OnPropertyChanged(nameof(ReplayMinimum));OnPropertyChanged(nameof(ReplayCoverage));OnPropertyChanged(nameof(ReplayMode));
        foreach(var name in new[]{nameof(CanInitialize),nameof(CanStart),nameof(CanPause),nameof(CanResume),nameof(CanAbort),nameof(CanReset)})OnPropertyChanged(name);
        if(isReplayView)return;
        Snapshot=value;ReplayPosition=value.SimTime.TotalSeconds;
        UpdateDisplay(value);
    }
    private void UpdateDisplay(EquipmentSnapshot value)
    {
        UpdateByKey(Modules,value.Modules,x=>x.Module);
        UpdateByKey(Alarms,value.ActiveAlarms,x=>x.Id);
        var max=Math.Max(1,value.Stats.PhaseTotals.Values.Select(t=>t.TotalSeconds).DefaultIfEmpty(0).Max());
        UpdateByKey(Phases,value.Stats.PhaseTotals.OrderByDescending(x=>x.Value).Select(p=>new PhaseBar(p.Key,p.Value.TotalSeconds,p.Value.TotalSeconds/max*100)),x=>x.Phase);
        OnPropertyChanged(nameof(ReplayTime));OnPropertyChanged(nameof(ActiveStep));OnPropertyChanged(nameof(RobotStep));OnPropertyChanged(nameof(WaferDiameterMm));OnPropertyChanged(nameof(RobotMoveTime));
    }
    private void SampleLive()
    {
        if(!recordingRun)return;
        var value=simulator.Snapshot;
        if(value.State is EquipmentState.Running or EquipmentState.Paused or EquipmentState.Aborting)
        {
            replay.Record(value);
            RefreshSnapshot(value);
        }
        else if(replay.FrameCount>0 && value.State!=replay.LastFrame?.State)replay.Record(value);
    }
    private void LogTick(object? sender, EventArgs args)
    {
        SampleLive();
        var latest = new SortedDictionary<long, StepEvent>();
        while (pendingSteps.TryDequeue(out var step)) latest[step.Seq] = step;
        foreach (var step in latest.Values) AddStep(step);
    }
    partial void OnReplayPositionChanged(double value)
    {
        if(!isReplayView)return;
        var frame=replay.ViewAt(TimeSpan.FromSeconds(value));if(frame is null)return;
        Snapshot=frame;UpdateDisplay(frame);OnPropertyChanged(nameof(TimelineSteps));RefreshVisibleLogs();
    }
    private void RefreshVisibleLogs()
    {
        var source=isReplayView?replay.VisibleEvents(Snapshot.SimTime):Logs;
        var incoming=source.Where(e=>!WaitsOnly||e.IsWait).ToArray();
        var common=Math.Min(VisibleLogs.Count,incoming.Length);
        for(var i=0;i<common;i++)if(VisibleLogs[i]!=incoming[i])VisibleLogs[i]=incoming[i];
        while(VisibleLogs.Count>incoming.Length)VisibleLogs.RemoveAt(VisibleLogs.Count-1);
        for(var i=common;i<incoming.Length;i++)VisibleLogs.Add(incoming[i]);
    }
    [RelayCommand] private void EnterReplay()
    {
        if(!CanReplay)return;
        isReplayView=true;replayPlaying=false;ReplayPosition=replay.Duration.TotalSeconds;
        OnReplayPositionChanged(ReplayPosition);OnPropertyChanged(nameof(ReplayMode));OnPropertyChanged(nameof(IsReplayView));
    }
    [RelayCommand] private void ExitReplay()
    {
        isReplayView=false;replayPlaying=false;Snapshot=liveSnapshot;UpdateDisplay(liveSnapshot);
        RefreshVisibleLogs();OnPropertyChanged(nameof(TimelineSteps));OnPropertyChanged(nameof(ReplayMode));OnPropertyChanged(nameof(IsReplayView));
    }
    [RelayCommand] private void ToggleReplay(){if(!isReplayView)return;replayPlaying=!replayPlaying;if(replayPlaying&&ReplayPosition>=ReplayMaximum)ReplayPosition=ReplayMinimum;OnPropertyChanged(nameof(ReplayMode));}
    [RelayCommand] private void PreviousEvent(){if(isReplayView)ReplayPosition=replay.PreviousEvent(TimeSpan.FromSeconds(ReplayPosition)).TotalSeconds;}
    [RelayCommand] private void NextEvent(){if(isReplayView)ReplayPosition=replay.NextEvent(TimeSpan.FromSeconds(ReplayPosition)).TotalSeconds;}
    [RelayCommand] private void WaitingPreset(){LoadRecipe(ConceptPresets.WaitingExample());Message="가상 예시: 13점, 기본 진공 확인 3회. 좌표는 추정 배치";}
    [RelayCommand] private void ImprovedPreset(){LoadRecipe(ConceptPresets.ImprovementExample());Message="가상 개선: 대기 축소·Z 상승 축소·첫 지점 맵·비동기 저장. 접촉 확인 유지";}
    private void AddStep(StepEvent step)
    {
        if (isReplayView) { OnPropertyChanged(nameof(TimelineSteps)); }
        if (logIndex.TryGetValue(step.Seq, out var index))
        {
            Logs[index] = step;
            if (!isReplayView && (!WaitsOnly || step.IsWait))
            {
                for (var i = 0; i < VisibleLogs.Count; i++) if (VisibleLogs[i].Seq == step.Seq) { VisibleLogs[i] = step; break; }
            }
            return;
        }
        var lo = 0; var hi = Logs.Count;
        while (lo < hi) { var mid = (lo + hi) / 2; if (Logs[mid].Seq < step.Seq) lo = mid + 1; else hi = mid; }
        Logs.Insert(lo, step); for (var i = lo; i < Logs.Count; i++) logIndex[Logs[i].Seq] = i;
        if (!isReplayView && (!WaitsOnly || step.IsWait))
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
        if(recipe.ZLiftAfterMeasureUm<recipe.ZSafeHeightUm){Message="사이트 간 Z 높이는 안전 높이 이상이어야 함";return;}
        ExitReplay();replay.Clear();ReplayPosition=0;while(pendingSteps.TryDequeue(out _)){}Logs.Clear(); VisibleLogs.Clear(); logIndex.Clear(); Summaries.Clear(); SelectedSummary = null;
        Message = "로트 실행 중";
        recordingRun=true;
        try { var result=await simulator.StartAsync(recipe, WaferCount, CancellationToken.None);replay.Record(simulator.Snapshot);RefreshSnapshot(simulator.Snapshot);Report(result); }
        finally { recordingRun=false; }
    }
    [RelayCommand] private void Pause() => simulator.Pause();
    [RelayCommand] private void Resume() => simulator.Resume();
    [RelayCommand] private async Task AbortAsync() { await simulator.AbortAsync();replay.Record(simulator.Snapshot);RefreshSnapshot(simulator.Snapshot);Message = "중단 요청 완료"; }
    [RelayCommand] private async Task ResetAsync() { var result=await simulator.ResetAsync();if(result.Ok){ExitReplay();replay.Clear();ReplayPosition=0;ReplayMaximum=1;}Report(result); }
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
        ComparisonText = $"Tact 차이 {result.TactDelta.TotalSeconds:+0.000;-0.000;0} s · 예상 WPH 차이 {result.UphDelta:+0.0;-0.0;0}";
    }
    private void Report(DeviceResult result) => Message = result.Ok ? "명령 완료" : $"{result.Code}: {result.Message}";
    public void Dispose()
    {
        if (disposed) return; disposed = true; logTimer.Stop(); logTimer.Tick -= LogTick; replayTimer.Stop();
        simulator.SnapshotChanged -= snapshotHandler; simulator.StepLogged -= stepHandler;
        simulator.WaferCompleted -= waferHandler; simulator.RunCompleted -= runHandler;
    }
    public async Task StopAsync() { if (CanAbort) await simulator.AbortAsync(); Dispose(); }
}
public sealed record PhaseBar(PhaseKind Phase, double Seconds, double Percent);
