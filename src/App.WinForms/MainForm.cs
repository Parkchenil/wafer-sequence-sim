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
    private readonly ToolStripStatusLabel _state = new() { Text = "미초기화" };
    private readonly ToolStripStatusLabel _time = new() { Text = "시간 00:00:00" };
    private readonly ToolStripStatusLabel _tact = new() { Text = "Tact —" };
    private readonly ToolStripStatusLabel _uph = new() { Text = "UPH —" };
    private readonly ToolStripStatusLabel _count = new() { Text = "0/0" };
    private readonly ToolStripStatusLabel _alarmCount = new() { Text = "알람 0" };
    private readonly ToolStripProgressBar _queue = new() { Maximum = 1, Width = 75 };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };
    private readonly SplitContainer _outer = new() { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, IsSplitterFixed = true, BackColor = Color.FromArgb(33, 42, 54) };
    private readonly SplitContainer _right = new() { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2, IsSplitterFixed = true, BackColor = Color.FromArgb(33, 42, 54) };
    private readonly SplitContainer _center = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, BackColor = Color.FromArgb(33, 42, 54) };
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
        BackColor = Color.FromArgb(22, 29, 39);
        BuildLayout();
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
            DrainSteps();
            if (_schematicDirty) { _schematic.Invalidate(); _schematicDirty = false; }
            if (_ganttDirty) { _gantt.Invalidate(); _ganttDirty = false; }
            if (_mapDirty) { _map.Invalidate(); _mapDirty = false; }
            if (_statsDirty) { _stats.Invalidate(); _statsDirty = false; }
        };
        _timer.Start();
        Shown += (_, _) => { ConfigureLayout(); _ = RunUiTask(RefreshRunsAsync); };
        FormClosing += HandleFormClosing;
    }

    private void BuildLayout()
    {
        var toolbar = new ToolStrip { Dock = DockStyle.Fill, GripStyle = ToolStripGripStyle.Hidden, BackColor = Color.FromArgb(36, 46, 60), ForeColor = Color.WhiteSmoke };
        toolbar.Items.Add(new ToolStripLabel("WAFER SEQUENCE  /  HMI"));
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(new ToolStripLabel("가상 장비 · 공정 흐름"));
        var status = new StatusStrip { Dock = DockStyle.Fill, BackColor = Color.FromArgb(36, 46, 60), ForeColor = Color.WhiteSmoke, SizingGrip = false };
        status.Items.AddRange(new ToolStripItem[] { _state, new ToolStripStatusLabel("│"), _time, new ToolStripStatusLabel("│"), _tact, new ToolStripStatusLabel("│"), _uph, new ToolStripStatusLabel("│"), _count, new ToolStripStatusLabel("│ 저장 큐"), _queue, new ToolStripStatusLabel("│"), _alarmCount, new ToolStripStatusLabel("│"), _message });
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        _root.Controls.Add(toolbar, 0, 0);
        _root.Controls.Add(_outer, 0, 1);
        _root.Controls.Add(status, 0, 2);
        Controls.Add(_root);
        _outer.Panel2.Controls.Add(_right);
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 210));
        left.Controls.Add(new Label { Text = "레시피 / RECIPE", Dock = DockStyle.Fill, ForeColor = Color.WhiteSmoke }, 0, 0);
        left.Controls.Add(_editor, 0, 1);
        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
        controls.Controls.Add(new Label { Text = "웨이퍼 수", Width = 78, ForeColor = Color.WhiteSmoke, Padding = new Padding(0, 6, 0, 0) });
        controls.Controls.Add(_waferCount);
        _initializeButton = AddButton(controls, "초기화", () => Command(() => _sim.InitializeAsync(CancellationToken.None)));
        _startButton = AddButton(controls, "시작", () =>
        {
            var validation = _editor.Commit();
            if (validation is not null) { ShowError(validation); return Task.CompletedTask; }
            ResetRunView();
            _runTask = Command(() => _sim.StartAsync(_recipe.Build(), (int)_waferCount.Value, CancellationToken.None));
            return _runTask;
        });
        _pauseButton = AddButton(controls, "일시정지", () => { _sim.Pause(); return Task.CompletedTask; });
        _resumeButton = AddButton(controls, "재개", () => { _sim.Resume(); return Task.CompletedTask; });
        _abortButton = AddButton(controls, "중단", () => _sim.AbortAsync());
        _resetButton = AddButton(controls, "리셋", () => Command(() => _sim.ResetAsync()));
        AddButton(controls, "레시피 저장", SaveRecipeAsync);
        AddButton(controls, "레시피 불러오기", LoadRecipeAsync);
        left.Controls.Add(controls, 0, 2); _outer.Panel1.Controls.Add(left);
        _center.Panel1.Controls.Add(_schematic);
        var ganttBox = new Panel { Dock = DockStyle.Fill };
        var slider = new TrackBar { Dock = DockStyle.Bottom, Minimum = 10, Maximum = 300, Value = 60, TickFrequency = 30, Height = 38 };
        slider.ValueChanged += (_, _) => _gantt.WindowSeconds = slider.Value;
        ganttBox.Controls.Add(_gantt); ganttBox.Controls.Add(slider);
        _center.Panel2.Controls.Add(ganttBox); _right.Panel1.Controls.Add(_center);
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
        statsTab.Controls.Add(_stats); statsTab.Controls.Add(_comparison); statsTab.Controls.Add(_comparisonSummary); statsTab.Controls.Add(compareSelectors);
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

    public void ConfigureLayout()
    {
        _root.PerformLayout();
        _outer.PerformLayout();
        _right.PerformLayout();
        _center.PerformLayout();
        _outer.Panel1MinSize = 320;
        _outer.Panel2MinSize = 850;
        _outer.SplitterDistance = 320;
        _right.Panel1MinSize = 400;
        _right.Panel2MinSize = 430;
        _right.Dock = DockStyle.Fill;
        _right.SplitterDistance = _root.ClientSize.Width - _outer.SplitterDistance - _outer.SplitterWidth - _right.SplitterWidth - 430;
        _center.SplitterDistance = (int)(_center.ClientSize.Height * .56);
        _root.PerformLayout();
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
        finally { _busy = false; if (!_closing) ApplySnapshot(_sim.Snapshot); }
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
    private void SnapshotChanged(EquipmentSnapshot snapshot) => OnUi(() => ApplySnapshot(snapshot));
    private void StepLogged(StepEvent ev) => _pendingSteps.Enqueue(ev);
    private void MeasurementCompleted(MeasurementResult result) => OnUi(() =>
    {
        if (_measurements.Count == 5000) _measurements.RemoveAt(0);
        _measurements.Add(result); _map.Measurements = _measurements; _mapDirty = true;
    });
    private void WaferCompleted(WaferSummary summary) => OnUi(() => { _summaries.Add(summary); _map.Summary = summary; _mapDirty = true; });
    private void AlarmChanged(Alarm _) => OnUi(() => ApplySnapshot(_sim.Snapshot));
    private void RunCompleted(RunRecord run) => OnUi(() =>
    {
        _message.Text = "로트 완료";
        _message.ForeColor = Color.WhiteSmoke;
        _ = RunUiTask(RefreshRunsAsync);
    });
    private void ResetRunView()
    {
        while (_pendingSteps.TryDequeue(out _)) { }
        _events.Clear(); _eventIndexes.Clear(); _filteredIndexes.Clear(); _log.VirtualListSize = 0;
        _measurements.Clear(); _summaries.Clear(); _map.Measurements = _measurements; _map.Summary = null;
        _gantt.Steps = Array.Empty<StepEvent>(); _mapDirty = _ganttDirty = true;
    }
    private void DrainSteps()
    {
        if (_pendingSteps.IsEmpty) return;
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
    private static ListViewItem LogItem(StepEvent ev)
    {
        var item = new ListViewItem(ev.Seq.ToString());
        item.SubItems.AddRange(new[] { ev.Start.ToString(@"hh\:mm\:ss\.fff"), ev.WaferId, ev.SiteIndex?.ToString() ?? "—", ev.Module.ToString(), ev.Phase.ToString(), ev.Duration is { } d ? $"{d.TotalMilliseconds:0} ms" : "…", ev.WaitReason ?? "", ev.Note });
        return item;
    }
    private void ApplySnapshot(EquipmentSnapshot snapshot)
    {
        _schematic.Snapshot = snapshot; _schematic.WaferDiameterMm = _sim.CurrentRecipe?.WaferDiameterMm ?? _recipe.Recipe.WaferDiameterMm; _map.WaferDiameterMm = _schematic.WaferDiameterMm; _gantt.Now = snapshot.SimTime; _map.Snapshot = snapshot; _stats.Totals = snapshot.Stats.PhaseTotals;
        _schematicDirty = _ganttDirty = _mapDirty = _statsDirty = true;
        _state.Text = snapshot.State.ToString(); _state.BackColor = snapshot.State switch { EquipmentState.Running => Color.SeaGreen, EquipmentState.Alarm => Color.Firebrick, EquipmentState.Paused => Color.DarkGoldenrod, _ => Color.SteelBlue };
        _time.Text = $"시간 {snapshot.SimTime:hh\\:mm\\:ss}";
        _tact.Text = snapshot.Stats.AverageTact is { } tact ? $"Tact {tact.TotalSeconds:0.0}s" : "Tact —";
        _uph.Text = snapshot.Stats.Uph is { } uph ? $"UPH {uph:0.0}" : "UPH —";
        _count.Text = $"{snapshot.Stats.WafersDone}/{snapshot.Stats.WafersTotal}";
        _queue.Maximum = Math.Max(1, snapshot.Stats.SaveQueueCapacity); _queue.Value = Math.Clamp(snapshot.Stats.SaveQueueDepth, 0, _queue.Maximum);
        _alarmCount.Text = $"알람 {snapshot.ActiveAlarms.Count}";
        if (_initializeButton is not null) _initializeButton.Enabled = snapshot.State == EquipmentState.Uninitialized;
        if (_startButton is not null) _startButton.Enabled = snapshot.State is EquipmentState.Idle or EquipmentState.Completed;
        if (_pauseButton is not null) _pauseButton.Enabled = snapshot.State == EquipmentState.Running;
        if (_resumeButton is not null) _resumeButton.Enabled = snapshot.State == EquipmentState.Paused;
        if (_abortButton is not null) _abortButton.Enabled = snapshot.State is EquipmentState.Running or EquipmentState.Paused or EquipmentState.Aborting;
        if (_resetButton is not null) _resetButton.Enabled = (snapshot.State is EquipmentState.Alarm or EquipmentState.Idle or EquipmentState.Completed)
            && !snapshot.ActiveAlarms.Any(a => a.Severity == AlarmSeverity.Error && !a.Acked);
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
            _message.Text = "로트 완료";
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
        _comparisonSummary.Text = $"Tact 차이 {comparison.TactDelta.TotalSeconds:+0.00;-0.00;0.00}s    UPH 차이 {comparison.UphDelta:+0.00;-0.00;0.00}";
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
        if (disposing) { _timer.Stop(); _timer.Dispose(); Font.Dispose(); DetachEvents(); }
        base.Dispose(disposing);
    }
    public void CreateChildren()
    {
        void Recurse(Control parent) { foreach (Control child in parent.Controls) { child.CreateControl(); _ = child.Handle; Recurse(child); } }
        Recurse(this);
    }
}
