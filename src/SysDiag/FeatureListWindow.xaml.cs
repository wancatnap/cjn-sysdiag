using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>
/// 功能一览：把主界面的卡片按分组摊平成一页清单，每条写「功能名 + 解决什么问题」。
///
/// 用途是公告条那个「查看详情」——用户看到「更新了什么」之后，
/// 通常下一步想问「这软件到底能干什么」。所以这里不写更新日志，只写能力清单。
/// 数据直接从 <see cref="CardCatalog"/> 取，避免另维护一份会过期的文案。
/// </summary>
public partial class FeatureListWindow : Window
{
    private readonly List<CardGroup> _groups;
    private readonly string _noticeUrl;

    public FeatureListWindow(string noticeUrl = "")
    {
        InitializeComponent();

        _noticeUrl = noticeUrl ?? "";

        _groups = CardCatalog.Build();

        var total = _groups.Sum(g => g.Items.Count);
        var groupCount = _groups.Count;


        if (_noticeUrl.Length > 0)
        {
            LinkBtn.Visibility = Visibility.Visible;
            LinkBtn.Content = "在浏览器打开公告";
        }

        Render("");
    }

    /// <summary>窗口里的功能项数（自检用）。</summary>
    public int FeatureCount => _groups.Sum(g => g.Items.Count);

    /// <summary>窗口里的分组数（自检用）。</summary>
    public int GroupCount => _groups.Count;

    // ---------------------------------------------------------------- 渲染

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        var text = SearchBox.Text ?? "";

        SearchHint.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        Render(text.Trim());
    }

    private void Render(string keyword)
    {
        Host.Children.Clear();

        var shown = 0;

        foreach (var group in _groups)
        {
            var items = group.Items
                .Where(x => keyword.Length == 0
                            || x.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                            || x.Desc.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (items.Count == 0) continue;

            shown += items.Count;

            Host.Children.Add(BuildGroupHeader(group, items.Count));

            foreach (var item in items)
                Host.Children.Add(BuildItem(item, group));
        }

        if (shown == 0)
        {
            Host.Children.Add(new TextBlock
            {
                Text = "没有匹配的功能。换个关键词试试。",
                FontSize = 11,
                Foreground = Palette.InkFaint,
                Margin = new Thickness(4, 20, 0, 0)
            });
        }

        var total = _groups.Sum(g => g.Items.Count);

        FootText.Text = keyword.Length == 0
            ? $"共 {_groups.Count} 组 {total} 项功能"
            : $"匹配 {shown} 项（全部 {total} 项）";
    }

    private static UIElement BuildGroupHeader(CardGroup group, int shownCount)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 16, 0, 8)
        };

        row.Children.Add(new Border
        {
            Width = 3,
            Height = 14,
            CornerRadius = new CornerRadius(2),
            Background = group.AccentBrush,
            VerticalAlignment = VerticalAlignment.Center
        });

        row.Children.Add(new TextBlock
        {
            Text = group.Name,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Palette.Ink,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });

        row.Children.Add(new TextBlock
        {
            Text = $"{shownCount} 项",
            FontSize = 9.5,
            Foreground = Palette.InkFaint,
            Margin = new Thickness(8, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });

        return row;
    }

    private static UIElement BuildItem(CardItem item, CardGroup group)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new Image
        {
            Source = item.Icon,
            Width = 18,
            Height = 18,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0)
        };

        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var text = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };

        text.Children.Add(new TextBlock
        {
            Text = item.Name,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Palette.Ink
        });

        text.Children.Add(new TextBlock
        {
            Text = item.Desc,
            FontSize = 10,
            Foreground = Palette.InkTertiary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0),
            LineHeight = 15
        });

        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        return new Border
        {
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 0, 0, 6),
            CornerRadius = new CornerRadius(8),
            Background = group.TintBrush,
            BorderBrush = Palette.Line,
            BorderThickness = new Thickness(1),
            Child = grid
        };
    }

    // ---------------------------------------------------------------- 操作

    private void OnOpenLink(object sender, RoutedEventArgs e)
    {
        if (_noticeUrl.Length == 0) return;

        try
        {
            Process.Start(new ProcessStartInfo(_noticeUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "打不开链接：\n\n" + ex.Message, "功能一览",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
