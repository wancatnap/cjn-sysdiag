using System;
using System.Collections.Generic;

namespace SysDiag.Models;

/// <summary>一台显示器（对应用户看到的一个显示输出 + 一块屏）。</summary>
public sealed class IccMonitor
{
    /// <summary>GDI 设备名，例如 \\.\DISPLAY5。</summary>
    public string DeviceName { get; init; } = "";

    /// <summary>显示器实例号（注册表关联键的最后一段，例如 0002）。</summary>
    public string Instance { get; init; } = "";

    /// <summary>设备 ID，例如 MONITOR\HKC2441\{4d36e96e-…}\0002。</summary>
    public string DeviceId { get; init; } = "";

    /// <summary>给人看的名字，优先取 EDID 里的型号，取不到用设备 ID 的中段。</summary>
    public string Name { get; init; } = "";

    /// <summary>当前生效的 ICC 文件名（GDI 读出来的）。</summary>
    public string CurrentProfile { get; init; } = "";

    public string CurrentPath { get; init; } = "";

    /// <summary>是否在用户级关联里被改过（UsePerUserProfiles = 1）。</summary>
    public bool PerUserActive { get; init; }

    public string Display => Name.Length > 0 ? $"{Name}　·　{DeviceName}" : DeviceName;

    public string CurrentName => CurrentProfile.Length > 0 ? CurrentProfile : "（没有关联配置）";
}

/// <summary>一个可用的 ICC 配置文件。</summary>
public sealed class IccProfile
{
    public string FullPath { get; init; } = "";
    public string FileName { get; init; } = "";

    /// <summary>ICC 里写的描述，取不到就用文件名。</summary>
    public string Description { get; init; } = "";

    /// <summary>色彩空间，例如 RGB / GRAY / CMYK。</summary>
    public string ColorSpace { get; init; } = "";

    /// <summary>版本，例如 2.2 / 4.3。</summary>
    public string Version { get; init; } = "";

    /// <summary>是否装在系统 color 目录（其它程序也看得到）。</summary>
    public bool IsSystem { get; init; }

    /// <summary>矩阵/曲线型（可做近似预览）还是 LUT 型。</summary>
    public bool IsMatrix { get; init; }

    /// <summary>灰阶 gamma（TRC 只有一个值时的 u8Fixed8），没有则为 0。</summary>
    public double Gamma { get; init; }

    /// <summary>文件大小，给人看的。</summary>
    public string SizeText { get; init; } = "";

    /// <summary>
    /// 是否含校准表（vcgt）。**只有含这个，套上去画面才真的会变**；
    /// 只有色彩描述的文件，桌面和游戏根本不读，观感不会变。
    /// </summary>
    public bool HasVcgt { get; init; }

    /// <summary>校准表说明（表格型 / 公式型 / 无）。</summary>
    public string VcgtText { get; init; } = "";

    /// <summary>校准表相对线性的最大偏差（百分比）。太小说明曲线本来就平，看不出差别。</summary>
    public double VcgtDelta { get; init; }

    /// <summary>一句话说清「套了会怎样」。</summary>
    public string EffectText => HasVcgt
        ? VcgtDelta >= 1.5
            ? $"含校准表 · 偏差 {VcgtDelta:F0}%"
            : $"含校准表 · 偏差 {VcgtDelta:F1}%（很轻微）"
        : "仅色彩描述 · 画面不变";

    /// <summary>来源标签：系统 / 本程序导入。</summary>
    public string SourceText => IsSystem ? "系统目录" : "本程序";

    /// <summary>近似预览：只按 TRC 曲线换算亮度，不做色域映射。</summary>
    public (byte R, byte G, byte B) Simulate(byte r, byte g, byte b)
    {
        if (!IsMatrix || Gamma <= 0) return (r, g, b);

        // sRGB 的分段线性编码 → 与配置的 gamma 对比，得到「看起来会亮/暗多少」
        double ratio = 2.2 / Gamma;

        return (Apply(r), Apply(g), Apply(b));

        byte Apply(byte value)
        {
            var v = value / 255.0;
            var linear = v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            var coded = Math.Pow(Math.Max(linear, 0), 1.0 / ratio);
            return (byte)Math.Clamp(Math.Round(coded * 255), 0, 255);
        }
    }
}
