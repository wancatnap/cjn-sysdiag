using System.Windows;
using System.Windows.Controls;
using SysDiag.Services;

namespace SysDiag;

/// <summary>
/// 进程优先级与 CPU 亲和性：任务管理器「详细信息」里那两个右键菜单项的软件内版本。
///
/// 单独做成对话框，因为这两件事都是「选一个值再应用」：
/// 优先级有 6 档、亲和性有 N 个核心，塞进列表行的右键菜单会变成十几个菜单项。
/// </summary>
public partial class ProcessTuneWindow : Window
{
    private readonly ProcessInventoryService.ProcessEntry _entry;
    private readonly List<CheckBox> _cores = new();

    /// <summary>自检读取：应用后的结果消息（空串表示还没点应用）。</summary>
    public string ApplyMessage { get; private set; } = "";

    public ProcessTuneWindow(ProcessInventoryService.ProcessEntry entry)
    {
        InitializeComponent();

        WindowChromeBehavior.ApplyTheme(this);

        _entry = entry;

        ProcessTitle.Text = $"{entry.Name}.exe　·　PID {entry.Pid}";

        CurrentText.Text =
            $"当前优先级：{entry.PriorityText}　·　当前亲和性：{entry.AffinityText}" +
            (entry.Path.Length > 0 ? $"\n{entry.Path}" : "");

        BuildPriority();
        BuildCores();

        if (entry.Priority.Length == 0 && entry.AffinityMask == 0)
        {
            ApplyBtn.IsEnabled = false;
            CurrentText.Text += "\n\n这个进程的优先级与亲和性都读不到——通常是被反作弊或系统保护，改不了。";
        }
    }

    private void BuildPriority()
    {
        foreach (var option in ProcessInventoryService.PriorityOptions)
        {
            PriorityCombo.Items.Add(new ComboBoxItem
            {
                Content = option.Label,
                Tag = option.Value,
                FontSize = 11.5
            });
        }

        var index = Array.FindIndex(ProcessInventoryService.PriorityOptions,
            o => o.Value == _entry.Priority);

        PriorityCombo.SelectedIndex = index >= 0 ? index : 2;   // 读不到时默认「正常」

        PriorityCombo.SelectionChanged += (_, _) =>
        {
            var current = ProcessInventoryService.PriorityOptions[Math.Max(0, PriorityCombo.SelectedIndex)];
            PriorityNote.Text = current.Note;
        };

        PriorityNote.Text = ProcessInventoryService.PriorityOptions[Math.Max(0, PriorityCombo.SelectedIndex)].Note;
    }

    private void BuildCores()
    {
        var total = Environment.ProcessorCount;
        var mask = _entry.AffinityMask;

        if (mask == 0) mask = (1L << Math.Min(total, 62)) - 1;   // 读不到就默认全选

        for (var i = 0; i < total; i++)
        {
            var box = new CheckBox
            {
                Content = "CPU " + i,
                Tag = i,
                IsChecked = (mask & (1L << i)) != 0,
                FontSize = 10.5,
                Margin = new Thickness(0, 4, 16, 4),
                VerticalContentAlignment = VerticalAlignment.Center
            };

            box.Checked += (_, _) => UpdateToggleText();
            box.Unchecked += (_, _) => UpdateToggleText();

            _cores.Add(box);
            CorePanel.Children.Add(box);
        }

        UpdateToggleText();

        AffinityHint.Text = $"这台机器有 {total} 个逻辑处理器。只勾一个核心等于把这个进程钉死在单核上——" +
                            "排查「某个核心被吃满」时有用，平时会让它更慢。";
    }

    /// <summary>
    /// 全选 / 全不选合成一个按钮：已经全选就全部取消，否则全部勾上。
    /// 按钮文字始终写「点下去会发生什么」，不用记当前状态。
    /// </summary>
    private void OnToggleAll(object sender, RoutedEventArgs e)
    {
        var allChecked = _cores.Count > 0 && _cores.All(b => b.IsChecked == true);

        foreach (var box in _cores) box.IsChecked = !allChecked;

        UpdateToggleText();
    }

    private void UpdateToggleText()
    {
        if (ToggleBtn == null) return;

        var allChecked = _cores.Count > 0 && _cores.All(b => b.IsChecked == true);

        ToggleBtn.Content = allChecked ? "全不选" : "全选";
    }

    private long SelectedMask()
    {
        long mask = 0;

        foreach (var box in _cores)
            if (box.IsChecked == true && box.Tag is int index)
                mask |= 1L << index;

        return mask;
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        var messages = new List<string>();

        // 只在真的改了的时候动手：没改还去设置一次，等于白挨一次「拒绝访问」
        var priority = (PriorityCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Normal";
        if (priority != _entry.Priority)
        {
            var (ok, message) = ProcessInventoryService.SetPriority(_entry, priority);

            if (!ok)
            {
                UiDialog.Show(this, message, "改优先级失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            messages.Add(message);
        }

        var mask = SelectedMask();
        if (mask != _entry.AffinityMask)
        {
            var (ok, message) = ProcessInventoryService.SetAffinity(_entry, mask);

            if (!ok)
            {
                UiDialog.Show(this, message, "改亲和性失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            messages.Add(message);
        }

        ApplyMessage = messages.Count > 0 ? string.Join(" ", messages) : "没有改动。";

        DialogResult = messages.Count > 0;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
