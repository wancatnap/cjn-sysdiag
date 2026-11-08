namespace SysDiag.Models;

/// <summary>DLSS 检测结果：显卡、驱动、游戏目录与 DLSS 组件版本。</summary>
public sealed class DlssEnvironment
{
    public string GpuName { get; init; } = "";
    public string DriverVersion { get; init; } = "";
    public string GameDir { get; init; } = "";
    public string GameExe { get; init; } = "";
    public string DlssPath { get; init; } = "";
    public string DlssVersion { get; init; } = "";
    public string DlssGVersion { get; init; } = "";

    public bool GameFound => !string.IsNullOrEmpty(GameDir);
    public bool DlssFound => !string.IsNullOrEmpty(DlssPath);

    public string GameText => GameFound ? GameDir : "未找到（可手动选择目录）";
    public string DlssText => DlssFound
        ? $"nvngx_dlss.dll {DlssVersion}"
        : "游戏目录里没找到 nvngx_dlss.dll";

    public string DlssGText => string.IsNullOrEmpty(DlssGVersion)
        ? ""
        : $"帧生成 nvngx_dlssg.dll {DlssGVersion}";
}

/// <summary>DLSS 预设选项（值取自 NVIDIA DRS 定义）。</summary>
public sealed record DlssOption(int Value, string Label, string Hint = "");

/// <summary>一套 DLSS 配置选择。</summary>
public sealed class DlssChoice
{
    public int QualityLevel { get; set; } = 1;
    public int PresetLetter { get; set; } = 11;
    public int DllOverride { get; set; } = 1;
    public string PresetMode { get; set; } = "均衡";

    public DlssChoice Clone() => new()
    {
        QualityLevel = QualityLevel,
        PresetLetter = PresetLetter,
        DllOverride = DllOverride,
        PresetMode = PresetMode
    };
}
