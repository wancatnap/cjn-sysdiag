using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>
/// CPU 停核 / C-State 窗口。只做两件事：关闭核心停放、禁用处理器空闲，并支持还原。
/// 「性能提升模式」只读展示——它要按 CPU 代际判断，静态规则容易给错值。
/// </summary>
public partial class CoreParkingWindow : Window
{
    public CoreParkingWindow()
    {
        InitializeComponent();

        MaxHeight = SystemParameters.WorkArea.Height - 24;

        Reload();
    }

    /// <summary>自检用：渲染出的设置项数。</summary>
    public int ItemCount { get; private set; }

    private void OnReload(object sender, RoutedEventArgs e) => Reload();

    private void Reload()
    {
        var readings = CoreParkingService.Read();
        ItemCount = readings.Count;

        ItemHost.Children.Clear();

        var admin = ElevationService.IsAdmin;
        ElevateBtn.Visibility = admin ? Visibility.Collapsed : Visibility.Visible;

        if (!admin)
        {
            SummaryText.Text = "普通权限：可以读取状态，但改电源方案需要管理员。";
            ElevateBtn.Visibility = Visibility.Visible;
        }
        else
        {
            SummaryText.Text = $"已具备管理员权限。共 {readings.Count} 项处理器电源设置。" +
                               (CoreParkingService.HasBackup() ? "　有可还原的改动记录。" : "");
        }

        var x3d = CoreParkingService.IsDualCcdX3D();

        BatteryText.Text = x3d
            ? "⚠ 检测到双 CCD 的 X3D 处理器：" + CoreParkingService.ProcessorName() +
              "\n这类 CPU 靠核心停放把游戏固定在带 3D 缓存的 CCD 上，关闭停核会让游戏可能跑到无缓存 CCD，帧率反而变差。本软件已禁用「关闭核心停放」。"
            : CoreParkingService.OnBattery()
                ? "⚠ 当前正在用电池：关闭停核 / 禁用空闲会明显缩短续航，建议插电后再操作。"
                : "";

        RestoreBtn.IsEnabled = admin && CoreParkingService.HasBackup();

        foreach (var reading in readings)
            ItemHost.Children.Add(BuildCard(reading));
    }

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        var confirm = UiDialog.Show(this,
            "按改动前的原值还原核心停放与处理器空闲设置？\n\n" +
            "还原的是本软件记录过的项；没改过的项不会被动。",
            "CPU 停核 · C-State", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK) return;

        var (ok, message) = CoreParkingService.Restore();

        UiDialog.Show(this, message, "CPU 停核 · C-State",
            MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);

        Reload();
    }

    private Border BuildCard(CoreParkingService.Reading r)
    {
        var body = new StackPanel();

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock
        {
            Text = r.Title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Palette.Ink,
            VerticalAlignment = VerticalAlignment.Center
        });

        if (r.Applied)
            head.Children.Add(Chip("已应用", Palette.Ok, Palette.OkSoft));

        body.Children.Add(head);

        body.Children.Add(new TextBlock
        {
            Text = r.Desc,
            FontSize = 10.5,
            Foreground = Palette.InkSecondary,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 17,
            Margin = new Thickness(0, 7, 0, 0)
        });

        if (r.Effect.Length > 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = r.Effect,
                FontSize = 10,
                Foreground = Palette.Warn,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 16,
                Margin = new Thickness(0, 6, 0, 0)
            });
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(body, 0);
        grid.Children.Add(body);

        var actions = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };

        if (r.Id == "cp-parking")
        {
            var blocked = CoreParkingService.IsDualCcdX3D();

            actions.Children.Add(ActionButton(
                blocked ? "双 CCD X3D 已禁用" : r.Applied ? "已是关闭状态" : "关闭核心停放",
                !r.Applied && !blocked,
                () => CoreParkingService.DisableParking(),
                enabled: !r.Applied && !blocked));
        }
        else if (r.Id == "cp-idle")
        {
            actions.Children.Add(ActionButton(
                r.Applied ? "已禁用空闲" : "禁用处理器空闲",
                !r.Applied,
                () => CoreParkingService.DisableIdle(),
                enabled: !r.Applied));
        }
        else
        {
            actions.Children.Add(new Button
            {
                Content = "只读展示",
                MinWidth = 132,
                IsEnabled = false,
                Style = (Style)FindResource("ToolButton")
            });
        }

        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);

        return new Border
        {
            Background = Palette.Surface,
            BorderBrush = Palette.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 13, 16, 13),
            Margin = new Thickness(0, 0, 0, 9),
            Child = grid
        };
    }

    private Button ActionButton(string text, bool primary, Func<(bool Ok, string Message)> action,
        bool enabled = true)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 132,
            Height = 30,
            IsEnabled = enabled,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        button.SetResourceReference(StyleProperty, primary ? "PrimaryButton" : "ToolButton");

        button.Click += (_, _) =>
        {
            var confirm = UiDialog.Show(this,
                text + "？\n\n" +
                "· 只改当前生效的电源方案，不动其他方案模板。\n" +
                "· 代价：功耗与温度上升，笔记本续航变短。\n" +
                "· 改完建议用「延迟实测」做单变量对照，别只凭感觉。",
                "CPU 停核 · C-State", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.OK) return;

            var (ok, message) = action();
            UiDialog.Show(this, message, "CPU 停核 · C-State",
                MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
            Reload();
        };

        return button;
    }

    private void OnElevate(object sender, RoutedEventArgs e)
    {
        if (ElevationService.RelaunchAsAdmin("--open-core-parking", out var error))
        {
            Application.Current.Shutdown();
            return;
        }

        UiDialog.Show(this, "提权失败：\n\n" + error, "CPU 停核 · C-State",
            MessageBoxButton.OK, MessageBoxImage.Warning);
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
