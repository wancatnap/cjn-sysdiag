using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>DLSS 模型设置：检测 + 预设选择 + 生成 .nip 导入文件。</summary>
public partial class DlssWindow : Window
{
    private readonly Dictionary<string, Border> _cards = new();
    private DlssEnvironment _env = new();
    private DlssChoice _choice = new() { PresetMode = "均衡" };
    private bool _loading;

    /// <summary>自检用：卡片数量。</summary>
    public int PresetCount => _cards.Count;

    /// <summary>自检用：当前选中的方案名。</summary>
    public string CurrentMode => _choice.PresetMode;

    public DlssWindow()
    {
        InitializeComponent();

        PresetCombo.ItemsSource = DlssService.PresetOptions;
        OverrideCombo.ItemsSource = DlssService.OverrideOptions;
        EdgeCombo.ItemsSource = new[] { "档位一（轻微）", "档位二（默认）", "档位三（最强）" };
        EdgeCombo.SelectedIndex = 1;

        BuildPresetCards();
        ReloadChoice();

        Loaded += async (_, _) => await DetectAsync();
    }

    // ------------------------------------------------------------- 预设卡片

    private void BuildPresetCards()
    {
        PresetGrid.Children.Clear();
        _cards.Clear();

        AddPresetCard("帧率优先", "↑ 帧率提高", "↓ 画质降低", "#2F9E44", "#EBFBEE", "#B2F2BB", "优先追求流畅度");
        AddPresetCard("均衡", "↔ 帧率均衡", "↔ 画质均衡", "#1971C2", "#E7F5FF", "#A5D8FF", "兼顾流畅与细节");
        AddPresetCard("画质优先", "↑ 画质提高", "↓ 帧率降低", "#E8590C", "#FFF4E6", "#FFD8A8", "优先追求画面细节");
    }

    private void AddPresetCard(string mode, string line1, string line2,
        string accent, string tint, string border, string desc)
    {
        var stack = new StackPanel();

        stack.Children.Add(new TextBlock
        {
            Text = line1,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Palette.Get(accent)
        });

        stack.Children.Add(new TextBlock
        {
            Text = line2,
            FontSize = 11.5,
            Foreground = Palette.Get(accent),
            Margin = new Thickness(0, 4, 0, 0),
            Opacity = 0.85
        });

        stack.Children.Add(new Border
        {
            Height = 1,
            Background = Palette.Get(border),
            Margin = new Thickness(0, 10, 0, 8)
        });

        stack.Children.Add(new TextBlock
        {
            Text = desc,
            FontSize = 10.5,
            Foreground = Palette.Get("#868E96"),
            TextWrapping = TextWrapping.Wrap
        });

        var card = new Border
        {
            Background = Palette.Get("#FFFFFF"),
            BorderBrush = Palette.Get("#E9ECEF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 10, 0),
            Cursor = Cursors.Hand,
            Child = stack
        };

        card.MouseLeftButtonUp += (_, _) => SelectMode(mode);
        card.MouseEnter += (_, _) => { if (_choice.PresetMode != mode) card.Background = Palette.Get("#F8F9FA"); };
        card.MouseLeave += (_, _) => { if (_choice.PresetMode != mode) card.Background = Palette.Get("#FFFFFF"); };

        _cards[mode] = card;
        PresetGrid.Children.Add(card);
    }

    private void SelectMode(string mode)
    {
        _loading = true;

        _choice = DlssService.ApplyPreset(mode);
        PresetCombo.SelectedItem = DlssService.PresetOptions.FirstOrDefault(o => o.Value == _choice.PresetLetter);
        OverrideCombo.SelectedItem = DlssService.OverrideOptions.FirstOrDefault(o => o.Value == _choice.DllOverride);

        _loading = false;

        HighlightCards();
        StatusText.Text = $"已选择「{mode}」方案，还没保存。";
    }

    private void HighlightCards()
    {
        var accents = new Dictionary<string, (string Border, string Tint, string Accent)>
        {
            ["帧率优先"] = ("#8CE99A", "#EBFBEE", "#2F9E44"),
            ["均衡"] = ("#A5D8FF", "#E7F5FF", "#1971C2"),
            ["画质优先"] = ("#FFD8A8", "#FFF4E6", "#E8590C")
        };

        foreach (var (mode, card) in _cards)
        {
            var selected = mode == _choice.PresetMode;
            var colors = accents[mode];

            card.BorderBrush = Palette.Get(selected ? colors.Border : "#E9ECEF");
            card.BorderThickness = new Thickness(selected ? 2 : 1);
            card.Background = Palette.Get(selected ? colors.Tint : "#FFFFFF");
        }
    }

    // --------------------------------------------------------------- 检测

    private async Task DetectAsync(string gameDir = "")
    {
        StatusText.Text = "正在检测显卡与游戏目录…";

        try
        {
            _env = await Task.Run(() => DlssService.Detect(gameDir));
            RenderEnv();
            StatusText.Text = _env.GameFound
                ? "检测完成。"
                : "没自动找到游戏目录，点「选游戏目录」手动指定。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "检测失败：" + ex.Message;
        }
    }

    private void RenderEnv()
    {
        GpuText.Text = string.IsNullOrEmpty(_env.GpuName) ? "未检测到 NVIDIA 显卡" : _env.GpuName;

        if (string.IsNullOrEmpty(_env.DriverVersion))
        {
            DriverText.Text = "驱动版本未知";
            DriverBadge.Background = Palette.Get("#F1F3F5");
            DriverBadge.BorderBrush = Palette.Get("#DEE2E6");
            DriverText.Foreground = Palette.Get("#868E96");
        }
        else
        {
            DriverText.Text = "驱动 " + _env.DriverVersion;
            DriverBadge.Background = Palette.Get("#EBFBEE");
            DriverBadge.BorderBrush = Palette.Get("#B2F2BB");
            DriverText.Foreground = Palette.Get("#2F9E44");
        }

        DlssText.Text = _env.DlssFound ? "DLSS " + _env.DlssVersion : "未找到 DLSS";
        DlssBadge.Background = Palette.Get(_env.DlssFound ? "#E7F5FF" : "#F1F3F5");
        DlssBadge.BorderBrush = Palette.Get(_env.DlssFound ? "#A5D8FF" : "#DEE2E6");
        DlssText.Foreground = Palette.Get(_env.DlssFound ? "#1971C2" : "#868E96");

        GameText.Text = _env.GameFound ? "游戏目录：" + _env.GameDir : "游戏目录：未找到";

        var extra = _env.DlssGText;
        SupportText.Text = DlssService.PresetSupportHint(_env.DlssVersion)
                           + (string.IsNullOrEmpty(extra) ? "" : "　·　" + extra);
    }

    // --------------------------------------------------------------- 选择

    private void ReloadChoice()
    {
        _loading = true;

        _choice = DlssService.LoadChoice();

        PresetCombo.SelectedItem = DlssService.PresetOptions.FirstOrDefault(o => o.Value == _choice.PresetLetter)
                                   ?? DlssService.PresetOptions.First(o => o.Value == 11);
        OverrideCombo.SelectedItem = DlssService.OverrideOptions.FirstOrDefault(o => o.Value == _choice.DllOverride)
                                     ?? DlssService.OverrideOptions.First();

        _loading = false;

        HighlightCards();
        UpdatePresetHint();
    }

    private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (PresetCombo.SelectedItem is not DlssOption option) return;

        _choice.PresetLetter = option.Value;
        _choice.PresetMode = "自定义";
        HighlightCards();
        UpdatePresetHint();
    }

    private void OnOverrideChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (OverrideCombo.SelectedItem is not DlssOption option) return;

        _choice.DllOverride = option.Value;
    }

    private void UpdatePresetHint()
    {
        var option = DlssService.PresetOptions.FirstOrDefault(o => o.Value == _choice.PresetLetter);
        PresetHint.Text = option?.Hint ?? "";

        if (_choice.PresetLetter == 11 && _env.DlssFound && !DlssService.PresetSupportHint(_env.DlssVersion).Contains("支持"))
        {
            PresetHint.Text = "预设 K 需要 310.x 以上的 DLSS，当前版本偏低。";
        }
    }

    // --------------------------------------------------------------- 操作

    private async void OnPickGame(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择游戏主程序（DeltaForceClient-Win64-Shipping.exe 或游戏目录所在 exe）",
            Filter = "可执行文件|*.exe|所有文件|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true) return;

        var dir = Path.GetDirectoryName(dialog.FileName) ?? "";

        // 上溯到包含 DeltaForce 的那一层，保证能扫到 Plugins\Runtime\Nvidia
        for (var i = 0; i < 4; i++)
        {
            var parent = Path.GetDirectoryName(dir);
            if (parent == null) break;
            if (Directory.GetDirectories(parent).Any(d =>
                    Path.GetFileName(d).Equals("DeltaForce", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(d).Equals("Plugins", StringComparison.OrdinalIgnoreCase)))
                break;
            dir = parent;
        }

        await DetectAsync(dir);
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await DetectAsync(_env.GameDir);

    private void OnSave(object sender, RoutedEventArgs e)
    {
        // 以 ComboBox 的最新选择为准
        if (PresetCombo.SelectedItem is DlssOption preset) _choice.PresetLetter = preset.Value;
        if (OverrideCombo.SelectedItem is DlssOption ov) _choice.DllOverride = ov.Value;

        var (ok, message, path) = DlssService.Save(_env, _choice);

        StatusText.Text = ok ? "已生成：" + Path.GetFileName(path) : message;

        if (ok)
        {
            MessageBox.Show(this,
                $"{message}\n\n文件：\n{path}\n\n" +
                "怎么用：\n" +
                "1. 打开 NVIDIA Profile Inspector（nvidiaProfileInspector.exe）\n" +
                "2. 顶上 File → Import profile(s)，选中这个 .nip\n" +
                "3. 点 Apply changes 写入驱动\n\n" +
                $"当前选择：{DlssService.QualityName(_choice.QualityLevel)} / {DlssService.PresetName(_choice.PresetLetter)}\n" +
                "想还原就在 NVIDIA 控制面板里把该游戏的设置恢复默认。",
                "已生成导入文件", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show(this, message, "系统诊断台", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        DlssService.ClearChoice();

        _loading = true;
        _choice = new DlssChoice { PresetMode = "均衡", PresetLetter = 11, QualityLevel = 1, DllOverride = 1 };
        PresetCombo.SelectedItem = DlssService.PresetOptions.First(o => o.Value == 11);
        OverrideCombo.SelectedItem = DlssService.OverrideOptions.First(o => o.Value == 1);
        _loading = false;

        HighlightCards();
        StatusText.Text = "已恢复到「均衡」默认选择（驱动里的设置不受影响，需要在 NVIDIA 控制面板还原）。";
    }

    private void OnOpenNipDir(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(DlssService.NipDir);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + DlssService.NipDir + "\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "打不开目录", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
