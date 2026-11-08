using System.Windows;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>
/// 服务的「恢复」选项卡：服务异常退出后要不要自动重启、隔多久。
///
/// services.msc 里这项在一个多页属性对话框的第三页，实际用到的只有几种固定组合，
/// 所以这里做成三个预设而不是「自定义每一段动作」——少一层界面，也不会配出
/// 「重启自己 100 次」这种把机器拖死的组合。
/// </summary>
public partial class ServiceRecoveryWindow : Window
{
    private readonly ServiceEntry _entry;

    /// <summary>自检读取：应用结果消息。</summary>
    public string ApplyMessage { get; private set; } = "";

    public ServiceRecoveryWindow(ServiceEntry entry)
    {
        InitializeComponent();

        WindowChromeBehavior.ApplyTheme(this);

        _entry = entry;
        ServiceTitle.Text = $"{entry.DisplayName}　·　{entry.Name}";

        var (configured, summary) = ServiceInventoryService.ReadFailureActions(entry.Name);
        CurrentText.Text = $"当前恢复策略：{summary}";

        // 已经配过的默认落在「慢速重启」上，未配过的落在「不自动重启」
        SlowRadio.IsChecked = configured;
        NoneRadio.IsChecked = !configured;

        HintText.Text = entry.StartRaw == 4
            ? "这个服务当前是「禁用」状态：即使设了失败重启，它也不会自己启动——恢复动作只在服务已经在运行时才有意义。"
            : "提示：改了恢复策略不影响当前运行状态，下次异常退出时才生效。";
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        var preset = FastRadio.IsChecked == true ? 2 : SlowRadio.IsChecked == true ? 1 : 0;

        var (ok, message) = ServiceInventoryService.SetFailureActions(_entry.Name, preset);

        if (!ok)
        {
            UiDialog.Show(this, message, "设置恢复策略失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ApplyMessage = message;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
