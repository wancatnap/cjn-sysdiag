namespace SysDiag.Models;

/// <summary>
/// 统一日志行模型：所有「发现 / 记录 / 事件」类列表都映射成它，
/// 再交给 Controls.LogGridView 展示，全软件共用一套搜索 / 筛选 / 排序 / 时间柱状图。
/// </summary>
public sealed class LogRow
{
    /// <summary>主标题（粗体）。</summary>
    public string Title { get; set; } = "";

    /// <summary>详情（第二行，可换行）。</summary>
    public string Detail { get; set; } = "";

    /// <summary>依据 / 辅助（等宽小字）。</summary>
    public string Basis { get; set; } = "";

    /// <summary>徽章文字（如「腾讯 ACE」「临时目录」），空串不显示。</summary>
    public string Tag { get; set; } = "";

    /// <summary>等级：warn / hint / ok / known / none。</summary>
    public string Level { get; set; } = "none";

    /// <summary>等级显示文本（高危 / 提示 / 正常 / 可信…）。</summary>
    public string LevelText { get; set; } = "";

    /// <summary>时间显示文本；无时间留空。</summary>
    public string TimeText { get; set; } = "";

    /// <summary>时间值，排序与时间柱状图用；无时间传 null。</summary>
    public DateTime? Time { get; set; }

    /// <summary>数值列（如运行次数、句柄权限掩码文本），紧凑模式显示。</summary>
    public string MetricText { get; set; } = "";

    /// <summary>原始数据对象，供窗口自己回查。</summary>
    public object Payload { get; set; }

    /// <summary>左侧色条颜色：走 Palette 语义表，改色不用动这里。</summary>
    public string LevelBrush => Level == "none" ? "Transparent" : Palette.LevelHex(Level);

    /// <summary>等级文字颜色。</summary>
    public string LevelChipBrush => Palette.LevelChipHex(Level);

    /// <summary>搜索索引：全部可见字段的小写拼接。</summary>
    private string? _searchIndex;

    /// <summary>
    /// 搜索索引：第一次用到时拼一次并缓存。
    /// 原实现每次访问都拼 7 个字段 + 转小写——搜索框每敲一个字，3000 行就是 3000 次重新拼接。
    /// LogRow 的字段在填入表格后不再变动，所以缓存安全。
    /// </summary>
    public string SearchIndex => _searchIndex ??= string.Join('\n',
        Title, Detail, Basis, Tag, LevelText, TimeText, MetricText).ToLowerInvariant();

    /// <summary>徽章是否可见（XAML 绑定用）。</summary>
    public bool TagVis => Tag.Length > 0;

    /// <summary>有无时间（决定时间柱状图是否可用）。</summary>
    public bool HasTime => Time.HasValue;
}

/// <summary>等级的显示顺序：高危最前，无等级最后。</summary>
public static class LogRowOrder
{
    public static int Rank(string level) => level switch
    {
        "warn" => 0,
        "hint" => 1,
        "ok" => 2,
        "known" => 3,
        _ => 4
    };
}
