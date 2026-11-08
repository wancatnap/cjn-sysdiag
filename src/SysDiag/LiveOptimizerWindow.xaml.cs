using System.Linq;
using System.Windows;
using System.Windows.Threading;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>直播伴侣优化：指标轮询 + 优化 / 还原。逻辑对齐桌面上的同名工具。</summary>
public partial class LiveOptimizerWindow : Window
{
    private readonly DispatcherTimer _timer;

    public string CpuTextValue => CpuValue.Text;

    public bool TimerRunning => _timer.IsEnabled;

    public LiveOptimizerWindow()
    {
        InitializeComponent();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Refresh();

        Loaded += (_, _) =>
        {
            Refresh();
            RefreshDiagnostics();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    private void Refresh()
    {
        var s = LiveOptimizerService.Snapshot();
        CpuValue.Text = s.CpuText;
        MemValue.Text = s.MemoryText;
        TempValue.Text = s.TemperatureText;
        ProcValue.Text = s.ProcessText;
        ProcValue.Foreground = (s.Running ? Palette.Ok : Palette.Danger);
    }

    /// <summary>
    /// 系统层状态：HAGS、游戏模式、覆盖层。
    /// 只在加载和手动刷新时算——每 2 秒扫一遍进程表没必要，还会把开销算到自己头上。
    /// </summary>
    private void RefreshDiagnostics()
    {
        var list = LiveStreamTweaks.Diagnostics();
        DiagText.Text = string.Join("　·　", list.Select(d => $"{d.Name} {d.Value}"));
        DiagText.ToolTip = string.Join("\n", list.Select(d => $"{d.Name}：{d.Hint}"));
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        Refresh();
        RefreshDiagnostics();
        StatusText.Text = "已刷新 " + DateTime.Now.ToString("HH:mm:ss");
    }

    private void OnOptimize(object sender, RoutedEventArgs e)
    {
        var (ok, message) = LiveOptimizerService.Optimize();
        Append(message);
        StatusText.Text = ok ? "优化完成" : "未找到直播伴侣进程";
        Refresh();
    }

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        var (_, message) = LiveOptimizerService.Restore();
        Append(message);
        StatusText.Text = "已还原";
        Refresh();
    }

    private void Append(string message)
    {
        var prefix = $"[{DateTime.Now:HH:mm:ss}] ";
        LogText.Text = LogText.Text == "等待操作…"
            ? prefix + message
            : LogText.Text + Environment.NewLine + Environment.NewLine + prefix + message;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
