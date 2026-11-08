using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SysDiag.Services;

namespace SysDiag;

/// <summary>
/// 大文件管理：按大小列出磁盘上的大文件，交给用户自己判断去留。
///
/// 为什么不做「一键清理推荐」：20 GB 的游戏安装包和 20 GB 的工程素材在程序看来一模一样，
/// 但价值天差地别。这个窗口的定位是**帮你找到它**，不是**替你做决定**。
/// 所以删除只有手动一条路，而且一律进回收站。
/// </summary>
public partial class BigFileWindow : Window
{
    private readonly List<BigFileItem> _items = new();

    private CancellationTokenSource _cts;
    private bool _scanning;

    /// <summary>可选阈值。必须是带真实属性的类：命名元组的字段名只是编译期别名，
    /// 运行时是 Item1/Item2，ComboBox 的 DisplayMemberPath 反射不到，下拉框会一片空白。</summary>
    private sealed class ThresholdOption
    {
        public long Bytes { get; init; }
        public string Text { get; init; } = "";
    }

    private static readonly ThresholdOption[] Thresholds =
    {
        new() { Bytes = 100L * 1024 * 1024, Text = "100 MB" },
        new() { Bytes = 500L * 1024 * 1024, Text = "500 MB" },
        new() { Bytes = 1024L * 1024 * 1024, Text = "1 GB" },
        new() { Bytes = 2L * 1024L * 1024 * 1024, Text = "2 GB" },
        new() { Bytes = 5L * 1024L * 1024 * 1024, Text = "5 GB" },
        new() { Bytes = 10L * 1024L * 1024 * 1024, Text = "10 GB" }
    };

    /// <summary>当前选中阈值的显示文本，不依赖 ComboBox.Text（后者在自定义项下可能为空）。</summary>
    private string ThresholdText =>
        SizeCombo.SelectedItem is ThresholdOption t ? t.Text : "所选大小";

    public BigFileWindow()
    {
        InitializeComponent();

        MaxHeight = SystemParameters.WorkArea.Height - 24;

        FillDrives();
        FillThresholds();
        UpdateSummary();
    }

    // ---------------------------------------------------------------- 初始化

    private void FillDrives()
    {
        var drives = BigFileService.FixedDrives();

        if (drives.Count == 0)
        {
            DrivePanel.Children.Add(new TextBlock
            {
                Text = "没有找到固定磁盘",
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center
            });

            ScanBtn.IsEnabled = false;
            return;
        }

        foreach (var (root, label) in drives)
        {
            var box = new CheckBox
            {
                Content = label,
                Tag = root,
                IsChecked = true,
                FontSize = 10.5,
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            DrivePanel.Children.Add(box);
        }
    }

    private void FillThresholds()
    {
        SizeCombo.DisplayMemberPath = "Text";
        SizeCombo.SelectedValuePath = "Bytes";
        SizeCombo.ItemsSource = Thresholds;
        SizeCombo.SelectedIndex = 1;              // 默认 500 MB
    }

    // ---------------------------------------------------------------- 扫描

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        if (_scanning) return;

        var roots = DrivePanel.Children.OfType<CheckBox>()
            .Where(c => c.IsChecked == true)
            .Select(c => (string)c.Tag)
            .ToList();

        if (roots.Count == 0)
        {
            UiDialog.Show(this, "先勾选要扫描的磁盘。", "大文件管理",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var minBytes = SizeCombo.SelectedValue is long b ? b : 500L * 1024 * 1024;

        _cts = new CancellationTokenSource();
        SetScanning(true);

        var progress = new Progress<string>(text => StatusText.Text = text);

        try
        {
            var result = await Task.Run(() => BigFileService.Scan(minBytes, roots, progress, _cts.Token));

            _items.Clear();
            _items.AddRange(result);

            RefreshList();

            var total = _items.Sum(x => x.Size);

            StatusText.Text = _items.Count == 0
                ? $"范围内没有大于 {ThresholdText} 的文件。"
                : $"找到 {_items.Count} 个大于 {ThresholdText} 的文件，合计 {BigFileService.FormatSize(total)}。" +
                  "　按大小降序，双击可打开所在文件夹。";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已停止。列表保持上次结果。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "扫描失败：" + ex.Message;
        }
        finally
        {
            SetScanning(false);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        try { _cts?.Cancel(); } catch { }

        StatusText.Text = "正在停止…（当前目录处理完就退出）";
    }

    private void SetScanning(bool on)
    {
        _scanning = on;

        ScanBtn.IsEnabled = !on;
        ScanBtn.Content = on ? "扫描中…" : "开始扫描";
        CancelBtn.IsEnabled = on;
        Bar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        SizeCombo.IsEnabled = !on;

        foreach (var box in DrivePanel.Children.OfType<CheckBox>()) box.IsEnabled = !on;
    }

    private void RefreshList()
    {
        FileList.ItemsSource = null;
        FileList.ItemsSource = _items;

        UpdateSummary();
    }

    // ---------------------------------------------------------------- 选择与操作

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSummary();

    private void UpdateSummary()
    {
        var selected = FileList.SelectedItems.OfType<BigFileItem>().ToList();
        var total = _items.Sum(x => x.Size);

        TotalText.Text = selected.Count > 0
            ? $"已选 {selected.Count} 个 · {BigFileService.FormatSize(selected.Sum(x => x.Size))}　·　" +
              $"列表 {_items.Count} 项合计 {BigFileService.FormatSize(total)}"
            : _items.Count > 0
                ? $"列表 {_items.Count} 项 · 合计 {BigFileService.FormatSize(total)}"
                : "还没有结果";

        OpenBtn.IsEnabled = selected.Count == 1;
        DeleteBtn.IsEnabled = selected.Count > 0;
    }

    private void OnRowDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (FileList.SelectedItems.OfType<BigFileItem>().FirstOrDefault() is { } item)
            Reveal(item.FullPath);
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItems.OfType<BigFileItem>().FirstOrDefault() is { } item)
            Reveal(item.FullPath);
    }

    private void Reveal(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "打不开所在文件夹：\n\n" + ex.Message, "大文件管理",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        var selected = FileList.SelectedItems.OfType<BigFileItem>().ToList();
        if (selected.Count == 0) return;

        var size = BigFileService.FormatSize(selected.Sum(x => x.Size));

        var preview = string.Join("\n", selected.Take(8).Select(x => $"· {x.Name}（{x.SizeText}）"));

        if (selected.Count > 8) preview += $"\n… 另外 {selected.Count - 8} 个";

        var confirm = UiDialog.Show(this,
            $"要把这 {selected.Count} 个文件（合计 {size}）移入回收站吗？\n\n{preview}\n\n" +
            "移入回收站后可以在资源管理器里右键「还原」；清空回收站之后就无法恢复了。\n" +
            "正在被程序占用的文件会跳过，并如实报告。",
            "移入回收站", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        var errors = new List<string>();
        var moved = RecycleBin.MoveToRecycleBin(selected.Select(x => x.FullPath), errors);

        var movedSet = new HashSet<string>(moved, StringComparer.OrdinalIgnoreCase);

        var remain = _items.Where(x => !movedSet.Contains(x.FullPath)).ToList();
        _items.Clear();
        _items.AddRange(remain);

        RefreshList();

        var message = $"已移入回收站：{moved.Count} 个文件。";

        if (errors.Count > 0)
            message += $"\n\n有 {errors.Count} 个没删掉：\n" + string.Join("\n", errors.Take(6));

        StatusText.Text = message.Replace("\n", " ");

        UiDialog.Show(this, message, moved.Count > 0 ? "已移入回收站" : "没有删除成功",
            MessageBoxButton.OK,
            moved.Count > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    protected override void OnClosed(EventArgs e)
    {
        try { _cts?.Cancel(); } catch { }

        base.OnClosed(e);
    }
}
