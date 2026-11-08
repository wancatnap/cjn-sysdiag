using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SysDiag.Controls;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>
/// 内嵌事件查看器：读本机事件通道，替代系统「事件查看器」窗口。
/// 交互对齐系统版本的核心用法：选日志 → 浏览列表 → 按级别/时间筛选 → 点开看详情。
/// </summary>
public partial class EvtxViewerWindow : Window
{
    private readonly Action<ReportPayload> _showReport;

    private List<ViewEvent> _events = new();
    private ViewEvent? _selected;
    private bool _busy;

    /// <summary>自检读取：当前事件数。</summary>
    public int EventCount => _events.Count;

    public string CurrentChannel => ChannelBox.SelectedItem is ChannelItem ci ? ci.Channel : "";

    public bool ElevateButtonVisible => ElevateBtn.Visibility == Visibility.Visible;

    /// <summary>下拉项：通道名 + 中文标签。</summary>
    private sealed class ChannelItem
    {
        public string Channel { get; init; } = "";
        public string Label { get; init; } = "";
        public override string ToString() => Label;
    }

    public EvtxViewerWindow() : this(_ => { })
    {
    }

    public EvtxViewerWindow(Action<ReportPayload> showReport)
    {
        InitializeComponent();

        _showReport = showReport ?? (_ => { });

        Kpi.Set(("0", "事件", ""), ("0", "错误", ""), ("0", "警告", ""), ("0", "信息 / 其他", ""));

        var items = EventViewerService.Channels
            .Select(c => new ChannelItem { Channel = c.Channel, Label = c.Name })
            .ToList();

        ChannelBox.ItemsSource = items;
        ChannelBox.SelectedIndex = 0;

        EventGrid.SetRows(Array.Empty<LogRow>(), new LogViewOptions
        {
            Title = "事件列表",
            TitleNote = "选择日志后自动读取",
            ShowTimeline = true,
            DefaultSort = "time-desc"
        });

        // 时间区间任意变化（预设切换或自定义调整）都重新查询；无效区间只提示不查
        RangePicker.RangeChanged += () =>
        {
            if (!IsLoaded) return;
            if (RangePicker.HasInvalidRange)
            {
                _events = new List<ViewEvent>();
                EventGrid.SetRows(Array.Empty<LogRow>(), new LogViewOptions
                {
                    Title = "事件列表",
                    TitleNote = "等待有效的结束时间",
                    ShowTimeline = false
                });
                ApplyKpiEmpty();
                StateText.Text = "区间无效：结束时间必须晚于开始时间";
                StateText.Foreground = Palette.Warn;
                ReportBtn.IsEnabled = false;
                return;
            }

            _ = ReloadAsync();
        };

        EventGrid.RowSelected += row =>
        {
            if (row?.Payload is ViewEvent ev) ShowDetail(ev);
        };

        var admin = ElevationService.IsAdmin;
        ElevateBtn.Visibility = admin ? Visibility.Collapsed : Visibility.Visible;

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await ReloadAsync();
    }

    /// <summary>同步读取，供无头自检调用。</summary>
    public void ReloadSync()
    {
        var p = CurrentParams();
        var (from, to) = p.Range.Bounds();
        var list = EventViewerService.Read(p.Channel, from, to, p.Max, out var state, out var note);
        ApplyRead((list, p.Name, p.Range, state, note));
    }

    /// <summary>读取参数：必须先在 UI 线程取好，后台线程不能碰控件。</summary>
    private (string Channel, string Name, TimeRange Range, int Max) CurrentParams()
    {
        var channel = CurrentChannel.Length > 0 ? CurrentChannel : "System";
        var name = ChannelBox.SelectedItem is ChannelItem ci ? ci.Label : channel;
        var max = MaxBox.SelectedItem is ComboBoxItem m && int.TryParse(m.Tag?.ToString(), out var n)
            ? n : EventViewerService.DefaultMax;

        return (channel, name, RangePicker.Range, max);
    }

    private async Task ReloadAsync()
    {
        if (_busy) return;

        var p = CurrentParams();

        _busy = true;
        ReloadBtn.IsEnabled = false;
        ReportBtn.IsEnabled = false;
        HintText.Text = "读取中…";

        try
        {
            var data = await Task.Run(() =>
            {
                var (from, to) = p.Range.Bounds();
                var list = EventViewerService.Read(p.Channel, from, to, p.Max, out var state, out var note);
                return (Events: list, Name: p.Name, Range: p.Range, State: state, Note: note);
            });

            ApplyRead(data);
        }
        catch (Exception ex)
        {
            StateText.Text = "读取失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            ReloadBtn.IsEnabled = true;
            ReportBtn.IsEnabled = _events.Count > 0;
        }
    }

    private void ApplyRead((List<ViewEvent> Events, string Name, TimeRange Range, string State, string Note) data)
    {
        _events = data.Events;
        _selected = null;
        DetailBox.Visibility = Visibility.Collapsed;

        EventGrid.SetRows(_events.Select(e => new LogRow
        {
            Title = e.HeaderText,
            Detail = e.ShortMessage,
            Basis = $"{e.Channel} · 记录 {e.RecordId}",
            Level = e.LevelDisplay,
            LevelText = e.LevelText,
            Time = e.Time,
            TimeText = e.TimeText,
            Payload = e
        }), new LogViewOptions
        {
            Title = "事件列表",
            TitleNote = $"{data.Name} · {data.Range.Label}",
            ShowTimeline = true,
            DefaultSort = "time-desc"
        });

        var err = _events.Count(e => e.LevelDisplay == "warn");
        var warn = _events.Count(e => e.LevelDisplay == "hint");

        Kpi.Set(
            (_events.Count.ToString("N0"), "事件", ""),
            (err.ToString(), "错误", err > 0 ? Palette.WarnHex : Palette.OkHex),
            (warn.ToString(), "警告", warn > 0 ? Palette.HintHex : Palette.OkHex),
            ((_events.Count - err - warn).ToString(), "信息 / 其他", ""));

        // 状态栏同时给「查的范围」和「查到多少」，两者来自同一个 TimeRange
        StateText.Text = data.State switch
        {
            "limited" => $"{data.Range.Label} · 受限：{data.Note}",
            "missing" => $"{data.Range.Label} · {data.Note}",
            "failed" => $"{data.Range.Label} · 读取失败：{data.Note}",
            _ => $"{data.Range.Label} · {data.Note}"
        };
        StateText.Foreground = data.State == "done" ? Palette.InkSecondary : Palette.Hint;

        HintText.Text = data.State == "done" ? "" : data.Note;
        ReportBtn.IsEnabled = _events.Count > 0;
    }

    private void ApplyKpiEmpty()
    {
        Kpi.Set(("0", "事件", ""), ("0", "错误", ""), ("0", "警告", ""), ("0", "信息 / 其他", ""));
    }

    private void ShowDetail(ViewEvent ev)
    {
        _selected = ev;

        DetailTitle.Text = $"{ev.HeaderText}　·　{ev.TimeText}　·　{ev.LevelText}";

        var full = ev.Message;
        if (full.Length == 0 && ev.RecordId > 0)
            full = EventViewerService.ReadFullMessage(ev.Channel, ev.RecordId);

        DetailText.Text = full.Length > 0
            ? full
            : "这条事件没有可读的描述文本（可能缺少消息 DLL，或事件只含二进制数据）。";

        DetailBox.Visibility = Visibility.Visible;
    }

    private void OnHideDetail(object sender, RoutedEventArgs e)
    {
        DetailBox.Visibility = Visibility.Collapsed;
        _selected = null;
    }

    private async void OnChannelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        await ReloadAsync();
    }

    private async void OnMaxChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        await ReloadAsync();
    }

    private async void OnReload(object sender, RoutedEventArgs e) => await ReloadAsync();

    private void OnReport(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = ChannelBox.SelectedItem is ChannelItem ci ? ci.Label : CurrentChannel;
            var payload = EventViewerService.BuildReport(name, _events, RangePicker.Range, StateText.Text);
            _showReport(payload);
            HintText.Text = "报告已生成";
        }
        catch (Exception ex)
        {
            HintText.Text = "报告生成失败：" + ex.Message;
        }
    }

    private void OnElevate(object sender, RoutedEventArgs e)
    {
        if (ElevationService.RelaunchAsAdmin("--open-events", out var error))
        {
            Close();
            return;
        }

        if (!string.IsNullOrEmpty(error) && error != ElevationService.CancelledMessage)
            HintText.Text = error;
    }
}
