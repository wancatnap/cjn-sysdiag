using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using SysDiag.Models;
using SysDiag.Services;
using System.IO;

namespace SysDiag;

public partial class ReportWindow : Window
{
    private readonly ReportPayload _payload;

    public ReportWindow(ReportPayload payload)
    {
        InitializeComponent();
        _payload = payload;

        TitleText.Text = string.IsNullOrEmpty(payload.Title) ? "报告" : payload.Title;
        SubText.Text = payload.Subtitle ?? "";

        if (!string.IsNullOrEmpty(payload.EngineTag))
        {
            EngineText.Text = payload.EngineTag;
            EnginePill.Visibility = Visibility.Visible;
        }

        Title = $"{TitleText.Text} — 破烂优化";

        if (!string.IsNullOrEmpty(payload.PlainSummary))
            CopyBtn.Visibility = Visibility.Visible;

        // 关闭即释放渲染实例：报告窗口不常驻，避免 WebView2 进程一直占内存
        Closed += (_, _) =>
        {
            try { Web.Dispose(); } catch { /* 已经释放过 */ }
            MemoryTuner.TrimIfIdle();
        };

        Loaded += OnLoaded;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private void OnCopySummary(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_payload.PlainSummary);
            CopyBtn.Content = "已复制";
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "复制失败：" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 导出 .sdreport：zip 里放 report.json + report.html + manifest。
    /// 对方拿到后可以在「报告对比」里导入，也可以直接解压看 HTML。
    /// </summary>
    private void OnExportPackage(object sender, RoutedEventArgs e)
    {
        if (_payload.Data == null)
        {
            UiDialog.Show(this,
                "这份报告没有结构化数据，无法导出报告包。\n\n" +
                "带结构化数据的报告：外挂痕迹体检、日志规则匹配、报告对比。",
                "导出报告包", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出报告包",
            Filter = $"报告包 (*{ReportPackage.Extension})|*{ReportPackage.Extension}",
            FileName = Path.GetFileName(ReportPackage.SuggestPath(_payload)),
            InitialDirectory = AppPaths.OutputDir
        };

        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var path = ReportPackage.Export(_payload, dlg.FileName);

            UiDialog.Show(this,
                $"已导出报告包：\n{path}\n\n" +
                "这个文件可以直接发给别人：解压能看 HTML；\n" +
                "在本软件的「报告对比」里导入，还能和别的报告做差异对比。",
                "导出报告包", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "导出失败：\n\n" + ex.Message, "导出报告包",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_payload.HtmlPath) || !File.Exists(_payload.HtmlPath))
        {
            ShowFallback("报告文件不存在，可能已被清理。");
            return;
        }

        try
        {
            Directory.CreateDirectory(AppPaths.WebViewDir);
            var env = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewDir);
            await Web.EnsureCoreWebView2Async(env);

            Web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Web.CoreWebView2.Settings.IsZoomControlEnabled = true;
            Web.CoreWebView2.Navigate(new Uri(_payload.HtmlPath).AbsoluteUri);

            Loading.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ShowFallback($"内置浏览器初始化失败：{ex.Message}");
        }
    }

    private void ShowFallback(string message)
    {
        Loading.Visibility = Visibility.Collapsed;
        Fallback.Visibility = Visibility.Visible;
        FallbackMsg.Text = message;
    }

    private void OnReload(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Web.CoreWebView2 != null) Web.CoreWebView2.Reload();
            else OnLoaded(null, null);
        }
        catch { }
    }

    private void OnOpenBrowser(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_payload.HtmlPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, ex.Message, "破烂优化", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
        => LauncherService.RevealInExplorer(_payload.HtmlPath);

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
