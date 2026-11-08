using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SysDiag.Models;

namespace SysDiag.Controls;

/// <summary>
/// 时间区间选择器：预设（1 / 24 / 72 小时、7 / 30 天、不限）+ 自定义按小时起止。
/// 对外只暴露一个 <see cref="Range"/>，查询与显示都用它，保证「看到的范围」和「查的范围」一致。
/// 模式用显式字段维护，不依赖 ComboBox 的内部选中状态（初始化阶段它会抖）。
/// </summary>
public partial class HourRangePicker : UserControl
{
    private enum Mode { Preset, Custom }

    private Mode _mode = Mode.Preset;
    private int _presetHours = 24;
    private bool _ready;
    private bool _suppress;

    /// <summary>任意选项变化（预设切换或自定义调整）后触发。</summary>
    public event Action? RangeChanged;

    /// <summary>当前区间（已做边界处理）。</summary>
    public TimeRange Range { get; private set; } = TimeRange.LastHours(24);

    /// <summary>自定义模式下区间无效（结束不晚于开始）。</summary>
    public bool HasInvalidRange => _mode == Mode.Custom && !Range.IsValid;

    /// <summary>自检读取：当前是否自定义模式。</summary>
    public bool IsCustomMode => _mode == Mode.Custom;

    /// <summary>自检读取：当前预设小时数（自定义模式返回 0）。</summary>
    public int PresetHours => _mode == Mode.Preset ? _presetHours : 0;

    public HourRangePicker()
    {
        InitializeComponent();

        var hours = Enumerable.Range(0, 24).Select(h => $"{h:00}:00").ToList();
        FromHour.ItemsSource = hours;
        ToHour.ItemsSource = hours;

        // 初始：最近 24 个整点
        ApplyPreset(24, selectIndex: 1);
        SyncCustomInputs(Range);

        _ready = true;

        // 模板应用后校正一次 UI，保证界面与模式一致
        Loaded += (_, _) => SyncUiToMode();
    }

    // ---------------- 对外 API ----------------

    /// <summary>外部设置预设（提权重启恢复参数时用）。没有精确值就取最接近的预设，绝不切自定义。</summary>
    public void SetPresetHours(int hours)
    {
        var target = Math.Max(0, hours);
        var index = -1;
        var bestDiff = int.MaxValue;
        var chosen = 24;

        for (var i = 0; i < PresetBox.Items.Count; i++)
        {
            if (PresetBox.Items[i] is not ComboBoxItem item) continue;

            var tag = item.Tag?.ToString() ?? "";
            if (tag == "custom" || !int.TryParse(tag, out var h)) continue;

            var diff = Math.Abs(h - target);
            if (diff >= bestDiff) continue;

            bestDiff = diff;
            index = i;
            chosen = h;

            if (diff == 0) break;
        }

        if (index < 0) return;

        ApplyPreset(chosen, index);
        RangeChanged?.Invoke();
    }

    /// <summary>外部设置一个自定义区间（例如从报告或参数恢复）。</summary>
    public void SetCustom(DateTime fromHour, DateTime toHour)
    {
        _mode = Mode.Custom;

        _suppress = true;
        PresetBox.SelectedIndex = PresetBox.Items.Count - 1;
        _suppress = false;

        CustomPanel.Visibility = Visibility.Visible;

        _suppress = true;
        FromDate.SelectedDate = fromHour.Date;
        ToDate.SelectedDate = toHour.Date;
        FromHour.SelectedIndex = fromHour.Hour;
        ToHour.SelectedIndex = toHour.Hour;
        _suppress = false;

        Recompute();
    }

    // ---------------- 内部 ----------------

    private void ApplyPreset(int hours, int? selectIndex = null)
    {
        _mode = Mode.Preset;
        _presetHours = Math.Max(0, hours);
        Range = _presetHours == 0 ? TimeRange.All() : TimeRange.LastHours(_presetHours);

        HintText.Text = _presetHours == 0 ? "读取该日志的全部可用记录" : "";
        HintText.Foreground = Palette.InkFaint;
        CustomPanel.Visibility = Visibility.Collapsed;

        if (!selectIndex.HasValue) return;

        _suppress = true;
        PresetBox.SelectedIndex = selectIndex.Value;
        _suppress = false;
    }

    private void SyncUiToMode()
    {
        if (_mode == Mode.Custom)
        {
            CustomPanel.Visibility = Visibility.Visible;
            Recompute();
            return;
        }

        CustomPanel.Visibility = Visibility.Collapsed;
        HintText.Text = _presetHours == 0 ? "读取该日志的全部可用记录" : "";
    }

    private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _suppress) return;

        var tag = (PresetBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "24";

        if (tag == "custom")
        {
            // 用进入自定义之前的区间做初值，少点两下
            var seed = Range;
            var now = DateTime.Now;
            var from = seed.IsAll ? now.AddHours(-24) : seed.From;

            _mode = Mode.Custom;
            CustomPanel.Visibility = Visibility.Visible;

            _suppress = true;
            FromDate.SelectedDate = from.Date;
            ToDate.SelectedDate = (seed.IsAll ? now : seed.To).Date;
            FromHour.SelectedIndex = from.Hour;
            ToHour.SelectedIndex = (seed.IsAll ? now : seed.To).Hour;
            _suppress = false;

            Recompute();
            return;
        }

        var hours = int.TryParse(tag, out var h) ? h : 24;
        ApplyPreset(hours);
        RangeChanged?.Invoke();
    }

    private void OnCustomChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _suppress || _mode != Mode.Custom) return;
        Recompute();
    }

    private void Recompute()
    {
        if (_mode != Mode.Custom) return;

        if (FromDate.SelectedDate is not DateTime fromDate || ToDate.SelectedDate is not DateTime toDate
            || FromHour.SelectedIndex < 0 || ToHour.SelectedIndex < 0)
        {
            HintText.Text = "请选择完整的开始与结束时间";
            HintText.Foreground = Palette.Hint;
            RangeChanged?.Invoke();
            return;
        }

        var fromHour = fromDate.Date.AddHours(FromHour.SelectedIndex);
        var toHour = toDate.Date.AddHours(ToHour.SelectedIndex);

        Range = TimeRange.Custom(fromHour, toHour);

        // 对调/截断后回填控件，让界面显示与查询一致
        var expectedTo = toHour.AddHours(1).AddTicks(-1);
        if (Range.IsValid && (!Range.From.Equals(fromHour) || Range.To != expectedTo))
        {
            _suppress = true;
            FromDate.SelectedDate = Range.From.Date;
            FromHour.SelectedIndex = Range.From.Hour;
            ToDate.SelectedDate = Range.To.Date;
            ToHour.SelectedIndex = Range.To.Hour;
            _suppress = false;
        }

        if (!Range.IsValid)
        {
            HintText.Text = "区间无效：结束时间必须晚于开始时间";
            HintText.Foreground = Palette.Warn;
        }
        else if (Range.Adjustment.Length > 0)
        {
            HintText.Text = Range.Adjustment;
            HintText.Foreground = Palette.Hint;
        }
        else
        {
            HintText.Text = Range.Label;
            HintText.Foreground = Palette.Ok;
        }

        RangeChanged?.Invoke();
    }

    private void SyncCustomInputs(TimeRange range)
    {
        var now = DateTime.Now;
        var from = range.IsAll ? now.AddHours(-24) : range.From;
        var to = range.IsAll ? now : range.To;

        _suppress = true;
        FromDate.SelectedDate = from.Date;
        ToDate.SelectedDate = to.Date;
        FromHour.SelectedIndex = from.Hour;
        ToHour.SelectedIndex = to.Hour;
        _suppress = false;
    }
}
