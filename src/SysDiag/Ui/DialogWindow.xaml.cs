using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SysDiag.Models;

namespace SysDiag.Ui;

/// <summary>
/// 自绘对话框：替代 Windows 默认 MessageBox，外观与主界面一致。
/// 图标、按钮组合（确定 / 确定·取消 / 是·否 / 是·否·取消）与 MessageBoxImage 语义对应。
/// </summary>
public partial class DialogWindow : Window
{
    private MessageBoxResult _result = MessageBoxResult.Cancel;

    public DialogWindow()
    {
        InitializeComponent();
    }

    public MessageBoxResult Result => _result;

    /// <summary>按 MessageBox 语义配置内容与按钮。</summary>
    public void Configure(string message, string caption, MessageBoxButton button, MessageBoxImage icon)
    {
        CaptionText.Text = string.IsNullOrWhiteSpace(caption) ? "破烂优化" : caption;
        MessageText.Text = message ?? "";

        ApplyIcon(icon);
        BuildButtons(button);

        PreviewKeyDown += (s, e) =>
        {
            if (e.Key == Key.Escape)
            {
                _result = button switch
                {
                    MessageBoxButton.OK => MessageBoxResult.OK,
                    MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
                    MessageBoxButton.YesNo => MessageBoxResult.No,
                    _ => MessageBoxResult.Cancel
                };
                DialogResult = false;
                e.Handled = true;
            }
        };
    }

    private void ApplyIcon(MessageBoxImage icon)
    {
        var (glyph, fg, bg, border) = icon switch
        {
            MessageBoxImage.Error => ("!", Palette.WarnHex, Palette.DangerSoftHex, Palette.DangerHex),
            MessageBoxImage.Warning => ("!", Palette.HintHex, Palette.WarnSoftHex, Palette.HintHex),
            MessageBoxImage.Question => ("?", Palette.KnownHex, Palette.KnownHex, Palette.KnownHex),
            MessageBoxImage.Information => ("i", Palette.PrimaryHex, Palette.PrimarySoftHex, Palette.PrimaryHex),
            _ => ("i", Palette.PrimaryHex, Palette.PrimarySoftHex, Palette.PrimaryHex)
        };

        IconGlyph.Text = glyph;
        IconGlyph.Foreground = Palette.Get(fg);
        IconBox.Background = Palette.Get(bg);
        IconBox.BorderBrush = Palette.Get(border);

        if (icon is MessageBoxImage.Question or MessageBoxImage.Information)
        {
            // Question/Known 用浅底，避免整块紫得刺眼
            IconBox.Background = Palette.SurfaceRaise;
        }
    }

    private void BuildButtons(MessageBoxButton button)
    {
        ButtonRow.Children.Clear();

        void Add(string text, MessageBoxResult result, bool primary)
        {
            var b = new Button
            {
                Content = text,
                Margin = new Thickness(8, 0, 0, 0),
                MinWidth = 76,
                Style = (Style)FindResource(primary ? "PrimaryButton" : "ToolButton")
            };
            b.Click += (_, _) =>
            {
                _result = result;
                DialogResult = result is MessageBoxResult.OK or MessageBoxResult.Yes;
            };
            ButtonRow.Children.Add(b);
        }

        switch (button)
        {
            case MessageBoxButton.OKCancel:
                Add("取消", MessageBoxResult.Cancel, false);
                Add("确定", MessageBoxResult.OK, true);
                break;

            case MessageBoxButton.YesNo:
                Add("否", MessageBoxResult.No, false);
                Add("是", MessageBoxResult.Yes, true);
                break;

            case MessageBoxButton.YesNoCancel:
                Add("取消", MessageBoxResult.Cancel, false);
                Add("否", MessageBoxResult.No, false);
                Add("是", MessageBoxResult.Yes, true);
                break;

            default:
                Add("确定", MessageBoxResult.OK, true);
                break;
        }
    }

    private void OnTitleDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { /* 拖动中断无所谓 */ }
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        _result = MessageBoxResult.Cancel;
        DialogResult = false;
    }
}
