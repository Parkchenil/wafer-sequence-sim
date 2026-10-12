using Avalonia.Controls;
using Avalonia.Platform.Storage;
namespace App.Avalonia;
public partial class MainWindow : Window
{
    private bool closingAfterStop;
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
