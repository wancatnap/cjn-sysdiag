using System.Windows;
using System.Windows.Controls;

namespace SysDiag.Controls;

/// <summary>
/// 页面标题区：标题 + 可选徽章。
///
/// 刻意**没有副标题**。理由不是省事，是这类工具最容易犯的错：
/// 用一段说明去解释「这个页面是干嘛的」，然后每个页面都变成「标题 + 一段话 + 一堆控件」。
/// 页面本身应该让人一眼看明白；看不懂就该改布局，而不是加一段话。
/// 需要状态的地方用徽章（短词），需要确认的地方放进操作对话框（那时用户才真的要看）。
///
/// 样式来自 Theme.xaml 的 PageTitle / PageBadge，改样式只改主题。
/// </summary>
public partial class PageHeader : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(PageHeader),
        new PropertyMetadata("", (d, _) => ((PageHeader)d).Refresh()));

    public static readonly DependencyProperty BadgeProperty = DependencyProperty.Register(
        nameof(Badge), typeof(string), typeof(PageHeader),
        new PropertyMetadata("", (d, _) => ((PageHeader)d).Refresh()));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Badge
    {
        get => (string)GetValue(BadgeProperty);
        set => SetValue(BadgeProperty, value);
    }

    public PageHeader()
    {
        InitializeComponent();
        Refresh();
    }

    private void Refresh()
    {
        TitleText.Text = Title;
        BadgeText.Text = Badge;
        BadgeBox.Visibility = string.IsNullOrEmpty(Badge) ? Visibility.Collapsed : Visibility.Visible;
    }
}
