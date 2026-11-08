namespace SysDiag.Models;

/// <summary>着色器缓存位置（系统级，与具体游戏无关）。</summary>
public sealed class ShaderCacheItem
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string Vendor { get; init; } = "";
    public int FileCount { get; init; }
    public long Size { get; init; }
    public bool Selected { get; set; }

    public string SizeText => Size >= 1024 * 1024
        ? $"{Size / 1024.0 / 1024.0:0.0} MB"
        : $"{Size / 1024.0:0} KB";

    public string CountText => $"{FileCount} 个文件";
}

/// <summary>VC++ 运行库状态。</summary>
public sealed class VcRuntimeInfo
{
    public string Arch { get; init; } = "";
    public string Version { get; init; } = "";
    public bool Installed { get; init; }

    public string StateText => Installed ? "已安装" : "未安装";

    public string Detail => Installed ? $"{Arch} · v{Version}" : $"{Arch} · 缺失";
}

/// <summary>当前游戏图形设置的两项开关。</summary>
public sealed class GameGraphicsState
{
    public string ExePath { get; init; } = "";

    public bool GpuHighPerformance { get; init; }
    public string GpuRaw { get; init; } = "";

    public bool FullscreenOptDisabled { get; init; }
    public string CompatRaw { get; init; } = "";

    public bool ExeFound => !string.IsNullOrEmpty(ExePath);

    public string GpuText => GpuHighPerformance ? "已设为高性能" : "未设置";
    public string FullscreenText => FullscreenOptDisabled ? "已勾选" : "未勾选";
}

/// <summary>一键优化里的单步结果。</summary>
public sealed class OptimizeStepResult
{
    public string Name { get; init; } = "";
    public bool Ok { get; init; }
    public string Message { get; init; } = "";
    public bool Skipped { get; init; }

    public string Line => (Skipped ? "跳过" : Ok ? "完成" : "失败") + " · " + Name +
                          (string.IsNullOrEmpty(Message) ? "" : "：" + Message);
}
