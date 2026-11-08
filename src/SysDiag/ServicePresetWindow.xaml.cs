using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>
/// 服务预设窗口：按清单批量禁用服务，但每一项都要看得见后果、改前留底、随时还原。
///
/// 交互上刻意不做「一键全选」——这类操作的风险集中在「用户不知道某一项会关掉什么」，
/// 一个全选按钮正好把最后一道判断也省掉了。
/// </summary>
public partial class ServicePresetWindow : Window
{
    private sealed class Row
    {
        public ServicePresetService.Preset Preset { get; init; } = new();
        public CheckBox Box { get; init; }
    }

    private readonly List<Row> _rows = new();
    private string _filter = "all";

    public ServicePresetWindow()
    {
        InitializeComponent();

        MaxHeight = SystemParameters.WorkArea.Height - 24;

        Loaded += (_, _) => Reload();
    }

    /// <summary>自检用：清单项数。</summary>
    public int ItemCount => _rows.Count;

    /// <summary>自检用：当前是不是有可还原记录。</summary>
    public bool HasRestorable => ServicePresetService.HasBackup();

    // ---------------------------------------------------------------- 渲染

    private void Reload()
    {
        var presets = ServicePresetService.Scan();

        _rows.Clear();
        ItemHost.Children.Clear();

        foreach (var preset in presets)
        {
            if (_filter == "safe" && preset.Risk != "safe") continue;
            if (_filter == "careful" && preset.Risk != "careful") continue;

            var box = new CheckBox
            {
                IsChecked = false,
                Width = 18,
                MinHeight = 18,
                VerticalAlignment = VerticalAlignment.Center,
                // 未安装或已禁用的项不可勾，但仍然画出来——否则整行看起来像坏了
                IsEnabled = preset.Installed && !preset.Disabled,
                Visibility = Visibility.Visible
            };

            box.Checked += (_, _) => UpdateSummary();
            box.Unchecked += (_, _) => UpdateSummary();

            ItemHost.Children.Add(BuildRow(preset, box));
            _rows.Add(new Row { Preset = preset, Box = box });
        }

        if (_rows.Count == 0)
        {
            ItemHost.Children.Add(new TextBlock
            {
                Text = "这个筛选下没有可禁用的服务。",
                FontSize = 11,
                Foreground = Palette.InkTertiary,
                Margin = new Thickness(2, 8, 0, 0)
            });
        }

        UpdateSummary();

        // 换筛选后必须回到顶部，否则会停在上一次的滚动位置，看起来像「列表从中间开始」
        ListScroll?.ScrollToTop();
    }

    private Border BuildRow(ServicePresetService.Preset p, CheckBox box)
    {
        var card = new Border
        {
            Padding = new Thickness(14, 11, 14, 11),
            Margin = new Thickness(0, 0, 0, 6),
            CornerRadius = new CornerRadius(8),
            Background = Palette.Surface,
            BorderBrush = Palette.Line,
            BorderThickness = new Thickness(1),
            Opacity = p.Installed ? 1.0 : 0.55
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        box.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetColumn(box, 0);
        grid.Children.Add(box);

        var body = new StackPanel();
        Grid.SetColumn(body, 1);

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock
        {
            Text = p.Title,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Palette.Ink,
            VerticalAlignment = VerticalAlignment.Center
        });
        head.Children.Add(new TextBlock
        {
            Text = "　" + p.Name + "　·　" + p.Category,
            FontSize = 9.5,
            Foreground = Palette.InkFaint,
            VerticalAlignment = VerticalAlignment.Center
        });
        body.Children.Add(head);

        body.Children.Add(new TextBlock
        {
            Text = p.Effect,
            FontSize = 10,
            Foreground = Palette.InkSecondary,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 16,
            Margin = new Thickness(0, 5, 0, 0)
        });

        grid.Children.Add(body);

        var chips = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(10, 0, 0, 0)
        };

        chips.Children.Add(Chip(p.RiskText,
            p.Risk == "careful" ? Palette.Warn : Palette.Ok,
            p.Risk == "careful" ? Palette.WarnSoft : Palette.OkSoft));

        chips.Children.Add(Chip(p.StateText,
            p.Disabled ? Palette.InkSecondary : p.Running ? Palette.Ok : Palette.InkTertiary,
            Palette.SurfaceRaise));

        if (p.AtlasDisables)
            chips.Children.Add(Chip("Atlas 也禁", Palette.InkSecondary, Palette.SurfaceRaise));

        Grid.SetColumn(chips, 2);
        grid.Children.Add(chips);

        card.Child = grid;
        return card;
    }

    private static Border Chip(string text, Brush foreground, Brush background)
    {
        return new Border
        {
            Background = background,
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(0, 0, 5, 0),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 9,
                Foreground = foreground
            }
        };
    }

    // ---------------------------------------------------------------- 操作

    private void UpdateSummary()
    {
        var picked = _rows.Where(r => r.Box.IsChecked == true).ToList();

        ApplyBtn.IsEnabled = picked.Count > 0;
        RestoreBtn.IsEnabled = ServicePresetService.HasBackup();

        if (picked.Count == 0)
        {
            HintText.Text = ServicePresetService.HasBackup()
                ? $"未选择任何项（有 {ServicePresetService.BackupCount()} 项可还原）"
                : "未选择任何项";
            return;
        }

        var careful = picked.Count(r => r.Preset.Risk == "careful");
        var withDeps = picked.Count(r => r.Preset.DependentCount > 0);

        HintText.Text = $"已选 {picked.Count} 项" +
                        (careful > 0 ? $"　·　其中 {careful} 项标了「谨慎」" : "") +
                        (withDeps > 0 ? $"　·　{withDeps} 项被其他服务依赖" : "");
    }

    private void OnFilterAll(object sender, RoutedEventArgs e) => SetFilter("all");
    private void OnFilterSafe(object sender, RoutedEventArgs e) => SetFilter("safe");
    private void OnFilterCareful(object sender, RoutedEventArgs e) => SetFilter("careful");

    private void SetFilter(string filter)
    {
        _filter = filter;
        Reload();
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        var picked = _rows.Where(r => r.Box.IsChecked == true).Select(r => r.Preset).ToList();
        if (picked.Count == 0) return;

        // 二次确认里把「会发生什么」逐条列出来，而不是只问一句「确定吗」
        var careful = picked.Where(p => p.Risk == "careful").ToList();
        var deps = picked.Where(p => p.DependentCount > 0).ToList();

        var text = new System.Text.StringBuilder();
        text.AppendLine($"将把 {picked.Count} 个服务的启动类型改为「禁用」：");
        text.AppendLine();
        text.AppendLine(string.Join("、", picked.Take(10).Select(p => p.Name)) +
                        (picked.Count > 10 ? $" 等 {picked.Count} 项" : ""));
        text.AppendLine();
        text.AppendLine("只改启动类型，不会立即停止正在运行的服务；重启后完全生效。");
        text.AppendLine("原值会写入备份，可随时「还原全部改动」。");

        if (careful.Count > 0)
        {
            text.AppendLine();
            text.AppendLine($"⚠ 其中有 {careful.Count} 项标了「谨慎」，请确认你不需要它们：");
            text.AppendLine("　" + string.Join("、", careful.Select(p => p.Title)));
        }

        if (deps.Count > 0)
        {
            text.AppendLine();
            text.AppendLine($"⚠ {deps.Count} 项被其他服务依赖，禁用后依赖它的服务可能起不来：");
            text.AppendLine("　" + string.Join("、", deps.Select(p => $"{p.Name}（{p.DependentCount} 个）")));
        }

        var answer = UiDialog.Show(this, text.ToString().TrimEnd(), "服务预设",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes) return;

        var (ok, message) = ServicePresetService.Apply(picked.Select(p => p.Name));

        UiDialog.Show(this, message, "服务预设",
            MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);

        Reload();
    }

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        var count = ServicePresetService.BackupCount();
        if (count == 0)
        {
            UiDialog.Show(this, "没有可还原的记录。", "服务预设",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var answer = UiDialog.Show(this,
            $"把 {count} 个服务恢复到改动前的启动类型？\n\n" +
            "只会改启动类型，不会启动或停止服务；需要的话重启后生效。",
            "服务预设", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;

        var (ok, message) = ServicePresetService.Restore();

        UiDialog.Show(this, message, "服务预设",
            MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);

        Reload();
    }
}
