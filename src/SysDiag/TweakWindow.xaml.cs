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
/// 系统优化：把逆向出来的开关按分类铺成卡片，每条可单独开关。
///
/// 界面刻意不做「一键优化全部」——这类工具最容易出事的正是那个按钮：
/// 用户不知道它动了什么，出问题也退不回去。这里只给「逐条开」，以及
/// 一个「还原全部改动」。
/// </summary>
public partial class TweakWindow : Window
{
    private TweakGroup _group = TweakGroup.Common;
    private readonly Dictionary<TweakGroup, Border> _groupButtons = new();
    private readonly Dictionary<TweakGroup, TextBlock> _groupCounts = new();
    private bool _busy;

    /// <summary>自检用：当前分类渲染出来的条目数。</summary>
    public int ItemCount { get; private set; }

    /// <summary>自检用：当前分类里处于「已开启」的条目数。</summary>
    public int AppliedCount { get; private set; }

    /// <summary>自检用：当前分类里「读不到」的条目数。</summary>
    public int UnknownCount { get; private set; }

    public TweakWindow()
    {
        InitializeComponent();

        // 默认高 820，在 768p 屏幕上会超出工作区，底栏被裁掉且整体没有滚动。
        // 限制到可用高度后，主体区左右两个滚动区各自接管溢出内容。
        MaxHeight = SystemParameters.WorkArea.Height - 24;

        BuildGroupButtons();
        Reload();
        ApplyAdminState();
    }

    /// <summary>硬件调优含 CPU 四档下拉、批量服务禁用，不是逐项布尔开关能表达的，
    /// 因此保留独立面板；但入口收拢到本窗口，主界面不再出现两个近似同名的功能。</summary>
    private void OnOpenHardwareTune(object sender, RoutedEventArgs e)
    {
        new SystemTuneWindow { Owner = this }.ShowDialog();
    }

    // --------------------------------------------------------------- 分类

    private void BuildGroupButtons()
    {
        GroupHost.Children.Clear();
        _groupButtons.Clear();
        _groupCounts.Clear();

        foreach (var (group, title, note) in TweakCatalog.Groups)
        {
            var count = new TextBlock
            {
                Text = "",
                FontSize = 10,
                FontFamily = new FontFamily("Consolas"),
                Foreground = Palette.InkTertiary,
                VerticalAlignment = VerticalAlignment.Center
            };

            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Palette.Ink,
                VerticalAlignment = VerticalAlignment.Center
            });
            head.Children.Add(count);

            // 只在按钮上放「名称 + 条数」。
            // 原来这里还有一段分组说明，10 个分组时每块被撑到 68px，一屏只放得下 5 个，
            // 剩下 5 个（包括「Atlas 参考」「延迟与调度」）必须滚动才看得到。
            // 说明改挂 ToolTip：鼠标停一下就有，不占版面。
            var body = new StackPanel();
            body.Children.Add(head);

            // 用 Border 而不是 Button：主题里的按钮模板没有圆角，包一层 Border 也不生效
            // （Button 会画自己的背景），这里直接做成可点击的圆角块。
            var container = new Border
            {
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 6),
                ToolTip = note,
                Background = Palette.Surface,
                BorderBrush = Palette.Line,
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = body,
                Tag = group
            };

            container.MouseLeftButtonUp += (_, _) =>
            {
                _group = group;
                Reload();
            };

            container.MouseEnter += (_, _) =>
            {
                if (group != _group) container.Background = Palette.SurfaceRaise;
            };

            container.MouseLeave += (_, _) =>
            {
                if (group != _group) container.Background = Palette.Surface;
            };

            _groupButtons[group] = container;
            _groupCounts[group] = count;
            GroupHost.Children.Add(container);
        }

        StyleGroups();
    }

    private void StyleGroups()
    {
        foreach (var (group, container) in _groupButtons)
        {
            var active = group == _group;

            container.Background = active ? Palette.PrimarySoft : Palette.Surface;
            container.BorderBrush = active ? Palette.PrimaryLine : Palette.Line;
            container.BorderThickness = new Thickness(1);

            if (container.Child is StackPanel body && body.Children[0] is StackPanel head
                && head.Children[0] is TextBlock title)
            {
                title.Foreground = active ? Palette.Primary : Palette.Ink;
            }
        }
    }

    // --------------------------------------------------------------- 状态

    public void Reload()
    {
        var items = TweakCatalog.OfGroup(_group);
        var readings = TweakService.ReadAll(items);

        ItemCount = readings.Count;
        AppliedCount = readings.Count(r => r.IsOn);
        UnknownCount = readings.Count(r => r.State == TweakState.Unknown);

        ItemHost.Children.Clear();

        foreach (var reading in readings)
            ItemHost.Children.Add(BuildCard(reading));

        EmptyHint.Text = readings.Count == 0 ? "这个分类下还没有条目。" : "";
        EmptyHint.Visibility = readings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        UpdateGroupCounts();
        StyleGroups();

        var risk = items.Count(x => x.Risk == TweakRisk.Risky);
        var careful = items.Count(x => x.Risk == TweakRisk.Careful);
        var safe = items.Count(x => x.Risk == TweakRisk.Safe);

        SummaryText.Text = $"{TweakCatalog.Groups.First(g => g.Group == _group).Title}：{readings.Count} 项" +
                           $"　·　已开启 {AppliedCount}" +
                           $"　·　安全 {safe} / 谨慎 {careful} / 高风险 {risk}" +
                           (UnknownCount > 0 ? $"　·　读不到 {UnknownCount}" : "");

        Bar.SetElapsed($"备份中 {TweakService.BackupCount} 项　·　{DateTime.Now:HH:mm:ss}");
    }

    private void UpdateGroupCounts()
    {
        foreach (var (group, text) in _groupCounts)
        {
            var all = TweakCatalog.OfGroup(group);
            var on = all.Count(x => TweakService.Read(x).IsOn);

            text.Text = $"　{on}/{all.Count}";
            text.Foreground = on > 0 ? Palette.Primary : Palette.InkFaint;
        }
    }

    private void ApplyAdminState()
    {
        var admin = TweakService.IsAdmin;

        AdminBanner.Background = admin ? Palette.OkSoft : Palette.DangerSoft;
        AdminBanner.BorderBrush = admin ? Palette.Ok : Palette.Danger;
        AdminTitle.Text = admin ? "已具备管理员权限" : "普通权限：只能改 HKCU 里的项";
        AdminTitle.Foreground = admin ? Palette.Ok : Palette.Danger;

        AdminDetail.Text = admin
            ? "策略键、服务、系统层开关都能直接改。带「需重启」标记的要重启系统才生效。"
            : "标了「需管理员」的条目会写 HKLM 或改服务，当前会被拒绝并给出提示。" +
              "点右下角「以管理员身份重启」后会自动回到本窗口。";
    }

    // --------------------------------------------------------------- 卡片

    private Border BuildCard(TweakReading reading)
    {
        var item = reading.Item;

        // 标题 + 状态徽标 + 风险徽标 + 需重启
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };

        titleRow.Children.Add(new TextBlock
        {
            Text = item.Title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Palette.Ink,
            VerticalAlignment = VerticalAlignment.Center
        });

        titleRow.Children.Add(Chip(reading.StateText, StateBrush(reading.State), StateSoft(reading.State)));

        titleRow.Children.Add(Chip(item.RiskText,
            item.Risk == TweakRisk.Safe ? Palette.Ok
                : item.Risk == TweakRisk.Careful ? Palette.Warn : Palette.Danger,
            item.Risk == TweakRisk.Safe ? Palette.OkSoft
                : item.Risk == TweakRisk.Careful ? Palette.WarnSoft : Palette.DangerSoft));

        if (item.NeedsRestart)
            titleRow.Children.Add(Chip("需重启", Palette.Hint, Palette.SurfaceRaise));

        if (item.NeedsAdmin && !TweakService.IsAdmin)
            titleRow.Children.Add(Chip("需管理员", Palette.Warn, Palette.WarnSoft));

        var body = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };

        body.Children.Add(new TextBlock
        {
            Text = item.Desc,
            FontSize = 11,
            Foreground = Palette.InkSecondary,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 17
        });

        if (item.Effect.Length > 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = "代价：" + item.Effect,
                FontSize = 10.5,
                Foreground = item.Risk == TweakRisk.Risky ? Palette.Danger : Palette.Warn,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 16,
                Margin = new Thickness(0, 5, 0, 0)
            });
        }

        body.Children.Add(new TextBlock
        {
            Text = reading.Detail,
            FontSize = 9.5,
            FontFamily = new FontFamily("Consolas"),
            Foreground = Palette.InkFaint,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        });

        // 右侧开关
        var toggle = new Button
        {
            Content = reading.IsOn ? "还原这项" : "开启它",
            MinWidth = 76,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            IsEnabled = !_busy
        };

        toggle.SetResourceReference(StyleProperty, reading.IsOn ? "ToolButton" : "PrimaryButton");
        toggle.Click += (_, _) => Toggle(reading);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        left.Children.Add(titleRow);
        left.Children.Add(body);
        Grid.SetColumn(left, 0);

        Grid.SetColumn(toggle, 1);

        grid.Children.Add(left);
        grid.Children.Add(toggle);

        return new Border
        {
            Background = Palette.Surface,
            BorderBrush = Palette.Line,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 12, 0, 12),
            Child = grid
        };
    }

    private static Border Chip(string text, Brush foreground, Brush background) => new()
    {
        Background = background,
        BorderBrush = foreground,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(7),
        Padding = new Thickness(7, 1, 7, 1),
        Margin = new Thickness(8, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 9.5, Foreground = foreground }
    };

    private static Brush StateBrush(TweakState state) => state switch
    {
        TweakState.On => Palette.Ok,
        TweakState.Partial => Palette.Warn,
        TweakState.Off => Palette.InkTertiary,
        _ => Palette.Danger
    };

    private static Brush StateSoft(TweakState state) => state switch
    {
        TweakState.On => Palette.OkSoft,
        TweakState.Partial => Palette.WarnSoft,
        TweakState.Off => Palette.SurfaceRaise,
        _ => Palette.DangerSoft
    };

    // --------------------------------------------------------------- 交互

    private void Toggle(TweakReading reading)
    {
        var item = reading.Item;
        var target = !reading.IsOn;

        if (item.Risk == TweakRisk.Risky)
        {
            var confirm = UiDialog.Show(this,
                $"「{item.Title}」属于高风险设置。\n\n{item.Desc}\n\n{item.Effect}\n\n" +
                "确定要继续吗？（改错了可以用「还原全部改动」回退）",
                "确认修改高风险设置", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.OK) return;
        }

        _busy = true;

        try
        {
            var (ok, message) = TweakService.Apply(item, target);

            if (!ok)
            {
                UiDialog.Show(this, message, "没有改成", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 不代用户重启系统：这台机器可能是别人正在用的。
            // 只把「要重启」讲清楚，由用户自己决定什么时候重启。
            if (item.NeedsRestart)
            {
                UiDialog.Show(this,
                    message + "\n\n这一项要重启系统才生效（属于内核/外壳层设置，不能只重启资源管理器）。",
                    "已写入，需重启", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        finally
        {
            _busy = false;
            Reload();
        }
    }

    private void OnReload(object sender, RoutedEventArgs e) => Reload();

    private void OnRestoreAll(object sender, RoutedEventArgs e)
    {
        if (TweakService.BackupCount == 0)
        {
            UiDialog.Show(this, "还没有可还原的改动。\n\n（备份记录为空：说明本程序还没改过任何项，或者已经还原过了）",
                "没有可还原的项", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = UiDialog.Show(this,
            $"将把 {TweakService.BackupCount} 个条目改回本程序修改前的原值。\n\n" +
            "还原后如果界面没变化，可能需要重启资源管理器或重启系统。\n\n继续吗？",
            "还原全部改动", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK) return;

        var (ok, message) = TweakService.RestoreAll();

        UiDialog.Show(this, message, ok ? "已还原" : "部分失败",
            MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);

        Reload();
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        try
        {
            var (reg, cmd) = TweakService.ExportRestoreScript();

            UiDialog.Show(this,
                "已导出还原脚本：\n\n" + reg + "\n" + cmd +
                "\n\n把这两个文件拷走，以后双击 .reg 导入、双击 .cmd 执行即可还原，" +
                "不需要本程序在场。",
                "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "导出失败：" + ex.Message, "导出失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
