using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

public partial class LogFinderWindow : Window
{
    private readonly ObservableCollection<GameLogDir> _items = new();
    private readonly Action<LogSource> _onAnalyze;
    private bool _scanning;

    public LogFinderWindow(Action<LogSource> onAnalyze)
    {
        InitializeComponent();
        _onAnalyze = onAnalyze;
        List.ItemsSource = _items;
        Loaded += async (_, _) => await ScanAsync();
    }

    /// <summary>供无头自检读取。</summary>
    public int ResultCount => _items.Count;

    private async Task ScanAsync()
    {
        if (_scanning) return;
        _scanning = true;
        AnalyzeBtn.IsEnabled = false;
        _items.Clear();
        EmptyHint.Visibility = Visibility.Visible;
        EmptyHint.Text = "正在扫描…";
        StatusText.Text = "扫描中…";

        var progress = new Progress<string>(s => StatusText.Text = s);

        try
        {
            var found = await Task.Run(() => GameLogFinder.FindAll(progress));
            foreach (var f in found) _items.Add(f);

            EmptyHint.Text = found.Count == 0
                ? "没有找到游戏日志目录（仅识别 Unreal 引擎的 Saved\\Logs）"
                : "";
            EmptyHint.Visibility = found.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            StatusText.Text = found.Count == 0
                ? "未找到"
                : $"找到 {found.Count} 处，合计 {LogFileItem.FormatSize(found.Sum(f => f.Size))}（双击可自选文件）";
        }
        catch (Exception ex)
        {
            StatusText.Text = "扫描失败：" + ex.Message;
            EmptyHint.Text = "扫描失败";
        }
        finally
        {
            _scanning = false;
            if (_items.Count > 0)
            {
                List.SelectedIndex = 0;
                AnalyzeBtn.IsEnabled = true;
                PickBtn.IsEnabled = true;
            }
        }
    }

    private async void OnRescan(object sender, RoutedEventArgs e) => await ScanAsync();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var has = List.SelectedItem is GameLogDir;
        AnalyzeBtn.IsEnabled = has;
        PickBtn.IsEnabled = has;
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (List.SelectedItem is GameLogDir) PickFromSelected();
    }

    private void OnAnalyze(object sender, RoutedEventArgs e) => AnalyzeSelected();

    private void OnPick(object sender, RoutedEventArgs e) => PickFromSelected();

    private void AnalyzeSelected()
    {
        if (List.SelectedItem is not GameLogDir dir) return;
        _onAnalyze?.Invoke(LogSource.FromFolder(dir.Path));
        Close();
    }

    /// <summary>打开文件选择窗口，由用户自己挑要分析哪几份。</summary>
    private void PickFromSelected()
    {
        if (List.SelectedItem is not GameLogDir dir) return;

        var picker = new LogFilePickerWindow(dir.Path) { Owner = this };
        if (picker.ShowDialog() != true) return;
        if (picker.SelectedPaths.Count == 0) return;

        _onAnalyze?.Invoke(LogSource.FromPickedFiles(dir.Path, picker.SelectedPaths));
        Close();
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not GameLogDir dir) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "explorer.exe", $"\"{dir.Path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, ex.Message, "破烂优化", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
