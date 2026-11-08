using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>延迟优化：时钟 / MSI 中断 / 外设节能 / 计时器 / 虚拟化 / 电源与显卡，逐条列出，能改的给按钮。</summary>
public partial class LatencyWindow : Window
{
    private List<LatencyService.Check> _checks = new();

    /// <summary>自检用：检查项数量。</summary>
    public int Count => _checks.Count;

    /// <summary>自检用：可执行动作的项数。</summary>
    public int Actionable => _checks.Count(x => x.HasActions);

    public LatencyWindow()
    {
        InitializeComponent();

        Reload();
    }

    public void Reload()
    {
        _checks = LatencyService.Scan();

        CheckHost.Children.Clear();

        var groups = _checks.GroupBy(x => x.Group);

        foreach (var group in groups)
        {
            CheckHost.Children.Add(new TextBlock
            {
                Text = group.Key,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Palette.Ink,
                Margin = new Thickness(2, 12, 0, 8)
            });

            foreach (var check in group)
                CheckHost.Children.Add(BuildCard(check));
        }

        var ok = _checks.Count(x => x.State == "ok");
        var off = _checks.Count(x => x.State == "off");
        var unknown = _checks.Count(x => x.State == "unknown");
        var actionable = _checks.Count(x => x.HasActions);

        SummaryText.Text = $"{_checks.Count} 项：已优化 {ok}、待处理 {off}、读不到 {unknown}" +
                           (actionable > 0
                               ? $"　·　可执行 {actionable} 项"
                               : "　·　当前没有能一键执行的项（要么已是最优，要么要管理员才能改）");

        ElevateBtn.Visibility = ElevationService.IsAdmin ? Visibility.Collapsed : Visibility.Visible;

        Bar.SetElapsed($"{(ElevationService.IsAdmin ? "管理员" : "普通权限")}　·　{DateTime.Now:HH:mm:ss}");
    }

    private Border BuildCard(LatencyService.Check check)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock
        {
            Text = check.Title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Palette.Ink,
            VerticalAlignment = VerticalAlignment.Center
        });

        head.Children.Add(Chip(check.StateText, StateBrush(check.State), StateSoft(check.State)));

        if (check.NeedsAdmin && !ElevationService.IsAdmin)
            head.Children.Add(Chip("需管理员", Palette.Warn, Palette.WarnSoft));

        if (check.NeedsRestart)
            head.Children.Add(Chip("需重启", Palette.Hint, Palette.SurfaceRaise));

        var body = new StackPanel { Margin = new Thickness(0, 7, 0, 0) };

        body.Children.Add(new TextBlock
        {
            Text = check.Detail,
            FontSize = 10.5,
            Foreground = Palette.InkSecondary,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 17
        });

        if (check.Effect.Length > 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = check.Effect,
                FontSize = 10,
                Foreground = Palette.Warn,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 16,
                Margin = new Thickness(0, 5, 0, 0)
            });
        }

        var left = new StackPanel();
        left.Children.Add(head);
        left.Children.Add(body);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        if (check.Options.Count > 0)
        {
            // 多动作（例如 HPET 的两种模式）：竖排按钮，主推项用主色
            var stack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };

            for (var i = 0; i < check.Options.Count; i++)
            {
                var option = check.Options[i];

                var button = new Button
                {
                    Content = option.Label,
                    MinWidth = 148,
                    Margin = new Thickness(0, i == 0 ? 0 : 6, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    ToolTip = option.Note
                };

                button.SetResourceReference(StyleProperty, i == 0 ? "PrimaryButton" : "ToolButton");

                var captured = option;
                button.Click += (_, _) => Execute(check, captured.Label, captured.Run);

                stack.Children.Add(button);
            }

            Grid.SetColumn(stack, 1);
            grid.Children.Add(stack);
        }
        else if (check.Apply != null)
        {
            var button = new Button
            {
                Content = string.IsNullOrEmpty(check.ActionLabel) ? "执行" : check.ActionLabel,
                MinWidth = 96,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };

            button.SetResourceReference(StyleProperty, "PrimaryButton");
            button.Click += (_, _) => Execute(check, check.ActionLabel, check.Apply);

            Grid.SetColumn(button, 1);
            grid.Children.Add(button);
        }

        return new Border
        {
            Background = Palette.Surface,
            BorderBrush = Palette.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(0, 0, 0, 8),
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

    private static Brush StateBrush(string state) => state switch
    {
        "ok" => Palette.Ok,
        "off" => Palette.Warn,
        "warn" => Palette.Warn,
        _ => Palette.InkTertiary
    };

    private static Brush StateSoft(string state) => state switch
    {
        "ok" => Palette.OkSoft,
        "off" => Palette.WarnSoft,
        "warn" => Palette.WarnSoft,
        _ => Palette.SurfaceRaise
    };

    private void Run(LatencyService.Check check) => Execute(check, check.ActionLabel, check.Apply);

    /// <summary>执行一个动作：先判权限，再确认，再跑，最后刷新。</summary>
    private void Execute(LatencyService.Check check, string label, Func<(bool Ok, string Message)> action)
    {
        if (action == null) return;

        if (check.NeedsAdmin && !ElevationService.IsAdmin)
        {
            var answer = UiDialog.Show(this,
                $"「{check.Title}」要写系统设置，需要管理员权限。\n\n现在以管理员身份重启本程序吗？重启后会自动回到这一页。",
                "需要管理员权限", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer == MessageBoxResult.Yes) OnElevate(this, new RoutedEventArgs());
            return;
        }

        var confirm = UiDialog.Show(this,
            $"要执行「{(string.IsNullOrEmpty(label) ? check.Title : label)}」吗？\n\n" +
            check.Detail + (check.Effect.Length > 0 ? "\n\n" + check.Effect : ""),
            "延迟优化", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK) return;

        try
        {
            var (ok, message) = action();

            UiDialog.Show(this, message, ok ? "已完成" : "没有改成",
                MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "执行失败：\n\n" + ex.Message, "延迟优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        Reload();
    }

    private void OnRescan(object sender, RoutedEventArgs e) => Reload();

    private void OnElevate(object sender, RoutedEventArgs e)
    {
        if (ElevationService.RelaunchAsAdmin("--open-latency", out var error))
        {
            Close();
            return;
        }

        if (!string.IsNullOrEmpty(error) && error != ElevationService.CancelledMessage)
            Bar.SetElapsed(error);
    }
}
