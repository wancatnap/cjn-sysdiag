using System;

namespace SysDiag.Models;

/// <summary>
/// 手动调色参数：gamma / 亮度 / 对比度 / RGB 增益。
///
/// 设计约定（很重要，决定结果可预期）：
///
/// 1. **生成的是绝对 LUT，不是叠加**。所有参数都有「中性值」
///    （gamma=1、亮度=0、对比度=1、增益=1），中性值下一共得到一条完全线性的表。
///    参数相同 → 结果一定相同，不会因为先后顺序漂移。
/// 2. **运算在编码空间做**，不先解码到线性光。理由：这样滑块的观感符合直觉
///    （gamma 调大就是暗部变亮），而且和绝大多数调色工具的行为一致。
///
///    代价：这不是色彩学意义上的精确校准，不含色域映射。它改的是显卡 LUT，
///    效果类似显示器 OSD 里的 gamma/对比度，看得见、能还原，但不是校色。
/// 3. **顺序固定**：gamma → 对比度 → 亮度 → 通道增益。
///    顺序换了结果会差一点，这里写死，避免以后改代码时悄悄变了观感。
/// </summary>
public sealed class ManualTune
{
    // ---------------- 取值范围（UI 与钳制共用） ----------------

    public const double GammaMin = 0.40;
    public const double GammaMax = 3.00;

    public const double BrightnessMin = -50;
    public const double BrightnessMax = 50;

    public const double ContrastMin = 0.50;
    public const double ContrastMax = 2.00;

    public const double GainMin = 0.50;
    public const double GainMax = 2.00;

    /// <summary>中间调：&gt;1 提亮暗部，&lt;1 压暗。中性 1.0。</summary>
    public double Gamma { get; set; } = 1.0;

    /// <summary>整体偏移（百分比）。正值抬黑位，负值压黑位。中性 0。</summary>
    public double Brightness { get; set; }

    /// <summary>以中灰为轴的对比度。中性 1.0。</summary>
    public double Contrast { get; set; } = 1.0;

    /// <summary>红通道增益。中性 1.0。</summary>
    public double GainR { get; set; } = 1.0;

    public double GainG { get; set; } = 1.0;

    public double GainB { get; set; } = 1.0;

    /// <summary>全部处于中性值（等价于线性 LUT）。</summary>
    public bool IsNeutral =>
        Math.Abs(Gamma - 1.0) < 0.005
        && Math.Abs(Brightness) < 0.05
        && Math.Abs(Contrast - 1.0) < 0.005
        && Math.Abs(GainR - 1.0) < 0.005
        && Math.Abs(GainG - 1.0) < 0.005
        && Math.Abs(GainB - 1.0) < 0.005;

    public ManualTune Clone() => new()
    {
        Gamma = Gamma,
        Brightness = Brightness,
        Contrast = Contrast,
        GainR = GainR,
        GainG = GainG,
        GainB = GainB
    };

    /// <summary>复位到中性值。</summary>
    public void Reset()
    {
        Gamma = 1.0;
        Brightness = 0;
        Contrast = 1.0;
        GainR = 1.0;
        GainG = 1.0;
        GainB = 1.0;
    }

    /// <summary>把越界值拉回允许区间（UI 拖到边界、读旧配置文件时都会用到）。</summary>
    public ManualTune Clamped() => new()
    {
        Gamma = Math.Clamp(Gamma, GammaMin, GammaMax),
        Brightness = Math.Clamp(Brightness, BrightnessMin, BrightnessMax),
        Contrast = Math.Clamp(Contrast, ContrastMin, ContrastMax),
        GainR = Math.Clamp(GainR, GainMin, GainMax),
        GainG = Math.Clamp(GainG, GainMin, GainMax),
        GainB = Math.Clamp(GainB, GainMin, GainMax)
    };

    /// <summary>生成 3×256 的显卡 LUT（R、G、B 各 256 项，0..65535）。</summary>
    public ushort[] BuildRamp()
    {
        var tune = Clamped();
        var ramp = new ushort[768];

        var gamma = tune.Gamma;
        var contrast = tune.Contrast;
        var brightness = tune.Brightness / 100.0;
        var gains = new[] { tune.GainR, tune.GainG, tune.GainB };

        for (var channel = 0; channel < 3; channel++)
        {
            var gain = gains[channel];

            for (var i = 0; i < 256; i++)
            {
                var x = i / 255.0;

                // 1) 中间调：gamma 越大越亮（等价于显示器 gamma 变小）
                x = Math.Pow(x, 1.0 / gamma);

                // 2) 对比度：以中灰为轴拉开或压紧
                x = (x - 0.5) * contrast + 0.5;

                // 3) 亮度：整体偏移，抬黑位能让暗部看清，代价是纯黑变灰
                x += brightness;

                // 4) 通道增益：调白平衡 / 冷暖
                x *= gain;

                ramp[channel * 256 + i] = (ushort)Math.Clamp(Math.Round(x * 65535), 0, 65535);
            }
        }

        return ramp;
    }

    /// <summary>LUT 相对线性的最大偏差（百分比）。用来判断「调了会不会看得出来」。</summary>
    public double MaxDeltaPercent()
    {
        var ramp = BuildRamp();

        var max = 0;
        for (var i = 0; i < 256; i++)
        {
            var linear = i * 257;
            for (var channel = 0; channel < 3; channel++)
                max = Math.Max(max, Math.Abs(ramp[channel * 256 + i] - linear));
        }

        return max * 100.0 / 65535;
    }

    /// <summary>一句话描述当前设置，用于列表与导出文件名。</summary>
    public string Summary()
    {
        if (IsNeutral) return "中性（无调整）";

        var parts = new List<string>();

        if (Math.Abs(Gamma - 1.0) >= 0.005) parts.Add($"gamma {Gamma:0.00}");
        if (Math.Abs(Contrast - 1.0) >= 0.005) parts.Add($"对比度 {Contrast:0.00}");
        if (Math.Abs(Brightness) >= 0.05) parts.Add($"亮度 {(Brightness > 0 ? "+" : "")}{Brightness:0}%");
        if (Math.Abs(GainR - 1.0) >= 0.005) parts.Add($"R {GainR:0.00}");
        if (Math.Abs(GainG - 1.0) >= 0.005) parts.Add($"G {GainG:0.00}");
        if (Math.Abs(GainB - 1.0) >= 0.005) parts.Add($"B {GainB:0.00}");

        return string.Join(" · ", parts);
    }
}

/// <summary>一个保存下来的调色方案（参数 + 名字 + 时间）。</summary>
public sealed class ManualTunePreset
{
    public string Name { get; set; } = "";
    public double Gamma { get; set; } = 1.0;
    public double Brightness { get; set; }
    public double Contrast { get; set; } = 1.0;
    public double GainR { get; set; } = 1.0;
    public double GainG { get; set; } = 1.0;
    public double GainB { get; set; } = 1.0;
    public string Time { get; set; } = "";

    public ManualTune ToTune() => new()
    {
        Gamma = Gamma,
        Brightness = Brightness,
        Contrast = Contrast,
        GainR = GainR,
        GainG = GainG,
        GainB = GainB
    };

    public static ManualTunePreset From(string name, ManualTune tune) => new()
    {
        Name = name,
        Gamma = tune.Gamma,
        Brightness = tune.Brightness,
        Contrast = tune.Contrast,
        GainR = tune.GainR,
        GainG = tune.GainG,
        GainB = tune.GainB,
        Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
    };

    public string Detail => ToTune().Summary();
}
