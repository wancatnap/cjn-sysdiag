using System.Diagnostics;
using System.Windows;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>
/// 注册表清理：只处理指向失效目标的孤儿项，删除前一律先导出备份。
/// 默认不勾选任何条目——注册表不像缓存，删错了没有「自动重建」这回事。
/// </summary>
public partial class RegistryCleanWindow : Window
{
    private RegistryScanResult _result = new();
    private bool _busy;

    /// <summary>自检读取：最近一次扫描结果。</summary>
    public RegistryScanResult Result => _result;

    public int FindingCount => _result.Findings.Count;

    public RegistryCleanWindow()
    {
        InitializeComponent();

        Kpi.Set(("—", "孤儿项", ""), ("0", "安全", ""), ("0", "谨慎", ""), ("0", "已选", ""));

        Loaded += (_, _) => Scan();
    }

    /// <summary>执行扫描（按钮与自检共用）。</summary>
    public void Scan()
    {
        if (_busy) return;
        _busy = true;

        try
        {
            EmptyHint.Text = "正在扫描注册表…";
            ScanBtn.IsEnabled = false;

            _result = RegistryCleanService.Scan();

            FindingList.ItemsSource = null;
            FindingList.ItemsSource = _result.Findings;

            EmptyHint.Text = _result.Findings.Count == 0
                ? $"没有发现指向失效目标的注册表项（扫描 {_result.ElapsedText}）"
                : "";

            Kpi.Set(
                (_result.Findings.Count.ToString(), "孤儿项", _result.Findings.Count > 0 ? Palette.WarnHex : Palette.OkHex),
                (_result.SafeCount.ToString(), "安全", Palette.OkHex),
                (_result.CarefulCount.ToString(), "谨慎", Palette.HintHex),
                ("0", "已选", ""));

            Bar.SetElapsed(_result.Summary + $"　·　{_result.ElapsedText}");
            CleanBtn.IsEnabled = _result.Findings.Count > 0;
        }
        catch (Exception ex)
        {
            Bar.SetElapsed("扫描失败：" + ex.Message);
            EmptyHint.Text = "扫描失败：" + ex.Message;
        }
        finally
        {
            ScanBtn.IsEnabled = true;
            _busy = false;
        }
    }

    private void OnRescan(object sender, RoutedEventArgs e) => Scan();

    private void OnSelectSafe(object sender, RoutedEventArgs e)
    {
        foreach (var f in _result.Findings)
            f.Selected = f.Risk == RegRisk.Safe;

        // 单选按钮不会刷新统计，这里手动刷一次
        Refresh();
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        foreach (var f in _result.Findings) f.Selected = false;
        Refresh();
    }

    private void Refresh()
    {
        Kpi.Set(
            (_result.Findings.Count.ToString(), "孤儿项", _result.Findings.Count > 0 ? Palette.WarnHex : Palette.OkHex),
            (_result.SafeCount.ToString(), "安全", Palette.OkHex),
            (_result.CarefulCount.ToString(), "谨慎", Palette.HintHex),
            (_result.SelectedCount.ToString(), "已选", _result.SelectedCount > 0 ? Palette.PrimaryHex : ""));

        CleanBtn.IsEnabled = _result.SelectedCount > 0;
        Bar.SetElapsed(_result.Summary + $"　·　已选 {_result.SelectedCount} 条");
    }

    private void OnClean(object sender, RoutedEventArgs e)
    {
        var items = _result.Findings.Where(f => f.Selected).ToList();
        if (items.Count == 0) return;

        var caution = items.Count(f => f.Risk == RegRisk.Careful);

        var question = $"将删除 {items.Count} 条注册表项" +
                       (caution > 0 ? $"（其中 {caution} 条属于「谨慎」档）" : "") +
                       "。\n\n删除前会先把涉及的键整段导出到 outputs\\reg-backup-…，想回滚双击 .reg 导入即可。\n\n" +
                       "要注意：注册表项删掉不会「自动重建」，删错了只能靠备份恢复。";

        if (UiDialog.Show(this, question, "确认清理注册表", MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            CleanBtn.IsEnabled = false;
            Bar.SetElapsed("正在备份并删除…");

            var outcome = RegistryCleanService.Clean(items);

            UiDialog.Show(this,
                outcome.Summary + "\n\n备份目录：\n" + outcome.BackupDir,
                "注册表清理完成", MessageBoxButton.OK,
                outcome.Failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

            Scan();
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "清理失败：" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            CleanBtn.IsEnabled = true;
        }
    }
}
