using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>
/// 防护状态审查窗口。只读，不提供任何关闭防护的按钮。
/// 理由见 <see cref="DefenderReviewService.WhyNoDisableButton"/>，通过按钮弹出，而不是常驻一屏说明。
/// </summary>
public partial class DefenderReviewWindow : Window
{
    private DefenderReviewService.Review _review;
    private bool _busy;

    public DefenderReviewWindow()
    {
        InitializeComponent();

        MaxHeight = SystemParameters.WorkArea.Height - 24;

        Loaded += async (_, _) => await Rerun();
    }

    /// <summary>自检用：上次审查的结论等级。</summary>
    public string Verdict => _review?.Verdict ?? "unknown";

    /// <summary>自检用：审查项数量。</summary>
    public int FindingCount => _review?.Findings.Count ?? 0;

    /// <summary>自检用：安全中心里注册的产品数。</summary>
    public int ProviderCount => _review?.Providers.Count ?? 0;

    // ---------------------------------------------------------------- 扫描

    private async Task Rerun()
    {
        if (_busy) return;
        _busy = true;

        VerdictText.Text = "审查中…";
        SummaryText.Text = "正在读取安全中心与 Defender 状态…";

        try
        {
            var review = await Task.Run(() => DefenderReviewService.Scan());
            _review = review;
            Render(review);
        }
        catch (Exception ex)
        {
            VerdictText.Text = "失败";
            SummaryText.Text = "审查失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await Rerun();

    private void OnWhyNoDisable(object sender, RoutedEventArgs e)
    {
        UiDialog.Show(this, DefenderReviewService.WhyNoDisableButton, "防护状态审查",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---------------------------------------------------------------- 渲染

    private void Render(DefenderReviewService.Review r)
    {
        VerdictText.Text = r.VerdictText;
        VerdictChip.Background = r.Verdict switch
        {
            "ok" => Palette.OkSoft,
            "weak" => Palette.WarnSoft,
            "suspicious" => Palette.DangerSoft,
            _ => Palette.SurfaceRaise
        };
        VerdictText.Foreground = r.Verdict switch
        {
            "ok" => Palette.Ok,
            "weak" => Palette.Warn,
            "suspicious" => Palette.Danger,
            _ => Palette.InkSecondary
        };

        // 详细状态读不到时直接说清是哪个接口缺了，不要用「请提权」敷衍——
        // 本机实测以管理员运行同样为空，提权解决不了。
        var blind = !r.AntivirusEnabled.HasValue && !r.RealTimeEnabled.HasValue
                    && !r.PassiveMode.HasValue && !r.TamperProtected.HasValue;

        SummaryText.Text = blind
            ? "Defender 的状态接口在本机不可用，详细状态无法判断。安全中心里注册的产品与结论见下方。"
            : r.Summary;

        DefenderStateText.Text = r.AntivirusEnabled switch
        {
            true => "在防护",
            false => "未防护",
            _ => r.DefenderPresent ? "读不到" : "已移除"
        };

        RealTimeText.Text = r.RealTimeEnabled switch
        {
            true => "开",
            false => "关",
            _ => "读不到"
        };

        TamperText.Text = r.TamperProtected switch
        {
            true => "开",
            false => "关",
            _ => "读不到"
        };

        PassiveText.Text = r.PassiveMode switch
        {
            true => "是",
            false => "否",
            _ => "读不到"
        };

        SignatureText.Text = r.SignatureAgeDays.HasValue
            ? (r.SignatureAgeDays.Value <= 0 ? "最新" : $"{r.SignatureAgeDays} 天前")
            : "读不到";

        RenderProviders(r);
        RenderFindings(r);
    }

    private void RenderProviders(DefenderReviewService.Review r)
    {
        ProviderHost.Children.Clear();

        if (!r.WscReadOk)
        {
            ProviderHost.Children.Add(Note("读不到安全中心。可能「安全中心」服务被停用，或系统被精简过。"));
            return;
        }

        if (r.Providers.Count == 0)
        {
            ProviderHost.Children.Add(Note("没有任何安全产品注册。正常的 Windows 至少会注册 Defender。"));
            return;
        }

        foreach (var p in r.Providers)
        {
            var card = new Border
            {
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 0, 0, 6),
                CornerRadius = new CornerRadius(8),
                Background = Palette.SurfaceRaise,
                BorderBrush = Palette.Line,
                BorderThickness = new Thickness(1)
            };

            var stack = new StackPanel();

            stack.Children.Add(new TextBlock
            {
                Text = p.Name,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Palette.Ink,
                TextWrapping = TextWrapping.Wrap
            });

            stack.Children.Add(new TextBlock
            {
                Text = p.StateText + (p.SignatureOutdated ? "　签名过期" : ""),
                FontSize = 10,
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = p.RealTimeOn ? Palette.Ok : Palette.Danger
            });

            if (!p.IsMicrosoft && p.Exe.Length > 0)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = p.Exe,
                    FontSize = 9.5,
                    Margin = new Thickness(0, 4, 0, 0),
                    Foreground = Palette.InkFaint,
                    TextWrapping = TextWrapping.Wrap
                });
            }

            card.Child = stack;
            ProviderHost.Children.Add(card);
        }
    }

    private void RenderFindings(DefenderReviewService.Review r)
    {
        FindingHost.Children.Clear();

        foreach (var f in r.Findings)
        {
            var card = new Border
            {
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 7),
                CornerRadius = new CornerRadius(8),
                Background = LevelSoft(f.Level),
                BorderBrush = LevelBrush(f.Level),
                BorderThickness = new Thickness(1)
            };

            var stack = new StackPanel();

            stack.Children.Add(new TextBlock
            {
                Text = f.Title,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = LevelInk(f.Level),
                TextWrapping = TextWrapping.Wrap
            });

            stack.Children.Add(new TextBlock
            {
                Text = f.Detail,
                FontSize = 10,
                Margin = new Thickness(0, 5, 0, 0),
                Foreground = Palette.InkSecondary,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 16
            });

            card.Child = stack;
            FindingHost.Children.Add(card);
        }

        // 排除项单列，它们是排查「为什么没查出来」的第一站。
        if (r.ExclusionPaths.Count > 0 || r.ExclusionProcesses.Count > 0)
        {
            var text = string.Join("\n", r.ExclusionPaths.Concat(r.ExclusionProcesses).Take(30));

            var card = new Border
            {
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 7),
                CornerRadius = new CornerRadius(8),
                Background = Palette.SurfaceRaise,
                BorderBrush = Palette.Line,
                BorderThickness = new Thickness(1)
            };

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = "排除项（不扫描）",
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Palette.Ink
            });
            stack.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 9.5,
                Margin = new Thickness(0, 5, 0, 0),
                Foreground = Palette.InkTertiary,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas, Microsoft YaHei UI")
            });

            card.Child = stack;
            FindingHost.Children.Add(card);
        }
    }

    private static TextBlock Note(string text) => new()
    {
        Text = text,
        FontSize = 10.5,
        Foreground = Palette.InkTertiary,
        TextWrapping = TextWrapping.Wrap,
        LineHeight = 17
    };

    private static Brush LevelBrush(string level) => level switch
    {
        "ok" => Palette.Ok,
        "warn" => Palette.Warn,
        "risk" => Palette.Danger,
        _ => Palette.Line
    };

    private static Brush LevelSoft(string level) => level switch
    {
        "ok" => Palette.OkSoft,
        "warn" => Palette.WarnSoft,
        "risk" => Palette.DangerSoft,
        _ => Palette.SurfaceRaise
    };

    private static Brush LevelInk(string level) => level switch
    {
        "ok" => Palette.Ok,
        "warn" => Palette.Warn,
        "risk" => Palette.Danger,
        _ => Palette.Ink
    };
}
