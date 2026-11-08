using System.Windows;
using System.Windows.Input;

namespace SysDiag.Services;

/// <summary>
/// 自绘标题栏需要的命令绑定（最小化 / 最大化 / 还原 / 关闭）。
/// 由 Theme.xaml 的 Window 隐式样式通过附加属性启用，各窗口不用改一行代码。
/// </summary>
public static class WindowChromeBehavior
{
    /// <summary>
    /// 给窗口套上自绘标题栏样式。
    /// 必须在创建窗口句柄前调用（WindowStyle 建句柄后就改不动了），且只套一次。
    /// </summary>
    public static void ApplyTheme(Window window)
    {
        try
        {
            if (window == null || window.Style != null) return;
            if (Application.Current?.TryFindResource("ThemedWindow") is Style style)
                window.Style = style;
        }
        catch { /* 主题未加载时保持系统标题栏 */ }
    }

    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled", typeof(bool), typeof(WindowChromeBehavior),
            new PropertyMetadata(false, OnEnabled));

    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);

    private static void OnEnabled(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window window || e.NewValue is not true) return;

        window.CommandBindings.Add(new CommandBinding(
            SystemCommands.MinimizeWindowCommand,
            (_, _) => window.WindowState = WindowState.Minimized));

        window.CommandBindings.Add(new CommandBinding(
            SystemCommands.MaximizeWindowCommand,
            (_, _) => window.WindowState = WindowState.Maximized));

        window.CommandBindings.Add(new CommandBinding(
            SystemCommands.RestoreWindowCommand,
            (_, _) => window.WindowState = WindowState.Normal));

        window.CommandBindings.Add(new CommandBinding(
            SystemCommands.CloseWindowCommand,
            (_, _) => window.Close()));
    }
}
