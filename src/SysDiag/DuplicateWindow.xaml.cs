using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>
/// 重复文件窗口。
///
/// 交互上刻意「慢半拍」：扫描完**不自动勾选**任何文件，要用户先点「每组只留最早的」，
/// 再点「删除选中」，确认框里还要报一次文件数和总大小。重复文件删错就是照片/工程文件没了，
/// 所以默认不动比默认预选更重要——宁可多点两下。
///
/// 删除只走回收站（RecycleBin.MoveToRecycleBin），不提供永久删除。
/// </summary>
public partial class DuplicateWindow : Window
{
    /// <summary>结果最多画这么多组。全画上千行既慢又没人看，剩下的在状态里说明。</summary>
    private const int MaxRenderGroups = 50;

    private readonly HashSet<string> _checked = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private List<DuplicateGroup> _groups = new();

    // 无参构造要独立存在：反射找 Type.EmptyTypes 时，带可选参数的构造不算「无参」，
    // 布局体检会因此跳过这个窗口（踩过一次）。
    public DuplicateWindow() : this("") { }

    public DuplicateWindow(string initialPath)
    {
        InitializeComponent();

        MaxHeight = SystemParameters.WorkArea.Height - 24;

        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        PathBox.Text = Directory.Exists(downloads) ? downloads : "";

        // 命令行指定了目录就直接扫一次：这是「打开就能看」的用法
        if (initialPath.Length > 0 && Directory.Exists(initialPath))
        {
            PathBox.Text = initialPath;
            Loaded += (_, _) => OnScan(this, new RoutedEventArgs());
        }
    }

    // ---------------------------------------------------------------- 自检读取

    /// <summary>自检用：当前结果组数。</summary>
    public int GroupCount => _groups.Count;

    /// <summary>自检用：渲染出来的行数（文件行）。</summary>
    public int RenderedFileRows => CountRows();

    /// <summary>自检用：当前勾选数量。</summary>
    public int CheckedCount => _checked.Count;

    /// <summary>自检用：删除按钮是否可用。</summary>
    public bool DeleteEnabled => DeleteBtn.IsEnabled;

    /// <summary>自检用：状态栏文字。</summary>
    public string StatusLine => StatusText.Text;

    private int CountRows()
    {
        var count = 0;
        foreach (var child in GroupHost.Children)
            if (child is Border { Child: StackPanel panel })
                count += panel.Children.OfType<Border>().Count();

        return count;
    }

    /// <summary>自检用：把一次扫描结果塞进界面（不真的读盘），验证渲染与勾选联动。</summary>
    public void LoadForTest(List<DuplicateGroup> groups)
    {
        _groups = groups;
        Render();
    }

    /// <summary>自检用：等价于点一次「每组只留最早的」。</summary>
    public int KeepOneForTest()
    {
        OnKeepOne(this, new RoutedEventArgs());
        return _checked.Count;
    }

    // ---------------------------------------------------------------- 扫描

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        var root = PathBox.Text.Trim();
        if (!Directory.Exists(root))
        {
            UiDialog.Show(this, "先选一个存在的目录。", "没有目录");
            return;
        }

        var minSize = 1048576L;
        if (SizeBox.SelectedItem is ComboBoxItem { Tag: string tag } && long.TryParse(tag, out var parsed))
            minSize = parsed;

        _checked.Clear();
        _groups = new List<DuplicateGroup>();
        GroupHost.Children.Clear();
        UpdateSelection();

        _cts = new CancellationTokenSource();
        SetScanning(true);

        try
        {
            var token = _cts.Token;
            var progress = new Progress<string>(text => StatusText.Text = text);

            var result = await Task.Run(() =>
                DuplicateFinderService.Scan(root, minSize, token, t => ((IProgress<string>)progress).Report(t)), token);

            if (result.Error.Length > 0)
            {
                StatusText.Text = "扫描失败：" + result.Error;
                return;
            }

            if (result.Canceled)
            {
                StatusText.Text = "已取消。已扫到的结果不保留，重新扫描即可。";
                return;
            }

            _groups = result.Groups;
            Render();

            StatusText.Text = result.Summary
                + (result.Groups.Count == 0 ? "　——没有发现重复文件。" : "");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "扫描出错：" + ex.Message;
        }
        finally
        {
            SetScanning(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StatusText.Text = "正在停止…";
    }

    private void SetScanning(bool scanning)
    {
        ScanBtn.IsEnabled = !scanning;
        CancelBtn.IsEnabled = scanning;
        ScanBtn.Content = scanning ? "扫描中…" : "开始扫描";
        KeepOneBtn.IsEnabled = !scanning && _groups.Count > 0;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择要扫描的目录",
            Multiselect = false
        };

        if (Directory.Exists(PathBox.Text)) dialog.InitialDirectory = PathBox.Text;

        if (dialog.ShowDialog() == true) PathBox.Text = dialog.FolderName;
    }

    // ---------------------------------------------------------------- 渲染

    private void Render()
    {
        GroupHost.Children.Clear();

        if (_groups.Count == 0)
        {
            GroupHost.Children.Add(new TextBlock
            {
                Text = "还没有扫描结果。",
                FontSize = 11,
                Foreground = Palette.InkTertiary,
                Margin = new Thickness(6, 10, 0, 0)
            });
            UpdateSelection();
            return;
        }

        foreach (var group in _groups.Take(MaxRenderGroups))
            GroupHost.Children.Add(BuildGroup(group));

        if (_groups.Count > MaxRenderGroups)
        {
            GroupHost.Children.Add(new TextBlock
            {
                Text = $"另有 {_groups.Count - MaxRenderGroups} 组未显示（按可省空间排序，前 {MaxRenderGroups} 组已覆盖绝大部分空间）。",
                FontSize = 10,
                Foreground = Palette.InkFaint,
                Margin = new Thickness(6, 12, 0, 4)
            });
        }

        UpdateSelection();
    }

    private Border BuildGroup(DuplicateGroup group)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };

        // 组头：大小 × 份数 · 可省
        panel.Children.Add(new TextBlock
        {
            Text = group.Header,
            FontSize = 10.5,
            Foreground = Palette.InkSecondary,
            Margin = new Thickness(10, 8, 0, 5)
        });

        for (var i = 0; i < group.Files.Count; i++)
            panel.Children.Add(BuildFileRow(group.Files[i], isFirst: i == 0));

        return new Border
        {
            Padding = new Thickness(0, 2, 0, 4),
            Margin = new Thickness(0, 0, 0, 6),
            CornerRadius = new CornerRadius(8),
            Background = Palette.Surface,
            BorderBrush = Palette.Line,
            BorderThickness = new Thickness(1),
            Child = panel
        };
    }

    private Border BuildFileRow(DuplicateFile file, bool isFirst)
    {
        var row = new Border
        {
            Padding = new Thickness(10, 6, 10, 6),
            CornerRadius = new CornerRadius(5),
            Background = Brushes.Transparent
        };

        row.MouseEnter += (_, _) => row.Background = Palette.SurfaceRaise;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });

        var box = new CheckBox
        {
            IsChecked = _checked.Contains(file.Path),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = file
        };
        box.Checked += (_, _) => { _checked.Add(file.Path); UpdateSelection(); };
        box.Unchecked += (_, _) => { _checked.Remove(file.Path); UpdateSelection(); };
        Grid.SetColumn(box, 0);
        grid.Children.Add(box);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = file.Name + (isFirst ? "　（本组保留这个）" : ""),
            FontSize = 11.5,
            Foreground = isFirst ? Palette.InkSecondary : Palette.Ink,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        text.Children.Add(new TextBlock
        {
            Text = file.Folder,
            FontSize = 9.5,
            Foreground = Palette.InkFaint,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = file.Path
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var stamp = new TextBlock
        {
            Text = file.LastWriteText,
            FontSize = 10,
            FontFamily = new FontFamily("Consolas"),
            Foreground = Palette.InkFaint,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(stamp, 2);
        grid.Children.Add(stamp);

        row.Child = grid;
        return row;
    }

    // ---------------------------------------------------------------- 选区

    private void OnKeepOne(object sender, RoutedEventArgs e)
    {
        _checked.Clear();
        foreach (var path in DuplicateFinderService.PreselectDuplicates(_groups))
            _checked.Add(path);

        // 重新勾选：直接把状态画回去，避免整表重建导致滚动位置跳走
        ApplyChecksToVisible();
        UpdateSelection();
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _checked.Clear();
        ApplyChecksToVisible();
        UpdateSelection();
    }

    private void ApplyChecksToVisible()
    {
        foreach (var child in GroupHost.Children)
        {
            if (child is not Border { Child: StackPanel panel }) continue;

            foreach (var row in panel.Children.OfType<Border>())
            {
                if (row.Child is not Grid grid) continue;

                var box = grid.Children.OfType<CheckBox>().FirstOrDefault();
                if (box == null) continue;

                // 文件对象直接挂在 Tag 上：按名字反查在重名文件上会勾错行
                if (box.Tag is DuplicateFile file)
                    box.IsChecked = _checked.Contains(file.Path);
            }
        }
    }

    private void UpdateSelection()
    {
        var selected = _groups
            .SelectMany(g => g.Files)
            .Where(f => _checked.Contains(f.Path))
            .ToList();

        var bytes = selected.Sum(f => f.Size);

        SelText.Text = _groups.Count == 0
            ? "还没有扫描结果"
            : selected.Count == 0
                ? $"共 {_groups.Count} 组 · 全部处理可省 {DuplicateFinderService.FormatSize(_groups.Sum(g => g.WastedBytes))}（未勾选任何文件）"
                : $"已选 {selected.Count} 个文件 · {DuplicateFinderService.FormatSize(bytes)}";

        SelText.ToolTip = selected.Count == 0 ? null : string.Join(Environment.NewLine,
            selected.Take(30).Select(f => f.Path));

        DeleteBtn.IsEnabled = selected.Count > 0;
        ClearBtn.IsEnabled = selected.Count > 0;
        KeepOneBtn.IsEnabled = _groups.Count > 0;
        DeleteBtn.Content = selected.Count > 0 ? $"删除选中（{selected.Count}）" : "删除选中";
    }

    // ---------------------------------------------------------------- 删除

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        var selected = _groups
            .SelectMany(g => g.Files)
            .Where(f => _checked.Contains(f.Path))
            .ToList();

        if (selected.Count == 0) return;

        var bytes = selected.Sum(f => f.Size);

        var confirm = UiDialog.Show(this,
            $"把 {selected.Count} 个文件（{DuplicateFinderService.FormatSize(bytes)}）移到回收站。\n\n" +
            "每个重复组都至少保留了一份，不会被全部删掉。\n" +
            "移进回收站后可以还原，但回收站本身也占空间——要真正腾出空间需要清空回收站。\n\n确定删除？",
            "删除重复文件", MessageBoxButton.OKCancel);

        if (confirm != MessageBoxResult.OK) return;

        var errors = new List<string>();
        var done = RecycleBin.MoveToRecycleBin(selected.Select(f => f.Path), errors);

        foreach (var path in done) _checked.Remove(path);

        // 删掉的文件从结果里摘掉；某组只剩一个就整组移除
        var removed = done.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _groups = _groups
            .Select(g => new DuplicateGroup
            {
                Hash = g.Hash,
                Size = g.Size,
                Files = g.Files.Where(f => !removed.Contains(f.Path)).ToList()
            })
            .Where(g => g.Files.Count > 1)
            .ToList();

        Render();

        var message = $"已移入回收站 {done.Count} 个文件。";
        if (errors.Count > 0)
            message += Environment.NewLine + Environment.NewLine + "以下文件没能移动：" + Environment.NewLine
                + string.Join(Environment.NewLine, errors.Take(8));

        UiDialog.Show(this, message, errors.Count > 0 ? "部分完成" : "已完成");
    }
}
