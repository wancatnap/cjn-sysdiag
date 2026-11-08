using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>
/// 系统调优：Game Bar、GPU 硬件加速调度、系统服务、CPU 前后台调度。
/// 前三项按状态动态生成卡片，CPU 用下拉档位；全部改动都可还原。
/// </summary>
public partial class SystemTuneWindow : Window
{
    private readonly Dictionary<TuneKind, TuneRow> _rows = new();
    private bool _busy;

    /// <summary>自检用：当前渲染出来的调优项数量。</summary>
    public int ItemCount => _rows.Count;

    /// <summary>自检用：某项是否显示为已开启。</summary>
    public bool IsApplied(TuneKind kind) => _rows.TryGetValue(kind, out var row) && row.Entry.Applied;

    public SystemTuneWindow()
    {
        InitializeComponent();

        CpuCombo.ItemsSource = SystemTuningService.CpuProfiles;

        Reload();
        ApplyAdminState();
    }

    // --------------------------------------------------------------- 状态渲染

    public void Reload()
    {
        var entries = SystemTuningService.GetAll();
        _rows.Clear();
        ItemHost.Children.Clear();

        foreach (var entry in entries)
        {
            if (entry.Kind == TuneKind.CpuScheduling)
            {
                RenderCpu(entry);
                continue;
            }

            var row = BuildRow(entry);
            _rows[entry.Kind] = row;
            ItemHost.Children.Add(row.Container);
        }
    }

    private void ApplyAdminState()
    {
        var admin = SystemTuningService.IsAdmin;

        AdminBanner.Background = (admin ? Palette.OkSoft : Palette.DangerSoft);
        AdminBanner.BorderBrush = (admin ? Palette.Ok : Palette.Danger);
        AdminTitle.Text = admin ? "已具备管理员权限" : "需要管理员权限（Game Bar 除外）";
        AdminTitle.Foreground = (admin ? Palette.Ok : Palette.Danger);
        AdminDetail.Text = admin
            ? ""
            : "需要管理员";

        ElevateBtn.Visibility = admin ? Visibility.Collapsed : Visibility.Visible;
    }

    // --------------------------------------------------------------- 卡片构造

    private sealed class TuneRow
    {
        public TuneEntry Entry { get; init; } = new();
        public Border Container { get; init; } = new();
        public Border Badge { get; init; } = new();
        public TextBlock BadgeText { get; init; } = new();
        public TextBlock Detail { get; init; } = new();
        public Border WarnBox { get; init; } = new();
        public Button Action { get; init; } = new();
        public TextBlock RestartBadge { get; init; } = new();
    }

    private TuneRow BuildRow(TuneEntry entry)
    {
        // 标题行：标题 + 状态徽标 + 需重启标记
        var title = new TextBlock
        {
            Text = entry.Title,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Palette.Ink,
            VerticalAlignment = VerticalAlignment.Center
        };

        var badgeText = new TextBlock
        {
            Text = entry.StateText,
            FontSize = 10,
            Foreground = (entry.Applied ? Palette.Ok : Palette.InkTertiary)
        };

        var badge = new Border
        {
            Background = (entry.Applied ? Palette.OkSoft : Palette.SurfaceRaise),
            BorderBrush = (entry.Applied ? Palette.Ok : Palette.Line),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = badgeText
        };

        var restart = new TextBlock
        {
            Text = entry.NeedsRestart ? "需重启" : "",
            FontSize = 10,
            Foreground = Palette.Hint,
            Background = Palette.WarnSoft,
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = entry.NeedsRestart ? Visibility.Visible : Visibility.Collapsed
        };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(title);
        titleRow.Children.Add(badge);
        titleRow.Children.Add(restart);

        var desc = new TextBlock
        {
            Text = entry.Desc,
            FontSize = 11,
            Foreground = Palette.InkSecondary,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };

        var detail = new TextBlock
        {
            Text = entry.Detail,
            FontSize = 10.5,
            Foreground = Palette.InkTertiary,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };

        var warnText = new TextBlock
        {
            Text = "⚠  " + entry.Warning,
            FontSize = 10.5,
            Foreground = Palette.HintChip,
            TextWrapping = TextWrapping.Wrap
        };

        var warnBox = new Border
        {
            Background = Palette.WarnSoft,
            BorderBrush = Palette.Hint,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 8, 0, 0),
            Child = warnText,
            Visibility = entry.HasWarning ? Visibility.Visible : Visibility.Collapsed
        };

        var left = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        left.Children.Add(titleRow);
        left.Children.Add(desc);
        if (entry.HasDetail) left.Children.Add(detail);
        left.Children.Add(warnBox);

        var action = new Button
        {
            Content = entry.Applied ? "已开启" : "开启",
            Style = (Style)FindResource("ToolButton"),
            MinWidth = 88,
            Height = 30,
            Margin = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Background = (entry.Applied ? Palette.OkSoft : Palette.Surface),
            BorderBrush = (entry.Applied ? Palette.Ok : Palette.LineBright),
            Foreground = (entry.Applied ? Palette.Ok : Palette.InkSecondary)
        };
        action.Click += (_, _) => OnToggle(entry.Kind);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(left, 0);
        Grid.SetColumn(action, 1);
        grid.Children.Add(left);
        grid.Children.Add(action);

        var container = new Border
        {
            Background = Palette.Surface,
            BorderBrush = Palette.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 14, 16, 14),
            Margin = new Thickness(0, 0, 0, 10),
            Child = grid
        };

        return new TuneRow
        {
            Entry = entry,
            Container = container,
            Badge = badge,
            BadgeText = badgeText,
            Detail = detail,
            WarnBox = warnBox,
            Action = action,
            RestartBadge = restart
        };
    }

    private void RenderCpu(TuneEntry entry)
    {
        CpuBox.Visibility = Visibility.Visible;
        CpuDesc.Text = entry.Desc;
        CpuDetail.Text = entry.Detail;
        CpuWarnText.Text = "⚠  " + entry.Warning;

        var current = SystemTuningService.CpuProfiles.FirstOrDefault(p => entry.Detail.Contains(p.Name));
        if (current != null) CpuCombo.SelectedItem = current;
        else if (CpuCombo.SelectedItem == null) CpuCombo.SelectedIndex = 0;

        CpuBadgeText.Text = entry.Applied ? "已调整" : "系统默认";
        CpuBadge.Background = (entry.Applied ? Palette.OkSoft : Palette.PrimarySoft);
        CpuBadgeText.Foreground = (entry.Applied ? Palette.Ok : Palette.Primary);
    }

    // --------------------------------------------------------------- 交互

    private async void OnToggle(TuneKind kind)
    {
        if (_busy) return;

        var row = _rows[kind];
        var entry = row.Entry;

        if (entry.NeedsAdmin && !SystemTuningService.IsAdmin)
        {
            UiDialog.Show(this,
                "这一项要写 HKLM，需要管理员权限。\n\n点右下角「以管理员身份重启」，重启后会自动回到本窗口。",
                "破烂优化", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var turnOff = entry.Applied;

        var confirm = UiDialog.Show(this,
            turnOff
                ? $"要关闭「{entry.Title}」并恢复到修改前的状态吗？"
                : $"要开启「{entry.Title}」吗？\n\n{entry.Warning}",
            "确认", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK) return;

        _busy = true;
        row.Action.IsEnabled = false;
        StatusText.Text = "正在处理…";

        try
        {
            var (ok, message) = await Task.Run(() => kind switch
            {
                TuneKind.GameBar => SystemTuningService.SetGameBar(!turnOff),
                TuneKind.HwScheduling => SystemTuningService.SetHwScheduling(!turnOff),
                TuneKind.Services => SystemTuningService.SetServices(!turnOff),
                TuneKind.MouseAcceleration => SystemTuningService.SetMouseAcceleration(!turnOff),
                _ => (false, "未知项目")
            });

            StatusText.Text = FirstLine(message);
            if (!ok)
                UiDialog.Show(this, message, "破烂优化", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            StatusText.Text = "失败";
            UiDialog.Show(this, "操作失败：" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
            row.Action.IsEnabled = true;
            Reload();
        }
    }

    private async void OnApplyCpu(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (!SystemTuningService.IsAdmin)
        {
            UiDialog.Show(this,
                "CPU 调度写在 HKLM，需要管理员权限。\n\n点右下角「以管理员身份重启」，重启后会自动回到本窗口。",
                "破烂优化", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (CpuCombo.SelectedItem is not CpuTuneProfile profile) return;

        var confirm = UiDialog.Show(this,
            $"把 CPU 前后台调度设为「{profile.Name}」（{profile.Value}）？\n\n{profile.Summary}\n\n重启后生效，改坏了可以「还原全部改动」。",
            "确认", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK) return;

        _busy = true;
        CpuApplyBtn.IsEnabled = false;
        StatusText.Text = "正在写入…";

        try
        {
            var (ok, message) = await Task.Run(() => SystemTuningService.SetCpuProfile(profile.Value));
            StatusText.Text = FirstLine(message);

            if (!ok)
                UiDialog.Show(this, message, "破烂优化", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            StatusText.Text = "失败";
            UiDialog.Show(this, "操作失败：" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
            CpuApplyBtn.IsEnabled = true;
            Reload();
        }
    }

    private async void OnRestoreAll(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (!SystemTuningService.IsAdmin)
        {
            UiDialog.Show(this, "还原也要写注册表，需要管理员权限。",
                "破烂优化", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = UiDialog.Show(this,
            "把本页四项全部还原成第一次修改前的状态？\n\n会读 %LOCALAPPDATA%\\SysDiag\\tuning-backup.json 里的备份。",
            "确认还原", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        _busy = true;
        StatusText.Text = "正在还原…";

        try
        {
            var (ok, message) = await Task.Run(() => SystemTuningService.RestoreAll());
            StatusText.Text = FirstLine(message);

            if (!ok)
                UiDialog.Show(this, message, "破烂优化", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText.Text = "还原失败";
            UiDialog.Show(this, "还原失败：" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
            Reload();
        }
    }

    private void OnElevate(object sender, RoutedEventArgs e)
    {
        if (ElevationService.RelaunchAsAdmin("--open-tune", out var error))
        {
            Close();
            return;
        }

        if (!string.IsNullOrEmpty(error))
            StatusText.Text = error;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private static string FirstLine(string text)
    {
        var line = text.Split('\n')[0].Trim();
        return line.Length > 90 ? line[..90] + "…" : line;
    }
}
