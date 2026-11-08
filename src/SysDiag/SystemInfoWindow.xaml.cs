using System.Windows;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>
/// 系统信息：概览 + 精简系统判定，全部在软件内直接读取。
/// 不调用 msinfo32 / dxdiag——精简系统里这些 exe 往往第一个被删掉。
/// </summary>
public partial class SystemInfoWindow : Window
{
    private readonly Action<ReportPayload> _showReport;
    private SystemInfoResult _result = new();
    private bool _busy;

    /// <summary>自检读取：最近一次采集结果。</summary>
    public SystemInfoResult Result => _result;

    public TrimVerdict Verdict => _result.Verdict;

    public int TrimScore => _result.TrimScore;

    public int MissingCount => _result.MissingChecks.Count;

    public SystemInfoWindow() : this(_ => { })
    {
    }

    public SystemInfoWindow(Action<ReportPayload> showReport)
    {
        InitializeComponent();

        _showReport = showReport ?? (_ => { });

        Kpi.Set(("—", "精简判定", ""), ("0", "缺失分", ""), ("0", "缺失项", ""), ("0", "信息项", ""));

        Loaded += (_, _) => Collect();
    }

    /// <summary>采集并刷新（按钮与自检共用）。</summary>
    public void Collect()
    {
        if (_busy) return;
        _busy = true;

        try
        {
            RescanBtn.IsEnabled = false;
            Bar.SetElapsed("正在读取系统信息…");

            _result = SystemInfoService.Collect();
            Apply();
        }
        catch (Exception ex)
        {
            Bar.SetElapsed("采集失败：" + ex.Message);
            UiDialog.Show(this, "读取系统信息失败：\n\n" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            RescanBtn.IsEnabled = true;
            _busy = false;
        }
    }

    private void Apply()
    {
        var color = _result.VerdictColor;

        VerdictTitle.Text = _result.Headline;
        VerdictDetail.Text = _result.VerdictDetail;
        VerdictTitle.Foreground = Palette.Get(color);

        VerdictGlyph.Text = _result.Verdict switch
        {
            TrimVerdict.Complete => "✓",
            TrimVerdict.MinorMissing => "·",
            TrimVerdict.Trimmed => "!",
            _ => "?"
        };
        VerdictGlyph.Foreground = Palette.Get(color);
        VerdictIcon.BorderBrush = Palette.Get(color);

        VerdictBox.Background = _result.Verdict switch
        {
            TrimVerdict.Complete => Palette.Surface,
            TrimVerdict.MinorMissing => Palette.WarnSoft,
            TrimVerdict.Trimmed => Palette.DangerSoft,
            _ => Palette.Surface
        };

        Kpi.Set(
            (_result.VerdictText, "精简判定", color),
            (_result.TrimScore.ToString(), "缺失分", _result.TrimScore > 0 ? Palette.WarnHex : Palette.OkHex),
            (_result.MissingChecks.Count.ToString(), "缺失项",
                _result.MissingChecks.Count > 0 ? Palette.HintHex : Palette.OkHex),
            (_result.TotalItems.ToString(), "信息项", Palette.KnownHex));

        TrimHint.Text = $"共 {_result.CheckedCount} 项 · 强证据缺失 {_result.StrongMissing} 项 · 弱信号 {_result.WeakMissing} 项";

        TrimList.ItemsSource = null;
        TrimList.ItemsSource = _result.TrimChecks;

        GroupList.ItemsSource = null;
        GroupList.ItemsSource = _result.Groups;

        Bar.SetElapsed(
            $"{(_result.Admin ? "管理员模式" : "普通权限")}　·　信息 {_result.TotalItems} 项（不可用 {_result.UnavailableItems} 项）" +
            $"　·　耗时 {_result.ElapsedText}　·　{DateTime.Now:HH:mm:ss}");

        ReportBtn.IsEnabled = true;
        CopyBtn.IsEnabled = true;
    }

    private void OnRescan(object sender, RoutedEventArgs e) => Collect();

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(SystemInfoService.BuildPlainSummary(_result));
            Bar.SetElapsed("摘要已复制到剪贴板");
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "复制失败：" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnReport(object sender, RoutedEventArgs e)
    {
        try
        {
            _showReport(SystemInfoService.BuildReport(_result));
            Bar.SetElapsed("系统信息报告已生成");
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "生成失败：" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
