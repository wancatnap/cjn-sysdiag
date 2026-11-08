using System.Windows;
using SysDiag.Controls;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>
/// 机器码伪造检测：不依赖出厂基线，直接查当前机器的标识符自洽性与内核环境。
/// 结论只提示、不禁用功能——弱信号在真机上也常见，最终判断交给人。
/// </summary>
public partial class HwidIntegrityWindow : Window
{
    private readonly Action<ReportPayload> _showReport;
    private HwidIntegrityResult _result = new();

    /// <summary>自检读取：最近一次检测结果。</summary>
    public HwidIntegrityResult Result => _result;

    public int Score => _result.Score;

    public int HitCount => _result.HitCount;

    public HwidIntegrityWindow() : this(_ => { })
    {
    }

    public HwidIntegrityWindow(Action<ReportPayload> showReport)
    {
        InitializeComponent();

        _showReport = showReport ?? (_ => { });

        Kpi.Set(("—", "风险分", ""), ("0", "命中项", ""), ("0", "已检查", ""), ("0", "未检查", ""));

        CheckGrid.SetRows(Array.Empty<LogRow>(), new LogViewOptions
        {
            Title = "检查项",
            TitleNote = "不需要任何出厂对照",
            ShowTimeline = false,
            DefaultSort = "level"
        });

        Loaded += (_, _) => Rerun();
    }

    /// <summary>重新跑一遍检测（按钮与自检共用）。</summary>
    public void Rerun()
    {
        _result = HwidIntegrityService.Run();
        Apply();
    }

    private void Apply()
    {
        var color = _result.VerdictColor;

        StateTitle.Text = _result.Headline;
        StateDetail.Text = _result.Detail;
        StateTitle.Foreground = Palette.Get(color);

        StateGlyph.Text = _result.Verdict switch
        {
            HwidVerdict.Clean => "✓",
            HwidVerdict.Notice => "·",
            _ => "!"
        };
        StateGlyph.Foreground = Palette.Get(color);
        StateIcon.BorderBrush = Palette.Get(color);

        StateBox.Background = _result.Verdict switch
        {
            HwidVerdict.Clean => Palette.Surface,
            HwidVerdict.Notice => Palette.WarnSoft,
            _ => Palette.DangerSoft
        };

        Kpi.Set(
            (_result.Score.ToString(), "风险分", color),
            (_result.HitCount.ToString(), "命中项", _result.HitCount > 0 ? Palette.WarnHex : Palette.OkHex),
            (_result.CheckedCount.ToString(), "已检查", Palette.KnownHex),
            (_result.SkippedCount.ToString(), "未检查", _result.SkippedCount > 0 ? Palette.HintHex : Palette.NoneHex));

        CheckGrid.SetRows(_result.Checks.Select(c => new LogRow
        {
            Title = c.Title,
            Detail = c.Detail,
            Basis = c.Category + "　·　" + c.Source,
            Tag = c.ScoreText,
            Level = c.Level,
            LevelText = c.LevelText,
            Payload = c
        }), new LogViewOptions
        {
            Title = "检查项",
            TitleNote = $"{_result.Checks.Count} 项 · 强信号 {_result.StrongCount} 项 · 弱信号 {_result.WeakCount} 项",
            ShowTimeline = false,
            DefaultSort = "level"
        });

        Bar.SetElapsed(_result.SkippedCount == 0
            ? $"{(_result.Admin ? "管理员模式" : "普通权限")}　·　全部 {_result.CheckedCount} 项已完成　·　{DateTime.Now:HH:mm:ss}"
            : $"{(_result.Admin ? "管理员模式" : "普通权限")}　·　{_result.SkippedCount} 项未能执行");

        ReportBtn.IsEnabled = true;
    }

    private void OnRerun(object sender, RoutedEventArgs e)
    {
        try
        {
            Rerun();
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "检测失败：" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnRawList(object sender, RoutedEventArgs e)
    {
        try
        {
            _showReport(EvidenceService.HwidReport());
            Bar.SetElapsed("原始标识符清单已生成");
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "生成失败：" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnReport(object sender, RoutedEventArgs e)
    {
        try
        {
            _showReport(HwidIntegrityService.BuildReport(_result));
            Bar.SetElapsed("检测报告已生成");
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "生成失败：" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
