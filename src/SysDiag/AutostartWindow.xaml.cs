using System;
using System.Windows;
using System.Windows.Media;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>开机自启动设置。两种方式的状态、开启、关闭都在这一页。</summary>
public partial class AutostartWindow : Window
{
    /// <summary>自检用：当前读到的模式。</summary>
    public AutostartService.Mode Mode { get; private set; }

    public AutostartWindow()
    {
        InitializeComponent();

        Reload();
    }

    public void Reload()
    {
        var status = AutostartService.Read();
        Mode = status.Mode;

        StateBadge.Background = status.Enabled ? Palette.OkSoft : Palette.SurfaceRaise;
        StateBadge.BorderBrush = status.Enabled ? Palette.Ok : Palette.Line;
        StateBadge.BorderThickness = new Thickness(1);

        StateText.Text = status.ModeText;
        StateText.Foreground = status.Enabled ? Palette.Ok : Palette.InkTertiary;

        DetailText.Text = status.Detail;

        DisableBtn.IsEnabled = status.Enabled;

        var admin = ElevationService.IsAdmin;
        AdminBtn.Content = admin ? "用这种方式开启" : "需要先提权";

        // 路径对不上（程序被挪走了）要明确提示，否则用户会以为「开了但没生效」
        if (status.Enabled && !status.PathMatches)
        {
            DetailText.Text = "⚠ 已设置的自启动路径与当前程序位置不一致（程序可能被移动过）。建议先关闭再重新开启。\n" + status.Detail;
            DetailText.Foreground = Palette.Warn;
        }
        else
        {
            DetailText.Foreground = Palette.InkFaint;
        }

        PathText.Text = "当前程序路径：\n" + status.ExePath +
                        "\n\n注册表值：" + $@"HKCU\{@"Software\Microsoft\Windows\CurrentVersion\Run"}\{AutostartService.RunValueName}" +
                        $"\n计划任务名：{AutostartService.TaskName}" +
                        $"\n启动参数：{AutostartService.AutostartArg}";

        Bar.SetElapsed(AutostartService.IsPortableExe
            ? $"可自启动　·　{(admin ? "管理员" : "普通权限")}"
            : "调试宿主进程，自启动不可用");
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Reload();

    private void OnEnableUser(object sender, RoutedEventArgs e)
    {
        var (ok, message) = AutostartService.EnableUser();

        UiDialog.Show(this, message, ok ? "已开启" : "没有开启",
            MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);

        Reload();
    }

    private void OnEnableAdmin(object sender, RoutedEventArgs e)
    {
        if (!ElevationService.IsAdmin)
        {
            var answer = UiDialog.Show(this,
                "用计划任务方式需要管理员权限。\n\n现在就以管理员身份重启本程序吗？重启后会自动回到这一页。",
                "需要管理员权限", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer == MessageBoxResult.Yes)
            {
                if (ElevationService.RelaunchAsAdmin("--open-autostart", out var error) &&
                    string.IsNullOrEmpty(error))
                {
                    Close();
                    return;
                }

                if (!string.IsNullOrEmpty(error)) Bar.SetElapsed(error);
            }

            return;
        }

        var confirm = UiDialog.Show(this,
            $"将创建计划任务「{AutostartService.TaskName}」：\n\n" +
            $"· 触发：用户登录时\n· 运行：\"{AutostartService.ExePath}\" {AutostartService.AutostartArg}\n· 权限：最高\n\n" +
            "创建后登录即自动以管理员身份启动，不再弹 UAC。继续吗？",
            "创建开机自启动计划任务", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK) return;

        var (ok, message) = AutostartService.EnableAdmin();

        UiDialog.Show(this, message, ok ? "已开启" : "没有开启",
            MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);

        Reload();
    }

    private void OnDisable(object sender, RoutedEventArgs e)
    {
        var confirm = UiDialog.Show(this,
            "将清除本程序的所有开机自启动设置（Run 键与计划任务）。继续吗？",
            "关闭开机自启动", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK) return;

        var (ok, message) = AutostartService.Disable();

        UiDialog.Show(this, message, ok ? "已关闭" : "没有关闭",
            MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);

        Reload();
    }
}
