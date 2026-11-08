using System.Linq;
using System.Windows;
using SysDiag.Controls;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>
/// 机器码与出厂校验：登记一次出厂记录，之后校验当前机器码是否一致。
/// 不一致只提示、不禁用功能；差异项逐条列出，报告可导出复核。
/// </summary>
public partial class MachineWindow : Window
{
    private readonly Action<ReportPayload> _showReport;
    private MachineCheck _check = new();

    /// <summary>自检读取：当前校验结果。</summary>
    public MachineCheck Check => _check;

    public MachineCheckState State => _check.State;

    public int ChangedCount => _check.Changed.Count;

    public MachineWindow() : this(_ => { })
    {
    }

    public MachineWindow(Action<ReportPayload> showReport)
    {
        InitializeComponent();

        _showReport = showReport ?? (_ => { });

        Kpi.Set(("—", "当前机器码", ""), ("—", "出厂机器码", ""), ("0", "变化字段", ""), ("—", "登记时间", ""));

        FieldGrid.SetRows(Array.Empty<LogRow>(), new LogViewOptions
        {
            Title = "稳定字段比对",
            TitleNote = "只比对不会随重装系统变化的硬件标识",
            ShowTimeline = false,
            DefaultSort = "level"
        });

        Loaded += (_, _) => Verify();
    }

    /// <summary>执行校验并刷新界面。</summary>
    public void Verify()
    {
        _check = MachineIdentityService.Verify();
        Apply();
    }

    /// <summary>登记出厂记录后重新校验（自检与按钮共用）。</summary>
    public void Register()
    {
        MachineIdentityService.Register();
        Verify();
    }

    private void Apply()
    {
        StateTitle.Text = _check.Title;
        StateDetail.Text = _check.Detail;
        StateTitle.Foreground = Palette.Get(_check.StateColor);
        StateGlyph.Text = _check.State switch
        {
            MachineCheckState.Match => "✓",
            MachineCheckState.NoBaseline => "?",
            _ => "!"
        };
        StateGlyph.Foreground = Palette.Get(_check.StateColor);
        StateIcon.BorderBrush = Palette.Get(_check.StateColor);
        StateIcon.BorderThickness = new Thickness(1);

        if (_check.NeedsAttention)
            StateBox.Background = Palette.Get(_check.State == MachineCheckState.Mismatch
                ? Palette.DangerSoftHex : Palette.WarnSoftHex);
        else
            StateBox.Background = Palette.Surface;

        Kpi.Set(
            (_check.CurrentCode, "当前机器码", ""),
            (_check.BaselineCode.Length > 0 ? _check.BaselineCode : "—", "出厂机器码", Palette.KnownHex),
            (_check.Changed.Count.ToString(), "变化字段",
                _check.Changed.Count > 0 ? Palette.WarnHex : Palette.OkHex),
            (_check.SavedAt?.ToString("MM-dd HH:mm") ?? "—", "登记时间", ""));

        FieldGrid.SetRows(_check.Fields.Select(f => new LogRow
        {
            Title = f.Name + (f.IsCore ? "　核心" : ""),
            Detail = f.CurrentValue,
            Basis = f.Group,
            Tag = f.IsCore ? "核心" : "",
            Level = f.Changed ? "warn" : _check.State == MachineCheckState.NoBaseline ? "none" : "ok",
            LevelText = f.Changed ? "已变化" : _check.State == MachineCheckState.NoBaseline ? "未登记" : "一致",
            Payload = f
        }), new LogViewOptions
        {
            Title = "稳定字段比对",
            TitleNote = $"{_check.Fields.Count} 项 · 核心 {_check.Fields.Count(f => f.IsCore)} 项",
            ShowTimeline = false,
            DefaultSort = "level"
        });

        Bar.SetElapsed(_check.State == MachineCheckState.NoBaseline
            ? "尚未建立基线"
            : $"基线 {_check.SavedAt:yyyy-MM-dd HH:mm} · {_check.Computer}");

        ReportBtn.IsEnabled = true;
    }

    private void OnVerify(object sender, RoutedEventArgs e) => Verify();

    private void OnRegister(object sender, RoutedEventArgs e)
    {
        var question = _check.State == MachineCheckState.NoBaseline
            ? "把当前机器码登记为这台机器的出厂记录？之后会用它做一致性校验。"
            : "重新登记会覆盖现有的出厂记录（" + _check.SavedAt?.ToString("yyyy-MM-dd HH:mm") + "）。\n\n" +
              "只有确认这台机器是正常状态（换了硬件、重装系统之后）才这么做。";

        if (UiDialog.Show(this, question, "登记出厂记录", MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            Register();
            Bar.SetElapsed("出厂记录已更新：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "登记失败：" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>跳到「机器码伪造检测」：那里不需要登记，直接查标识符是否被改过。</summary>
    private void OnIntegrity(object sender, RoutedEventArgs e)
    {
        try
        {
            var window = new HwidIntegrityWindow(_showReport);
            WindowChromeBehavior.ApplyTheme(window);
            window.Owner = this;
            window.Show();
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "打开失败：" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnFullList(object sender, RoutedEventArgs e)
    {
        try
        {
            _showReport(EvidenceService.HwidReport());
            Bar.SetElapsed("完整机器码清单已生成");
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
            _showReport(MachineIdentityService.BuildReport(_check));
            Bar.SetElapsed("校验报告已生成");
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "生成失败：" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
