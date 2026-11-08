using System.Diagnostics;
using System.Linq;
using System.Windows.Threading;
using System.IO;
using System.Windows;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>
/// 进程句柄排查：选一个目标进程，看当前谁持有它的句柄、有什么权限。
/// 白名单（系统组件 / 反作弊）标「可信」，不计入风险——但照常列出，不隐藏证据。
/// </summary>
public partial class HandleWindow : Window
{
    private readonly Action<ReportPayload> _showReport;

    private HandleScanResult _result = new();
    private bool _busy;

    /// <summary>自动刷新：每 10 秒重扫当前目标，用于盯着「谁在读游戏」。</summary>
    private readonly DispatcherTimer _autoTimer = new() { Interval = TimeSpan.FromSeconds(10) };

    /// <summary>自检读取：本次识别到的游戏进程。</summary>
    public IReadOnlyList<GameProcess> DetectedGames { get; private set; } = Array.Empty<GameProcess>();

    /// <summary>自检读取：外部进程条数。</summary>
    public int OwnerTotal => _result.Findings.Count;

    /// <summary>自检读取：表格显示行数。</summary>
    public int OwnerShown => OwnerGrid.ShownCount;

    public HandleScanResult Snapshot => _result;

    public bool ElevateButtonVisible => ElevateBtn.Visibility == Visibility.Visible;

    public HandleWindow() : this(_ => { })
    {
    }

    public HandleWindow(Action<ReportPayload> showReport)
    {
        InitializeComponent();

        _showReport = showReport ?? (_ => { });

        var admin = ElevationService.IsAdmin;
        ElevateBtn.Visibility = admin ? Visibility.Collapsed : Visibility.Visible;

        _autoTimer.Tick += async (_, _) =>
        {
            if (_busy || !IsVisible || TargetCombo.SelectedItem is not string) return;
            await ScanAsyncForEntry(silent: true);
        };

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        RefreshProcessList(null);

        // 全表扫描不到一秒：有预选目标就直接扫，用户想换再点「开始扫描」
        if (TargetCombo.SelectedItem != null)
            await ScanAsyncForEntry();

        var game = DetectedGames.FirstOrDefault();
        if (game != null)
            ScanHint.Text = $"已自动识别 {game.Reason}：{game.Display}";
    }

    /// <summary>
    /// 刷新目标进程下拉框：先自动识别正在运行的游戏（已知游戏名 → 游戏目录），
    /// 识别到就直接选中它，识别不到才退回第一个进程。不拿任意进程冒充游戏。
    /// </summary>
    public void RefreshProcessList(string preferredName)
    {
        var items = new List<string>();

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == Environment.ProcessId) continue;
                items.Add($"{p.ProcessName}（{p.Id}）");
            }
            catch { /* 进程已退出 */ }
            finally { p.Dispose(); }
        }

        items.Sort(StringComparer.OrdinalIgnoreCase);
        TargetCombo.ItemsSource = items;

        DetectedGames = GameProcessCatalog.DetectRunningGames();

        string? pick = null;

        if (!string.IsNullOrEmpty(preferredName))
            pick = items.FirstOrDefault(x => x.StartsWith(preferredName + "（", StringComparison.OrdinalIgnoreCase));

        foreach (var g in DetectedGames)
        {
            if (pick != null) break;
            var exact = $"{g.Name}（{g.Pid}）";
            pick = items.FirstOrDefault(x => string.Equals(x, exact, StringComparison.OrdinalIgnoreCase))
                   ?? items.FirstOrDefault(x => x.StartsWith(g.Name + "（", StringComparison.OrdinalIgnoreCase));
        }

        pick ??= items.FirstOrDefault();
        if (pick != null) TargetCombo.SelectedItem = pick;
    }

    private void OnAutoRefreshChanged(object sender, RoutedEventArgs e)
    {
        if (AutoRefreshBox.IsChecked == true)
        {
            _autoTimer.Start();
            ScanHint.Text = "自动刷新已开启：每 10 秒重扫一次";
        }
        else
        {
            _autoTimer.Stop();
            ScanHint.Text = "自动刷新已关闭";
        }
    }

    private async Task ScanAsync(int targetPid, string targetName, bool silent = false)
    {
        if (_busy) return;

        _busy = true;
        RescanBtn.IsEnabled = false;
        ReportBtn.IsEnabled = false;
        TargetCombo.IsEnabled = false;
        if (!silent) ScanHint.Text = "枚举全表句柄…";
        VerdictText.Text = "正在扫描…";

        try
        {
            _result = await Task.Run(() => HandleScanService.Scan(targetPid, targetName));
            Apply();

            if (!silent)
                ScanHint.Text = $"完成：全表 {_result.TotalHandles:N0} 条 · 外部进程 {_result.Findings.Count} 个 · {_result.ElapsedText}";
            else
                ScanHint.Text = $"自动刷新：{DateTime.Now:HH:mm:ss} · 外部进程 {_result.Findings.Count} 个";
        }
        catch (Exception ex)
        {
            ScanHint.Text = "扫描失败：" + ex.Message;
            VerdictText.Text = "扫描失败";
        }
        finally
        {
            _busy = false;
            RescanBtn.IsEnabled = true;
            ReportBtn.IsEnabled = true;
            TargetCombo.IsEnabled = true;
        }
    }

    /// <summary>同步扫描指定进程，供无头自检调用。</summary>
    public void ScanSync(int targetPid)
    {
        _result = HandleScanService.Scan(targetPid, "selftest");
        Apply();
    }

    private void Apply()
    {
        OwnerGrid.SetRows(_result.Findings.Select(x => new LogRow
        {
            Title = x.Header,
            Detail = $"{x.Capability}　·　{x.FlagsText}",
            Basis = x.AccessHex + (x.Known ? "　已知组件，正常工作" : "　未知来源，请核对"),
            Tag = x.Tag,
            Level = x.Known ? "known" : x.Risk,
            LevelText = x.RiskText,
            Payload = x
        }), new SysDiag.Controls.LogViewOptions { ShowTimeline = false, DefaultSort = "level" });

        Kpi.Set(
            (_result.Findings.Count.ToString("N0"), "外部进程", ""),
            (_result.WarnCount.ToString(), "可写内存 / 建线程",
                _result.WarnCount > 0 ? Palette.WarnHex : Palette.OkHex),
            (_result.HintCount.ToString(), "仅可读内存",
                _result.HintCount > 0 ? Palette.HintHex : Palette.OkHex),
            (_result.OkCount.ToString(), "已知组件", Palette.OkHex));

        VerdictText.Text = _result.Findings.Count == 0
            ? "没有外部进程持有目标句柄"
            : _result.WarnCount > 0
                ? $"{_result.WarnCount} 个未知进程可写入目标内存"
                : _result.HintCount > 0
                    ? $"{_result.HintCount} 个未知进程可读取目标内存，请核对"
                    : "没有未知进程持有高危权限";
        VerdictText.Foreground = _result.WarnCount > 0 ? Palette.Danger
            : _result.HintCount > 0 ? Palette.Hint
            : Palette.Ok;

        Bar.SetElapsed(_result.ElapsedText);
    }

    private async Task ScanAsyncForEntry(bool silent = false)
    {
        if (TargetCombo.SelectedItem is not string entry)
        {
            ScanHint.Text = "先在下拉框里选一个目标进程";
            return;
        }

        // "name（pid）" → pid
        var i = entry.LastIndexOf('（');
        if (i <= 0 || !int.TryParse(entry[(i + 1)..^1], out var pid))
        {
            ScanHint.Text = "进程条目格式异常：" + entry;
            return;
        }

        var name = entry[..i];
        await ScanAsync(pid, name, silent);
    }

    private void OnScan(object sender, RoutedEventArgs e) => _ = ScanAsyncForEntry();

    private void OnReport(object sender, RoutedEventArgs e)
    {
        try
        {
            var payload = HandleScanService.BuildReport(_result);
            _showReport(payload);
            ScanHint.Text = "报告已生成：" + Path.GetFileName(payload.HtmlPath);
        }
        catch (Exception ex)
        {
            ScanHint.Text = "报告生成失败：" + ex.Message;
        }
    }

    private void OnElevate(object sender, RoutedEventArgs e)
    {
        if (ElevationService.RelaunchAsAdmin("--open-handle", out var error))
        {
            Close();
            return;
        }

        if (!string.IsNullOrEmpty(error) && error != ElevationService.CancelledMessage)
            ScanHint.Text = error;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
