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

        Title = $"{TitleText.Text} — 系统诊断台";

        if (!string.IsNullOrEmpty(payload.PlainSummary))
            CopyBtn.Visibility = Visibility.Visible;

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
            MessageBox.Show(this, "复制失败：" + ex.Message, "系统诊断台",
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
            MessageBox.Show(this, ex.Message, "系统诊断台", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
        => LauncherService.RevealInExplorer(_payload.HtmlPath);

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
