using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>日志记录开关：读取当前状态，勾选后应用（系统级改动会提权重启自身执行）。</summary>
public partial class LoggingWindow : Window
{
    private readonly ObservableCollection<LoggingSwitch> _rows = new();
    private List<LoggingSwitch> _state = new();

    public int RowCount => _rows.Count;

    public LoggingWindow()
    {
        InitializeComponent();
        List.ItemsSource = _rows;
        Loaded += (_, _) => Reload();
    }

    /// <summary>供无头自检：读取状态并填充列表。</summary>
    public int LoadState()
    {
        Reload();
        return _rows.Count;
    }

    private void Reload()
    {
        _state = LoggingService.ReadState();
        _rows.Clear();
        foreach (var s in _state)
        {
            s.Wanted = s.Enabled;
            _rows.Add(s);
        }

        var off = _state.Count(s => !s.Enabled);
        StatusText.Text = off == 0
            ? "四项记录都在开启状态。"
            : $"有 {off} 项记录处于关闭状态，勾选后点「应用更改」。";
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        Reload();
    }

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        var changed = _state.Where(s => s.Wanted != s.Enabled).ToList();
        if (changed.Count == 0)
        {
            MessageBox.Show(this, "没有需要更改的项目。", "系统诊断台",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var desired = _state.ToDictionary(s => s.Key, s => s.Wanted);

        if (LoggingService.NeedsElevation(changed) && !LoggingService.IsAdmin())
        {
            await ApplyElevatedAsync(desired);
            return;
        }

        var log = LoggingService.Apply(desired, LoggingService.IsAdmin());
        MessageBox.Show(this, string.Join("\n", log), "应用结果",
            MessageBoxButton.OK, MessageBoxImage.Information);
        Reload();
    }

    /// <summary>系统级开关需要管理员：以 runas 重新拉起自身，结果写到结果文件里再读回来。</summary>
    private async Task ApplyElevatedAsync(Dictionary<string, bool> desired)
    {
        var order = new[] { LoggingService.KeySystem, LoggingService.KeyApp, LoggingService.KeyPrefetch, LoggingService.KeyRecent };
        var flags = string.Join(",", order.Select(k => desired.TryGetValue(k, out var v) && v ? "1" : "0"));

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            MessageBox.Show(this, "拿不到程序路径，无法提权。", "系统诊断台",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            File.Delete(LoggingService.ResultFile);
        }
        catch { }

        try
        {
            ApplyBtn.IsEnabled = false;
            StatusText.Text = "等待管理员授权…";

            var psi = new ProcessStartInfo(exe, "--apply-logging " + flags)
            {
                UseShellExecute = true,
                Verb = "runas"
            };

            using var p = Process.Start(psi);
            if (p != null)
            {
                await Task.Run(() => p.WaitForExit(60000));
            }

            var result = File.Exists(LoggingService.ResultFile)
                ? File.ReadAllText(LoggingService.ResultFile)
                : "没有拿到应用结果（可能被取消或执行失败）。";

            MessageBox.Show(this, result, "应用结果", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            MessageBox.Show(this, "已取消管理员授权，未做任何更改。", "系统诊断台",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "提权失败：" + ex.Message, "系统诊断台",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ApplyBtn.IsEnabled = true;
            Reload();
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
