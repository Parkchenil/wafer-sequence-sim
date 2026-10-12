using Contracts;
using System.ComponentModel;

namespace App.WinForms;

public sealed class MainForm : Form
{
    private readonly ISimulator _sim;
    private readonly RecipeEditModel _recipe = new();
    private readonly List<StepEvent> _events = new();
    private readonly List<MeasurementResult> _measurements = new();
    private readonly List<WaferSummary> _summaries = new();
    private readonly SchematicPanel _schematic = new() { Dock = DockStyle.Fill };
    private readonly GanttPanel _gantt = new() { Dock = DockStyle.Fill };
    private readonly WaferMapPanel _map = new() { Dock = DockStyle.Fill };
    private readonly StatsPanel _stats = new() { Dock = DockStyle.Fill };
    private readonly PropertyGrid _grid = new() { Dock = DockStyle.Fill, HelpVisible = true, ToolbarVisible = false };
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
    private int[] _filtered = Array.Empty<int>();
    private EquipmentSnapshot _snapshot;
    private bool _busy;

    public MainForm(ISimulator simulator)
    {
        _sim = simulator; _snapshot = simulator.Snapshot;
        Text = "Wafer Sequence Simulator · HMI";
        Size = new Size(1280, 800);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);
        BackColor = Color.FromArgb(22, 29, 39);
        BuildLayout();
        _grid.SelectedObject = _recipe;
        _log.RetrieveVirtualItem += (_, e) => e.Item = LogItem(_events[_filtered[e.ItemIndex]]);
        _waitOnly.CheckedChanged += (_, _) => RefreshLog();
        _sim.SnapshotChanged += SnapshotChanged;
        _sim.StepLogged += StepLogged;
        _sim.MeasurementCompleted += MeasurementCompleted;
        _sim.WaferCompleted += WaferCompleted;
        _sim.AlarmRaised += AlarmChanged;
        _sim.AlarmCleared += AlarmChanged;
        ApplyCompletedRun();
        _timer.Tick += (_, _) => { _schematic.Invalidate(); _gantt.Invalidate(); _map.Invalidate(); _stats.Invalidate(); };
        _timer.Start();
        FormClosed += (_, _) => { _timer.Stop(); _sim.SnapshotChanged -= SnapshotChanged; _sim.StepLogged -= StepLogged; _sim.MeasurementCompleted -= MeasurementCompleted; _sim.WaferCompleted -= WaferCompleted; _sim.AlarmRaised -= AlarmChanged; _sim.AlarmCleared -= AlarmChanged; };
    }

    private void BuildLayout()
    {
        var toolbar = new ToolStrip { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden, BackColor = Color.FromArgb(36, 46, 60), ForeColor = Color.WhiteSmoke };
        toolbar.Items.Add(new ToolStripLabel("WAFER SEQUENCE  /  HMI"));
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(new ToolStripLabel("가상 장비 · 공정 흐름"));
        var status = new StatusStrip { Dock = DockStyle.Bottom, BackColor = Color.FromArgb(36, 46, 60), ForeColor = Color.WhiteSmoke, SizingGrip = false };
        status.Items.AddRange(new ToolStripItem[] { _state, new ToolStripStatusLabel("│"), _time, new ToolStripStatusLabel("│"), _tact, new ToolStripStatusLabel("│"), _uph, new ToolStripStatusLabel("│"), _count, new ToolStripStatusLabel("│ 저장 큐"), _queue, new ToolStripStatusLabel("│"), _alarmCount });
        Controls.Add(status);
        Controls.Add(toolbar);
        var outer = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 283, IsSplitterFixed = true, FixedPanel = FixedPanel.Panel1, BackColor = Color.FromArgb(33, 42, 54) };
        var right = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 575, IsSplitterFixed = true, FixedPanel = FixedPanel.Panel2, BackColor = Color.FromArgb(33, 42, 54) };
        outer.Panel2.Controls.Add(right);
        Controls.Add(outer);
        outer.BringToFront(); toolbar.BringToFront(); status.BringToFront();
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 175));
        left.Controls.Add(new Label { Text = "레시피 / RECIPE", Dock = DockStyle.Fill, ForeColor = Color.WhiteSmoke }, 0, 0);
        left.Controls.Add(_grid, 0, 1);
        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
        controls.Controls.Add(new Label { Text = "웨이퍼 수", Width = 78, ForeColor = Color.WhiteSmoke, Padding = new Padding(0, 6, 0, 0) });
        controls.Controls.Add(_waferCount);
        AddButton(controls, "초기화", async () => await Command(() => _sim.InitializeAsync(CancellationToken.None)));
        AddButton(controls, "시작", async () => await Command(() => _sim.StartAsync(_recipe.Build(), (int)_waferCount.Value, CancellationToken.None)));
        AddButton(controls, "일시정지", () => _sim.Pause());
        AddButton(controls, "재개", () => _sim.Resume());
        AddButton(controls, "중단", async () => await _sim.AbortAsync());
        AddButton(controls, "리셋", async () => await Command(() => _sim.ResetAsync()));
        AddButton(controls, "레시피 저장 (TODO)", () => { });
        AddButton(controls, "레시피 불러오기 (TODO)", () => { });
        left.Controls.Add(controls, 0, 2); outer.Panel1.Controls.Add(left);
        var center = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 385, BackColor = Color.FromArgb(33, 42, 54) };
        center.Panel1.Controls.Add(_schematic);
        var ganttBox = new Panel { Dock = DockStyle.Fill };
        var slider = new TrackBar { Dock = DockStyle.Bottom, Minimum = 10, Maximum = 300, Value = 60, TickFrequency = 30, Height = 38 };
        slider.ValueChanged += (_, _) => _gantt.WindowSeconds = slider.Value;
        ganttBox.Controls.Add(_gantt); ganttBox.Controls.Add(slider);
        center.Panel2.Controls.Add(ganttBox); right.Panel1.Controls.Add(center);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        _log.Columns.Add("Seq", 48); _log.Columns.Add("시각", 80); _log.Columns.Add("웨이퍼", 60); _log.Columns.Add("사이트", 53); _log.Columns.Add("모듈", 70); _log.Columns.Add("단계", 105); _log.Columns.Add("소요", 65); _log.Columns.Add("대기사유", 135); _log.Columns.Add("비고", 150);
        var logTab = new TabPage("로그"); logTab.Controls.Add(_log); logTab.Controls.Add(_waitOnly);
        var mapTab = new TabPage("웨이퍼 맵"); mapTab.Controls.Add(_map);
        var statsTab = new TabPage("통계"); statsTab.Controls.Add(_stats);
        _alarms.Columns.Add("시각", 75); _alarms.Columns.Add("코드", 75); _alarms.Columns.Add("모듈", 85); _alarms.Columns.Add("내용", 200); _alarms.Columns.Add("확인", 50);
        var alarmTab = new TabPage("알람"); var ack = new Button { Text = "선택 알람 확인", Dock = DockStyle.Bottom, Height = 30 };
        ack.Click += (_, _) => { foreach (ListViewItem item in _alarms.SelectedItems) if (item.Tag is string id) _sim.AcknowledgeAlarm(id); ApplySnapshot(_sim.Snapshot); };
        alarmTab.Controls.Add(_alarms); alarmTab.Controls.Add(ack);
        _devices.Columns.Add("모듈", 95); _devices.Columns.Add("상태", 95); _devices.Columns.Add("설명", 180);
        var deviceTab = new TabPage("장치"); deviceTab.Controls.Add(_devices);
        tabs.TabPages.AddRange(new[] { logTab, mapTab, statsTab, alarmTab, deviceTab }); right.Panel2.Controls.Add(tabs);
    }

    private static void AddButton(Control parent, string title, Action action)
    {
        var button = new Button { Text = title, Width = title.Length > 8 ? 128 : 80, Height = 29 };
        button.Click += (_, _) => action(); parent.Controls.Add(button);
    }
    private async Task Command(Func<Task<DeviceResult>> action)
    {
        if (_busy) return;
        _busy = true;
        try { var result = await action(); if (!result.Ok) _state.Text = $"실패: {result.Code} · {result.Message}"; }
        catch (OperationCanceledException) { _state.Text = "중단됨"; }
        finally { _busy = false; ApplySnapshot(_sim.Snapshot); }
    }
    private void OnUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) BeginInvoke(action); else action();
    }
    private void SnapshotChanged(EquipmentSnapshot snapshot) => OnUi(() => ApplySnapshot(snapshot));
    private void StepLogged(StepEvent ev) => OnUi(() => { var i = _events.FindIndex(x => x.Seq == ev.Seq); if (i < 0) { if (_events.Count == 5000) _events.RemoveAt(0); _events.Add(ev); } else _events[i] = ev; RefreshLog(); _gantt.Steps = _events; });
    private void MeasurementCompleted(MeasurementResult result) => OnUi(() => { _measurements.Add(result); _map.Measurements = _measurements; });
    private void WaferCompleted(WaferSummary summary) => OnUi(() => { _summaries.Add(summary); _map.Summary = summary; });
    private void AlarmChanged(Alarm _) => OnUi(() => ApplySnapshot(_sim.Snapshot));
    private void RefreshLog() { _filtered = Enumerable.Range(0, _events.Count).Where(i => !_waitOnly.Checked || _events[i].IsWait).Reverse().ToArray(); _log.VirtualListSize = _filtered.Length; }
    private static ListViewItem LogItem(StepEvent ev)
    {
        var item = new ListViewItem(ev.Seq.ToString());
        item.SubItems.AddRange(new[] { ev.Start.ToString(@"hh\:mm\:ss\.fff"), ev.WaferId, ev.SiteIndex?.ToString() ?? "—", ev.Module.ToString(), ev.Phase.ToString(), ev.Duration is { } d ? $"{d.TotalMilliseconds:0} ms" : "…", ev.WaitReason ?? "", ev.Note });
        return item;
    }
    private void ApplySnapshot(EquipmentSnapshot snapshot)
    {
        _snapshot = snapshot; _schematic.Snapshot = snapshot; _schematic.WaferDiameterMm = _sim.CurrentRecipe?.WaferDiameterMm ?? _recipe.Recipe.WaferDiameterMm; _map.WaferDiameterMm = _schematic.WaferDiameterMm; _gantt.Now = snapshot.SimTime; _map.Snapshot = snapshot; _stats.Totals = snapshot.Stats.PhaseTotals;
        _state.Text = snapshot.State.ToString(); _state.BackColor = snapshot.State switch { EquipmentState.Running => Color.SeaGreen, EquipmentState.Alarm => Color.Firebrick, EquipmentState.Paused => Color.DarkGoldenrod, _ => Color.SteelBlue };
        _time.Text = $"시간 {snapshot.SimTime:hh\\:mm\\:ss}";
        _tact.Text = snapshot.Stats.AverageTact is { } tact ? $"Tact {tact.TotalSeconds:0.0}s" : "Tact —";
        _uph.Text = snapshot.Stats.Uph is { } uph ? $"UPH {uph:0.0}" : "UPH —";
        _count.Text = $"{snapshot.Stats.WafersDone}/{snapshot.Stats.WafersTotal}";
        _queue.Maximum = Math.Max(1, snapshot.Stats.SaveQueueCapacity); _queue.Value = Math.Clamp(snapshot.Stats.SaveQueueDepth, 0, _queue.Maximum);
        _alarmCount.Text = $"알람 {snapshot.ActiveAlarms.Count}";
        _alarms.BeginUpdate(); _alarms.Items.Clear();
        foreach (var alarm in snapshot.ActiveAlarms)
        {
            var item = new ListViewItem(alarm.SetAt.ToString(@"hh\:mm\:ss")) { Tag = alarm.Id };
            item.SubItems.AddRange(new[] { alarm.Code.ToString(), alarm.Module.ToString(), alarm.Message, alarm.Acked ? "완료" : "대기" }); _alarms.Items.Add(item);
        }
        _alarms.EndUpdate(); _devices.BeginUpdate(); _devices.Items.Clear();
        foreach (var module in snapshot.Modules) { var item = new ListViewItem(module.Module.ToString()); item.SubItems.AddRange(new[] { module.State.ToString(), module.Detail }); _devices.Items.Add(item); }
        _devices.EndUpdate();
    }
    public void ApplyCompletedRun()
    {
        _events.Clear(); _events.AddRange(_sim.Log.TakeLast(5000)); RefreshLog(); _gantt.Steps = _events;
        ApplySnapshot(_sim.Snapshot);
    }
    public void LoadCompletedData(IReadOnlyList<MeasurementResult> measurements, IReadOnlyList<WaferSummary> summaries)
    {
        _measurements.Clear(); _measurements.AddRange(measurements); _map.Measurements = _measurements;
        _summaries.Clear(); _summaries.AddRange(summaries); _map.Summary = _summaries.LastOrDefault();
    }
    public void CreateChildren()
    {
        void Recurse(Control parent) { foreach (Control child in parent.Controls) { child.CreateControl(); _ = child.Handle; Recurse(child); } }
        Recurse(this);
    }
}
