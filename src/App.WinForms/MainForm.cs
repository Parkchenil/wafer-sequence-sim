using Core.Visualization;
using Contracts;
using System.Collections.Concurrent;
using Core.Measurement;

namespace App.WinForms;

public sealed class MainForm : Form
{
    private readonly ISimulator _sim;
    private readonly RecipeEditModel _recipe = new();
    private readonly List<StepEvent> _events = new();
    private readonly Dictionary<long, int> _eventIndexes = new();
    private readonly ConcurrentQueue<StepEvent> _pendingSteps = new();
    private readonly List<int> _filteredIndexes = new();
    private readonly Dictionary<string, ListViewItem> _alarmItems = new();
    private readonly Dictionary<ModuleId, ListViewItem> _deviceItems = new();
    private readonly ComboBox _runA = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 165 };
    private readonly ComboBox _runB = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 165 };
    private readonly ListView _comparison = new() { Dock = DockStyle.Bottom, Height = 205, View = View.Details, FullRowSelect = true };
    private readonly Label _comparisonSummary = new() { Dock = DockStyle.Bottom, Height = 25, ForeColor = Color.WhiteSmoke };
    private readonly Dictionary<string, RunRecord> _runs = new();
    private readonly ToolStripStatusLabel _message = new() { Text = "준비됨", Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private Button? _initializeButton, _startButton, _pauseButton, _resumeButton, _abortButton, _resetButton;
    private readonly List<MeasurementResult> _measurements = new();
    private readonly List<WaferSummary> _summaries = new();
    private readonly SchematicPanel _schematic = new() { Dock = DockStyle.Fill };
    private readonly Equipment3DPanel _scene3D = new() { Dock = DockStyle.Fill };
    private readonly RunReplay _replay = new();
    private readonly TrackBar _replaySlider = new() { Minimum=0,Maximum=1000,TickFrequency=100,Dock=DockStyle.Bottom,Height=30,Enabled=false };
    private readonly Label _replayLabel = new() { Text="실시간 · 종료 후 기록 재생",AutoSize=true,ForeColor=Color.WhiteSmoke };
    private TimeSpan _replayCursor; private bool _updatingReplaySlider;
    private volatile bool _recordingRun; private bool _replayPlaying; private bool _replayView; private double _replaySpeed=1;
    private readonly GanttPanel _gantt = new() { Dock = DockStyle.Fill };
    private readonly WaferMapPanel _map = new() { Dock = DockStyle.Fill };
    private readonly StatsPanel _stats = new() { Dock = DockStyle.Fill };
    private readonly RecipeEditorPanel _editor;
    private readonly TableLayoutPanel _root = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty, Padding = Padding.Empty };
    private readonly NumericUpDown _waferCount = new() { Minimum = 1, Maximum = 25, Value = 1, Width = 85 };
    private readonly ListView _log = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, VirtualMode = true };
    private readonly ListView _alarms = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };
    private readonly ListView _devices = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };
    private readonly CheckBox _waitOnly = new() { Text = "대기만", Dock = DockStyle.Top, ForeColor = Color.WhiteSmoke };
    private readonly Label _state = new() { Text = "미초기화" };
    private readonly Label _time = new() { Text = "00:00:00" };
    private readonly Label _tact = new() { Text = "—" };
    private readonly Label _uph = new() { Text = "—" };
    private readonly Label _count = new() { Text = "0/0" };
    private readonly Label _alarmCount = new() { Text = "0" };
    private readonly Label _queue = new() { Text = "0/0" };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };
    private readonly SplitContainer _right = new() { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2, IsSplitterFixed = true, BackColor = DashboardTheme.Background };
    private readonly ToolTip _metricTip = new();
    private readonly TableLayoutPanel _body = new() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
    private readonly TableLayoutPanel _content = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10) };
    private Form? _recipeWindow;
    private readonly FlowLayoutPanel _recipeControls = new() { Dock = DockStyle.Top, Height = 105, WrapContents = true };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private bool _schematicDirty = true, _ganttDirty = true, _mapDirty = true, _statsDirty = true;
    private bool _closing;
    private Task? _runTask;

    private bool _busy;

    public MainForm(ISimulator simulator)
    {
        _sim = simulator;
        if (simulator.CurrentRecipe is { } currentRecipe) _recipe.Load(currentRecipe);
        _editor = new RecipeEditorPanel(_recipe);
        Text = "Wafer Sequence Simulator · HMI";
        ClientSize = new Size(1280, 800);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);
        BackColor = DashboardTheme.Background;
        MinimumSize = new Size(1280, 800);
        BuildLayout();
        DashboardTheme.Apply(this);
        _log.RetrieveVirtualItem += (_, e) => e.Item = LogItem(_events[_filteredIndexes[_filteredIndexes.Count - 1 - e.ItemIndex]]);
        _waitOnly.CheckedChanged += (_, _) => RefreshLog();
        _sim.SnapshotChanged += SnapshotChanged;
        _sim.StepLogged += StepLogged;
        _sim.MeasurementCompleted += MeasurementCompleted;
        _sim.WaferCompleted += WaferCompleted;
        _sim.AlarmRaised += AlarmChanged;
        _sim.AlarmCleared += AlarmChanged;
        _sim.RunCompleted += RunCompleted;
        ApplyCompletedRun();
        _timer.Tick += (_, _) =>
        {
            var sampled=_sim.Snapshot;
            if(_recordingRun && sampled.State is (EquipmentState.Running or EquipmentState.Paused or EquipmentState.Aborting)){_replay.Record(sampled);if(!_replayView)ApplySnapshot(sampled);}
            DrainSteps();
            if (_schematicDirty) { _schematic.Invalidate(); _scene3D.Invalidate(); _schematicDirty = false; }
            if(_replayPlaying && _replayView){SeekReplay(_replayCursor+TimeSpan.FromSeconds(_timer.Interval/1000d*_replaySpeed));if(_replayCursor>=_replay.Duration)_replayPlaying=false;}
            if (_ganttDirty) { _gantt.Invalidate(); _ganttDirty = false; }
            if (_mapDirty) { _map.Invalidate(); _mapDirty = false; }
            if (_statsDirty) { _stats.Invalidate(); _statsDirty = false; }
        };
        _timer.Start();
        Shown += (_, _) => { ConfigureLayout(); _ = RunUiTask(RefreshRunsAsync); };
        _right.SizeChanged += (_, _) => ConfigureLayout();
        FormClosing += HandleFormClosing;
    }

    private void BuildLayout()
    {
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = DashboardTheme.Background, Padding = new Padding(8, 5, 8, 5) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(new Label { Text = "WAFER SEQUENCE SIMULATOR   ·   분석 기반 개념 모델", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font, FontStyle.Bold) }, 0, 0);
        var recipeButton = new Button { Text = "레시피 열기", AutoSize = true, Height = 30 };
        recipeButton.Click += (_, _) => OpenRecipeWindow();
        header.Controls.Add(recipeButton, 1, 0);
        var controls = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        header.Controls.Add(controls, 2, 0);
        var metrics = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 7, RowCount = 1, BackColor = DashboardTheme.Background, Padding = new Padding(0, 0, 0, 8) };
        for (var i = 0; i < 7; i++) metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 7));
        void Metric(string title, Label value, int index)
        {
            var card = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, BackColor = DashboardTheme.Panel, Margin = new Padding(3), Padding = new Padding(10, 7, 5, 4) };
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 21)); card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            card.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, ForeColor = DashboardTheme.Muted }, 0, 0);
            value.Dock = DockStyle.Fill; value.Font = new Font(Font.FontFamily, 11, FontStyle.Bold);
            card.Controls.Add(value, 0, 1); metrics.Controls.Add(card, index, 0);
        }
        Metric("장비 상태", _state, 0); Metric("시뮬레이션 시간", _time, 1); Metric("평균 Tact", _tact, 2);
        Metric("예상 WPH · 장/시간", _uph, 3); Metric("완료 / 전체", _count, 4); Metric("저장 큐", _queue, 5); Metric("활성 알람", _alarmCount, 6);
        _metricTip.SetToolTip(_uph, "3600 ÷ 완료 웨이퍼 평균 Tact(초). 첫 웨이퍼 완료 후 표시. 전체 경과시간 기준 실적 처리량과 다름.");
        var status = new StatusStrip { Dock = DockStyle.Fill, BackColor = DashboardTheme.Background, ForeColor = DashboardTheme.Accent, SizingGrip = false };
        status.Items.Add(_message);
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        _root.Controls.Add(header, 0, 0); _root.Controls.Add(_body, 0, 1); _root.Controls.Add(status, 0, 2);
        Controls.Add(_root);
        _body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62)); _body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var nav = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = DashboardTheme.Panel };
        var navNames = new[] { "로그", "맵", "통계", "알람", "장치" };
        for (var i = 0; i < navNames.Length; i++)
        {
            var index = i;
            var button = new Button { Text = navNames[i], Width = 58, Height = 46, Margin = new Padding(2, 6, 2, 0) };
            button.Click += (_, _) => _tabs.SelectedIndex = index;
            nav.Controls.Add(button);
        }
        _body.Controls.Add(nav, 0, 0); _body.Controls.Add(_content, 1, 0);
        _content.RowStyles.Add(new RowStyle(SizeType.Absolute, 87));
        _content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _content.RowStyles.Add(new RowStyle(SizeType.Absolute, 252));
        _content.Controls.Add(metrics, 0, 0);
        _content.Controls.Add(_right, 0, 1);
        _initializeButton = AddButton(controls, "초기화", () => Command(() => _sim.InitializeAsync(CancellationToken.None)));
        _startButton = AddButton(controls, "시작", () =>
        {
            var validation = _editor.Commit();
            if (validation is not null) { ShowError(validation); return Task.CompletedTask; }
            var candidate=_recipe.Build();
            if(candidate.ZLiftAfterMeasureUm<candidate.ZSafeHeightUm){ShowError("사이트 간 Z 높이는 안전 높이 이상이어야 함");return Task.CompletedTask;}
            ResetRunView();
            _recordingRun=true;
            _runTask = RunStartAsync();
            return _runTask;
        });
        _pauseButton = AddButton(controls, "일시정지", () => { _sim.Pause(); return Task.CompletedTask; });
        _resumeButton = AddButton(controls, "재개", () => { _sim.Resume(); return Task.CompletedTask; });
        _abortButton = AddButton(controls, "중단", async () => { await _sim.AbortAsync();_replay.Record(_sim.Snapshot);ApplySnapshot(_sim.Snapshot); });
        _resetButton = AddButton(controls, "리셋", async () => { await Command(() => _sim.ResetAsync());if(_sim.Snapshot.State==EquipmentState.Uninitialized)ResetRunView(); });
        var recipeControls = _recipeControls;
        AddButton(recipeControls, "대기 13점", () => { _recipe.Load(ConceptPresets.WaitingExample());_editor.Reload();_message.Text="가상 13점 예시 · 실제 레시피 아님";return Task.CompletedTask; });
        AddButton(recipeControls, "개선 13점", () => { _recipe.Load(ConceptPresets.ImprovementExample());_editor.Reload();_message.Text="가상 개선: 대기/Z/맵/중첩 설정";return Task.CompletedTask; });
        AddButton(recipeControls, "레시피 저장", SaveRecipeAsync);
        AddButton(recipeControls, "레시피 불러오기", LoadRecipeAsync);

        var sceneBox=new Panel { Dock=DockStyle.Fill };
        var sceneTools=new FlowLayoutPanel { Dock=DockStyle.Top,Height=31,WrapContents=false };
        void Tool(string text,Action action){var b=new Button { Text=text,AutoSize=true,Height=27 };b.Click+=(_,_)=>action();sceneTools.Controls.Add(b);}
        Tool("기본 시점",()=>_scene3D.ResetCamera());Tool("측정부 확대",()=>{_scene3D.CloseUp=!_scene3D.CloseUp;_scene3D.Invalidate();});Tool("외장 숨김",()=>{_scene3D.Cutaway=!_scene3D.Cutaway;_scene3D.Invalidate();});
        Tool("2D/3D",()=>{_scene3D.Visible=!_scene3D.Visible;_schematic.Visible=!_scene3D.Visible;});
        Tool("확대 창",()=>{
            var view=new Equipment3DPanel { Dock=DockStyle.Fill };
            var window=new Form { Text="3D 개념 장비",ClientSize=new Size(1100,750) };
            var bar=new FlowLayoutPanel { Dock=DockStyle.Top,Height=32,WrapContents=false };
            void Add(string text,Action action){var button=new Button { Text=text,AutoSize=true };button.Click+=(_,_)=>action();bar.Controls.Add(button);}
            Add("◀ 회전",()=>view.Rotate(-.15f,0));Add("회전 ▶",()=>view.Rotate(.15f,0));Add("확대 +",()=>view.Zoom(1.2f));Add("축소 −",()=>view.Zoom(.8f));Add("기본 시점",view.ResetCamera);
            window.Controls.Add(view);window.Controls.Add(bar);DashboardTheme.Apply(window);
            var timer=new System.Windows.Forms.Timer { Interval=100 };timer.Tick+=(_,_)=>{view.Snapshot=_scene3D.Snapshot;view.ActiveStep=_scene3D.ActiveStep;view.RobotStep=_scene3D.RobotStep;view.WaferDiameterMm=_scene3D.WaferDiameterMm;view.RobotMoveTime=_scene3D.RobotMoveTime;view.Invalidate();};
            window.FormClosed+=(_,_)=>timer.Dispose();timer.Start();window.Show(this);
        });
        sceneBox.Controls.Add(_scene3D);sceneBox.Controls.Add(_schematic);_schematic.Visible=false;sceneBox.Controls.Add(sceneTools);_right.Panel1.Controls.Add(sceneBox);
        var ganttBox = new Panel { Dock = DockStyle.Fill };
        var slider = new TrackBar { Dock = DockStyle.Bottom, Minimum = 10, Maximum = 300, Value = 60, TickFrequency = 30, Height = 38 };
        slider.ValueChanged += (_, _) => _gantt.WindowSeconds = slider.Value;
        var replayTools=new FlowLayoutPanel { Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,WrapContents=true };replayTools.Controls.Add(_replayLabel);
        void ReplayButton(string text,Action action){var b=new Button { Text=text,AutoSize=true,Height=26 };b.Click+=(_,_)=>action();replayTools.Controls.Add(b);}
        ReplayButton("기록 보기",EnterReplay);ReplayButton("실시간",ExitReplay);
        ReplayButton("◀",()=>SeekReplay(_replay.PreviousEvent(_replayCursor)));
        ReplayButton("재생/정지",()=>{if(_replayView){if(!_replayPlaying && _replayCursor>=_replay.Duration)SeekReplay(_replay.StartTime);_replayPlaying=!_replayPlaying;_replayLabel.Text=_replayPlaying?"기록 재생 중":"기록 정지";}});
        ReplayButton("▶",()=>SeekReplay(_replay.NextEvent(_replayCursor)));
        ReplayButton("배속",()=>{_replaySpeed=_replaySpeed==1?2:_replaySpeed==2?4:1;_replayLabel.Text=$"기록 재생 · {_replaySpeed}×";});
        _replaySlider.ValueChanged+=(_,_)=>{if(_replayView && !_updatingReplaySlider)SeekReplay(TimeSpan.FromSeconds(_replay.Duration.TotalSeconds*_replaySlider.Value/1000d),false);};
        sceneBox.Controls.Add(_replaySlider);sceneBox.Controls.Add(replayTools);replayTools.Dock=DockStyle.Bottom;
        _replaySlider.TickStyle=TickStyle.None;
        _metricTip.SetToolTip(_replaySlider, "기록 재생 위치");
        _metricTip.SetToolTip(slider, "타임라인에 표시할 최근 시간(초)");
        ganttBox.Controls.Add(_gantt);ganttBox.Controls.Add(slider);
        _content.Controls.Add(ganttBox, 0, 2);
        _log.Columns.Add("Seq", 48); _log.Columns.Add("시각", 80); _log.Columns.Add("웨이퍼", 60); _log.Columns.Add("사이트", 53); _log.Columns.Add("모듈", 70); _log.Columns.Add("단계", 105); _log.Columns.Add("소요", 65); _log.Columns.Add("대기사유", 135); _log.Columns.Add("비고", 150);
        var logTab = new TabPage("로그"); logTab.Controls.Add(_log); logTab.Controls.Add(_waitOnly);
        var mapTab = new TabPage("웨이퍼 맵"); mapTab.Controls.Add(_map);
        var statsTab = new TabPage("통계");
        var compareSelectors = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36, WrapContents = false };
        compareSelectors.Controls.Add(new Label { Text = "A", AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
        compareSelectors.Controls.Add(_runA);
        compareSelectors.Controls.Add(new Label { Text = "B", AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
        compareSelectors.Controls.Add(_runB);
        _runA.SelectedIndexChanged += (_, _) => CompareRuns();
        _runB.SelectedIndexChanged += (_, _) => CompareRuns();
        _comparison.Columns.Add("단계", 100); _comparison.Columns.Add("A", 80); _comparison.Columns.Add("B", 80); _comparison.Columns.Add("차이", 80);
        statsTab.AutoScroll = true;
        var statsLayout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 4 };
        foreach (var height in new[] { 180, 72, 42, 205 }) statsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        compareSelectors.Dock = DockStyle.Fill; compareSelectors.WrapContents = true;
        _runA.Width = 140; _runB.Width = 140;
        _comparisonSummary.Dock = DockStyle.Fill; _comparison.Dock = DockStyle.Fill;
        statsLayout.Controls.Add(_stats, 0, 0); statsLayout.Controls.Add(compareSelectors, 0, 1);
        statsLayout.Controls.Add(_comparisonSummary, 0, 2); statsLayout.Controls.Add(_comparison, 0, 3);
        statsTab.Controls.Add(statsLayout);
        _alarms.Columns.Add("시각", 75); _alarms.Columns.Add("코드", 75); _alarms.Columns.Add("모듈", 85); _alarms.Columns.Add("내용", 200); _alarms.Columns.Add("확인", 50);
        var alarmTab = new TabPage("알람"); var ack = new Button { Text = "선택 알람 확인", Dock = DockStyle.Bottom, Height = 30 };
        ack.Click += (_, _) => _ = RunUiTask(() =>
        {
            foreach (ListViewItem item in _alarms.SelectedItems) if (item.Tag is string id) _sim.AcknowledgeAlarm(id);
            ApplySnapshot(_sim.Snapshot);
            return Task.CompletedTask;
        });
        alarmTab.Controls.Add(_alarms); alarmTab.Controls.Add(ack);
        _devices.Columns.Add("모듈", 95); _devices.Columns.Add("상태", 95); _devices.Columns.Add("설명", 180);
        var deviceTab = new TabPage("장치"); deviceTab.Controls.Add(_devices);
        _tabs.TabPages.AddRange(new[] { logTab, mapTab, statsTab, alarmTab, deviceTab }); _right.Panel2.Controls.Add(_tabs);
    }

    private void OpenRecipeWindow()
    {
        if (_recipeWindow is { IsDisposed: false }) { _recipeWindow.Show(this); _recipeWindow.Activate(); return; }
        _recipeWindow = new Form { Text = "레시피", ClientSize = new Size(420, 680), MinimumSize = new Size(350, 480), StartPosition = FormStartPosition.CenterParent, BackColor = DashboardTheme.Panel };
        var waferRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42 };
        waferRow.Controls.Add(new Label { Text = "웨이퍼 수", Width = 95, Padding = new Padding(6, 8, 0, 0) });
        waferRow.Controls.Add(_waferCount);
        _recipeWindow.Controls.Add(_editor); _recipeWindow.Controls.Add(_recipeControls); _recipeWindow.Controls.Add(waferRow);
        DashboardTheme.Apply(_recipeWindow);
        _recipeWindow.FormClosing += (_, e) => { if (!_closing) { e.Cancel = true; _recipeWindow.Hide(); } };
        _recipeWindow.Show(this);
    }

    public void ConfigureLayout()
    {
        // 크기 변경 중 두 패널의 최소 너비를 먼저 만족시킨 뒤 분할 위치를 조정한다.
        var scale = DeviceDpi / 96f;
        var leftMinimum = (int)(460 * scale);
        var inspectorWidth = (int)(400 * scale);
        var available = _right.ClientSize.Width - _right.SplitterWidth;
        if (available < leftMinimum + inspectorWidth) return;
        _right.Panel1MinSize = leftMinimum; _right.Panel2MinSize = inspectorWidth;
        var split = available - inspectorWidth;
        if (_right.SplitterDistance != split) _right.SplitterDistance = split;
    }

    public void PrepareScreenshotLayout(Size size)
    {
        _root.Dock = DockStyle.None;
        _root.Bounds = new Rectangle(Point.Empty, size);
        _root.PerformLayout();
        ConfigureLayout();
    }

    public void DrawScreenshot(Bitmap bitmap)
    {
        _root.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
    }

    public void SelectScreenshotTab(int index)
    {
        _tabs.SelectedIndex = index;
        _tabs.PerformLayout();
        PerformLayout();
        Refresh();
    }

    private Button AddButton(Control parent, string title, Func<Task> action)
    {
        var button = new Button { Text = title, Width = title.Length > 8 ? 128 : 80, Height = 29 };
        button.Click += (_, _) => _ = RunUiTask(action);
        parent.Controls.Add(button);
        return button;
    }
    private async Task RunStartAsync()
    {
        try { await Command(() => _sim.StartAsync(_recipe.Build(),(int)_waferCount.Value,CancellationToken.None)); }
        finally { _recordingRun=false; }
    }
    private async Task RunUiTask(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { if (!_closing) ShowError("작업 취소됨"); }
        catch (Exception ex) { ShowError(ex.ToString()); }
    }
    private async Task Command(Func<Task<DeviceResult>> action)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var result = await action();
            if (!result.Ok) ShowError($"{result.Code}: {result.Message}");
            else
            {
                var stats = _sim.Snapshot.Stats;
                _message.Text = stats.WafersTotal > 0 && stats.WafersDone == stats.WafersTotal ? "로트 완료" : "명령 완료";
                _message.ForeColor = Color.WhiteSmoke;
            }
        }
        finally { _busy = false; if (!_closing){if(_recordingRun)_replay.Record(_sim.Snapshot);ApplySnapshot(_sim.Snapshot);} }
    }
    private void ShowError(string message)
    {
        if (_closing || IsDisposed) return;
        var line = message.Replace(Environment.NewLine, " ");
        _message.Text = line[..Math.Min(180, line.Length)];
        _message.ForeColor = Color.OrangeRed;
        Console.Error.WriteLine(message);
    }
    private void OnUi(Action action)
    {
        if (_closing || IsDisposed || !IsHandleCreated) return;
        void SafeAction()
        {
            if (_closing || IsDisposed) return;
            try { action(); }
            catch (Exception ex) { Console.Error.WriteLine($"UI 이벤트 처리 실패: {ex}"); }
        }
        try { if (InvokeRequired) BeginInvoke((Action)SafeAction); else SafeAction(); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { if (!_closing) Console.Error.WriteLine("UI 핸들 전환 중 이벤트 무시"); }
    }
    private void SnapshotChanged(EquipmentSnapshot snapshot){if(_recordingRun)_replay.Record(snapshot);OnUi(() => ApplySnapshot(snapshot));}
    private void StepLogged(StepEvent ev){if(_recordingRun){_replay.Record(ev);_replay.Record(_sim.Snapshot);}_pendingSteps.Enqueue(ev);}
    private void MeasurementCompleted(MeasurementResult result) => OnUi(() =>
    {
        if (_measurements.Count == 5000) _measurements.RemoveAt(0);
        _measurements.Add(result); _map.Measurements = _measurements; _mapDirty = true;
    });
    private void WaferCompleted(WaferSummary summary) => OnUi(() => { _summaries.Add(summary); _map.Summary = summary; _mapDirty = true; });
    private void AlarmChanged(Alarm _) => OnUi(() => ApplySnapshot(_sim.Snapshot));
    private void RunCompleted(RunRecord run) => OnUi(() =>
    {
        _replay.Record(_sim.Snapshot);_message.Text = "로트 완료";_replayLabel.Text="실시간 · 기록 보기 가능";
        _message.ForeColor = Color.WhiteSmoke;
        _ = RunUiTask(RefreshRunsAsync);
    });
    private void ResetRunView()
    {
        _replayView=false;_replay.Clear();_replayPlaying=false;_replaySlider.Enabled=false;_replaySlider.Minimum=0;_replaySlider.Value=0;_replayLabel.Text="실시간 · 종료 후 기록 재생";
        while (_pendingSteps.TryDequeue(out _)) { }
        _events.Clear(); _eventIndexes.Clear(); _filteredIndexes.Clear(); _log.VirtualListSize = 0;
        _measurements.Clear(); _summaries.Clear(); _map.Measurements = _measurements; _map.Summary = null;
        _gantt.Steps = Array.Empty<StepEvent>(); _mapDirty = _ganttDirty = true;
    }
    private void DrainSteps()
    {
        if (_pendingSteps.IsEmpty) return;
        if(_replayView){while(_pendingSteps.TryDequeue(out _)){}return;}
        var changed = false;
        while (_pendingSteps.TryDequeue(out var ev))
        {
            if (_eventIndexes.TryGetValue(ev.Seq, out var index)) _events[index] = ev;
            else
            {
                if (_events.Count == 5000)
                {
                    _events.RemoveRange(0, 500);
                    _eventIndexes.Clear();
                    for (var i = 0; i < _events.Count; i++) _eventIndexes[_events[i].Seq] = i;
                    RebuildFilter();
                }
                if (_events.Count > 0 && ev.Seq < _events[0].Seq) continue;
                _eventIndexes[ev.Seq] = _events.Count;
                _events.Add(ev);
                if (!_waitOnly.Checked || ev.IsWait) _filteredIndexes.Add(_events.Count - 1);
            }
            changed = true;
        }
        if (!changed) return;
        _log.VirtualListSize = _filteredIndexes.Count;
        _log.Invalidate();
        _gantt.Steps = _events.OrderBy(x => x.Start).ToArray();
        _ganttDirty = true;
    }
    private void RebuildFilter()
    {
        _filteredIndexes.Clear();
        for (var i = 0; i < _events.Count; i++) if (!_waitOnly.Checked || _events[i].IsWait) _filteredIndexes.Add(i);
        _log.VirtualListSize = _filteredIndexes.Count;
    }
    private void RefreshLog() => RebuildFilter();
    private void EnterReplay()
    {
        if(_replay.FrameCount==0 || _sim.Snapshot.State is EquipmentState.Running or EquipmentState.Paused or EquipmentState.Aborting)return;
        _replayView=true;_replayPlaying=false;_replaySlider.Enabled=true;
        _replaySlider.Minimum=(int)Math.Clamp(Math.Ceiling(_replay.StartTime.TotalSeconds/Math.Max(.001,_replay.Duration.TotalSeconds)*1000),0,1000);
        SeekReplay(_replay.Duration);
    }
    private void ExitReplay()
    {
        _replayView=false;_replayPlaying=false;_replaySlider.Enabled=false;_replayLabel.Text="실시간 · 종료 후 기록 보기";
        _events.Clear();_eventIndexes.Clear();foreach(var ev in _sim.Log.GroupBy(e=>e.Seq).Select(g=>g.Last()).TakeLast(5000)){_eventIndexes[ev.Seq]=_events.Count;_events.Add(ev);}
        RebuildFilter();_gantt.Steps=_events;ApplySnapshot(_sim.Snapshot);
    }
    private void SeekReplay(TimeSpan requested,bool updateSlider=true)
    {
        if(!_replayView)return;
        requested=TimeSpan.FromTicks(Math.Clamp(requested.Ticks,_replay.StartTime.Ticks,_replay.Duration.Ticks));
        _replayCursor=requested;
        var frame=_replay.ViewAt(requested);if(frame is null)return;
        var time=frame.SimTime;
        if(updateSlider){_updatingReplaySlider=true;try{_replaySlider.Value=(int)Math.Clamp(requested.TotalSeconds/Math.Max(.001,_replay.Duration.TotalSeconds)*1000,_replaySlider.Minimum,1000);}finally{_updatingReplaySlider=false;}}
        ApplySnapshot(frame,true);
        _gantt.Steps=_replay.VisibleEvents(time);_ganttDirty=true;
        _replayLabel.Text=$"기록 {(_replayPlaying?"재생 중":"정지")} {time.TotalSeconds:F2}s · {_replaySpeed}×"+(_replay.FramesTruncated||_replay.EventsTruncated?" · 앞부분 잘림":"");
        var visible=_replay.VisibleEvents(time);
        if(!_events.SequenceEqual(visible))
        {
            _events.Clear();_eventIndexes.Clear();
            foreach(var ev in visible){_eventIndexes[ev.Seq]=_events.Count;_events.Add(ev);}
            RebuildFilter();_log.Invalidate();
        }
    }
    private static ListViewItem LogItem(StepEvent ev)
    {
        var item = new ListViewItem(ev.Seq.ToString());
        item.SubItems.AddRange(new[] { ev.Start.ToString(@"hh\:mm\:ss\.fff"), ev.WaferId, ev.SiteIndex?.ToString() ?? "—", ev.Module.ToString(), ev.Phase.ToString(), ev.Duration is { } d ? $"{d.TotalMilliseconds:0} ms" : "…", ev.WaitReason ?? "", ev.Note });
        item.ToolTipText = string.Join("  |  ", item.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(part => part.Text));
        return item;
    }
    private void UpdateLiveButtons(EquipmentSnapshot live)
    {
        if (_initializeButton is not null) _initializeButton.Enabled=live.State==EquipmentState.Uninitialized;
        if (_startButton is not null) _startButton.Enabled=live.State is EquipmentState.Idle or EquipmentState.Completed;
        if (_pauseButton is not null) _pauseButton.Enabled=live.State==EquipmentState.Running;
        if (_resumeButton is not null) _resumeButton.Enabled=live.State==EquipmentState.Paused;
        if (_abortButton is not null) _abortButton.Enabled=live.State is EquipmentState.Running or EquipmentState.Paused or EquipmentState.Aborting;
        if (_resetButton is not null) _resetButton.Enabled=(live.State is EquipmentState.Alarm or EquipmentState.Idle or EquipmentState.Completed)&&!live.ActiveAlarms.Any(a=>a.Severity==AlarmSeverity.Error&&!a.Acked);
    }
    private void ApplySnapshot(EquipmentSnapshot snapshot,bool historical=false)
    {
        if(_replayView && !historical){UpdateLiveButtons(_sim.Snapshot);return;}
        _scene3D.Snapshot=snapshot;_scene3D.WaferDiameterMm=_sim.CurrentRecipe?.WaferDiameterMm??_recipe.Recipe.WaferDiameterMm;_scene3D.RobotMoveTime=_sim.CurrentRecipe?.RobotMoveTime??_recipe.Recipe.RobotMoveTime;_scene3D.ActiveStep=_replay.ActiveEvent(snapshot.SimTime);_scene3D.RobotStep=_replay.ActiveEvent(snapshot.SimTime,ModuleId.Robot);
        _schematic.Snapshot = snapshot; _schematic.WaferDiameterMm = _sim.CurrentRecipe?.WaferDiameterMm ?? _recipe.Recipe.WaferDiameterMm; _map.WaferDiameterMm = _schematic.WaferDiameterMm; _gantt.Now = snapshot.SimTime; _map.Snapshot = snapshot; _stats.Totals = snapshot.Stats.PhaseTotals;
        _schematicDirty = _ganttDirty = _mapDirty = _statsDirty = true;
        _state.Text = snapshot.State switch { EquipmentState.Uninitialized => "초기화 전", EquipmentState.Idle => "준비", EquipmentState.Running => "실행 중", EquipmentState.Paused => "일시정지", EquipmentState.Completed => "완료", EquipmentState.Alarm => "알람", EquipmentState.Aborting => "중단 중", _ => snapshot.State.ToString() }; _state.ForeColor = snapshot.State == EquipmentState.Alarm ? DashboardTheme.Accent : DashboardTheme.Text;
        _time.Text = $"{snapshot.SimTime:hh\\:mm\\:ss}";
        _tact.Text = snapshot.Stats.AverageTact is { } tact ? $"{tact.TotalSeconds:0.0}초" : "—";
        _uph.Text = snapshot.Stats.Uph is { } uph ? $"{uph:0.0}" : "—";
        _count.Text = $"{snapshot.Stats.WafersDone}/{snapshot.Stats.WafersTotal}";
        _queue.Text = $"{snapshot.Stats.SaveQueueDepth}/{snapshot.Stats.SaveQueueCapacity}";
        _alarmCount.Text = $"{snapshot.ActiveAlarms.Count}";
        UpdateLiveButtons(_sim.Snapshot);
        _alarms.BeginUpdate();
        var activeIds = snapshot.ActiveAlarms.Select(a => a.Id).ToHashSet();
        foreach (var old in _alarmItems.Keys.Where(id => !activeIds.Contains(id)).ToArray())
        {
            _alarms.Items.Remove(_alarmItems[old]); _alarmItems.Remove(old);
        }
        foreach (var alarm in snapshot.ActiveAlarms)
        {
            if (!_alarmItems.TryGetValue(alarm.Id, out var item))
            {
                item = new ListViewItem(alarm.SetAt.ToString(@"hh\:mm\:ss")) { Tag = alarm.Id };
                item.SubItems.AddRange(new[] { alarm.Code.ToString(), alarm.Module.ToString(), alarm.Message, alarm.Acked ? "완료" : "대기" });
                _alarmItems[alarm.Id] = item; _alarms.Items.Add(item);
            }
            else
            {
                item.SubItems[1].Text = alarm.Code.ToString(); item.SubItems[2].Text = alarm.Module.ToString();
                item.SubItems[3].Text = alarm.Message; item.SubItems[4].Text = alarm.Acked ? "완료" : "대기";
            }
        }
        _alarms.EndUpdate(); _devices.BeginUpdate();
        foreach (var module in snapshot.Modules)
        {
            if (!_deviceItems.TryGetValue(module.Module, out var item))
            {
                item = new ListViewItem(module.Module.ToString()); item.SubItems.AddRange(new[] { module.State.ToString(), module.Detail });
                _deviceItems[module.Module] = item; _devices.Items.Add(item);
            }
            else { item.SubItems[1].Text = module.State.ToString(); item.SubItems[2].Text = module.Detail; }
        }
        _devices.EndUpdate();
    }
    public void ApplyCompletedRun()
    {
        LoadCapturedState(_sim.Snapshot, _sim.Log, _measurements, _summaries);
        var stats = _sim.Snapshot.Stats;
        if (stats.WafersTotal > 0 && stats.WafersDone == stats.WafersTotal)
        {
            _replay.Record(_sim.Snapshot);_message.Text = "로트 완료";_replayLabel.Text="실시간 · 기록 보기 가능";
            _message.ForeColor = Color.WhiteSmoke;
        }
    }
    public void LoadCapturedState(EquipmentSnapshot snapshot, IReadOnlyList<StepEvent> events,
        IReadOnlyList<MeasurementResult> measurements, IReadOnlyList<WaferSummary> summaries)
    {
        _events.Clear(); _eventIndexes.Clear();
        foreach (var ev in events.GroupBy(x => x.Seq).Select(g => g.Last()).TakeLast(5000))
        { _eventIndexes[ev.Seq] = _events.Count; _events.Add(ev); }
        RefreshLog(); _gantt.Steps = _events.OrderBy(x => x.Start).ToArray(); _ganttDirty = true;
        _measurements.Clear(); _measurements.AddRange(measurements.TakeLast(5000)); _map.Measurements = _measurements;
        _summaries.Clear(); _summaries.AddRange(summaries); _map.Summary = _summaries.LastOrDefault();
        ApplySnapshot(snapshot);
        if (snapshot.State == EquipmentState.Running) _message.Text = "진행 중";
    }
    public void LoadCompletedData(IReadOnlyList<MeasurementResult> measurements, IReadOnlyList<WaferSummary> summaries)
    {
        _measurements.Clear(); _measurements.AddRange(measurements.TakeLast(5000)); _map.Measurements = _measurements;
        _summaries.Clear(); _summaries.AddRange(summaries); _map.Summary = _summaries.LastOrDefault(); _mapDirty = true;
    }
    public void LoadRunHistoryForScreenshot()
    {
        if (Services.Store is not { } store) return;
        var runs = Task.Run(() => store.ListRunsAsync(CancellationToken.None)).GetAwaiter().GetResult();
        ApplyRuns(runs);
    }
    private async Task SaveRecipeAsync()
    {
        var error = _editor.Commit(); if (error is not null) { ShowError(error); return; }
        using var dialog = new SaveFileDialog { Filter = "레시피 JSON (*.json)|*.json", DefaultExt = "json", FileName = "recipe.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await new RecipeFile().SaveAsync(dialog.FileName, _recipe.Build(), CancellationToken.None);
        _message.Text = "레시피 저장 완료";
    }
    private async Task LoadRecipeAsync()
    {
        using var dialog = new OpenFileDialog { Filter = "레시피 JSON (*.json)|*.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var recipe = await new RecipeFile().LoadAsync(dialog.FileName, CancellationToken.None);
        var candidate = new RecipeEditModel(); candidate.Load(recipe);
        var error = candidate.Validate(); if (error is not null) { ShowError(error); return; }
        _recipe.Load(recipe); _editor.Reload(); _message.Text = "레시피 불러오기 완료";
    }
    private async Task RefreshRunsAsync()
    {
        if (Services.Store is not { } store) return;
        var runs = await store.ListRunsAsync(CancellationToken.None);
        if (_closing || IsDisposed) return;
        ApplyRuns(runs);
    }
    private void ApplyRuns(IReadOnlyList<RunRecord> runs)
    {
        var selectedA = _runA.SelectedItem?.ToString(); var selectedB = _runB.SelectedItem?.ToString();
        _runs.Clear(); foreach (var run in runs) _runs[run.RunId] = run;
        _runA.Items.Clear(); _runB.Items.Clear();
        foreach (var run in runs) { _runA.Items.Add(run.RunId); _runB.Items.Add(run.RunId); }
        if (_runA.Items.Count > 0) _runA.SelectedItem = selectedA is not null && _runs.ContainsKey(selectedA) ? selectedA : _runA.Items[0];
        if (_runB.Items.Count > 0) _runB.SelectedItem = selectedB is not null && _runs.ContainsKey(selectedB) ? selectedB : _runB.Items[^1];
        CompareRuns();
    }
    private void CompareRuns()
    {
        if (_runA.SelectedItem is not string a || _runB.SelectedItem is not string b || !_runs.TryGetValue(a, out var runA) || !_runs.TryGetValue(b, out var runB)) return;
        var comparison = new RunComparer().Compare(runA, runB);
        _comparison.BeginUpdate(); _comparison.Items.Clear();
        foreach (var phase in comparison.Phases)
        {
            var row = new ListViewItem(phase.Phase.ToString());
            row.SubItems.AddRange(new[] { $"{phase.A.TotalSeconds:0.00}s", $"{phase.B.TotalSeconds:0.00}s", $"{phase.Delta.TotalSeconds:+0.00;-0.00;0.00}s" });
            _comparison.Items.Add(row);
        }
        _comparison.EndUpdate();
        _comparisonSummary.Text = $"Tact 차이 {comparison.TactDelta.TotalSeconds:+0.00;-0.00;0.00}s    예상 WPH 차이 {comparison.UphDelta:+0.00;-0.00;0.00}";
    }
    private void DetachEvents()
    {
        _sim.SnapshotChanged -= SnapshotChanged; _sim.StepLogged -= StepLogged;
        _sim.MeasurementCompleted -= MeasurementCompleted; _sim.WaferCompleted -= WaferCompleted;
        _sim.AlarmRaised -= AlarmChanged; _sim.AlarmCleared -= AlarmChanged; _sim.RunCompleted -= RunCompleted;
    }
    private void HandleFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_closing) return;
        _closing = true;
        DetachEvents();
        if (_sim.Snapshot.State is EquipmentState.Running or EquipmentState.Paused or EquipmentState.Aborting)
        {
            e.Cancel = true;
            _ = FinishCloseAsync();
        }
    }
    private async Task FinishCloseAsync()
    {
        try { await _sim.AbortAsync(); if (_runTask is { } task) await task; }
        catch (Exception ex) { Console.Error.WriteLine($"종료 중 오류: {ex}"); }
        if (!IsDisposed) BeginInvoke(new Action(Close));
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Stop(); _timer.Dispose(); _metricTip.Dispose(); _recipeWindow?.Dispose(); _editor.Dispose(); _recipeControls.Dispose(); _waferCount.Dispose(); Font.Dispose(); DetachEvents(); }
        base.Dispose(disposing);
    }
    public void CreateChildren()
    {
        void Recurse(Control parent) { foreach (Control child in parent.Controls) { child.CreateControl(); _ = child.Handle; Recurse(child); } }
        Recurse(this);
    }
}
