using System.Collections.Generic;

namespace SysDiag.Models;

/// <summary>条目所属的功能域。用来把「延迟优化」「系统优化」「免重装精简」的边界写死。</summary>
public enum NoteModule
{
    /// <summary>延迟优化：改的是「时序」——中断、调度、计时器、唤醒。判据见 <see cref="ModuleBoundary"/>。</summary>
    Latency,

    /// <summary>系统优化：改的是「行为」——服务、组件、隐私、界面、资源占用。</summary>
    System,

    /// <summary>免重装精简：AtlasOS 那类依赖镜像/重装的操作，在运行时可做的那部分。</summary>
    Debloat
}

/// <summary>
/// 证据强度。故意做成必填：社区里「听说有效」的推论太多，
/// 不标清是实测、是官方文档、还是社区经验，就等于把猜测当结论。
/// </summary>
public enum NoteConfidence
{
    /// <summary>官方文档或 Windows 自身行为可查，不依赖第三方结论。</summary>
    Documented,

    /// <summary>多个独立开源项目一致采用，且能指出具体键与语义。</summary>
    MultiSource,

    /// <summary>社区常见做法，效果依赖硬件/版本，本软件未做受控实测。</summary>
    Community,

    /// <summary>本软件在本机实测过，有数据。</summary>
    Measured
}

/// <summary>
/// 证据规模。和「证据等级」是两个维度：
/// 等级回答「这条结论是怎么来的」，规模回答「它能推广到多远」。
///
/// 比如 RigPolice 的停核数据是**实测**（等级高），但只在 i9-14900F + Win11 一台机器上做过
/// （规模小）——两条信息缺一条都会让人误用结论。
/// </summary>
public enum NoteScope
{
    /// <summary>讲的是机制或规范，不依赖测量，例如「MSI 减少中断次数」的电气原理。</summary>
    Mechanism,

    /// <summary>单机单配置的实测数据。方向可参考，绝对值不能外推。</summary>
    SingleMachine,

    /// <summary>多个来源或多台机器一致，可以当作通用结论。</summary>
    MultiSource
}

/// <summary>
/// 一条可复用的优化推论。
///
/// 这不是「操作项」——操作项在 TweakCatalog / LatencyService 里。这里只放**结论**，
/// 目的是让用户看懂「为什么这么改」「什么情况下不成立」，而不是照着开关盲点。
/// </summary>
public sealed class OptimizationNote
{
    public string Id { get; init; } = "";
    public NoteModule Module { get; init; }
    public string Title { get; init; } = "";

    /// <summary>结论本身，一句话说清。</summary>
    public string Conclusion { get; init; } = "";

    /// <summary>前提：这条结论在什么条件下才成立。空串表示无额外前提。</summary>
    public string Premise { get; init; } = "";

    /// <summary>依据：来自哪个仓库/文档/实测，越具体越好。</summary>
    public string Basis { get; init; } = "";

    /// <summary>适用场景：什么人、什么硬件、什么负载下值得做。</summary>
    public string Scope { get; init; } = "";

    /// <summary>限制：什么情况下不成立、有什么代价、别怎么用。</summary>
    public string Limits { get; init; } = "";

    public NoteConfidence Confidence { get; init; } = NoteConfidence.Community;

    /// <summary>证据规模：机制 / 单机实测 / 多源一致。</summary>
    public NoteScope EvidenceScope { get; init; } = NoteScope.Mechanism;

    /// <summary>
    /// 规模的具体出处，例如「i9-14900F / Win11 单机」「三份独立文档一致」。
    /// 空串表示这条不基于测量，此时界面上不显示规模标签。
    /// </summary>
    public string ScopeNote { get; init; } = "";

    /// <summary>来源仓库或文档，界面直接列出来。</summary>
    public List<string> Sources { get; init; } = new();

    /// <summary>关联到哪个窗口/卡片，便于用户跳过去。</summary>
    public string RelatedCard { get; init; } = "";

    public string ModuleText => Module switch
    {
        NoteModule.Latency => "延迟优化",
        NoteModule.System => "系统优化",
        NoteModule.Debloat => "免重装精简",
        _ => "其他"
    };

    public string ConfidenceText => Confidence switch
    {
        NoteConfidence.Documented => "官方文档",
        NoteConfidence.MultiSource => "多源一致",
        NoteConfidence.Community => "社区经验",
        NoteConfidence.Measured => "本机实测",
        _ => "未标注"
    };

    /// <summary>规模标签。机制类不显示（它本来就不适合用「规模」讨论）。</summary>
    public string ScopeText => EvidenceScope switch
    {
        NoteScope.SingleMachine => ScopeNote.Length > 0 ? "单机实测：" + ScopeNote : "单机实测",
        NoteScope.MultiSource => ScopeNote.Length > 0 ? "多源一致：" + ScopeNote : "多源一致",
        _ => ""
    };
}

/// <summary>
/// 三类功能域的判据。写在代码里而不是只写在文档里，
/// 是为了让界面能直接引用同一套说法，避免文档和界面各说一套。
/// </summary>
public static class ModuleBoundary
{
    public const string LatencyCriterion =
        "改的是「时序」：中断投递、线程调度、计时器分辨率、设备唤醒。判据是——改动会改变「事件发生到被处理」的时间分布，而不只是改变功能有无。";

    public const string SystemCriterion =
        "改的是「行为」：服务是否运行、组件是否存在、隐私是否上传、界面怎么显示、常驻资源占多少。判据是——改动影响「系统做什么」，不直接承诺时序变化。";

    public const string DebloatCriterion =
        "改的是「系统里有什么」：预装应用、可选功能、计划任务。AtlasOS 把这些做在镜像里（要重装），这里只做运行时能安全撤销的那部分。";

    /// <summary>容易归错类的典型例子，直接在界面里点出来。</summary>
    public static readonly string[] AmbiguousCases =
    {
        "「关闭 SysMain」：常被当延迟优化，其实是系统优化——它省的是内存与磁盘后台活动，时序影响测不出来。",
        "「关闭 Game Bar / GameDVR」：两者都有——录制组件占用属系统优化，但它引起的帧时间抖动属延迟优化。本软件归到延迟优化，因为可测的是抖动。",
        "「CPU 停核 / C-State」：属延迟优化。它改变的是核心被唤醒的时序，代价（功耗、温度）算在限制里，不影响归类。",
        "「减少遥测服务」：纯系统优化。它对延迟没有可验证的影响，别拿它当降延迟手段。"
    };
}
