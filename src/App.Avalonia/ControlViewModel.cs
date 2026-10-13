using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Sequence;
using Core.Visualization;
namespace App.Avalonia;
public partial class MainViewModel
{
    private readonly RecipeExperiment experiment = new();
    private CancellationTokenSource? comparisonCancel;
    [ObservableProperty] private string experimentText = "현재 레시피를 기준으로 캡처한 뒤, 레시피를 수정하고 비교 실행. 현재 로트는 유지됨.";
    public string ControlDetail => ControlText.Inspector(Snapshot, isReplayView);
    [RelayCommand] private void CommunicationOn() { if (!isReplayView) (simulator as SequenceEngine)?.SetCommunicationFailure(true); }
    [RelayCommand] private void CommunicationOff() { if (!isReplayView) (simulator as SequenceEngine)?.SetCommunicationFailure(false); }
    [RelayCommand] private void SensorOn() { if (!isReplayView) (simulator as SequenceEngine)?.SetSensorDelay(true); }
    [RelayCommand] private void SensorOff() { if (!isReplayView) (simulator as SequenceEngine)?.SetSensorDelay(false); }
    [RelayCommand] private void DatabaseOn() { if (!isReplayView) (simulator as SequenceEngine)?.SetDatabaseFailure(true); }
    [RelayCommand] private async Task DatabaseOffAsync()
    {
        if (isReplayView) return;
        try { if (simulator is SequenceEngine e) await e.RecoverStorageAsync(CancellationToken.None); Message = "DB 저널 복구 완료"; }
        catch (Exception e) { Message = "DB 복구 실패: " + e.Message; }
    }
    [RelayCommand] private async Task SafeRecoverAsync()
    {
        if (isReplayView || simulator is not SequenceEngine engine) return;
        var result = await engine.RecoverWafersAsync(default); Message = result.Message;
    }
    [RelayCommand] private void CancelRecovery() { if (!isReplayView) (simulator as SequenceEngine)?.CancelRecovery(); }
    [RelayCommand] private void CaptureBaseline()
    {
        if (comparisonCancel is not null) return;
        if (!RecipeEditor.TryBuild(RecipeGroups.SelectMany(g => g.Fields), out var recipe, out var error)) { ExperimentText = error; return; }
        experiment.Capture(recipe, WaferCount); ExperimentText = $"기준 캡처: {recipe.Name} · {WaferCount}장 · seed {recipe.Seed}. 레시피를 변경한 뒤 비교 실행.";
    }
    [RelayCommand] private async Task RunExperimentAsync()
    {
        if (comparisonCancel is not null) return;
        if (!RecipeEditor.TryBuild(RecipeGroups.SelectMany(g => g.Fields), out var recipe, out var error)) { ExperimentText = error; return; }
        using var cancel = new CancellationTokenSource(); comparisonCancel = cancel;
        try { ExperimentText = "독립 가상 엔진 비교 중 · 취소 가능"; var (a,b) = await Task.Run(() => experiment.CompareAsync(recipe, cancel.Token)); ExperimentText = RecipeExperiment.Describe(a,b); }
        catch (OperationCanceledException) { ExperimentText = "비교 취소됨 · 현재 로트 유지"; }
        catch (Exception e) { ExperimentText = "비교 실패: " + e.Message; }
        finally { comparisonCancel = null; }
    }
    [RelayCommand] private void CancelExperiment() => comparisonCancel?.Cancel();
}
