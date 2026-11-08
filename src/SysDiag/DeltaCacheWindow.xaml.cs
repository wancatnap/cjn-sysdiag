using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>
/// 游戏优化：缓存清理（游戏 + 着色器）、VC++ 运行库检查、图形设置、一键优化与回滚。
/// </summary>
public partial class DeltaCacheWindow : Window
{
    private readonly ObservableCollection<DeltaCacheItem> _items = new();
    private readonly ObservableCollection<ShaderCacheItem> _shaderItems = new();
    private readonly List<VcRuntimeInfo> _vc = new();

    private string _gameRoot = "";
    private string _exePath = "";
    private bool _busy;

    public int ItemCount => _items.Count;
    public int ShaderCount => _shaderItems.Count;
    public bool VcAllInstalled => _vc.Count > 0 && _vc.All(v => v.Installed);
    public bool GpuHighPerformance => _gpuOn;
    public bool FullscreenDisabled => _fsOn;

    private bool _gpuOn;
    private bool _fsOn;

    /// <param name="startOnTune">直接落在「全自动调优」页（--open-delta 用，方便一条命令打开到调优）。</param>
    public DeltaCacheWindow(bool startOnTune = false)
    {
        InitializeComponent();

        CacheList.ItemsSource = _items;
        ShaderList.ItemsSource = _shaderItems;

        BuildSteps();

        if (startOnTune) Tabs.SelectedIndex = 2;

        Loaded += async (_, _) => await ScanAsync();
    }

    // --------------------------------------------------------------- 扫描

    public async Task ScanAsync()
    {
        if (_busy) return;
        _busy = true;
        OptBtn.IsEnabled = false;
        GameCacheEmpty.Text = "正在全盘查找游戏目录…";

        try
        {
            var progress = new Progress<string>(s => StatusText.Text = s);

            var result = await Task.Run(() =>
            {
                var caches = DeltaCacheService.Scan(progress);
                var shaders = GameTweakService.ScanShaderCaches();

                var root = "";
                if (caches.Count > 0)
                {
                    var saved = caches[0].SavedRoot;
                    root = Path.GetDirectoryName(saved.TrimEnd('\\')) ?? "";
                }

                if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                {
                    var logDirs = GameLogFinder.FindAll(null);
                    var saved = logDirs
                        .Where(d => d.Path.Contains("Delta", StringComparison.OrdinalIgnoreCase)
                                    || d.Game.Contains("Delta", StringComparison.OrdinalIgnoreCase))
                        .Select(d => Path.GetDirectoryName(d.Path.TrimEnd('\\')))
                        .Where(p => !string.IsNullOrEmpty(p))
                        .Select(p => Path.GetDirectoryName(p!.TrimEnd('\\')))
                        .FirstOrDefault(p => !string.IsNullOrEmpty(p) && Directory.Exists(p));
                    root = saved ?? "";
                }

                var exe = GameTweakService.FindGameExe(root);
                return (caches, shaders, root, exe);
            });

            _items.Clear();
            foreach (var item in result.caches) _items.Add(item);

            _shaderItems.Clear();
            foreach (var item in result.shaders) _shaderItems.Add(item);

            _gameRoot = result.root;
            _exePath = result.exe;

            RenderPaths();
            RenderCaches();
            RenderGraphics();
        }
        catch (Exception ex)
        {
            StatusText.Text = "扫描失败：" + ex.Message;
            GameCacheEmpty.Text = "扫描失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            OptBtn.IsEnabled = true;
            UpdateSummary();
        }
    }

    private void RenderPaths()
    {
        if (string.IsNullOrEmpty(_exePath))
        {
            ExeText.Text = "未找到游戏主程序，点右侧「选择游戏 exe」手动指定。";
            ExeText.Foreground = Palette.Danger;
            GameRootText.Text = string.IsNullOrEmpty(_gameRoot) ? "" : "游戏目录：" + _gameRoot;
            return;
        }

        ExeText.Text = _exePath;
        ExeText.Foreground = Palette.InkSecondary;
        GameRootText.Text = "游戏目录：" + _gameRoot;
    }

    private void RenderCaches()
    {
        var gameSize = _items.Sum(i => i.Size);
        GameCacheSize.Text = $"{gameSize / 1024.0 / 1024.0:0.0} MB";

        GameCacheEmpty.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_items.Count == 0)
            GameCacheEmpty.Text = "没有找到可清理的游戏缓存（可能未安装，或已经清理过）。";

        var shaderSize = _shaderItems.Sum(i => i.Size);
        ShaderCacheSize.Text = $"{shaderSize / 1024.0 / 1024.0:0.0} MB";

        ShaderCacheEmpty.Visibility = _shaderItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_shaderItems.Count == 0)
            ShaderCacheEmpty.Text = "这台机器上没有找到着色器缓存目录。";

        StatusText.Text = $"游戏缓存 {_items.Count} 项 / {gameSize / 1024.0 / 1024.0:0.0} MB" +
                          $"　着色器 {_shaderItems.Count} 项 / {shaderSize / 1024.0 / 1024.0:0.0} MB";
    }

    private void RenderGraphics()
    {
        var (gpuOn, gpuRaw) = GameTweakService.GetGpuPreference(_exePath);
        _gpuOn = gpuOn;

        GpuBadge.Background = (gpuOn ? Palette.OkSoft : Palette.SurfaceRaise);
        GpuBadgeText.Foreground = (gpuOn ? Palette.Ok : Palette.InkTertiary);
        GpuBadgeText.Text = gpuOn ? "已设为高性能" : "未设置";
        GpuBtn.Content = gpuOn ? "取消设置" : "设为高性能";
        GpuRaw.Text = string.IsNullOrEmpty(gpuRaw) ? "" : "当前值：" + gpuRaw;

        var (fsOn, compatRaw) = GameTweakService.GetFullscreenOptimization(_exePath);
        _fsOn = fsOn;

        FsBadge.Background = (fsOn ? Palette.OkSoft : Palette.SurfaceRaise);
        FsBadgeText.Foreground = (fsOn ? Palette.Ok : Palette.InkTertiary);
        FsBadgeText.Text = fsOn ? "已勾选" : "未勾选";
        FsBtn.Content = fsOn ? "取消勾选" : "禁用全屏优化";
        CompatRaw.Text = string.IsNullOrEmpty(compatRaw) ? "" : "当前值：" + compatRaw;

        var applied = gpuOn && fsOn;
        OptBadge.Background = (applied ? Palette.OkSoft : Palette.SurfaceRaise);
        OptBadgeText.Foreground = (applied ? Palette.Ok : Palette.InkTertiary);
        OptBadgeText.Text = applied ? "已优化" : "未优化";

        BackupTimeText.Text = GameOptimizeService.BackupTimeText;
    }

    private void UpdateSummary()
    {
        var selected = _items.Where(i => i.Selected).ToList();
        if (selected.Count > 0)
            StatusText.Text = $"已选游戏缓存 {selected.Count} 项 / {selected.Sum(i => i.Size) / 1024.0 / 1024.0:0.0} MB";

        if (ToggleBtn != null)
        {
            var allChecked = _items.Count > 0 && _items.All(i => i.Selected);
            ToggleBtn.Content = allChecked ? "全不选" : "全选";
        }
    }

    // --------------------------------------------------------------- 缓存操作

    /// <summary>全选 / 全不选合成一个按钮：已全选就全部取消，否则全部勾上。</summary>
    private void OnToggleAll(object sender, RoutedEventArgs e)
    {
        var allChecked = _items.Count > 0 && _items.All(i => i.Selected);

        foreach (var i in _items) i.Selected = !allChecked;

        CacheList.Items.Refresh();
        UpdateSummary();
    }

    private void OnSelectAllShader(object sender, RoutedEventArgs e)
    {
        foreach (var i in _shaderItems) i.Selected = true;
        ShaderList.Items.Refresh();
        StatusText.Text = $"已勾选 {_shaderItems.Count} 项着色器缓存";
    }

    private async void OnClearShader(object sender, RoutedEventArgs e)
    {
        var selected = _shaderItems.Where(i => i.Selected).ToList();
        if (selected.Count == 0)
        {
            UiDialog.Show(this, "先勾选要清理的着色器缓存。", "游戏优化",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var ask = UiDialog.Show(this,
            $"将清理 {selected.Count} 项着色器缓存，共 {selected.Sum(i => i.Size) / 1024.0 / 1024.0:0.0} MB：\n\n" +
            string.Join("\n", selected.Select(i => $"· {i.Vendor} {i.Name}")) +
            "\n\n文件移入回收站。清理后首次进游戏会重新编译着色器，期间会掉帧。确定继续？",
            "确认清理", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ask != MessageBoxResult.OK) return;

        _busy = true;
        var ok = 0;
        var failed = new List<string>();

        foreach (var item in selected)
        {
            var (success, message) = await Task.Run(() => GameTweakService.ClearShaderCache(item));
            if (success) ok++;
            else failed.Add($"{item.Vendor} {item.Name}：{message}");
        }

        _busy = false;

        UiDialog.Show(this,
            failed.Count == 0
                ? $"已清理 {ok} 项着色器缓存。"
                : $"完成 {ok} 项，失败 {failed.Count} 项：\n\n" + string.Join("\n", failed.Take(5)),
            "清理结果", MessageBoxButton.OK,
            failed.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);

        await ScanAsync();
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        var selected = _items.Where(i => i.Selected).ToList();
        if (selected.Count == 0)
        {
            UiDialog.Show(this, "先勾选要清理的游戏缓存。", "游戏优化",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var ask = UiDialog.Show(this,
            $"将清理 {selected.Count} 项缓存，共 {selected.Sum(i => i.Size) / 1024.0 / 1024.0:0.0} MB：\n\n" +
            string.Join("\n", selected.Select(i => "· " + i.Name)) +
            "\n\n文件会移入回收站。游戏需要时会重新生成，首次进入地图可能稍慢。确定继续？",
            "确认清理", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ask != MessageBoxResult.OK) return;

        _busy = true;
        var ok = 0;
        var failed = new List<string>();

        foreach (var item in selected)
        {
            var (success, message) = await Task.Run(() => DeltaCacheService.Delete(item));
            if (success) ok++;
            else failed.Add(message);
        }

        _busy = false;
        StatusText.Text = failed.Count == 0 ? $"已清理 {ok} 项" : $"完成 {ok} 项，失败 {failed.Count} 项";

        if (failed.Count > 0)
            UiDialog.Show(this, string.Join("\n\n", failed.Take(5)), "清理结果",
                MessageBoxButton.OK, MessageBoxImage.Warning);

        await ScanAsync();
    }

    // --------------------------------------------------------------- 运行库

    private async void OnCheckVc(object sender, RoutedEventArgs e)
    {
        _vc.Clear();
        _vc.AddRange(await Task.Run(() => GameTweakService.CheckVcRuntimes()));

        var allInstalled = _vc.All(v => v.Installed);

        VcBadge.Background = (allInstalled ? Palette.OkSoft : Palette.DangerSoft);
        VcBadgeText.Foreground = (allInstalled ? Palette.Ok : Palette.Danger);
        VcBadgeText.Text = allInstalled ? "齐备" : "缺失";

        VcDetail.Text = string.Join("　", _vc.Select(v => v.Detail)) +
                        (allInstalled ? "" : "　·　缺的版本需要自己装，本工具不自动下载。");
    }

    // --------------------------------------------------------------- 图形设置

    private void OnToggleGpu(object sender, RoutedEventArgs e)
    {
        if (!RequireExe()) return;

        var (ok, message) = GameTweakService.SetGpuPreference(_exePath, !_gpuOn);
        StatusText.Text = message;

        if (!ok)
            UiDialog.Show(this, message, "游戏优化", MessageBoxButton.OK, MessageBoxImage.Warning);

        RenderGraphics();
    }

    private void OnToggleFullscreen(object sender, RoutedEventArgs e)
    {
        if (!RequireExe()) return;

        var (ok, message) = GameTweakService.SetFullscreenOptimization(_exePath, !_fsOn);
        StatusText.Text = message;

        if (!ok)
            UiDialog.Show(this, message, "游戏优化", MessageBoxButton.OK, MessageBoxImage.Warning);

        RenderGraphics();
    }

    private bool RequireExe()
    {
        if (!string.IsNullOrEmpty(_exePath)) return true;

        UiDialog.Show(this,
            "还没定位到游戏主程序。\n\n点顶部「选择游戏 exe」，选中 DeltaForceClient-Win64-Shipping.exe。",
            "游戏优化", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    // --------------------------------------------------------------- 全自动调优

    private readonly List<(DeltaAutoTune.Step Step, CheckBox Box)> _stepRows = new();

    /// <summary>自检用：流水线里渲染出来的步骤数。</summary>
    public int StepCount => _stepRows.Count;

    private void BuildSteps()
    {
        StepHost.Children.Clear();
        _stepRows.Clear();

        foreach (var step in DeltaAutoTune.Plan())
        {
            var box = new CheckBox
            {
                IsChecked = step.DefaultOn,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 12, 0)
            };

            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock
            {
                Text = step.Title,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Palette.Ink,
                VerticalAlignment = VerticalAlignment.Center
            });

            head.Children.Add(Chip(step.Risk,
                step.Risk == "安全" ? Palette.Ok : Palette.Warn,
                step.Risk == "安全" ? Palette.OkSoft : Palette.WarnSoft));

            if (step.SystemWide) head.Children.Add(Chip("系统级", Palette.InkTertiary, Palette.SurfaceRaise));
            if (step.NeedsRunning) head.Children.Add(Chip("需游戏在运行", Palette.Hint, Palette.SurfaceRaise));
            if (!step.DefaultOn) head.Children.Add(Chip("按需", Palette.InkTertiary, Palette.SurfaceRaise));

            var body = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };

            body.Children.Add(new TextBlock
            {
                Text = step.Desc,
                FontSize = 10.5,
                Foreground = Palette.InkSecondary,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 17
            });

            if (step.Effect.Length > 0)
            {
                body.Children.Add(new TextBlock
                {
                    Text = "代价：" + step.Effect,
                    FontSize = 10,
                    Foreground = Palette.Warn,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 16,
                    Margin = new Thickness(0, 4, 0, 0)
                });
            }

            var text = new StackPanel();
            text.Children.Add(head);
            text.Children.Add(body);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Grid.SetColumn(box, 0);
            Grid.SetColumn(text, 1);

            grid.Children.Add(box);
            grid.Children.Add(text);

            StepHost.Children.Add(new Border
            {
                BorderBrush = Palette.Line,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 11, 0, 11),
                Child = grid
            });

            _stepRows.Add((step, box));
        }

        ResetStepScroll();
    }

    /// <summary>
    /// 把步骤清单拉回顶部。
    ///
    /// 只设一次不够：窗口显示 → 测量 → 布局 → 焦点，每一步都可能把 VerticalOffset 改回去，
    /// 结果就是「前两步不见了、从第三步开始显示」。这里分四拍归零：
    /// Loaded、Background、再加一个 250ms 的兜底，覆盖首帧之后的重新布局。
    /// </summary>
    private void ResetStepScroll()
    {
        void Zero() => StepScroll.ScrollToVerticalOffset(0);

        Dispatcher.BeginInvoke(new Action(() =>
        {
            Zero();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                Zero();

                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(250)
                };

                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    Zero();
                };

                timer.Start();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
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

    /// <summary>
    /// TabItem 的内容在「被选中」之前不在可视化树里，ScrollViewer 的 ViewportHeight 一直是 0，
    /// 所以构造函数里那次归零是空操作。真正可靠的时机是这里：选中后再归零。
    /// </summary>
    private void OnTabChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Tabs)) return;
        if (Tabs.SelectedIndex != 2) return;

        ResetStepScroll();
    }

    private void OnStepSelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var (_, box) in _stepRows) box.IsChecked = true;
    }

    private void OnStepSelectSafe(object sender, RoutedEventArgs e)
    {
        foreach (var (step, box) in _stepRows) box.IsChecked = step.Risk == "安全";
    }

    private async void OnRunAutoTune(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var chosen = _stepRows.Where(x => x.Box.IsChecked == true).Select(x => x.Step).ToList();

        if (chosen.Count == 0)
        {
            UiDialog.Show(this, "至少勾选一项再开始。默认已经勾好了推荐的那几项。",
                "全自动调优", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var missingGame = chosen.Any(x => x.NeedsGame) && string.IsNullOrWhiteSpace(_exePath);

        var ask = "将依次执行 " + chosen.Count + " 项：\n\n" +
                  string.Join("\n", chosen.Select((x, i) => $"{i + 1}. {x.Title}"));

        if (missingGame)
            ask += "\n\n注意：还没选定游戏 exe，需要游戏的那几步会被跳过。";

        ask += "\n\n建议先完全退出游戏。系统级改动写在 HKLM，普通权限会被拒绝（会逐项报出来，不会中断）。确定继续？";

        if (UiDialog.Show(this, ask, "全自动调优", MessageBoxButton.OKCancel, MessageBoxImage.Question)
            != MessageBoxResult.OK)
        {
            return;
        }

        _busy = true;
        OptBtn.IsEnabled = false;
        LogBox.Text = "";

        AppendLog("========== 全自动调优开始 ==========");
        AppendLog(string.IsNullOrWhiteSpace(_exePath) ? "游戏：未选定" : "游戏：" + _exePath);
        AppendLog(TweakService.IsAdmin ? "权限：管理员" : "权限：普通（系统级步骤会被拒绝，会逐项报出来）");

        try
        {
            var progress = new Progress<string>(AppendLog);
            var results = await DeltaAutoTune.RunAsync(_exePath, chosen.Select(x => x.Id), progress);

            var skipped = results.Count(r => r.Skipped);
            var counted = results.Count - skipped;
            var ok = results.Count(r => r.Ok);

            AppendLog($"========== 结束：成功 {ok} / {counted}" + (skipped > 0 ? $"，跳过 {skipped}" : "") + " ==========");
            AppendLog("GPU 调度与 CPU 档位要重启系统才生效；游戏级的项要完全退出游戏再启动。");
            AppendLog("想还原点「一键回滚」。");

            StatusText.Text = $"全自动调优：{ok}/{counted} 项成功" + (skipped > 0 ? $"，{skipped} 项跳过" : "");

            // 日志在页面底部，跑完主动滚过去，否则用户看不到结果
            LogCard.BringIntoView();
        }
        catch (Exception ex)
        {
            AppendLog("出错：" + ex.Message);
        }
        finally
        {
            _busy = false;
            OptBtn.IsEnabled = true;
            RenderGraphics();
        }
    }

    private void OnRollback(object sender, RoutedEventArgs e)
    {
        var ask = UiDialog.Show(this,
            "按备份把改动整批恢复？\n\n" +
            "· 游戏级：GPU 首选项、全屏优化标记、鼠标设置\n" +
            "· 系统调优：Game Bar、GPU 调度、系统服务、CPU 档位\n" +
            "· 系统优化：小组件/更新/休眠等条目\n\n" +
            "（只还原有备份记录的那些，没改过的不会被动）",
            "一键回滚", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (ask != MessageBoxResult.OK) return;

        var (ok, message) = DeltaAutoTune.Rollback();

        AppendLog((ok ? "回滚：" : "回滚（部分失败）：") + message.Replace("\n", "\n           "));
        StatusText.Text = ok ? "已按备份回滚" : "回滚时有问题，详见日志";

        if (!ok)
            UiDialog.Show(this, message, "游戏优化", MessageBoxButton.OK, MessageBoxImage.Warning);

        RenderGraphics();
    }

    private void OnManualBackup(object sender, RoutedEventArgs e)
    {
        if (!RequireExe()) return;

        var (ok, message) = GameOptimizeService.CreateBackup(_exePath);
        AppendLog((ok ? "备份：" : "备份失败：") + message);
        StatusText.Text = message;
        BackupTimeText.Text = GameOptimizeService.BackupTimeText;
    }

    private void AppendLog(string line)
    {
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        LogBox.AppendText($"\n[{stamp}] {line}");
        LogBox.ScrollToEnd();
    }

    private void OnClearLog(object sender, RoutedEventArgs e) => LogBox.Text = "";

    // --------------------------------------------------------------- 其他

    private async void OnPickExe(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择游戏主程序（任一 xxxx-Win64-Shipping.exe 或游戏本体）",
            Filter = "可执行文件|*.exe|所有文件|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true) return;

        _exePath = dialog.FileName;

        // 上溯到 Binaries 之上两层作为游戏根
        var dir = Path.GetDirectoryName(_exePath) ?? "";
        for (var i = 0; i < 3; i++)
        {
            var parent = Path.GetDirectoryName(dir);
            if (parent == null) break;
            if (Directory.GetDirectories(parent).Any(d =>
                    Path.GetFileName(d).Equals("Saved", StringComparison.OrdinalIgnoreCase)))
                break;
            dir = parent;
        }

        _gameRoot = dir;
        RenderPaths();
        RenderGraphics();
        StatusText.Text = "已切换游戏路径。";

        await Task.CompletedTask;
    }

    private async void OnRescan(object sender, RoutedEventArgs e) => await ScanAsync();

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
