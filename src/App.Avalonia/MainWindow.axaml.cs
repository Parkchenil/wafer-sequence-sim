using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
namespace App.Avalonia;
public partial class MainWindow : Window
{
    private bool closingAfterStop;
    private Window? sceneWindow;
    private Equipment3DView? expandedView;
    private void RotateLeft(object? sender,RoutedEventArgs e)=>expandedView?.Rotate(-.15f,0);
    private void RotateRight(object? sender,RoutedEventArgs e)=>expandedView?.Rotate(.15f,0);
    private void ZoomIn(object? sender,RoutedEventArgs e)=>expandedView?.Zoom(1.2f);
    private void ZoomOut(object? sender,RoutedEventArgs e)=>expandedView?.Zoom(.8f);
    private void ResetExpanded(object? sender,RoutedEventArgs e)=>expandedView?.ResetCamera();
    private void ToggleRecipe(object? sender, RoutedEventArgs e) => RecipeDrawer.IsVisible = !RecipeDrawer.IsVisible;
    private void ShowDashboard(object? sender, RoutedEventArgs e) => InspectorTabs.SelectedIndex = 0;
    private void ShowLogs(object? sender, RoutedEventArgs e) => InspectorTabs.SelectedIndex = 0;
    private void ShowWaferMap(object? sender, RoutedEventArgs e) => InspectorTabs.SelectedIndex = 1;
    private void ShowStatistics(object? sender, RoutedEventArgs e) => InspectorTabs.SelectedIndex = 2;
    private void ShowAlarms(object? sender, RoutedEventArgs e) => InspectorTabs.SelectedIndex = 3;
    private void ShowControl(object? sender, RoutedEventArgs e) => InspectorTabs.SelectedIndex = 5;
    private void ShowExperiment(object? sender, RoutedEventArgs e) => InspectorTabs.SelectedIndex = 6;
    private void ShowDevices(object? sender, RoutedEventArgs e) => InspectorTabs.SelectedIndex = 4;
    private void Reset3D(object? sender, RoutedEventArgs e) => Scene3D.ResetCamera();
    private void Transport3D(object? sender, RoutedEventArgs e) => Scene3D.TransportCamera();
    private void Close3D(object? sender, RoutedEventArgs e) { Scene3D.ResetCamera(); Scene3D.CloseUp = !Scene3D.CloseUp; }
    private void Cut3D(object? sender, RoutedEventArgs e) => Scene3D.Cutaway = !Scene3D.Cutaway;
    private void Toggle2D(object? sender, RoutedEventArgs e) { Scene3D.IsVisible = !Scene3D.IsVisible; Scene2D.IsVisible = !Scene3D.IsVisible; }
    private void Open3D(object? sender, RoutedEventArgs e)
    {
        if(sceneWindow is { IsVisible: true }){sceneWindow.Activate();return;}
        var view=new Equipment3DView();expandedView=view;
        view.Bind(Equipment3DView.SnapshotProperty,new global::Avalonia.Data.Binding("Snapshot") { Source=DataContext });
        view.Bind(Equipment3DView.ActiveStepProperty,new global::Avalonia.Data.Binding("ActiveStep") { Source=DataContext });
        view.Bind(Equipment3DView.RobotStepProperty,new global::Avalonia.Data.Binding("RobotStep") { Source=DataContext });
        view.Bind(Equipment3DView.WaferDiameterMmProperty,new global::Avalonia.Data.Binding("WaferDiameterMm") { Source=DataContext });
        view.Bind(Equipment3DView.RobotMoveTimeProperty,new global::Avalonia.Data.Binding("RobotMoveTime") { Source=DataContext });
        var bar=new StackPanel { Orientation=global::Avalonia.Layout.Orientation.Horizontal,Spacing=6 };
        void Add(string label,EventHandler<RoutedEventArgs> action){var button=new Button { Content=label };button.Click+=action;bar.Children.Add(button);}
        Add("◀ 회전",RotateLeft);Add("회전 ▶",RotateRight);Add("확대 +",ZoomIn);Add("축소 −",ZoomOut);Add("기본 시점",ResetExpanded);Add("반송 확대",(_,_)=>view.TransportCamera());
        var panel=new DockPanel();DockPanel.SetDock(bar,Dock.Top);panel.Children.Add(bar);panel.Children.Add(view);
        sceneWindow=new Window { Title="3D 개념 장비 · 드래그 회전 / 휠 확대",Width=1100,Height=750,Content=panel };sceneWindow.Closed+=(_,_)=>expandedView=null;sceneWindow.Show(this);
    }
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel(this);
        Closing += OnClosing;
    }
    public async Task<string?> PickRecipeSavePathAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "레시피 저장", SuggestedFileName = "recipe.json", DefaultExtension = "json", FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }] });
        return file?.TryGetLocalPath();
    }
    public async Task<string?> PickRecipeOpenPathAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "레시피 불러오기", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }] });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (closingAfterStop) return;
        e.Cancel = true;
        if (DataContext is MainViewModel model) await model.StopAsync();
        closingAfterStop = true;
        Close();
    }
}
