namespace SysDiag.Models;

/// <summary>
/// 日志查询的时间区间。**查询、显示、报告都从同一个实例取边界**，避免三处各算一遍导致不一致。
/// 构造时统一做边界处理：起止颠倒自动对调、未来截到当前、跨度过长截断、零长度判无效。
/// </summary>
public sealed class TimeRange
{
    /// <summary>跨度上限：超过就截断（避免误读整本日志把内存顶爆）。</summary>
    public static readonly TimeSpan MaxSpan = TimeSpan.FromDays(366);

    /// <summary>区间起点（含）。不限时间时为 DateTime.MinValue。</summary>
    public DateTime From { get; private set; }

    /// <summary>区间终点（含）。</summary>
    public DateTime To { get; private set; }

    /// <summary>是否「不限时间」。</summary>
    public bool IsAll { get; private set; }

    /// <summary>是否为用户自定义区间。</summary>
    public bool IsCustom { get; private set; }

    /// <summary>规范化的调整说明（对调/截断/截到当前），空串表示没有调整。</summary>
    public string Adjustment { get; private set; } = "";

    /// <summary>区间是否可用（自定义且 From ≥ To 时为 false）。</summary>
    public bool IsValid => IsAll || From < To;

    /// <summary>覆盖的整点小时数（含首尾，显示用）。选同一小时的起止也至少算 1。</summary>
    public int Hours => IsAll ? 0 : Math.Max(1, (int)Math.Ceiling((To - From).TotalHours - 0.0001));

    /// <summary>统一的时间显示文案：查询、状态栏、报告都用它。</summary>
    public string Label => IsAll
        ? "不限时间（全部可用记录）"
        : $"{From:MM-dd HH}:00 — {To:MM-dd HH}:59（{Hours} 个整点）";

    /// <summary>更完整的区间描述（含年份，报告用）。</summary>
    public string Describe() => IsAll
        ? "不限时间"
        : $"{From:yyyy-MM-dd HH:mm} — {To:yyyy-MM-dd HH:mm}（{Hours} 个整点）";

    /// <summary>直接给查询用的上下限。</summary>
    public (DateTime From, DateTime To) Bounds() => (From, To);

    private TimeRange() { }

    /// <summary>不限时间。</summary>
    public static TimeRange All() => new()
    {
        From = DateTime.MinValue,
        To = DateTime.Now,
        IsAll = true
    };

    /// <summary>最近 N 个整点（含当前这一小时）。预设与自定义用同一套整点语义。</summary>
    public static TimeRange LastHours(int hours)
    {
        var now = DateTime.Now;
        var thisHour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
        var to = thisHour.AddHours(1).AddTicks(-1);

        // N 个整点 = 从 (当前整点 - (N-1) 小时) 开始，到当前整点末
        var from = thisHour.AddHours(-(Math.Max(1, hours) - 1));

        return new TimeRange { From = from, To = to };
    }

    /// <summary>
    /// 按「小时」构造自定义区间：起点取该小时的 00 分，终点取结束小时的 59 分 59 秒（含整点那一小时）。
    /// </summary>
    public static TimeRange Custom(DateTime fromHour, DateTime toHour)
    {
        var from = new DateTime(fromHour.Year, fromHour.Month, fromHour.Day, fromHour.Hour, 0, 0);
        var to = new DateTime(toHour.Year, toHour.Month, toHour.Day, toHour.Hour, 59, 59, 999);

        var notes = new List<string>();

        // 1) 起止颠倒：自动对调（比直接报错更省事，且用户能看懂提示）
        if (from > to)
        {
            (from, to) = (new DateTime(to.Year, to.Month, to.Day, to.Hour, 0, 0),
                          new DateTime(from.Year, from.Month, from.Day, from.Hour, 59, 59, 999));
            notes.Add("起止时间颠倒，已自动对调");
        }

        var now = DateTime.Now;

        // 2) 未来时间：终点截到当前（未来没有日志可读）
        if (from > now)
        {
            // 整段都在未来：退化成最近 1 小时，并明确说明
            return new TimeRange
            {
                From = now.AddHours(-1),
                To = now,
                IsCustom = true,
                Adjustment = "所选区间在未来，已改为最近 1 小时"
            };
        }

        if (to > now)
        {
            to = now;
            notes.Add("结束时间超过当前时刻，已截到当前");
        }

        // 3) 跨度过长：截断到上限
        if (to - from > MaxSpan)
        {
            from = to - MaxSpan;
            notes.Add($"区间超过 {MaxSpan.TotalDays:0} 天，已截断为最近 {MaxSpan.TotalDays:0} 天");
        }

        // 4) 零长度（同一小时且被截断到同一秒）：判为无效，交由界提示
        return new TimeRange
        {
            From = from,
            To = to,
            IsCustom = true,
            Adjustment = string.Join("；", notes)
        };
    }

    /// <summary>把区间写回小时（用于「起止框」回填，避免秒级偏差）。</summary>
    public static DateTime HourOf(DateTime time) =>
        new(time.Year, time.Month, time.Day, time.Hour, 0, 0);
}
