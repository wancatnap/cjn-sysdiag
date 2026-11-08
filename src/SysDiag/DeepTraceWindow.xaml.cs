using System.Linq;
using System.IO;
using System.Windows;
using SysDiag.Controls;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>
/// 深度硬件痕迹：DMA 攻击面 / 采集卡链路 / 输入模拟设备。
/// 打开即跑快速体检（约 1 秒），重活放在「深度扫描」里，保持主流程轻量。
/// </summary>
public partial class DeepTraceWindow : Window
{
    private readonly Action<ReportPayload> _showReport;


    private DeepTraceResult _last;
    private bool _busy;

    /// <summary>自检读取：当前结果条数。</summary>
    public int ItemCount => _last?.Items.Count ?? 0;

    public DeepTraceResult LastResult => _last;

    public DeepTraceWindow() : this(_ => { })
    {
    }

    public DeepTraceWindow(Action<ReportPayload> showReport)
    {
        InitializeComponent();

        _showReport = showReport ?? (_ => { });


        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        Reload();
    }

    /// <summary>跑一次快速体检并刷新界面。自检直接调用，不依赖窗口显示。</summary>
    public void Reload()
    {
        try
        {
            Apply(DeepTraceService.Scan(false));
            StatusText.Text = $"已完成快速体检，耗时 {_last.ElapsedMs} ms。需要更深入时点右上角「深度扫描」。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "体检失败：" + ex.Message;
        }
    }

    private async void OnDeep(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        _busy = true;
        DeepBtn.IsEnabled = false;
        StatusText.Text = "深度扫描中，约 3–10 秒…";

        try
        {
            var progress = new Progress<string>(s => StatusText.Text = s + "…");
            var result = await Task.Run(() => DeepTraceService.Scan(true, progress));

            Apply(result);
            StatusText.Text = $"深度扫描完成，耗时 {result.ElapsedMs} ms，共 {result.Items.Count} 项。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "深度扫描失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            DeepBtn.IsEnabled = true;
        }
    }

    private void Apply(DeepTraceResult r)
    {
        _last = r;

        void Fill(LogGridView grid, DeepGroup g, string title)
        {
            grid.SetRows(r.Of(g).Select(i => new LogRow
            {
                Title = i.Name,
                Detail = i.Detail,
                Basis = i.Basis,
                Level = i.Level,
                LevelText = i.Level switch { "warn" => "线索", "hint" => "提示", _ => "正常" },
                Tag = i.DeepOnly ? "深度" : "",
                Payload = i
            }), new SysDiag.Controls.LogViewOptions
            {
                Title = title,
                ShowTimeline = false,
                DefaultSort = "level",
                ExpandAll = true,
                ShowSearch = false
            });
        }

        Fill(DmaGrid, DeepGroup.Dma, "DMA 设备（直读内存卡）");
        Fill(CaptureGrid, DeepGroup.Capture, "采集卡 / 视频捕获");
        Fill(InputGrid, DeepGroup.Input, "输入模拟（键鼠虚拟化）");

        VerdictText.Text = r.Verdict;
        VerdictText.Foreground = Palette.Get(r.VerdictColor);
        VerdictBar.Background = Palette.Get(r.VerdictColor);
        VerdictBox.Background = Palette.Get(r.VerdictBg);
        VerdictDetail.Text = r.VerdictDetail;

        CountText.Text = $"线索 {r.WarnCount} · 提示 {r.HintCount} · 正常 {r.OkCount}　{r.ScopeText}　耗时 {r.ElapsedMs} ms";

        // 填充内容后内部控件可能把外层页面滚到中间（焦点行为）。
        // ScrollToTop 必须等布局完成才有效，所以放到 ApplicationIdle 队列里执行。
        Dispatcher.BeginInvoke(new Action(() => PageScroll.ScrollToTop()),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        ReportBtn.IsEnabled = true;
    }

    private void OnReport(object sender, RoutedEventArgs e)
    {
        if (_last == null) return;

        try
        {
            var payload = DeepTraceService.BuildReport(_last);
            _showReport(payload);
            StatusText.Text = "报告已生成：" + Path.GetFileName(payload.HtmlPath);
        }
        catch (Exception ex)
        {
            StatusText.Text = "报告生成失败：" + ex.Message;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
