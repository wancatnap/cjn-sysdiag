using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SysDiag.Services;

/// <summary>
/// 图标总览渲染（开发校验用，参数 <c>--icon-sheet</c>）。
///
/// 不开窗口，直接把全部图标用 RenderTargetBitmap 画成一张 PNG：
/// 左边是「卡片里真实尺寸」的圆角块预览（34px 底 + 21px 图标），
/// 右边是 44px 放大版，用来判断图形在大尺寸下是否还站得住。
/// </summary>
public static class IconSheet
{
    public static string Render(string path, string accentHex = "#4C6EF5", bool large = false)
    {
        Color accent;
        try { accent = (Color)ColorConverter.ConvertFromString(accentHex); }
        catch { accent = Color.FromRgb(0x4C, 0x6E, 0xF5); }

        var tint = new SolidColorBrush(Color.FromArgb(38, accent.R, accent.G, accent.B));
        tint.Freeze();

        var panel = new WrapPanel { Width = large ? 760 : 1080, Background = Brushes.White };

        foreach (var key in IconLibrary.AllKeys)
        {
            // 卡片里的真实样子
            var badge = new Grid { Width = 34, Height = 34 };
            badge.Children.Add(new Border { CornerRadius = new CornerRadius(11), Background = tint });
            badge.Children.Add(new Image
            {
                Source = IconLibrary.Get(key, accentHex),
                Width = 21,
                Height = 21,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(badge);
            row.Children.Add(new Image
            {
                Source = IconLibrary.Get(key, accentHex),
                Width = large ? 96 : 44,
                Height = large ? 96 : 44,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(12, 0, 0, 0)
            });

            var cell = new StackPanel { Width = large ? 240 : 172, Margin = new Thickness(4, 8, 4, 8) };
            cell.Children.Add(row);
            cell.Children.Add(new TextBlock
            {
                Text = key,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x58, 0x60))
            });

            panel.Children.Add(cell);
        }

        var host = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xFD, 0xF9, 0xFA)),
            Padding = new Thickness(24),
            Child = panel
        };

        host.Measure(new Size(large ? 808 : 1128, double.PositiveInfinity));
        host.Arrange(new Rect(0, 0, host.DesiredSize.Width, host.DesiredSize.Height));
        host.UpdateLayout();

        var width = (int)Math.Ceiling(host.ActualWidth);
        var height = (int)Math.Ceiling(host.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }
}
