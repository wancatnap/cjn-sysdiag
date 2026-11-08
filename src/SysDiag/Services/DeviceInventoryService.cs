using Microsoft.Win32;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 设备清单：把「设备管理器」那一棵树变成可搜索、可出报告的列表。
///
/// 数据来源是 <c>Win32_PnPEntity</c>（设备与状态）+ <c>Win32_PnPSignedDriver</c>（驱动版本）。
/// 不调用 devmgmt.msc —— 精简镜像常把管理控制台组件删掉，点卡片只会弹「找不到文件」。
/// </summary>
public static class DeviceInventoryService
{
    /// <summary>设备类别中文名。键是 Control\Class 下的英文类名（系统里就是英文）。</summary>
    private static readonly Dictionary<string, string> ClassNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Display adapters"] = "显示适配器",
        ["Network adapters"] = "网络适配器",
        ["Sound, video and game controllers"] = "声音、视频和游戏控制器",
        ["Keyboards"] = "键盘",
        ["Mice and other pointing devices"] = "鼠标和其他指针设备",
        ["Monitors"] = "监视器",
        ["Disk drives"] = "磁盘驱动器",
        ["DVD/CD-ROM drives"] = "DVD/CD-ROM 驱动器",
        ["USB controllers"] = "USB 控制器",
        ["USB devices"] = "USB 设备",
        ["Human Interface Devices"] = "人体学输入设备",
        ["Processors"] = "处理器",
        ["System devices"] = "系统设备",
        ["Computer"] = "计算机",
        ["Firmware"] = "固件",
        ["Storage controllers"] = "存储控制器",
        ["Storage volumes"] = "存储卷",
        ["IDE ATA/ATAPI controllers"] = "IDE ATA/ATAPI 控制器",
        ["Bluetooth"] = "蓝牙",
        ["Batteries"] = "电池",
        ["Ports (COM & LPT)"] = "端口（COM 和 LPT）",
        ["Print queues"] = "打印队列",
        ["Printers"] = "打印机",
        ["Cameras"] = "摄像头",
        ["Imaging devices"] = "图像设备",
        ["Security devices"] = "安全设备",
        ["Software devices"] = "软件设备",
        ["Audio inputs and outputs"] = "音频输入和输出",
        ["Biometric devices"] = "生物识别设备",
        ["Smart card readers"] = "智能卡读取器",
        ["Modems"] = "调制解调器",
        ["IEEE 1394 controllers"] = "IEEE 1394 控制器",
        ["PCMCIA adapters"] = "PCMCIA 适配器",
        ["Other devices"] = "其他设备",
        ["Unknown"] = "未知设备"
    };

    /// <summary>常见问题代码的中文解释（与设备管理器属性页的说法一致）。</summary>
    private static readonly Dictionary<int, string> ProblemCodes = new()
    {
        [1] = "设备未正确配置",
        [3] = "驱动程序内存不足",
        [10] = "设备无法启动",
        [12] = "资源不足，无法启动",
        [14] = "需要重启计算机才能生效",
        [18] = "需要重新安装驱动程序",
        [19] = "注册表配置损坏",
        [21] = "系统正在移除该设备",
        [22] = "已被禁用",
        [24] = "设备不存在、工作不正常或缺少驱动",
        [28] = "驱动程序未安装",
        [29] = "固件未提供所需资源",
        [31] = "该设备无法正常工作",
        [32] = "驱动程序启动失败",
        [37] = "驱动无法初始化",
        [39] = "驱动程序损坏或缺失",
        [43] = "设备报告故障后已停止",
        [45] = "当前未连接"
    };

    /// <summary>采全部设备。耗时主要花在 Win32_PnPSignedDriver（本机约 1~3 秒）。</summary>
    public static List<DeviceEntry> Scan()
    {
        var list = new List<DeviceEntry>();

        var entities = EvidenceService.Wmi(
            "SELECT Name, DeviceID, Status, ConfigManagerErrorCode, Manufacturer, Service, PNPClass " +
            "FROM Win32_PnPEntity");

        var drivers = ReadDrivers();

        foreach (var d in entities)
        {
            var id = EvidenceService.Str(d, "DeviceID");
            if (id.Length == 0) continue;

            var code = int.TryParse(EvidenceService.Str(d, "ConfigManagerErrorCode"), out var c) ? c : 0;

            drivers.TryGetValue(id, out var driver);

            list.Add(new DeviceEntry
            {
                Name = CleanName(EvidenceService.Str(d, "Name")),
                DeviceId = id,
                Category = CategoryOf(EvidenceService.Str(d, "PNPClass"), id),
                PnpClass = EvidenceService.Str(d, "PNPClass"),
                Manufacturer = EvidenceService.Str(d, "Manufacturer"),
                ErrorCode = code,
                Status = EvidenceService.Str(d, "Status"),
                ProblemText = code == 0 ? "" : ProblemCodes.TryGetValue(code, out var text) ? text : $"问题代码 {code}",
                DriverVersion = driver.Version,
                DriverProvider = driver.Provider,
                DriverDate = driver.Date,
                InfName = driver.Inf,
                ServiceName = EvidenceService.Str(d, "Service")
            });
        }

        return list
            .OrderByDescending(d => d.HasProblem)
            .ThenByDescending(d => d.IsDisabled)
            .ThenBy(d => d.Category)
            .ThenBy(d => d.Name)
            .ToList();
    }

    /// <summary>设备 ID → 驱动信息。一次查询全表，避免每台设备单独查。</summary>
    private static Dictionary<string, (string Version, string Provider, string Date, string Inf)> ReadDrivers()
    {
        var map = new Dictionary<string, (string, string, string, string)>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var rows = EvidenceService.Wmi(
                "SELECT DeviceID, DriverVersion, DriverProviderName, DriverDate, InfName " +
                "FROM Win32_PnPSignedDriver");

            foreach (var r in rows)
            {
                var id = EvidenceService.Str(r, "DeviceID");
                if (id.Length == 0 || map.ContainsKey(id)) continue;

                map[id] = (
                    EvidenceService.Str(r, "DriverVersion"),
                    EvidenceService.Str(r, "DriverProviderName"),
                    EvidenceService.WmiDate(EvidenceService.Str(r, "DriverDate")),
                    EvidenceService.Str(r, "InfName"));
            }
        }
        catch
        {
            // 驱动表读不到不影响设备清单：版本列留空即可
        }

        return map;
    }

    /// <summary>
    /// 类别中文名：优先用 PNPClass 的映射，其次查 Control\Class\{GUID} 的默认值，
    /// 最后按设备 ID 前缀兜底（USB\ / PCI\ / HID\ / SWD\）。
    /// </summary>
    private static string CategoryOf(string pnpClass, string deviceId)
    {
        if (pnpClass.Length > 0)
        {
            var byPnp = pnpClass switch
            {
                "Display" => "显示适配器",
                "Net" => "网络适配器",
                "MEDIA" => "声音、视频和游戏控制器",
                "Keyboard" => "键盘",
                "Mouse" => "鼠标和其他指针设备",
                "Monitor" => "监视器",
                "DiskDrive" => "磁盘驱动器",
                "CDROM" => "DVD/CD-ROM 驱动器",
                "USB" => "USB 控制器",
                "HIDClass" => "人体学输入设备",
                "Processor" => "处理器",
                "System" => "系统设备",
                "Computer" => "计算机",
                "Firmware" => "固件",
                "SCSIAdapter" => "存储控制器",
                "Volume" => "存储卷",
                "hdc" => "IDE ATA/ATAPI 控制器",
                "Bluetooth" => "蓝牙",
                "Battery" => "电池",
                "Ports" => "端口（COM 和 LPT）",
                "Printer" => "打印队列",
                "Camera" => "摄像头",
                "Image" => "图像设备",
                "SecurityDevices" => "安全设备",
                "SoftwareDevice" => "软件设备",
                "AudioEndpoint" => "音频输入和输出",
                "Biometric" => "生物识别设备",
                "SmartCardReader" => "智能卡读取器",
                "Modem" => "调制解调器",
                "1394" => "IEEE 1394 控制器",
                "PCMCIA" => "PCMCIA 适配器",
                _ => ""
            };

            if (byPnp.Length > 0) return byPnp;

            if (ClassNames.TryGetValue(pnpClass, out var friendly)) return friendly;

            return pnpClass;
        }

        var prefix = deviceId.Split('\\')[0].ToUpperInvariant();

        return prefix switch
        {
            "USBSTOR" => "USB 存储设备",
            "USB" => "USB 设备",
            "HID" => "人体学输入设备",
            "PCI" => "PCI 设备",
            "ACPI" => "ACPI 设备",
            "SWD" => "软件设备",
            "ROOT" => "根设备",
            _ => "其他设备"
        };
    }

    /// <summary>WMI 里有些设备名为空（驱动没装），给一个可读的替代名。</summary>
    private static string CleanName(string name)
    {
        if (name.Length > 0) return name.Trim();
        return "未命名设备（缺少驱动）";
    }

    /// <summary>统计：故障 / 禁用 / 未装驱动。</summary>
    public static (int Total, int Problem, int Disabled, int NoDriver) Count(List<DeviceEntry> devices)
        => (devices.Count,
            devices.Count(d => d.HasProblem),
            devices.Count(d => d.IsDisabled),
            devices.Count(d => d.ErrorCode == 28));
}
