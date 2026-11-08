using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>
/// 显示器配色（ICC）：选显示器 → 选配置 → 应用；也可以导入自己的 .icc / .icm 或把现有的导出去。
/// </summary>
public partial class IccWindow : Window
{
    private List<IccMonitor> _monitors = new();
    private List<IccProfile> _profiles = new();
    private IccMonitor _monitor;
    private IccProfile _selected;

    /// <summary>自检用：当前枚举到的显示器数与配置数。</summary>
    public int MonitorCount => _monitors.Count;

    public int ProfileCount => _profiles.Count;

    public IccWindow()
    {
        InitializeComponent();

        StrengthSlider.ValueChanged += (_, _) => StrengthText.Text = $"{StrengthSlider.Value:F0}%";
        AutoCheck.Click += OnAutoReapplyChanged;

        Reload();
    }

    public void Reload()
    {
        _monitors = IccService.Monitors();
        _profiles = IccService.Profiles();

        MonitorCombo.ItemsSource = null;
        MonitorCombo.ItemsSource = _monitors.Select(m => m.Display).ToList();

        if (_monitors.Count > 0)
        {
            MonitorCombo.SelectedIndex = 0;
            _monitor = _monitors[0];
        }

        // 默认选中「当前生效的那个」，这样一打开预览就是有意义的对比
        _selected = _profiles.FirstOrDefault(p => _monitor != null
                                                  && p.FileName.Equals(_monitor.CurrentProfile, StringComparison.OrdinalIgnoreCase))
                    ?? _profiles.FirstOrDefault();

        RenderProfiles();
        RenderMonitor();
        RenderPreview();

        if (_monitor != null)
            StrengthSlider.Value = IccService.SavedStrength(_monitor.DeviceName) * 100;
    }

    private void RenderMonitor()
    {
        if (_monitor == null)
        {
            CurrentText.Text = "没有找到显示器";
            DetailText.Text = "";
            StateBadge.Visibility = Visibility.Collapsed;
            ApplyBtn.IsEnabled = false;
            RestoreBtn.IsEnabled = false;
            LutApplyBtn.IsEnabled = false;
            LutRestoreBtn.IsEnabled = false;
            LutStatusText.Text = "没有可用显示器。";
            return;
        }

        CurrentText.Text = _monitor.CurrentName;

        var assoc = IccService.ReadAssociation(_monitor.Instance);

        var perUser = assoc is { UsePerUser: true };
        var ours = IccService.BackupCount > 0;

        StateBadge.Visibility = Visibility.Visible;
        StateBadge.Background = perUser ? Palette.PrimarySoft : Palette.SurfaceRaise;
        StateBadge.BorderBrush = perUser ? Palette.PrimaryLine : Palette.Line;
        StateBadge.BorderThickness = new Thickness(1);

        StateText.Text = perUser ? "用户级配置已生效" : "系统默认";
        StateText.Foreground = perUser ? Palette.Primary : Palette.InkTertiary;

        var list = assoc == null ? "（没有关联记录）" : $"{assoc.Profiles.Length} 项";

        DetailText.Text =
            $"实例 {_monitor.Instance}　·　关联清单 {list}　·　UsePerUserProfiles = {(perUser ? 1 : 0)}" +
            (_monitor.CurrentPath.Length > 0
                ? "\n系统级关联（GDI 报告，不含用户级设置）：" + _monitor.CurrentPath
                : "");

        ApplyBtn.IsEnabled = _profiles.Count > 0;
        RestoreBtn.IsEnabled = true;
        LutApplyBtn.IsEnabled = _selected != null;
        LutRestoreBtn.IsEnabled = true;

        // LUT 状态：这条才是「画面到底变没变」的依据
        var lutState = IccService.LutStatus(_monitor);
        var selectedLine = _selected == null
            ? ""
            : $"\n所选配置：{_selected.EffectText}" + (_selected.HasVcgt ? "　·　" + _selected.VcgtText : "");

        LutStatusText.Text = $"当前：{lutState}{selectedLine}";

        AutoCheck.IsChecked = IccService.AutoReapply(_monitor.DeviceName);

        Bar.SetElapsed($"{(perUser ? "用户级配置生效中" : "系统默认")}　·　LUT {lutState}");
    }

    private void OnAutoReapplyChanged(object sender, RoutedEventArgs e)
    {
        if (_monitor == null) return;

        var want = AutoCheck.IsChecked == true;

        if (!IccService.SetAutoReapply(_monitor, want))
        {
            AutoCheck.IsChecked = false;

            UiDialog.Show(this, "这台显示器还没套过配置，现在没有可以自动重放的东西。\n\n先点一次「应用所选」，再回来勾这一项。",
                "还没有可重放的配置", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void RenderProfiles()
    {
        ProfileHost.Children.Clear();

        CountText.Text = $"{_profiles.Count} 个";

        if (_profiles.Count == 0)
        {
            ProfileHost.Children.Add(new TextBlock
            {
                Text = "没有找到 ICC 配置。点上面「导入 ICC…」加一个。",
                FontSize = 11,
                Foreground = Palette.InkTertiary,
                Margin = new Thickness(8, 10, 0, 0)
            });

            return;
        }

        foreach (var profile in _profiles)
            ProfileHost.Children.Add(BuildRow(profile));
    }

    private Border BuildRow(IccProfile profile)
    {
        var active = _selected?.FullPath == profile.FullPath;

        var title = new TextBlock
        {
            Text = profile.Description,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = active ? Palette.Primary : Palette.Ink,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(title);
        head.Children.Add(Chip(profile.SourceText, profile.IsSystem ? Palette.InkTertiary : Palette.Hint,
            Palette.SurfaceRaise));

        if (profile.ColorSpace.Length > 0)
            head.Children.Add(Chip(profile.ColorSpace, Palette.InkTertiary, Palette.SurfaceRaise));

        if (profile.Version.Length > 0)
            head.Children.Add(Chip("v" + profile.Version, Palette.InkTertiary, Palette.SurfaceRaise));

        // 有没有校准表，决定套上去画面变不变——这是用户最需要一眼看到的
        head.Children.Add(profile.HasVcgt
            ? Chip($"含校准表 {profile.VcgtDelta:F0}%", Palette.Ok, Palette.OkSoft)
            : Chip("仅描述", Palette.InkTertiary, Palette.SurfaceRaise));

        var body = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        body.Children.Add(new TextBlock
        {
            Text = profile.FileName + "　·　" + profile.SizeText +
                   (profile.IsMatrix && profile.Gamma > 0 ? $"　·　gamma {profile.Gamma:0.00}" : "") +
                   (profile.HasVcgt ? "" : "　·　套上桌面不会变"),
            FontSize = 9.5,
            FontFamily = new FontFamily("Consolas"),
            Foreground = Palette.InkFaint,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var stack = new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(body);

        var container = new Border
        {
            Background = active ? Palette.PrimarySoft : Palette.Surface,
            BorderBrush = active ? Palette.PrimaryLine : Palette.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 0, 0, 6),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = stack,
            ToolTip = profile.FullPath
        };

        container.MouseLeftButtonUp += (_, _) =>
        {
            _selected = profile;
            RenderProfiles();
            RenderPreview();
            ExportBtn.IsEnabled = true;
        };

        return container;
    }

    private static Border Chip(string text, Brush foreground, Brush background) => new()
    {
        Background = background,
        BorderBrush = foreground,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(6, 1, 6, 1),
        Margin = new Thickness(7, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 9, Foreground = foreground }
    };

    private static readonly (byte R, byte G, byte B, string Name)[] Swatches =
    {
        (0, 0, 0, "黑"), (64, 64, 64, "暗部"), (128, 128, 128, "中灰"), (200, 200, 200, "亮部"),
        (255, 255, 255, "白"), (214, 68, 68, "红"), (86, 170, 86, "绿"), (70, 110, 210, "蓝"),
        (230, 180, 150, "肤色"), (40, 90, 160, "夜空"), (200, 150, 60, "土黄"), (120, 200, 190, "青")
    };

    private void RenderPreview()
    {
        BeforeRow.Children.Clear();
        AfterRow.Children.Clear();

        foreach (var (r, g, b, name) in Swatches)
        {
            BeforeRow.Children.Add(Swatch(r, g, b, 1.0));
            AfterRow.Children.Add(Swatch(r, g, b, 1.0, _selected));
        }

        PreviewNote.Text = _selected == null
            ? "左边选一个配置看效果。真正改变屏幕的是配置里的校准表（vcgt），不是色域描述。"
            : _selected.HasVcgt
                ? $"这个配置带校准表，相对原始最大偏差 {_selected.VcgtDelta:F1}%。应用后会把曲线写进显卡 LUT，桌面和游戏都能看到变化。" +
                  "上面色块只是按曲线估的近似值。"
                : "这个配置只有色彩描述，没有校准表——套上去桌面和游戏不会变，只有 Photoshop 这类读 ICC 的软件才认。" +
                  "想看到变化，勾选下面「按色调曲线合成」再用。";
    }

    private Border Swatch(byte r, byte g, byte b, double scale, IccProfile profile = null)
    {
        var outColor = profile == null ? Color.FromRgb(r, g, b) : Apply(profile, r, g, b);

        return new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(outColor),
            BorderBrush = Palette.Line,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 4, 0)
        };
    }

    private static Color Apply(IccProfile profile, byte r, byte g, byte b)
    {
        var (nr, ng, nb) = profile.Simulate(r, g, b);
        return Color.FromRgb(nr, ng, nb);
    }

    // ---------------------------------------------------------------- 操作

    private void OnMonitorChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = MonitorCombo.SelectedIndex;
        if (index < 0 || index >= _monitors.Count) return;

        _monitor = _monitors[index];
        RenderMonitor();
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Reload();

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (_monitor == null || _selected == null)
        {
            UiDialog.Show(this, "先在上面选显示器，再在左边点一个 ICC 配置。", "显示器配色",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var (ok, message) = IccService.Apply(
            _monitor,
            _selected,
            StrengthSlider.Value / 100.0,
            applyLut: true,
            synthIfMissing: SynthCheck.IsChecked == true);

        UiDialog.Show(this, message, ok ? "已应用" : "没有应用",
            MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);

        Reload();
    }

    /// <summary>只重写 LUT，不动色彩关联。</summary>
    private void OnApplyLut(object sender, RoutedEventArgs e)
    {
        if (_monitor == null || _selected == null)
        {
            UiDialog.Show(this, "先选显示器和配置。", "显卡 LUT",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var (ok, message, _) = IccService.ApplyLut(
            _monitor,
            _selected,
            StrengthSlider.Value / 100.0,
            SynthCheck.IsChecked == true);

        UiDialog.Show(this, message, ok ? "LUT 已写入" : "没有写入",
            MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);

        RenderMonitor();
        RenderPreview();
    }

    private void OnRestoreLut(object sender, RoutedEventArgs e)
    {
        if (_monitor == null) return;

        var (ok, message) = IccService.RestoreLut(_monitor);

        UiDialog.Show(this, message, ok ? "LUT 已还原" : "没有还原",
            MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);

        RenderMonitor();
    }

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        if (_monitor == null) return;

        var (ok, message) = IccService.RestoreDefault(_monitor);

        UiDialog.Show(this, message, ok ? "已恢复" : "没有恢复",
            MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);

        Reload();
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 ICC 配置文件",
            Filter = "ICC 配置 (*.icc;*.icm)|*.icc;*.icm|所有文件 (*.*)|*.*",
            Multiselect = true
        };

        if (dialog.ShowDialog(this) != true) return;

        var lines = new List<string>();
        var last = (IccProfile)null;

        foreach (var file in dialog.FileNames)
        {
            var (ok, message, profile) = IccService.Import(file);
            lines.Add((ok ? "✓ " : "✗ ") + Path.GetFileName(file) + "：" + message);
            if (ok) last = profile ?? last;
        }

        Reload();

        if (last != null) _selected = _profiles.FirstOrDefault(p => p.FileName == last.FileName) ?? _selected;
        RenderProfiles();
        RenderPreview();

        UiDialog.Show(this, string.Join("\n\n", lines), "导入结果",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (_selected == null)
        {
            UiDialog.Show(this, "先在左边点一个配置。", "导出 ICC",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出 ICC 配置",
            FileName = _selected.FileName,
            Filter = "ICC 配置 (*.icc)|*.icc|ICC 配置 (*.icm)|*.icm"
        };

        if (dialog.ShowDialog(this) != true) return;

        var (ok, message) = IccService.Export(_selected, dialog.FileName);

        UiDialog.Show(this, message, ok ? "已导出" : "导出失败",
            MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    /// <summary>打开手动调色面板（gamma / 亮度 / 对比度 / 增益）。</summary>
    private void OnOpenTune(object sender, RoutedEventArgs e)
    {
        try
        {
            var window = new ColorTuneWindow { Owner = this };
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "打开手动调色失败：\n\n" + ex.Message, "显示器配色",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // 调色可能改过 LUT，回来刷新一下状态
        Reload();
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)    {
        var target = Directory.Exists(IccService.DataDir) ? IccService.DataDir : AppPaths.OutputDir;

        try
        {
            Directory.CreateDirectory(target);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Bar.SetElapsed("打不开目录：" + ex.Message);
        }
    }
}
