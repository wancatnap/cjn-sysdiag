using System.Windows;
using SysDiag.Controls;

namespace SysDiag.Models;

/// <summary>
/// 一个「软件内工具页」的描述：数据怎么采、KPI 显示什么、能对选中行做什么操作。
///
/// 设备管理器 / 服务 / 电源计划 / 可靠性监视器 / 程序和功能 / 任务管理器 /
/// 网络连接 / 防火墙 / 磁盘管理 这九张卡片原本都是往外丢 msc / cpl / exe，
/// 现在统一交给 <see cref="SysDiag.ToolListWindow"/> 渲染——那套页头、KPI、
/// 统一表格（搜索 / 等级筛选 / 排序）本来就是全软件共用的，没必要写九遍。
/// </summary>
public sealed class ToolPage
{
    /// <summary>卡片 target，与 CardCatalog 对应，便于自检反查。</summary>
    public string Key { get; set; } = "";

    public string Title { get; set; } = "";

    public string Badge { get; set; } = "";

    public string Subtitle { get; set; } = "";

    /// <summary>底栏说明：讲清「哪些能在这里做、哪些必须回系统工具」。</summary>
    public string Note { get; set; } = "";

    /// <summary>底栏说明的动态版本：刷新后重新求值（例如把当天统计写进去）。为 null 用 Note。</summary>
    public Func<string> NoteProvider { get; set; }

    /// <summary>打开子窗口（由 MainWindow 注入，保证子窗口与主窗口同一套标题栏行为）。</summary>
    public Action<Window> OpenChild { get; set; }

    /// <summary>采集结果为空的提示（不是错误，例如「没有发现异常设备」）。</summary>
    public string EmptyText { get; set; } = "没有可显示的内容。";

    /// <summary>提权重启时带回来的启动参数（如 --open-devices）。</summary>
    public string LaunchArg { get; set; } = "";

    /// <summary>表格选项（紧凑模式 / 是否显示时间柱状图 / 默认排序）。</summary>
    public LogViewOptions Options { get; set; } = new();

    /// <summary>采集：返回要展示的行。每次点「刷新」都会重新调。</summary>
    public Func<List<LogRow>> Load { get; set; } = () => new List<LogRow>();

    /// <summary>KPI 取值（最多 4 个）。为 null 则不显示 KPI 条。</summary>
    public Func<List<ToolKpi>> Kpis { get; set; }

    /// <summary>工具条按钮（对「选中行」生效）。</summary>
    public List<ToolAction> Actions { get; } = new();

    /// <summary>右键菜单里的动作（不常用的那些）。</summary>
    public List<ToolAction> RowActions { get; } = new();

    /// <summary>
    /// 选中行的稳定键：自动刷新后用它把「刚才选中的那条」重新定位回来。
    /// 为空时用「标题 + 依据」兜底。不这么做的话，2 秒一次的刷新会把选中行清掉，
    /// 用户点开详情条、正要按按钮时它自己消失。
    /// </summary>
    public Func<LogRow, string> SelectionKey { get; set; }

    /// <summary>自动刷新间隔（秒），0 表示不刷新。任务管理器用 2 秒。</summary>
    public int AutoRefreshSeconds { get; set; }

    /// <summary>是否需要管理员才有完整能力（不给权限时按钮置灰并说明原因）。</summary>
    public bool NeedsAdmin { get; set; }

    /// <summary>无法在软件内完成的操作：给一个按钮跳回系统原生工具。</summary>
    public string NativeFallbackLabel { get; set; } = "";

    public Action<Window> NativeFallback { get; set; }

    /// <summary>最近一次采集的行（报告与自检复用）。</summary>
    public List<LogRow> Rows { get; set; } = new();

    /// <summary>把当前行按 Tag 分组写进报告；没有自定义说明时用通用文字。</summary>
    public Func<ToolPage, ReportPayload> Report { get; set; }
}

/// <summary>KPI 一格：数值 + 名称 + 语义色（Palette 里的 hex）。</summary>
public sealed record ToolKpi(string Value, string Label, string Color = "");

/// <summary>一个可执行动作：工具条按钮或右键菜单项。</summary>
public sealed class ToolAction
{
    public string Label { get; set; } = "";

    public string Tip { get; set; } = "";

    /// <summary>工具条主按钮样式（每个页面最多一个）。</summary>
    public bool Primary { get; set; }

    /// <summary>必须先在表格里选中一行才能点。</summary>
    public bool NeedsSelection { get; set; } = true;

    /// <summary>需要管理员权限；非管理员时置灰并提示提权。</summary>
    public bool NeedsAdmin { get; set; }

    public Func<ToolActionContext, ToolActionResult> Run { get; set; } = _ => new ToolActionResult(true, "");
}

/// <summary>动作执行环境：需要弹对话框时用 Owner，需要知道选了哪几行时用 Selection。</summary>
public sealed class ToolActionContext
{
    public Window Owner { get; init; }

    public IReadOnlyList<LogRow> Selection { get; init; } = Array.Empty<LogRow>();

    /// <summary>选中的第一条（单选动作常用）。可能为 null。</summary>
    public LogRow First => Selection.Count > 0 ? Selection[0] : null;
}

/// <summary>动作执行结果：Ok 决定提示图标，Reload 决定是否刷新列表。</summary>
public sealed record ToolActionResult(bool Ok, string Message, bool Reload = true)
{
    public static ToolActionResult Fail(string message) => new(false, message);
}
