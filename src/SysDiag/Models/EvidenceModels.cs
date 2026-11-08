using System.IO;

namespace SysDiag.Models;

/// <summary>机器码（HWID）里的一个字段。</summary>
public sealed class HwidField
{
    public string Group { get; set; } = "";
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>驱动安装 / 设备接入事件（来自 setupapi.dev.log）。</summary>
public sealed class DeviceEvent
{
    public DateTime? Time { get; set; }
    public string Action { get; set; } = "";
    public string Target { get; set; } = "";
    public string Caller { get; set; } = "";
    public string Status { get; set; } = "";

    public string TimeText => Time?.ToString("yyyy-MM-dd HH:mm:ss") ?? "";

    public string StatusCss => Status.Contains("FAIL", StringComparison.OrdinalIgnoreCase)
        ? "t-bad"
        : "t-ok";
}

/// <summary>注册表里记录的 USB 设备。</summary>
public sealed class UsbDeviceInfo
{
    public string Name { get; set; } = "";
    public string VidPid { get; set; } = "";
    public string Instance { get; set; } = "";
    public string Serial { get; set; } = "";
    public string Service { get; set; } = "";
    public string Location { get; set; } = "";
    public string Note { get; set; } = "";
    public string ContainerId { get; set; } = "";

    public string NoteCss => Note.Length > 0 ? "t-warn" : "t-ok";
}

/// <summary>PnP 配置事件的一行。</summary>
public sealed class PnpEventRow
{
    public DateTime Time { get; set; }
    public int Id { get; set; }
    public string Level { get; set; } = "";
    public string Device { get; set; } = "";
    public string Message { get; set; } = "";

    public string TimeText => Time.ToString("yyyy-MM-dd HH:mm:ss");

    public string LevelCss => Level switch
    {
        "错误" => "t-bad",
        "警告" => "t-warn",
        _ => "t-bin"
    };
}

/// <summary>日志记录开关的一行。</summary>
public sealed class LoggingSwitch
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Detail { get; set; } = "";
    public bool Enabled { get; set; }
    public bool NeedsAdmin { get; set; }

    /// <summary>「最近使用」被组策略/权限锁住时，只有「开启」需要管理员。</summary>
    public bool PolicyLocked { get; set; }

    /// <summary>界面上的目标状态，初始等于当前状态。</summary>
    public bool Wanted { get; set; }

    /// <summary>按目标方向判断这次改动是否需要管理员。</summary>
    public bool RequiresAdmin(bool wanted) => Key == "recent" ? wanted && PolicyLocked : NeedsAdmin;

    public string StateText => Enabled ? "已开启" : "已关闭";
    public string StateCss => Enabled ? "t-ok" : "t-warn";
    public string AdminText => NeedsAdmin ? "需管理员" : "直接生效";

    public System.Windows.Media.Brush StateBrush => (Enabled ? Palette.Ok : Palette.HintChip);
    public System.Windows.Media.Brush StateBg => (Enabled ? Palette.OkSoft : Palette.WarnSoft);
    public System.Windows.Media.Brush AdminBrush => (NeedsAdmin ? Palette.None : Palette.Ok);
}

/// <summary>一次硬件快照（比对基线）。</summary>
public sealed class DeviceSnapshot
{
    public DateTime SavedAt { get; set; }
    public string Computer { get; set; } = "";
    public string Os { get; set; } = "";
    public List<HwidField> Fields { get; set; } = new();
    public List<UsbDeviceInfo> Usb { get; set; } = new();
}

/// <summary>基线与当前状态的差异。</summary>
public sealed class BaselineDiff
{
    public DeviceSnapshot Baseline { get; set; } = new();
    public List<(string Key, string Old, string New)> Changed { get; } = new();
    public List<(string Key, string Value)> Added { get; } = new();
    public List<(string Key, string Value)> Removed { get; } = new();
    public List<string> UsbAdded { get; } = new();
    public List<string> UsbRemoved { get; } = new();
    public int SameCount { get; set; }

    public int TotalChanges => Changed.Count + Added.Count + Removed.Count + UsbAdded.Count + UsbRemoved.Count;
}

