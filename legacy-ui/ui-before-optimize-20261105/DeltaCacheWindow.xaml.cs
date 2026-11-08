using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>
/// 三角洲优化：缓存清理（游戏 + 着色器）、VC++ 运行库检查、图形设置、一键优化与回滚。
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

    public DeltaCacheWindow()
    {
        InitializeComponent();

        CacheList.ItemsSource = _items;
        ShaderList.ItemsSource = _shaderItems;

        Loaded += async (_, _) => await ScanAsync();
    }

    // --------------------------------------------------------------- 扫描

    public async Task ScanAsync()
    {
        if (_busy) return;
        _busy = true;
        OptBtn.IsEnabled = false;
        GameCacheEmpty.Text = "正在全盘查找三角洲目录…";

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
            ExeText.Foreground = Palette.Get("#C92A2A");
            GameRootText.Text = string.IsNullOrEmpty(_gameRoot) ? "" : "游戏目录：" + _gameRoot;
            return;
        }

        ExeText.Text = _exePath;
        ExeText.Foreground = Palette.Get("#495057");
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

        GpuBadge.Background = Palette.Get(gpuOn ? "#EBFBEE" : "#F1F3F5");
        GpuBadgeText.Foreground = Palette.Get(gpuOn ? "#2F9E44" : "#868E96");
        GpuBadgeText.Text = gpuOn ? "已设为高性能" : "未设置";
        GpuBtn.Content = gpuOn ? "取消设置" : "设为高性能";
        GpuRaw.Text = string.IsNullOrEmpty(gpuRaw) ? "" : "当前值：" + gpuRaw;

        var (fsOn, compatRaw) = GameTweakService.GetFullscreenOptimization(_exePath);
        _fsOn = fsOn;

        FsBadge.Background = Palette.Get(fsOn ? "#EBFBEE" : "#F1F3F5");
        FsBadgeText.Foreground = Palette.Get(fsOn ? "#2F9E44" : "#868E96");
        FsBadgeText.Text = fsOn ? "已勾选" : "未勾选";
        FsBtn.Content = fsOn ? "取消勾选" : "禁用全屏优化";
        CompatRaw.Text = string.IsNullOrEmpty(compatRaw) ? "" : "当前值：" + compatRaw;

        var applied = gpuOn && fsOn;
        OptBadge.Background = Palette.Get(applied ? "#EBFBEE" : "#F1F3F5");
        OptBadgeText.Foreground = Palette.Get(applied ? "#2F9E44" : "#868E96");
        OptBadgeText.Text = applied ? "已优化" : "未优化";

        BackupTimeText.Text = GameOptimizeService.BackupTimeText;
    }

    private void UpdateSummary()
    {
        var selected = _items.Where(i => i.Selected).ToList();
        if (selected.Count > 0)
            StatusText.Text = $"已选游戏缓存 {selected.Count} 项 / {selected.Sum(i => i.Size) / 1024.0 / 1024.0:0.0} MB";
    }

    // --------------------------------------------------------------- 缓存操作

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var i in _items) i.Selected = true;
        CacheList.Items.Refresh();
        UpdateSummary();
    }

    private void OnClearAll(object sender, RoutedEventArgs e)
    {
        foreach (var i in _items) i.Selected = false;
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
            MessageBox.Show(this, "先勾选要清理的着色器缓存。", "三角洲优化",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var ask = MessageBox.Show(this,
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

        MessageBox.Show(this,
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
            MessageBox.Show(this, "先勾选要清理的游戏缓存。", "三角洲优化",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var ask = MessageBox.Show(this,
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
            MessageBox.Show(this, string.Join("\n\n", failed.Take(5)), "清理结果",
                MessageBoxButton.OK, MessageBoxImage.Warning);

        await ScanAsync();
    }

    // --------------------------------------------------------------- 运行库

    private async void OnCheckVc(object sender, RoutedEventArgs e)
    {
        _vc.Clear();
        _vc.AddRange(await Task.Run(() => GameTweakService.CheckVcRuntimes()));

        var allInstalled = _vc.All(v => v.Installed);

        VcBadge.Background = Palette.Get(allInstalled ? "#EBFBEE" : "#FFF5F5");
        VcBadgeText.Foreground = Palette.Get(allInstalled ? "#2F9E44" : "#C92A2A");
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
            MessageBox.Show(this, message, "三角洲优化", MessageBoxButton.OK, MessageBoxImage.Warning);

        RenderGraphics();
    }

    private void OnToggleFullscreen(object sender, RoutedEventArgs e)
    {
        if (!RequireExe()) return;

        var (ok, message) = GameTweakService.SetFullscreenOptimization(_exePath, !_fsOn);
        StatusText.Text = message;

        if (!ok)
            MessageBox.Show(this, message, "三角洲优化", MessageBoxButton.OK, MessageBoxImage.Warning);

        RenderGraphics();
    }

    private bool RequireExe()
    {
        if (!string.IsNullOrEmpty(_exePath)) return true;

        MessageBox.Show(this,
            "还没定位到游戏主程序。\n\n点顶部「选择游戏 exe」，选中 DeltaForceClient-Win64-Shipping.exe。",
            "三角洲优化", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    // --------------------------------------------------------------- 一键优化

    private async void OnRunOptimize(object sender, RoutedEventArgs e)
    {
        if (!RequireExe()) return;
        if (_busy) return;

        var ask = MessageBox.Show(this,
            "将依次执行：\n\n" +
            "① 备份当前设置\n" +
            "② 三角洲设为高性能 GPU\n" +
            "③ 三角洲禁用全屏优化\n" +
            "④ 关闭鼠标加速（全局）\n\n" +
            "第④项影响整个系统，不只影响这个游戏。执行前会自动备份，可用「一键回滚」恢复。\n\n" +
            "建议先完全退出游戏再执行。确定继续？",
            "一键优化", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ask != MessageBoxResult.OK) return;

        _busy = true;
        OptBtn.IsEnabled = false;
        AppendLog("========== 开始一键优化 ==========");
        AppendLog("目标：" + _exePath);

        try
        {
            var progress = new Progress<string>(AppendLog);
            var results = await GameOptimizeService.RunAsync(_exePath, progress);

            var okCount = results.Count(r => r.Ok);
            AppendLog($"========== 结束：成功 {okCount} / {results.Count} ==========");
            AppendLog("重新启动游戏后生效。想还原点「一键回滚」。");

            StatusText.Text = $"一键优化完成：{okCount}/{results.Count} 项成功";
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
        var ask = MessageBox.Show(this,
            "按备份把 GPU 设置、兼容性设置、鼠标设置恢复成优化前的状态？",
            "一键回滚", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ask != MessageBoxResult.OK) return;

        var (ok, message) = GameOptimizeService.Rollback();
        AppendLog((ok ? "回滚：" : "回滚失败：") + message);
        StatusText.Text = message;

        if (!ok)
            MessageBox.Show(this, message, "三角洲优化", MessageBoxButton.OK, MessageBoxImage.Warning);

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
            Title = "选择三角洲主程序（DeltaForceClient-Win64-Shipping.exe）",
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
