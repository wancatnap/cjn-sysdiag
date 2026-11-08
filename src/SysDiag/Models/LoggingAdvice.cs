namespace SysDiag.Models;

/// <summary>
/// 一条「取证日志建议」：某类日志/审计该不该开、现在是什么状态、怎么开。
///
/// 清单依据的是 Yamato Security 的 <c>EnableWindowsLogSettings</c>（Hayabusa 作者的
/// 「取证到底该开哪些日志」文档）里最常被点名的几项，只保留我们能在本机核实状态的。
/// 只读：这里不做任何开关操作，能一键开的项在窗口上半部分。
/// </summary>
public sealed class LoggingAdvice
{
    public string Name { get; init; } = "";

    /// <summary>分类：容量 / 审计策略 / 通道 / 组件。</summary>
    public string Category { get; init; } = "";

    /// <summary>当前状态（人话，带数值）。</summary>
    public string Current { get; init; } = "";

    /// <summary>建议。</summary>
    public string Advice { get; init; } = "";

    /// <summary>怎么开（命令行或路径）。</summary>
    public string HowTo { get; init; } = "";

    /// <summary>ok = 已满足；hint = 建议开启；warn = 关键项缺失。</summary>
    public string Level { get; init; } = "hint";

    public string LevelText => Level switch
    {
        "ok" => "已满足",
        "warn" => "缺失",
        _ => "建议开启"
    };

    public string LevelBrush => Palette.LevelChipHex(Level);
}
