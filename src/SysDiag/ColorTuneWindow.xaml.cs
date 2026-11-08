using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>
/// 手动调色面板：gamma / 亮度 / 对比度 / RGB 增益，拖动即写显卡 LUT。
///
/// 两个设计取舍：
///
/// 1. **实时写入带 80ms 节流**。SetDeviceGammaRamp 本身不慢，但拖滑块时每帧都写会让
///    整个桌面（包括本窗口）闪，观感很差。80ms 是「跟手」和「不闪」的折中。
/// 2. **关窗口时问一次**。调完颜色直接关掉，屏幕会一直留在那个色调上——
///    而用户通常以为「关掉窗口就恢复了」。所以非中性且有实际写入时弹一次三选一。
/// </summary>
public partial class ColorTuneWindow : Window
{
    private readonly ManualTune _tune = new();

    private List<IccMonitor> _monitors = new();
    private List<ManualTunePreset> _presets = new();
    private IccMonitor _monitor;

    private readonly DispatcherTimer _throttle;

    /// <summary>构造期间滑块赋值也会触发 ValueChanged，这个标记挡住它。</summary>
    private bool _ready;

    /// <summary>真的往显卡写过东西（关窗口时才需要问）。</summary>
    private bool _touched;

    private bool _closing;

    /// <summary>自检用：枚举到的显示器数。</summary>
    public int MonitorCount => _monitors.Count;

    /// <summary>自检用：当前参数是不是中性。</summary>
    public bool IsNeutral => _tune.IsNeutral;

    /// <summary>自检用：当前参数。</summary>
    public ManualTune Tune => _tune;

    public ColorTuneWindow()
        : this(false)
    {
    }

    /// <summary>
    /// <paramref name="demo"/> 为 true 时直接套一个非中性预设（「增强对比」），
    /// 用于截图核对与演示——否则打开时是中性值，对比区两侧一样，看不出效果。
    /// </summary>
    public ColorTuneWindow(bool demo)
    {
        InitializeComponent();

        _throttle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _throttle.Tick += (_, _) =>
        {
            _throttle.Stop();
            PushLut();
        };

        LoadMonitors();
        RenderQuickActions();
        RenderPresets();
        UpdateValueTexts();
        BuildCompare();

        _ready = true;

        if (demo) ApplyToSliders(QuickPresets[2].Build());

        Closing += OnClosing;
    }

    // ---------------------------------------------------------------- 显示器

    private void LoadMonitors()
    {
        _monitors = IccService.Monitors();

        MonitorCombo.ItemsSource = null;
        MonitorCombo.ItemsSource = _monitors.Select(m => m.Display).ToList();

        if (_monitors.Count > 0)
        {
            MonitorCombo.SelectedIndex = 0;
            _monitor = _monitors[0];
        }

        UpdateState();
    }

    private void UpdateState()
    {
        if (_monitor == null)
        {
            LutStateText.Text = "没有找到显示器。";
            TuneSummaryText.Text = "";
            ApplyBtn.IsEnabled = false;
            RestoreBtn.IsEnabled = false;
            return;
        }

        LutStateText.Text = "当前 LUT：" + IccService.LutStatus(_monitor);
        TuneSummaryText.Text = _tune.Summary() + $"　·　相对线性最大变化 {_tune.MaxDeltaPercent():F1}%";

        Bar.SetElapsed($"{(ElevationService.IsAdmin ? "管理员" : "普通权限")}　·　{DateTime.Now:HH:mm:ss}");
    }

    private void OnMonitorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;

        var index = MonitorCombo.SelectedIndex;
        if (index < 0 || index >= _monitors.Count) return;

        _monitor = _monitors[index];
        _touched = false;

        UpdateState();
    }

    // ---------------------------------------------------------------- 滑块

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;

        ReadSliders();

        UpdateValueTexts();
        RenderCurve();
        BuildCompare();
        UpdateState();

        // 实时预览关掉时不写 LUT，等用户点「应用」
        if (RealtimeCheck.IsChecked != true)
        {
            Bar.SetElapsed("实时预览已关：改完点「应用」才写入");
            return;
        }

        _throttle.Stop();
        _throttle.Start();
    }

    private void ReadSliders()
    {
        _tune.Gamma = GammaSlider.Value;
        _tune.Brightness = BrightnessSlider.Value;
        _tune.Contrast = ContrastSlider.Value;
        _tune.GainR = GainRSlider.Value;
        _tune.GainG = GainGSlider.Value;
        _tune.GainB = GainBSlider.Value;
    }

    private void UpdateValueTexts()
    {
        GammaValue.Text = _tune.Gamma.ToString("0.00");
        BrightnessValue.Text = (_tune.Brightness > 0 ? "+" : "") + _tune.Brightness.ToString("0") + "%";
        ContrastValue.Text = _tune.Contrast.ToString("0.00");
        GainRValue.Text = _tune.GainR.ToString("0.00");
        GainGValue.Text = _tune.GainG.ToString("0.00");
        GainBValue.Text = _tune.GainB.ToString("0.00");
    }

    /// <summary>把参数灌进滑块，同时只写一次 LUT（而不是六个滑块各触发一次）。</summary>
    private void ApplyToSliders(ManualTune tune)
    {
        tune = tune.Clamped();

        _ready = false;

        GammaSlider.Value = tune.Gamma;
        BrightnessSlider.Value = tune.Brightness;
        ContrastSlider.Value = tune.Contrast;
        GainRSlider.Value = tune.GainR;
        GainGSlider.Value = tune.GainG;
        GainBSlider.Value = tune.GainB;

        _tune.Gamma = tune.Gamma;
        _tune.Brightness = tune.Brightness;
        _tune.Contrast = tune.Contrast;
        _tune.GainR = tune.GainR;
        _tune.GainG = tune.GainG;
        _tune.GainB = tune.GainB;

        _ready = true;

        UpdateValueTexts();
        RenderCurve();
        BuildCompare();
        UpdateState();

        PushLut();
    }

    // ---------------------------------------------------------------- 写 LUT

    private void PushLut()
    {
        if (_monitor == null) return;

        var (ok, message) = IccService.ApplyTune(_monitor, _tune);

        if (ok) _touched = true;

        TuneSummaryText.Text = _tune.Summary() + $"　·　相对线性最大变化 {_tune.MaxDeltaPercent():F1}%";

        if (!ok)
        {
            Bar.SetElapsed(message.Split('\n')[0]);
            return;
        }

        Bar.SetElapsed(_tune.IsNeutral ? "已回到未调整状态" : "已写入：" + _tune.Summary());
        LutStateText.Text = "当前 LUT：" + IccService.LutStatus(_monitor);
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        ReadSliders();
        PushLut();
    }

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        if (_monitor == null) return;

        var (ok, message) = IccService.RestoreLut(_monitor);

        ApplyToSliders(new ManualTune());
        _touched = false;

        UiDialog.Show(this, message, ok ? "已还原" : "没有还原",
            MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);

        UpdateState();
    }

    // ---------------------------------------------------------------- 曲线与预览

    private void OnCurveSizeChanged(object sender, SizeChangedEventArgs e) => RenderCurve();

    private void RenderCurve()
    {
        CurveCanvas.Children.Clear();

        var w = CurveCanvas.ActualWidth;
        var h = CurveCanvas.ActualHeight;

        if (w < 20 || h < 20) return;

        // 四等分网格
        for (var i = 1; i < 4; i++)
        {
            var x = w * i / 4.0;
            var y = h * i / 4.0;

            CurveCanvas.Children.Add(new Line
            {
                X1 = x, Y1 = 0, X2 = x, Y2 = h,
                Stroke = Palette.Line, StrokeThickness = 0.7
            });

            CurveCanvas.Children.Add(new Line
            {
                X1 = 0, Y1 = y, X2 = w, Y2 = y,
                Stroke = Palette.Line, StrokeThickness = 0.7
            });
        }

        // 参考：不调整时应该贴着这条对角线
        CurveCanvas.Children.Add(new Line
        {
            X1 = 0, Y1 = h, X2 = w, Y2 = 0,
            Stroke = Palette.InkFaint,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 4 }
        });

        var ramp = _tune.BuildRamp();

        DrawChannel(0, Color.FromRgb(0xD6, 0x3B, 0x3B));
        DrawChannel(1, Color.FromRgb(0x2F, 0x9E, 0x44));
        DrawChannel(2, Color.FromRgb(0x3B, 0x6E, 0xD8));

        void DrawChannel(int channel, Color color)
        {
            var line = new Polyline
            {
                Stroke = new SolidColorBrush(color),
                StrokeThickness = 1.7,
                StrokeLineJoin = PenLineJoin.Round
            };

            for (var i = 0; i < 256; i++)
            {
                var x = w * i / 255.0;
                var y = h - ramp[channel * 256 + i] / 65535.0 * h;

                line.Points.Add(new Point(x, y));
            }

            CurveCanvas.Children.Add(line);
        }
    }

    private static readonly (byte R, byte G, byte B)[] Swatches =
    {
        (0, 0, 0), (32, 32, 32), (64, 64, 64), (128, 128, 128), (200, 200, 200), (255, 255, 255),
        (214, 68, 68), (86, 170, 86), (70, 110, 210), (230, 180, 150), (40, 90, 160), (200, 150, 60)
    };

    /// <summary>
    /// 分屏对比：同一张参考图放两遍，上层（调整后）用 Clip 只露出竖线右侧。
    /// 左原始 / 右调整后，拖竖线就等于在两版之间来回划。
    ///
    /// 底图优先用数据目录里的 color-reference.jpg（用户可换成自己的游戏截图），
    /// 没有就用打进 exe 的那张；两张都读不到才退回合成色块。
    /// </summary>
    private void BuildCompare()
    {
        BeforeLayer.Children.Clear();
        AfterLayer.Children.Clear();

        var hasImage = EnsureReference();

        if (hasImage)
        {
            BeforeLayer.Children.Add(new Image { Source = _refImage, Stretch = Stretch.Uniform });
            AfterLayer.Children.Add(new Image { Source = RunRamp(_tune.BuildRamp()), Stretch = Stretch.Uniform });
        }
        else
        {
            // 没选底图时用合成图案，功能不残废
            BeforeLayer.Children.Add(BuildPattern(null));
            AfterLayer.Children.Add(BuildPattern(_tune.BuildRamp()));
        }

        UpdateSplit();

        // 没底图时把「换底图…」提到主色，否则用户不知道该点哪
        NoRefHint.Visibility = hasImage ? Visibility.Collapsed : Visibility.Visible;
        PickImageBtn.SetResourceReference(StyleProperty, hasImage ? "ToolButton" : "PrimaryButton");
        ClearImageBtn.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;

        NoteText.Text = _tune.IsNeutral
            ? "当前是中性参数：竖线两侧完全一样。拖动左边滑块后，右侧会立刻变。"
            : $"相对线性最大变化 {_tune.MaxDeltaPercent():F1}%。竖线左侧是原始、右侧是调整后——" +
              "拖到两头可以看整幅效果。";

        if (!hasImage)
        {
            NoteText.Text += "\n\n还没有底图，先用色卡顶着。点右上角「换底图…」选一张自己的截图" +
                             "（游戏画面最直观），只存在本机，不会跟着程序分发。";
        }
    }

    // ---------------------------------------------------------------- 参考图

    private BitmapSource _refImage;
    private byte[] _refPixels;
    private WriteableBitmap _adjusted;
    private byte[] _adjustBuf;
    private bool _refLoadTried;

    /// <summary>参考图只加载一次；失败也记住，避免每次拖滑块都去碰磁盘。</summary>
    private bool EnsureReference()
    {
        if (_refImage != null) return true;
        if (_refLoadTried) return false;

        _refLoadTried = true;

        try
        {
            var bitmap = LoadReference();
            if (bitmap == null) return false;

            // 统一转成 Bgra32：后面按下标逐像素改时格式固定，不用再判断
            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            converted.Freeze();

            var stride = converted.PixelWidth * 4;
            var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);

            _refImage = converted;
            _refPixels = pixels;
            _adjusted = new WriteableBitmap(converted);
            _adjustBuf = new byte[pixels.Length];

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 用户自选底图的位置。
    /// **程序不内置任何图片素材**——游戏截图、摄影作品都有版权，不能随源码分发。
    /// 想用真实画面就自己选一张，存这儿，下次启动直接命中。
    /// </summary>
    internal static string ReferencePath => System.IO.Path.Combine(AppPaths.DataDir, "color-reference.jpg");

    /// <summary>读用户自选底图；没有就返回 null，界面自动退回合成色卡。</summary>
    private static BitmapSource LoadReference()
    {
        try
        {
            var path = ReferencePath;
            return System.IO.File.Exists(path) ? LoadImageFile(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把任意图片文件解码成 BitmapSource。自检也走这条，能独立验证解码链路。</summary>
    internal static BitmapSource LoadImageFile(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        image.EndInit();
        image.Freeze();
        return image;
    }

    // ---------------------------------------------------------------- 换底图

    /// <summary>
    /// 选一张自己的底图，统一重编码成 jpg 存进数据目录。
    /// 存固定位置的好处：下次启动直接命中，不用用户自己去翻 AppData。
    /// </summary>
    private void OnPickImage(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选一张对比用的底图",
            Filter = "图片|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            // 长边超过 1920 就没意义了：对比区也就七八百像素宽，缩一下省内存也省重算时间
            var image = Downscale(LoadImageFile(dialog.FileName), 1920);

            AppPaths.Ensure();

            var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
            encoder.Frames.Add(BitmapFrame.Create(image));

            using (var stream = System.IO.File.Create(ReferencePath))
                encoder.Save(stream);

            ResetReference();
            BuildCompare();

            NoteText.Text =
                $"底图已换成「{System.IO.Path.GetFileName(dialog.FileName)}」（{image.PixelWidth}×{image.PixelHeight}）。" +
                "拖动竖线就能对比左右两侧。";
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "这张图读不了：\n\n" + ex.Message, "换底图",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnClearImage(object sender, RoutedEventArgs e)
    {
        try
        {
            if (System.IO.File.Exists(ReferencePath)) System.IO.File.Delete(ReferencePath);
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "删不掉旧底图：\n\n" + ex.Message, "用色卡",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ResetReference();
        BuildCompare();
    }

    private void ResetReference()
    {
        _refImage = null;
        _refPixels = null;
        _adjusted = null;
        _adjustBuf = null;
        _refLoadTried = false;
    }

    /// <summary>长边超过 max 时等比缩小。</summary>
    private static BitmapSource Downscale(BitmapSource source, int max)
    {
        var longest = Math.Max(source.PixelWidth, source.PixelHeight);
        if (longest <= max) return source;

        var scale = (double)max / longest;

        var transformed = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        transformed.Freeze();
        return transformed;
    }

    /// <summary>
    /// 把 LUT 套到参考图上，返回「调整后」的那张。
    ///
    /// 复用同一个 WriteableBitmap + 同一块缓冲：拖滑块时每秒要重算十几次，
    /// 每次新分配 2MB 会让 GC 一直响。全图 1024×462 大约 2~4 ms，够快。
    /// </summary>
    private BitmapSource RunRamp(ushort[] ramp)
    {
        if (_adjusted == null || _adjustBuf == null) return _refImage;

        ApplyRampBytes(_refPixels, _adjustBuf, ramp);

        _adjusted.WritePixels(
            new Int32Rect(0, 0, _adjusted.PixelWidth, _adjusted.PixelHeight),
            _adjustBuf,
            _adjusted.PixelWidth * 4,
            0);

        return _adjusted;
    }

    /// <summary>
    /// 逐像素查表。src / dst 都是 Bgra32 的原始字节（每像素 4 字节），长度必须一致。
    ///
    /// 抽成静态方法是为了让自检能直接测通道顺序——B/G/R 接错位在画面上不容易看出来，
    /// 但会让人像变蓝、草地变红，必须用断言钉住。
    /// </summary>
    internal static void ApplyRampBytes(byte[] src, byte[] dst, ushort[] ramp)
    {
        if (ramp is not { Length: 768 }) { Array.Copy(src, dst, src.Length); return; }

        // LUT 先压成 0..255 字节表：像素循环里只做一次数组取值，不再右移
        Span<byte> lut = stackalloc byte[768];

        for (var i = 0; i < 768; i++)
            lut[i] = (byte)(ramp[i] >> 8);

        for (var i = 0; i < src.Length; i += 4)
        {
            dst[i] = lut[512 + src[i]];         // B
            dst[i + 1] = lut[256 + src[i + 1]]; // G
            dst[i + 2] = lut[src[i + 2]];       // R
            dst[i + 3] = src[i + 3];            // A
        }
    }

    /// <summary>测试图案：灰阶 + RGB 三通道渐变 + 色块。ramp 为 null 时画原始值。</summary>
    private static Panel BuildPattern(ushort[] ramp)
    {
        var root = new Grid();

        for (var i = 0; i < 3; i++)
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // 第一行：灰阶（最能看出 gamma / 亮度 / 对比度）
        var gray = new Border { Background = GrayBrush(ramp), Margin = new Thickness(0, 0, 0, 1) };
        Grid.SetRow(gray, 0);
        root.Children.Add(gray);

        // 第二行：R / G / B 三段渐变（看增益与偏色）
        var bands = new Grid { Margin = new Thickness(0, 0, 0, 1) };

        for (var i = 0; i < 3; i++)
            bands.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (var channel = 0; channel < 3; channel++)
        {
            var band = new Border
            {
                Background = ChannelBrush(ramp, channel),
                Margin = new Thickness(channel == 0 ? 0 : 1, 0, 0, 0)
            };

            Grid.SetColumn(band, channel);
            bands.Children.Add(band);
        }

        Grid.SetRow(bands, 1);
        root.Children.Add(bands);

        // 第三行：色块（肤色 / 天空 / 草绿 / 金属这些真实场景的典型色）
        var swatches = new UniformGrid { Rows = 1, Columns = Swatches.Length };

        foreach (var (r, g, b) in Swatches)
        {
            swatches.Children.Add(new Border
            {
                Background = new SolidColorBrush(Map(ramp, r, g, b)),
                Margin = new Thickness(1, 0, 0, 0)
            });
        }

        Grid.SetRow(swatches, 2);
        root.Children.Add(swatches);

        return root;
    }

    private static Brush GrayBrush(ushort[] ramp)
    {
        const int stops = 17;

        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };

        for (var i = 0; i < stops; i++)
        {
            var v = (byte)(i * 255 / (stops - 1));
            brush.GradientStops.Add(new GradientStop(Map(ramp, v, v, v), i / (double)(stops - 1)));
        }

        return brush;
    }

    private static Brush ChannelBrush(ushort[] ramp, int channel)
    {
        const int stops = 17;

        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };

        for (var i = 0; i < stops; i++)
        {
            var v = (byte)(i * 255 / (stops - 1));

            var r = channel == 0 ? v : (byte)0;
            var g = channel == 1 ? v : (byte)0;
            var b = channel == 2 ? v : (byte)0;

            brush.GradientStops.Add(new GradientStop(Map(ramp, r, g, b), i / (double)(stops - 1)));
        }

        return brush;
    }

    /// <summary>把一个 RGB 值过一遍 LUT。ramp 为 null 表示不调整。</summary>
    private static Color Map(ushort[] ramp, byte r, byte g, byte b)
    {
        if (ramp is not { Length: 768 }) return Color.FromRgb(r, g, b);

        return Color.FromRgb(
            (byte)(ramp[r] >> 8),
            (byte)(ramp[256 + g] >> 8),
            (byte)(ramp[512 + b] >> 8));
    }

    // ---------------------------------------------------------------- 分割线拖动

    private bool _dragging;

    /// <summary>0 = 全原始，1 = 全调整后。窗口缩放时按比例保持。</summary>
    private double _split = 0.5;

    private void OnCompareDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;

        CompareHost.CaptureMouse();
        SetSplit(e.GetPosition(CompareHost).X);
    }

    private void OnCompareMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;

        SetSplit(e.GetPosition(CompareHost).X);
    }

    private void OnCompareUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;

        _dragging = false;
        CompareHost.ReleaseMouseCapture();
    }

    private void OnCompareSizeChanged(object sender, SizeChangedEventArgs e) => UpdateSplit();

    private void SetSplit(double x)
    {
        var width = CompareHost.ActualWidth;
        if (width <= 0) return;

        _split = Math.Clamp(x / width, 0, 1);
        UpdateSplit();
    }

    private void UpdateSplit()
    {
        var width = CompareHost.ActualWidth;
        var height = CompareHost.ActualHeight;

        if (width <= 0 || height <= 0) return;

        var x = width * _split;

        // 上层是「调整后」，只露出竖线右侧 —— 这样左边是原始、右边是调整后，和标注一致。
        // （之前写成 (0,0,x,height) 只露左侧，标签说的是左边原始，实际反了。）
        AfterClip.Rect = new Rect(x, 0, Math.Max(0, width - x), height);

        SplitLine.X1 = x;
        SplitLine.X2 = x;
        SplitLine.Y2 = height;

        SplitHandle.Margin = new Thickness(x - 10, 0, 0, 0);
    }

    // ---------------------------------------------------------------- 快速方案

    private void RenderQuickActions()
    {
        QuickHost.Children.Clear();

        foreach (var (name, tune) in QuickPresets)
        {
            var button = new Button
            {
                Content = name,
                FontSize = 10,
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(0, 0, 6, 6)
            };

            button.SetResourceReference(StyleProperty, "ToolButton");

            var captured = tune;
            button.Click += (_, _) => ApplyToSliders(captured());

            QuickHost.Children.Add(button);
        }
    }

    private static readonly (string Name, Func<ManualTune> Build)[] QuickPresets =
    {
        ("中性", () => new ManualTune()),

        // 抬黑位：白天/亮环境里让暗部细节浮出来，代价是纯黑变灰
        ("提亮暗部", () => new ManualTune { Gamma = 1.45, Brightness = 3, Contrast = 1.02 }),

        // 压黑做对比：画面更「透」，暗部细节会丢
        ("增强对比", () => new ManualTune { Gamma = 0.88, Contrast = 1.18, Brightness = -2 }),

        ("电影感", () => new ManualTune { Gamma = 1.10, Contrast = 1.10, Brightness = -3, GainR = 1.04, GainB = 0.96 }),

        ("偏冷", () => new ManualTune { GainR = 0.95, GainB = 1.08 }),

        ("偏暖", () => new ManualTune { GainR = 1.08, GainB = 0.94 })
    };

    // ---------------------------------------------------------------- 方案保存

    private void RenderPresets()
    {
        _presets = IccService.LoadTunePresets();

        PresetCombo.ItemsSource = null;
        PresetCombo.ItemsSource = _presets.Select(p => $"{p.Name}　·　{p.Detail}").ToList();
        PresetCombo.SelectedIndex = -1;
    }

    private void OnPresetSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;

        var index = PresetCombo.SelectedIndex;
        if (index < 0 || index >= _presets.Count) return;

        var preset = _presets[index];

        PresetNameBox.Text = preset.Name;
        ApplyToSliders(preset.ToTune());
    }

    private void OnSavePreset(object sender, RoutedEventArgs e)
    {
        var name = PresetNameBox.Text?.Trim() ?? "";

        if (name.Length == 0)
        {
            UiDialog.Show(this, "先在上面填一个方案名。", "保存方案",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var (ok, message) = IccService.SaveTunePreset(name, _tune);

        UiDialog.Show(this, message, ok ? "已保存" : "没有保存",
            MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);

        RenderPresets();
    }

    private void OnDeletePreset(object sender, RoutedEventArgs e)
    {
        var index = PresetCombo.SelectedIndex;

        if (index < 0 || index >= _presets.Count)
        {
            UiDialog.Show(this, "先在列表里选一个方案。", "删除方案",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var name = _presets[index].Name;

        var confirm = UiDialog.Show(this, $"要删除方案「{name}」吗？", "删除方案",
            MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK) return;

        var (ok, message) = IccService.DeleteTunePreset(name);

        UiDialog.Show(this, message, ok ? "已删除" : "没有删除",
            MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);

        RenderPresets();
    }

    // ---------------------------------------------------------------- 导出 ICC

    private void OnExportIcc(object sender, RoutedEventArgs e)
    {
        var name = PresetNameBox.Text?.Trim() ?? "";
        if (name.Length == 0) name = "SysDiag-Tune";

        var dialog = new SaveFileDialog
        {
            Title = "导出 ICC 配置",
            FileName = name + ".icc",
            Filter = "ICC 配置 (*.icc)|*.icc|ICC 配置 (*.icm)|*.icm"
        };

        if (dialog.ShowDialog(this) != true) return;

        var (ok, message) = IccService.ExportTuneIcc(_tune, name, dialog.FileName);

        UiDialog.Show(this, message, ok ? "已导出" : "导出失败",
            MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    // ---------------------------------------------------------------- 关闭

    private void OnClosing(object sender, CancelEventArgs e)
    {
        if (_closing || !_touched || _monitor == null || _tune.IsNeutral) return;

        var answer = UiDialog.Show(this,
            "屏幕还停在你调的颜色上。关掉窗口后要保留吗？\n\n" +
            "· 是：保留当前效果（下次启动本程序不会自动恢复）\n" +
            "· 否：还原成调整前的样子\n" +
            "· 取消：先不关，回去把参数存成方案",
            "手动调色", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        if (answer == MessageBoxResult.Cancel)
        {
            e.Cancel = true;
            return;
        }

        if (answer == MessageBoxResult.No)
        {
            IccService.RestoreLut(_monitor);
        }
        else
        {
            // 保留：把当前 LUT 记成「本程序改过」，下次打开窗口能看出状态
            IccService.ApplyTune(_monitor, _tune);
        }

        _closing = true;
    }
}
