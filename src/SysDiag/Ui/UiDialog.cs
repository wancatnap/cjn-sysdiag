using System.Linq;
using System.Windows;

namespace SysDiag;

/// <summary>
/// MessageBox 的替身：签名与 <see cref="MessageBox.Show(string)"/> 系列一致，
/// 调用点只需把 MessageBox.Show 换成 UiDialog.Show，交互与返回值不变。
/// </summary>
public static class UiDialog
{
    public static MessageBoxResult Show(string messageBoxText) =>
        Show(null, messageBoxText, "", MessageBoxButton.OK, MessageBoxImage.None);

    public static MessageBoxResult Show(string messageBoxText, string caption) =>
        Show(null, messageBoxText, caption, MessageBoxButton.OK, MessageBoxImage.None);

    public static MessageBoxResult Show(Window owner, string messageBoxText) =>
        Show(owner, messageBoxText, "", MessageBoxButton.OK, MessageBoxImage.None);

    public static MessageBoxResult Show(Window owner, string messageBoxText, string caption) =>
        Show(owner, messageBoxText, caption, MessageBoxButton.OK, MessageBoxImage.None);

    public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button) =>
        Show(null, messageBoxText, caption, button, MessageBoxImage.None);

    public static MessageBoxResult Show(Window owner, string messageBoxText, string caption, MessageBoxButton button) =>
        Show(owner, messageBoxText, caption, button, MessageBoxImage.None);

    public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon) =>
        Show(null, messageBoxText, caption, button, icon);

    public static MessageBoxResult Show(Window owner, string messageBoxText, string caption,
        MessageBoxButton button, MessageBoxImage icon)
    {
        var dialog = new SysDiag.Ui.DialogWindow();

        var target = owner ?? Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                     ?? Application.Current?.MainWindow;
        if (target != null) dialog.Owner = target;

        dialog.Configure(messageBoxText, caption, button, icon);

        try
        {
            dialog.ShowDialog();
            return dialog.Result;
        }
        catch
        {
            // 无 UI 线程（自检/服务）时退化为非模态，返回默认结果
            return button switch
            {
                MessageBoxButton.OK => MessageBoxResult.OK,
                MessageBoxButton.OKCancel => MessageBoxResult.OK,
                _ => MessageBoxResult.Yes
            };
        }
    }
}
