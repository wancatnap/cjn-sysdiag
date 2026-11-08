using System.Windows;
using System.Windows.Controls;

namespace SysDiag.Controls;

/// <summary>
/// 底部栏：耗时 + 关闭按钮。
/// 刻意不提供说明文字入口——常驻的「提示」会变成没人读的免责声明，
/// 该解释的信息应挪进布局或操作确认框。
/// </summary>
public partial class BottomBar : UserControl
{
    public static readonly DependencyProperty ElapsedProperty = DependencyProperty.Register(
        nameof(Elapsed), typeof(string), typeof(BottomBar),
        new PropertyMetadata("", (d, _) => ((BottomBar)d).Refresh()));

    public static readonly DependencyProperty ShowCloseProperty = DependencyProperty.Register(
        nameof(ShowClose), typeof(bool), typeof(BottomBar),
        new PropertyMetadata(true, (d, _) => ((BottomBar)d).Refresh()));

    /// <summary>用户点了关闭按钮。窗口自行处理（默认就是 Close）。</summary>
    public event EventHandler? CloseRequested;

    public string Elapsed
    {
        get => (string)GetValue(ElapsedProperty);
        set => SetValue(ElapsedProperty, value);
    }

    public bool ShowClose
    {
        get => (bool)GetValue(ShowCloseProperty);
        set => SetValue(ShowCloseProperty, value);
    }

    public BottomBar()
    {
        InitializeComponent();
        Refresh();
    }

    /// <summary>只改耗时。</summary>
    public void SetElapsed(string text) => Elapsed = text;

    private void Refresh()
    {
        ElapsedText.Text = Elapsed;
        CloseBtn.Visibility = ShowClose ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (CloseRequested != null)
            CloseRequested(this, EventArgs.Empty);
        else
            Window.GetWindow(this)?.Close();
    }
}
