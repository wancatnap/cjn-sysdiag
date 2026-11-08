using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>
/// 免重装精简窗口。
///
/// 默认只列「常见预装/推广应用」，避免一上来扔几百个包名给用户。
/// 想清点全部再勾「显示全部」。所有项默认不勾——这是硬规则：
/// 精简是不可逆性较高的操作，不能默认替用户做决定。
/// </summary>
public partial class SlimWindow : Window
{
    private SlimService.ScanResult _result;
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource _cts;
    private bool _busy;
    private bool _showAllApps;

    public SlimWindow()
    {
        InitializeComponent();

        MaxHeight = SystemParameters.WorkArea.Height - 24;

        RenderPlaceholder();
    }

    /// <summary>自检用：当前渲染出的「可操作项」数量。</summary>
    public int ActionableCount { get; private set; }

    /// <summary>自检用：当前渲染出的「运行时做不到」条目数。</summary>
    public int ImpossibleCount { get; private set; }

    // ---------------------------------------------------------------- 扫描

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        _cts = new CancellationTokenSource();
        SetBusy(true);

        var progress = new Progress<string>(text => StatusText.Text = text);

        try
        {
            _result = await SlimService.ScanAsync(progress, _cts.Token);

            _selected.Clear();
            Render();

            var total = _result.Apps.Count + _result.Tasks.Count;
            StatusText.Text = $"扫描完成：预装应用 {_result.Apps.Count} 个（常见 {_result.Apps.Count(x => x.CommonBloat)} 个）、" +
                              $"计划任务 {_result.Tasks.Count} 个。默认全不勾，请逐项确认。" +
                              (_result.ExcludedRuntime > 0 ? $"　已排除 {_result.ExcludedRuntime} 个运行时依赖包。" : "") +
                              (_result.Error.Length > 0 ? "　部分失败：" + _result.Error : "");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已停止扫描。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "扫描失败：" + ex.Message;
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StatusText.Text = "正在停止…";
    }

    private void SetBusy(bool on)
    {
        _busy = on;
        ScanBtn.IsEnabled = !on;
        CancelBtn.IsEnabled = on;
        UpdateActionButtons();
    }

    // ---------------------------------------------------------------- 渲染

    private void RenderPlaceholder()
    {
        ItemHost.Children.Clear();
        ItemHost.Children.Add(new TextBlock
        {
            Text = "还没有扫描结果。\n\n" +
                   "扫描只读取当前状态：列出这台机器上的预装应用与遥测计划任务，不会改动任何东西。",
            FontSize = 11,
            Foreground = Palette.InkFaint,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 18,
            Margin = new Thickness(4, 16, 0, 0)
        });
    }

    private void Render()
    {
        ItemHost.Children.Clear();
        ActionableCount = 0;
        ImpossibleCount = 0;

        if (_result == null)
        {
            RenderPlaceholder();
            return;
        }

        // ---- 预装应用
        var apps = _showAllApps ? _result.Apps : _result.Apps.Where(x => x.CommonBloat).ToList();

        ItemHost.Children.Add(SectionHeader("预装应用",
            $"常见预装/推广应用 {apps.Count} 个。卸载后多数可从 Microsoft Store 装回，但部分应用已下架，属于「难恢复」。"));

        if (_result.Apps.Count == 0)
        {
            ItemHost.Children.Add(EmptyHint("没扫到可移除的预装应用（可能已被精简过，或 PowerShell 调用失败）。"));
        }
        else if (apps.Count == 0)
        {
            // 常见名单一个都没命中：说明这台机器已经清过了。要如实说，并给出继续的入口。
            ItemHost.Children.Add(EmptyHint(
                $"这台机器上没有命中「常见预装名单」的应用（候选里 {_result.Apps.Count} 个都不在名单内），" +
                "说明预装应用可能已经被清理过。如果仍想逐个检查，点下面的「显示全部已安装应用」。"));
        }

        if (_result.Apps.Count > 0)
        {
            foreach (var item in apps)
                ItemHost.Children.Add(BuildRow(item));

            ActionableCount += apps.Count;

            var hidden = _result.Apps.Count - apps.Count;
            var toggle = new Button
            {
                Content = _showAllApps
                    ? $"收起，只看常见项（隐藏 {hidden} 个）"
                    : $"显示全部已安装应用（另有 {hidden} 个不在常见名单里）",
                Margin = new Thickness(0, 4, 0, 12),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            toggle.SetResourceReference(StyleProperty, "ToolButton");
            toggle.Click += (_, _) =>
            {
                _showAllApps = !_showAllApps;
                Render();
            };
            ItemHost.Children.Add(toggle);
        }

        // 显示全部时，把「已排除运行时依赖」这件事也写进列表区域，避免用户以为漏扫
        if (_showAllApps && _result.ExcludedRuntime > 0)
        {
            ItemHost.Children.Add(EmptyHint(
                $"另有 {_result.ExcludedRuntime} 个运行时/依赖包（WinAppRuntime、VCLibs、XAML 等）没有列出——" +
                "删掉它们会让依赖的应用起不来，因此不提供操作。"));
        }

        // ---- 计划任务
        ItemHost.Children.Add(SectionHeader("计划任务",
            "遥测与上报类任务。禁用可原样恢复，是这三类里可逆性最好的。"));

        if (_result.Tasks.Count == 0)
            ItemHost.Children.Add(EmptyHint("没找到目标计划任务（系统版本可能不同）。"));
        else
        {
            foreach (var item in _result.Tasks)
                ItemHost.Children.Add(BuildRow(item));

            ActionableCount += _result.Tasks.Count;
        }

        // ---- 运行时做不到
        ItemHost.Children.Add(SectionHeader("运行时做不到（仅说明，无操作）",
            "这些是 AtlasOS 在镜像阶段做的事。运行时强行做会破坏更新或安全，所以本程序只讲清楚，不提供按钮。"));

        foreach (var item in _result.Impossible)
            ItemHost.Children.Add(BuildRow(item));

        ImpossibleCount = _result.Impossible.Count;

        UpdateActionButtons();
    }

    private Border SectionHeader(string title, string subtitle)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Palette.Ink
        });
        panel.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 10,
            Foreground = Palette.InkFaint,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 16,
            Margin = new Thickness(0, 4, 0, 0)
        });

        return new Border
        {
            Margin = new Thickness(0, 8, 0, 8),
            Child = panel
        };
    }

    private TextBlock EmptyHint(string text) => new()
    {
        Text = text,
        FontSize = 10.5,
        Foreground = Palette.InkFaint,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(2, 2, 0, 14)
    };

    private Border BuildRow(SlimService.SlimItem item)
    {
        var body = new StackPanel();

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock
        {
            Text = item.Name,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Palette.Ink,
            VerticalAlignment = VerticalAlignment.Center
        });

        head.Children.Add(Chip(item.ReversibilityText,
            item.Reversibility == SlimService.Reversibility.Easy ? Palette.Ok
                : item.Reversibility == SlimService.Reversibility.Hard ? Palette.Warn
                : Palette.Danger,
            item.Reversibility == SlimService.Reversibility.Easy ? Palette.OkSoft
                : item.Reversibility == SlimService.Reversibility.Hard ? Palette.WarnSoft
                : Palette.DangerSoft));

        if (item.AtlasDoes)
            head.Children.Add(Chip("Atlas 会做", Palette.Primary, Palette.PrimarySoft));

        body.Children.Add(head);

        body.Children.Add(new TextBlock
        {
            Text = item.Detail,
            FontSize = 10,
            Foreground = Palette.InkSecondary,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 16,
            Margin = new Thickness(0, 6, 0, 0)
        });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        if (item.CanAct)
        {
            var box = new CheckBox
            {
                IsChecked = _selected.Contains(item.Id),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 12, 0),
                Tag = item.Id
            };
            box.Checked += (_, _) => { _selected.Add(item.Id); UpdateActionButtons(); };
            box.Unchecked += (_, _) => { _selected.Remove(item.Id); UpdateActionButtons(); };

            Grid.SetColumn(box, 0);
            grid.Children.Add(box);
        }
        else
        {
            var dot = new TextBlock
            {
                Text = "—",
                FontSize = 11,
                Foreground = Palette.InkFaint,
                Margin = new Thickness(0, 2, 14, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            Grid.SetColumn(dot, 0);
            grid.Children.Add(dot);
        }

        Grid.SetColumn(body, 1);
        grid.Children.Add(body);

        return new Border
        {
            Background = Palette.Surface,
            BorderBrush = item.CanAct ? Palette.Line : Palette.DangerSoft,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 11, 14, 11),
            Margin = new Thickness(0, 0, 0, 8),
            Opacity = item.CanAct ? 1.0 : 0.86,
            Child = grid
        };
    }

    private void UpdateActionButtons()
    {
        if (_busy)
        {
            RemoveAppsBtn.IsEnabled = false;
            DisableTasksBtn.IsEnabled = false;
            return;
        }

        var apps = _selected.Count(x => x.StartsWith("appx-", StringComparison.OrdinalIgnoreCase));
        var tasks = _selected.Count(x => x.StartsWith("task-", StringComparison.OrdinalIgnoreCase));

        RemoveAppsBtn.IsEnabled = apps > 0;
        DisableTasksBtn.IsEnabled = tasks > 0;

        RemoveAppsBtn.Content = apps > 0 ? $"移除选中应用（{apps}）" : "移除选中应用";
        DisableTasksBtn.Content = tasks > 0 ? $"禁用选中任务（{tasks}）" : "禁用选中任务";
    }

    // ---------------------------------------------------------------- 执行

    private async void OnRemoveApps(object sender, RoutedEventArgs e)
    {
        if (_result == null || _busy) return;

        var targets = _result.Apps.Where(x => _selected.Contains(x.Id)).ToList();
        if (targets.Count == 0) return;

        var preview = string.Join("\n", targets.Take(12).Select(x => "· " + x.Name));
        if (targets.Count > 12) preview += $"\n… 共 {targets.Count} 个";

        var confirm = UiDialog.Show(this,
            $"将移除以下 {targets.Count} 个预装应用：\n\n{preview}\n\n" +
            "· 可逆性：难恢复。需要时多数要从 Microsoft Store 重新安装，部分应用可能已下架。\n" +
            "· 依赖该应用的功能会失效。\n" +
            "· 只影响当前用户，不影响其他账户。\n\n确定继续？",
            "免重装精简", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        _cts = new CancellationTokenSource();
        SetBusy(true);
        StatusText.Text = "正在移除应用…";

        try
        {
            var (ok, message) = await SlimService.RemoveAppsAsync(
                targets, new Progress<string>(t => StatusText.Text = t), _cts.Token);

            UiDialog.Show(this, message, "免重装精简",
                MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);

            _selected.Clear();
            OnScan(this, new RoutedEventArgs());
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "移除失败：" + ex.Message;
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnDisableTasks(object sender, RoutedEventArgs e)
    {
        if (_result == null || _busy) return;

        var targets = _result.Tasks.Where(x => _selected.Contains(x.Id)).ToList();
        if (targets.Count == 0) return;

        var confirm = UiDialog.Show(this,
            $"将禁用以下 {targets.Count} 个计划任务：\n\n" +
            string.Join("\n", targets.Select(x => "· " + x.Name)) +
            "\n\n· 可逆性：可恢复。点「还原」即可重新启用。\n" +
            "· 只影响遥测与上报，不影响系统更新与磁盘维护。\n\n确定继续？",
            "免重装精简", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK) return;

        var (ok, message) = SlimService.DisableTasks(targets);

        UiDialog.Show(this, message, "免重装精简",
            MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);

        _selected.Clear();
        OnScan(this, new RoutedEventArgs());
    }

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        var (ok, message) = SlimService.Restore();

        UiDialog.Show(this, message, "免重装精简",
            MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);

        OnScan(this, new RoutedEventArgs());
    }

    private static Border Chip(string text, Brush foreground, Brush background)
        => new()
        {
            Background = background,
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(7, 2, 7, 2),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = 9.5, Foreground = foreground }
        };
}
