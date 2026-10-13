using Core.Sequence;
using Core.Visualization;
namespace App.WinForms;
public sealed partial class MainForm
{
    private readonly Label _controlDetail = new() { AutoSize = true, MaximumSize = new Size(365, 0) };
    private readonly FlowLayoutPanel _faultButtons = new() { AutoSize = true, MaximumSize = new Size(365, 0) };
    private readonly Label _experimentText = new() { AutoSize = true, MaximumSize = new Size(365, 0), Text = "현재 레시피 기준 캡처 → 수정 → 비교 실행" };
    private readonly RecipeExperiment _experiment = new();
    private CancellationTokenSource? _experimentCancel;
    private void BuildControlInspector()
    {
        var tab = new TabPage("제어") { AutoScroll = true };
        var content = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        void Button(FlowLayoutPanel target, string label, Func<Task> action)
        {
            var button = new Button { Text = label, AutoSize = true };
            button.Click += async (_, _) => { try { await action(); } catch (Exception e) { _message.Text = e.Message; } }; target.Controls.Add(button);
        }
        Task Fault(Action<SequenceEngine> action) { if (!_replayView && _sim is SequenceEngine engine) action(engine); return Task.CompletedTask; }
        Button(_faultButtons,"통신 단절",()=>Fault(e=>e.SetCommunicationFailure(true)));
        Button(_faultButtons,"통신 복원",()=>Fault(e=>e.SetCommunicationFailure(false)));
        Button(_faultButtons,"센서 지연",()=>Fault(e=>e.SetSensorDelay(true)));
        Button(_faultButtons,"센서 정상",()=>Fault(e=>e.SetSensorDelay(false)));
        Button(_faultButtons,"DB 실패",()=>Fault(e=>e.SetDatabaseFailure(true)));
        Button(_faultButtons,"DB 복구",async ()=> { if (!_replayView && _sim is SequenceEngine e) await e.RecoverStorageAsync(default); });
        Button(_faultButtons,"안전 회수",async ()=> { if (!_replayView && _sim is SequenceEngine e) _message.Text = (await e.RecoverWafersAsync(default)).Message; });
        Button(_faultButtons,"회수 중단",()=>Fault(e=>e.CancelRecovery()));
        content.Controls.Add(_faultButtons); content.Controls.Add(_controlDetail); tab.Controls.Add(content); _tabs.TabPages.Add(tab);
        var experimentTab = new TabPage("실험");
        var experiment = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        Button(experiment,"기준 캡처",()=> { if (_experimentCancel is not null) return Task.CompletedTask; if (_editor.Commit() is { } error) throw new ArgumentException(error); _experiment.Capture(_recipe.Build(), (int)_waferCount.Value); _experimentText.Text = "기준 캡처 완료 · 레시피 수정 후 비교 실행"; return Task.CompletedTask; });
        Button(experiment,"비교 실행",async ()=>
        {
            if (_experimentCancel is not null) return;
            if (_editor.Commit() is { } error) throw new ArgumentException(error);
            using var cancel = new CancellationTokenSource(); _experimentCancel = cancel;
            try { _experimentText.Text = "독립 가상 엔진 비교 중"; var recipe = _recipe.Build(); var (a,b) = await Task.Run(()=>_experiment.CompareAsync(recipe,cancel.Token)); _experimentText.Text = RecipeExperiment.Describe(a,b); }
            catch (OperationCanceledException) { _experimentText.Text = "비교 취소됨"; }
            finally { _experimentCancel = null; }
        });
        Button(experiment,"비교 취소",()=> { _experimentCancel?.Cancel(); return Task.CompletedTask; });
        experiment.Controls.Add(_experimentText); experimentTab.Controls.Add(experiment); _tabs.TabPages.Add(experimentTab);
    }
}
