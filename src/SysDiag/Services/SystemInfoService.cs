using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;
using Microsoft.Win32;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 系统信息采集 + 精简系统检测（全部只读，不依赖任何外部工具）。
///
/// <para><b>为什么要自己采</b>：原来这张卡片直接调 <c>msinfo32.exe</c>，两个问题——
/// 精简镜像往往把 msinfo32 一起删了（点了没反应，用户以为程序坏了），
/// 而且弹出去的是系统窗口，信息带不走、也没法判断「这系统是不是被动过」。</para>
///
/// <para><b>精简系统怎么判</b>：不看"感觉"，只看「正常安装的 Windows 不可能缺的东西」。
/// 判断依据分三档权重：</para>
/// <list type="bullet">
/// <item><b>3 分（强证据）</b>：Defender 组件、Windows 更新服务、TrustedInstaller、组件存储包、
///       WinRE 恢复环境、DriverStore、事件日志目录——精简镜像做的第一件事就是删这些；</item>
/// <item><b>2 分（中证据）</b>：SysWOW64、PowerShell、应用商店、System32 文件数量明显少、镜像部署脚本残留；</item>
/// <item><b>1 分（弱证据）</b>：dxdiag/resmon 等单个工具缺失、unattend.xml 残留、版本号与产品名矛盾。</item>
/// </list>
///
/// <para>结论规则：命中 2 个以上强证据、或总分 ≥ 6 → 疑似精简；有缺失但没到线 → 零星缺失；
/// 大部分项读不到（权限不足）→ 不下结论。每个判断都带「缺了什么、在哪个路径」。</para>
/// </summary>
public static class SystemInfoService
{
    // =========================================================
    // 采集
    // =========================================================

    public static SystemInfoResult Collect(IProgress<string> progress = null)
    {
        var sw = Stopwatch.StartNew();
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        void Step(string text) => progress?.Report(text);

        var groups = new List<InfoGroup>();

        Step("读取操作系统信息…");
        groups.Add(CollectOs(win));

        Step("读取处理器与内存…");
        groups.Add(CollectCpu());
        groups.Add(CollectMemory());

        Step("读取品牌与注册信息…");
        groups.Add(CollectBrand());

        Step("读取主板与固件…");
        groups.Add(CollectBoard());

        Step("读取显示适配器…");
        groups.Add(CollectGpu());

        Step("读取磁盘与分区…");
        groups.Add(CollectDisk());

        Step("读取网络适配器…");
        groups.Add(CollectNetwork());

        Step("读取运行环境…");
        groups.Add(CollectRuntime(win));

        Step("检测系统完整性…");
        var checks = DetectTrimmed(win);
        var score = checks.Where(c => c.Missing).Sum(c => c.Weight);
        var strong = checks.Count(c => c.Missing && c.Weight >= 3);
        var checkedCount = checks.Count(c => !c.Skipped);

        var verdict = checkedCount < 6
            ? TrimVerdict.Unknown
            : strong >= 2 || score >= 6
                ? TrimVerdict.Trimmed
                : checks.Any(c => c.Missing) ? TrimVerdict.MinorMissing : TrimVerdict.Complete;

        return new SystemInfoResult
        {
            Groups = groups,
            TrimChecks = checks,
            TrimScore = score,
            Verdict = verdict,
            Admin = ElevationService.IsAdmin,
            ElapsedText = $"{sw.ElapsedMilliseconds / 1000.0:0.0} 秒"
        };
    }

    // ---------------- 操作系统 ----------------

    private static InfoGroup CollectOs(string win)
    {
        var items = new List<InfoItem>();

        var product = RegStr(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName");
        var edition = RegStr(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "EditionID");
        var display = RegStr(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion");
        var build = RegStr(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuild");
        var ubr = RegInt(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR");
        var install = RegInt(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "InstallDate");

        items.Add(Text("产品名称", product, "注册表 CurrentVersion\\ProductName 读不到"));

        if (display.Length > 0)
            items.Add(Text("版本号", display, ""));

        var buildText = build.Length > 0
            ? $"Build {build}{(ubr > 0 ? "." + ubr : "")}"
            : Environment.OSVersion.Version.ToString();

        // 产品名与 build 矛盾时标"可疑"：精简镜像常把产品名留在 10 而 build 是 11
        var mismatch = build.Length > 0 && product.Length > 0 &&
                       (product.Contains("Windows 10", StringComparison.OrdinalIgnoreCase) && int.TryParse(build, out var b) && b >= 22000);

        items.Add(new InfoItem
        {
            Name = "内部版本",
            Value = buildText,
            State = mismatch ? InfoState.Suspect : InfoState.Ok,
            Note = mismatch ? "产品名写的是 Windows 10，但内部版本属于 Windows 11" : ""
        });

        items.Add(Text("版本类型", edition, "注册表 CurrentVersion\\EditionID 读不到"));

        items.Add(install > 0
            ? Text("安装日期", DateTimeOffset.FromUnixTimeSeconds(install).LocalDateTime.ToString("yyyy-MM-dd HH:mm"), "")
            : new InfoItem { Name = "安装日期", State = InfoState.Unavailable, Note = "注册表没有 InstallDate" });

        var boot = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
        items.Add(Text("上次启动", boot.ToString("yyyy-MM-dd HH:mm"), ""));

        items.Add(Text("系统目录", win, ""));
        items.Add(Text("系统架构", System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(), ""));
        items.Add(Text("计算机名", Environment.MachineName, ""));
        items.Add(Text("当前用户", $"{Environment.UserDomainName}\\{Environment.UserName}", ""));

        return new InfoGroup { Name = "操作系统", Items = items };
    }

    // ---------------- 处理器 ----------------

    private static InfoGroup CollectCpu()
    {
        var items = new List<InfoItem>();

        try
        {
            var cpu = EvidenceService.Wmi("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, ProcessorId FROM Win32_Processor")
                .FirstOrDefault();

            if (cpu == null)
            {
                items.Add(new InfoItem { Name = "处理器", State = InfoState.Unavailable, Note = "WMI 查询 Win32_Processor 没有返回数据" });
                return new InfoGroup { Name = "处理器", Items = items };
            }

            items.Add(Text("型号", EvidenceService.Str(cpu, "Name"), ""));
            items.Add(Text("核心 / 线程",
                $"{EvidenceService.Str(cpu, "NumberOfCores")} 核 / {EvidenceService.Str(cpu, "NumberOfLogicalProcessors")} 线程", ""));

            var clock = EvidenceService.Str(cpu, "MaxClockSpeed");
            items.Add(long.TryParse(clock, out var mhz) && mhz > 0
                ? Text("基准频率", $"{mhz} MHz", "")
                : new InfoItem { Name = "基准频率", State = InfoState.Unavailable, Note = "WMI 没返回 MaxClockSpeed" });

            items.Add(Text("ProcessorId", EvidenceService.Str(cpu, "ProcessorId"), "WMI 没返回 ProcessorId"));
        }
        catch (Exception ex)
        {
            items.Add(new InfoItem { Name = "处理器", State = InfoState.Unavailable, Note = "WMI 查询失败：" + ex.Message });
        }

        return new InfoGroup { Name = "处理器", Items = items };
    }

    // ---------------- 内存 ----------------

    private static InfoGroup CollectMemory()
    {
        var items = new List<InfoItem>();

        try
        {
            var osList = EvidenceService.Wmi("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
            var os = osList.FirstOrDefault();

            var totalKb = long.TryParse(EvidenceService.Str(os, "TotalVisibleMemorySize"), out var t) ? t : 0;
            var freeKb = long.TryParse(EvidenceService.Str(os, "FreePhysicalMemory"), out var f) ? f : 0;

            if (totalKb > 0)
            {
                items.Add(Text("总容量", $"{totalKb / 1024.0 / 1024:0.#} GB", ""));
                items.Add(Text("可用", $"{freeKb / 1024.0 / 1024:0.#} GB（{freeKb * 100.0 / totalKb:0.#}%）", ""));
            }
            else
            {
                items.Add(new InfoItem { Name = "总容量", State = InfoState.Unavailable, Note = "WMI 查询 Win32_OperatingSystem 没有返回内存大小" });
            }

            var sticks = EvidenceService.Wmi("SELECT Capacity, Speed, Manufacturer, PartNumber FROM Win32_PhysicalMemory");
            if (sticks.Count == 0)
            {
                items.Add(new InfoItem { Name = "内存条", State = InfoState.Unavailable, Note = "读不到 Win32_PhysicalMemory（部分精简镜像删了这条 WMI 类）" });
            }
            else
            {
                var totalGb = sticks
                    .Select(s => long.TryParse(EvidenceService.Str(s, "Capacity"), out var c) ? c : 0)
                    .Sum() / 1024.0 / 1024 / 1024;

                items.Add(Text("内存条", $"{sticks.Count} 条 / 合计 {totalGb:0.#} GB", ""));

                for (var i = 0; i < sticks.Count; i++)
                {
                    var s = sticks[i];
                    var cap = long.TryParse(EvidenceService.Str(s, "Capacity"), out var c) ? c / 1024.0 / 1024 / 1024 : 0;
                    var speed = EvidenceService.Str(s, "Speed");
                    var maker = EvidenceService.Str(s, "Manufacturer");

                    items.Add(Text($"第 {i + 1} 条",
                        $"{cap:0.#} GB{(speed.Length > 0 ? $" · {speed} MHz" : "")}{(maker.Length > 0 ? $" · {maker}" : "")}",
                        ""));
                }
            }
        }
        catch (Exception ex)
        {
            items.Add(new InfoItem { Name = "内存", State = InfoState.Unavailable, Note = "WMI 查询失败：" + ex.Message });
        }

        return new InfoGroup { Name = "内存", Items = items };
    }

    // ---------------- 品牌与注册信息 ----------------

    /// <summary>
    /// 「这台机器叫什么名字」的几个落点。
    /// 精简/魔改镜像最爱改这三处：ProductName（winver 与设置-关于显示的就是它）、
    /// RegisteredOrganization、OEMInformation（系统属性里的「制造商/型号」）。
    /// </summary>
    private static InfoGroup CollectBrand()
    {
        var items = new List<InfoItem>();

        items.Add(Text("产品名称（winver 显示）", RegStr(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName"),
            "注册表 CurrentVersion\\ProductName 读不到"));

        items.Add(Text("版本类型（EditionID）", RegStr(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "EditionID"), ""));

        var owner = RegStr(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "RegisteredOwner");
        items.Add(Text("注册给（用户）", owner, "没有 RegisteredOwner（未激活或未填写）"));

        var org = RegStr(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "RegisteredOrganization");
        items.Add(org.Length > 0
            ? Text("注册给（组织）", org, "")
            : new InfoItem { Name = "注册给（组织）", State = InfoState.Unavailable, Note = "没有 RegisteredOrganization（正常装机通常为空）" });

        items.Add(Text("内部版本标识（BuildLab）", RegStr(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "BuildLab"),
            "没有 BuildLab"));

        var oemMaker = RegStr(@"SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation", "Manufacturer");
        var oemModel = RegStr(@"SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation", "Model");
        var oemUrl = RegStr(@"SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation", "SupportURL");

        items.Add(oemMaker.Length > 0
            ? Text("OEM 制造商", oemMaker, "")
            : new InfoItem { Name = "OEM 制造商", State = InfoState.Unavailable, Note = "没有 OEMInformation（DIY 机器正常为空）" });

        items.Add(oemModel.Length > 0
            ? Text("OEM 型号", oemModel, "")
            : new InfoItem { Name = "OEM 型号", State = InfoState.Unavailable, Note = "没有 OEMInformation" });

        items.Add(oemUrl.Length > 0
            ? Text("OEM 支持链接", oemUrl, "")
            : new InfoItem { Name = "OEM 支持链接", State = InfoState.Unavailable, Note = "没有 OEMInformation" });

        var notice = RegStr(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "LegalNoticeCaption");
        items.Add(notice.Length > 0
            ? new InfoItem
            {
                Name = "登录前提示标题",
                Value = notice,
                State = InfoState.Suspect,
                Note = "正常系统不会设置它，常见于魔改/企业镜像"
            }
            : new InfoItem { Name = "登录前提示标题", State = InfoState.Unavailable, Note = "未设置（正常）" });

        return new InfoGroup { Name = "品牌与注册信息", Items = items };
    }

    // ---------------- 主板与固件 ----------------

    private static InfoGroup CollectBoard()
    {
        var items = new List<InfoItem>();

        try
        {
            var board = EvidenceService.Wmi("SELECT Manufacturer, Product, SerialNumber FROM Win32_BaseBoard").FirstOrDefault();
            items.Add(Text("主板厂商", EvidenceService.Str(board, "Manufacturer"), "WMI 没有返回主板厂商"));
            items.Add(Text("主板型号", EvidenceService.Str(board, "Product"), "WMI 没有返回主板型号"));
            items.Add(Text("主板序列号", EvidenceService.Str(board, "SerialNumber"), "WMI 没有返回主板序列号（部分主板未烧录）"));

            var bios = EvidenceService.Wmi("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS").FirstOrDefault();
            items.Add(Text("BIOS 厂商", EvidenceService.Str(bios, "Manufacturer"), ""));
            items.Add(Text("BIOS 版本", EvidenceService.Str(bios, "SMBIOSBIOSVersion"), ""));
            items.Add(Text("BIOS 日期", EvidenceService.WmiDate(EvidenceService.Str(bios, "ReleaseDate")), "WMI 没有返回 BIOS 日期"));

            var csp = EvidenceService.Wmi("SELECT UUID FROM Win32_ComputerSystemProduct").FirstOrDefault();
            items.Add(Text("SMBIOS UUID", EvidenceService.Str(csp, "UUID"), "WMI 没有返回 UUID"));
        }
        catch (Exception ex)
        {
            items.Add(new InfoItem { Name = "主板与固件", State = InfoState.Unavailable, Note = "WMI 查询失败：" + ex.Message });
        }

        return new InfoGroup { Name = "主板与固件", Items = items };
    }

    // ---------------- 显卡 ----------------

    private static InfoGroup CollectGpu()
    {
        var items = new List<InfoItem>();

        try
        {
            var gpus = EvidenceService.Wmi("SELECT Name, DriverVersion, AdapterRAM FROM Win32_VideoController");
            if (gpus.Count == 0)
            {
                items.Add(new InfoItem { Name = "显示适配器", State = InfoState.Unavailable, Note = "WMI 没有返回 Win32_VideoController" });
                return new InfoGroup { Name = "显示适配器", Items = items };
            }

            for (var i = 0; i < gpus.Count; i++)
            {
                var g = gpus[i];
                var name = EvidenceService.Str(g, "Name");
                var driver = EvidenceService.Str(g, "DriverVersion");

                var vram = "";
                if (long.TryParse(EvidenceService.Str(g, "AdapterRAM"), out var bytes) && bytes > 0)
                {
                    // AdapterRAM 是 uint32，>4GB 的卡会溢出成 4GB 附近，这里如实标注
                    vram = $" · 显存 {bytes / 1024.0 / 1024 / 1024:0.#} GB" + (bytes >= 4_200_000_000 ? "（WMI 上限，实际可能更大）" : "");
                }

                items.Add(Text(gpus.Count > 1 ? $"显卡 {i + 1}" : "显卡",
                    $"{name}{(driver.Length > 0 ? $" · 驱动 {driver}" : "")}{vram}", ""));
            }
        }
        catch (Exception ex)
        {
            items.Add(new InfoItem { Name = "显示适配器", State = InfoState.Unavailable, Note = "WMI 查询失败：" + ex.Message });
        }

        return new InfoGroup { Name = "显示适配器", Items = items };
    }

    // ---------------- 磁盘 ----------------

    private static InfoGroup CollectDisk()
    {
        var items = new List<InfoItem>();

        try
        {
            var disks = EvidenceService.Wmi("SELECT Index, Model, Size, InterfaceType FROM Win32_DiskDrive")
                .OrderBy(d => EvidenceService.Str(d, "Index"))
                .ToList();

            if (disks.Count == 0)
            {
                items.Add(new InfoItem { Name = "物理磁盘", State = InfoState.Unavailable, Note = "WMI 没有返回 Win32_DiskDrive" });
            }
            else
            {
                foreach (var d in disks)
                {
                    var index = EvidenceService.Str(d, "Index");
                    var model = EvidenceService.Str(d, "Model");
                    var iface = EvidenceService.Str(d, "InterfaceType");

                    var byteCount = long.TryParse(EvidenceService.Str(d, "Size"), out var sz) ? sz : 0;
                    var size = byteCount >= 1L << 40
                        ? $"{byteCount / 1024.0 / 1024 / 1024 / 1024:0.##} TB"
                        : $"{byteCount / 1024.0 / 1024 / 1024:0} GB";

                    items.Add(Text($"磁盘 {index}", $"{model} · {size}{(iface.Length > 0 ? " · " + iface : "")}", ""));
                }
            }

            var volumes = EvidenceService.Wmi("SELECT DeviceID, VolumeName, FileSystem, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType=3");
            foreach (var v in volumes)
            {
                var id = EvidenceService.Str(v, "DeviceID");
                var fs = EvidenceService.Str(v, "FileSystem");
                var name = EvidenceService.Str(v, "VolumeName");

                var size = long.TryParse(EvidenceService.Str(v, "Size"), out var s) ? s / 1024.0 / 1024 / 1024 : 0;
                var free = long.TryParse(EvidenceService.Str(v, "FreeSpace"), out var fr) ? fr / 1024.0 / 1024 / 1024 : 0;

                items.Add(Text($"卷 {id}",
                    $"{size:0.#} GB（剩余 {free:0.#} GB）{(fs.Length > 0 ? " · " + fs : "")}{(name.Length > 0 ? " · " + name : "")}", ""));
            }
        }
        catch (Exception ex)
        {
            items.Add(new InfoItem { Name = "磁盘", State = InfoState.Unavailable, Note = "WMI 查询失败：" + ex.Message });
        }

        return new InfoGroup { Name = "磁盘与卷", Items = items };
    }

    // ---------------- 网络 ----------------

    private static InfoGroup CollectNetwork()
    {
        var items = new List<InfoItem>();

        try
        {
            string[] virtualHints =
            {
                "Virtual", "Wi-Fi Direct", "Hyper-V", "Bluetooth", "VPN", "TAP-", "Npcap",
                "Loopback", "Tunnel", "VMware", "VirtualBox"
            };

            var nics = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.GetPhysicalAddress().GetAddressBytes().Length == 6)
                .Where(n => !virtualHints.Any(h => (n.Description ?? "").Contains(h, StringComparison.OrdinalIgnoreCase)))
                .Where(n => !virtualHints.Any(h => (n.Name ?? "").Contains(h, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(n => n.Name)
                .ToList();

            if (nics.Count == 0)
            {
                items.Add(new InfoItem { Name = "网络适配器", State = InfoState.Unavailable, Note = "没有找到物理网卡" });
                return new InfoGroup { Name = "网络", Items = items };
            }

            foreach (var nic in nics)
            {
                var mac = string.Join("-", nic.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));

                var speed = "";
                try
                {
                    if (nic.Speed > 0) speed = $" · {nic.Speed / 1_000_000} Mbps";
                }
                catch { /* 网卡未连接时可能抛异常 */ }

                items.Add(Text(nic.Name, $"{mac}{speed} · {nic.NetworkInterfaceType}", ""));
            }
        }
        catch (Exception ex)
        {
            items.Add(new InfoItem { Name = "网络", State = InfoState.Unavailable, Note = "枚举网卡失败：" + ex.Message });
        }

        return new InfoGroup { Name = "网络", Items = items };
    }

    // ---------------- 运行环境 ----------------

    private static InfoGroup CollectRuntime(string win)
    {
        var items = new List<InfoItem>();

        items.Add(Text(".NET 运行时", Environment.Version.ToString(), ""));

        var webview = "";
        try
        {
            webview = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString() ?? "";
        }
        catch { /* 没装 WebView2 运行时会抛 */ }

        items.Add(webview.Length > 0
            ? Text("WebView2 运行时", webview, "")
            : new InfoItem { Name = "WebView2 运行时", State = InfoState.Unavailable, Note = "未安装或安装损坏，报告窗口的内嵌预览会不可用" });

        var ps = Path.Combine(win, @"System32\WindowsPowerShell\v1.0\powershell.exe");
        items.Add(File.Exists(ps)
            ? Text("PowerShell", "已安装（System32\\WindowsPowerShell\\v1.0）", "")
            : new InfoItem { Name = "PowerShell", State = InfoState.Unavailable, Note = "System32\\WindowsPowerShell\\v1.0\\powershell.exe 不存在（精简镜像常见）" });

        var dx = RegStr(@"SOFTWARE\Microsoft\DirectX", "Version");
        items.Add(dx.Length > 0
            ? Text("DirectX", dx, "")
            : new InfoItem { Name = "DirectX", State = InfoState.Unavailable, Note = "注册表没有 DirectX 版本记录" });

        var sysDrive = Path.GetPathRoot(win) ?? "C:\\";
        try
        {
            var di = new DriveInfo(sysDrive);
            items.Add(Text("系统盘剩余", $"{di.AvailableFreeSpace / 1024.0 / 1024 / 1024:0.#} GB / {di.TotalSize / 1024.0 / 1024 / 1024:0.#} GB", ""));
        }
        catch
        {
            items.Add(new InfoItem { Name = "系统盘剩余", State = InfoState.Unavailable, Note = "读取磁盘容量失败" });
        }

        return new InfoGroup { Name = "运行环境", Items = items };
    }

    // =========================================================
    // 精简系统检测
    // =========================================================

    /// <summary>
    /// 只跑精简检查，不采集系统信息。
    ///
    /// 抽出来给 <see cref="TrimAwareness"/> 用：其他模块（防护审查、外挂体检、DMA 检测）
    /// 需要知道「这台机器是不是被精简过」，才能把「读不到」归因到正确原因，
    /// 而不是含糊地说「无法判断」。这些检查只读文件与注册表，不跑 WMI，开销很小。
    /// </summary>
    public static List<TrimCheck> DetectTrimmedOnly()
        => DetectTrimmed(Environment.GetFolderPath(Environment.SpecialFolder.Windows));

    private static List<TrimCheck> DetectTrimmed(string win)
    {
        var checks = new List<TrimCheck>();

        // ---- 3 分：正常 Windows 不可能缺的组件 ----

        var defenderDirs = new[]
        {
            @"C:\Program Files\Windows Defender",
            @"C:\ProgramData\Microsoft\Windows Defender"
        };

        checks.Add(CheckAnyDir("defender-dir", "Defender 组件目录", "安全", 3,
            "Windows Defender 的程序目录和平台目录都不存在。正常安装的 Windows 一定有，"
            + "精简镜像与部分「优化版」第一件事就是删它。",
            defenderDirs));

        checks.Add(CheckService("defender-svc", "WinDefend 服务", "安全", 3,
            "防病毒服务在注册表里根本没有登记（不是停止，是没有这个键）。"
            + "手动关闭 Defender 只会改 Start 值，不会删服务——删键是精简镜像的做法。",
            "WinDefend"));

        checks.Add(CheckService("wuauserv", "Windows Update 服务", "更新", 3,
            "Windows 更新服务注册项不存在。删了它系统就再也打不上补丁，这是精简版最典型的取舍。",
            "wuauserv"));

        checks.Add(CheckService("trustedinstaller", "TrustedInstaller 服务", "组件", 3,
            "Windows 模块安装服务不存在。没有它就无法安装/卸载系统更新和可选功能。",
            "TrustedInstaller"));

        checks.Add(CheckComponentPackages());

        checks.Add(CheckAnyDir("winsxs", "组件存储 WinSxS", "组件", 3,
            "C:\\Windows\\WinSxS 不存在。这个目录存放所有系统组件的副本，"
            + "精简镜像为了省几个 GB 会整个删掉，代价是无法再安装任何 Windows 功能。",
            new[] { Path.Combine(win, "WinSxS") }));

        var winre = Path.Combine(win, @"System32\Recovery\WinRE.wim");
        var recoveryDir = Path.Combine(win, @"System32\Recovery");
        checks.Add(new TrimCheck
        {
            Id = "winre",
            Name = "恢复环境（WinRE）",
            Category = "恢复",
            Weight = 3,
            Missing = !File.Exists(winre) && !Directory.Exists(recoveryDir),
            Detail = "WinRE.wim 与 Recovery 目录都不存在，系统坏了没有「修复计算机」可用。精简版常删它省 500MB~1GB。",
            Basis = @"C:\Windows\System32\Recovery\WinRE.wim"
        });

        checks.Add(CheckAnyDir("winevt", "事件日志目录", "日志", 3,
            "C:\\Windows\\System32\\winevt 不存在——事件日志被整体移除，出问题后没有任何记录可查。",
            new[] { Path.Combine(win, @"System32\winevt") }));

        checks.Add(CheckAnyDir("drivestore", "驱动仓库 DriverStore", "组件", 3,
            "C:\\Windows\\System32\\DriverStore 不存在。这里存放所有已安装驱动的原始副本，"
            + "删掉之后驱动无法回滚、新设备装不了。",
            new[] { Path.Combine(win, @"System32\DriverStore") }));

        // ---- 2 分：中证据 ----

        checks.Add(CheckAnyDir("wow64", "32 位子系统 SysWOW64", "组件", 2,
            "C:\\Windows\\SysWOW64 不存在，32 位程序将无法运行。部分极致精简镜像会删它。",
            new[] { Path.Combine(win, "SysWOW64") }));

        checks.Add(CheckAnyDir("store", "应用商店 WindowsApps", "应用", 2,
            "C:\\Program Files\\WindowsApps 目录不存在，UWP 应用与商店被整体移除。",
            new[] { @"C:\Program Files\WindowsApps" }));

        checks.Add(CheckSystem32Count(win));

        // tiny11builder 的 SelectedOptions 里明确会删的几项（edge / onedrive / search / print / ime），
        // 正好补上我这 17 项没覆盖的地方。见 README「借鉴」一节。
        checks.Add(CheckAnyDir("edge", "Edge 浏览器", "应用", 2,
            "Edge 程序目录不存在。普通系统不会删它（欧盟地区的系统现在允许卸载，所以这里只算 2 分而非 3 分）；"
            + "精简镜像则一律移除，顺带让依赖 WebView2 的组件一起失效。",
            new[]
            {
                @"C://Program Files (x86)//Microsoft//Edge//Application",
                @"C://Program Files//Microsoft//Edge//Application"
            }));

        checks.Add(CheckAnyDir("onedrive", "OneDrive 客户端", "应用", 1,
            "OneDrive 的程序目录与安装器都不在。用户自己卸载也会这样，所以只作弱信号。",
            new[]
            {
                @"C://Program Files//Microsoft OneDrive",
                @"C://Program Files (x86)//Microsoft OneDrive",
                Path.Combine(win, @"SysWOW64\OneDriveSetup.exe")
            }));

        checks.Add(CheckService("wsearch", "Windows Search 服务", "组件", 2,
            "搜索服务在注册表里没有登记项。关掉它的人很多，但「删掉服务」是 debloat 脚本的做法——"
            + "代价是开始菜单搜索、文件索引永久失效。",
            "WSearch"));

        checks.Add(CheckAnyDir("spool", "打印后台组件", "组件", 2,
            "C:////Windows////System32////spool 不存在，打印子系统被整体移除。"
            + "部分「游戏优化」脚本会删它来省内存，删了之后任何打印机都装不上。",
            new[] { Path.Combine(win, @"System32\spool") }));

        checks.Add(CheckAnyDir("ime", "中文输入法组件", "应用", 2,
            "C:////Windows////System32////IME 不存在，系统自带输入法被移除。"
            + "中文用户会立刻感觉到——只能自己装第三方输入法。精简镜像常把输入法文件当「无用组件」删掉。",
            new[] { Path.Combine(win, @"System32\IME") }));

        checks.Add(CheckSetupScript(win));

        // ---- 1 分：弱证据 ----

        checks.Add(CheckTools(win));

        checks.Add(CheckUnattend(win));

        checks.Add(CheckKnownTrimProjects());

        checks.Add(CheckVersionMismatch());

        checks.Add(CheckBrandName());
        checks.Add(CheckOemInformation());
        checks.Add(CheckRegisteredOrg());

        return checks;
    }

    /// <summary>一组路径全不存在才算缺失（有些组件在不同版本里位置不同）。</summary>
    private static TrimCheck CheckAnyDir(string id, string name, string category, int weight,
        string detailWhenMissing, string[] paths)
    {
        var missing = paths.All(p => !Directory.Exists(p) && !File.Exists(p));

        return new TrimCheck
        {
            Id = id,
            Name = name,
            Category = category,
            Weight = weight,
            Missing = missing,
            Detail = missing ? detailWhenMissing : "存在。",
            Basis = paths.FirstOrDefault() ?? ""
        };
    }

    private static TrimCheck CheckService(string id, string name, string category, int weight,
        string detailWhenMissing, string serviceName)
    {
        var exists = ServiceExists(serviceName);

        return new TrimCheck
        {
            Id = id,
            Name = name,
            Category = category,
            Weight = weight,
            Missing = !exists,
            Detail = exists ? "服务已登记。" : detailWhenMissing,
            Basis = $@"HKLM\SYSTEM\CurrentControlSet\Services\{serviceName}"
        };
    }

    private static bool ServiceExists(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
            return key != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>组件存储里的包数量：正常两千以上，精简镜像常常只剩几百甚至几十。</summary>
    private static TrimCheck CheckComponentPackages()
    {
        var count = -1;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\Packages");

            if (key != null) count = key.GetSubKeyNames().Length;
        }
        catch { /* 读不到就跳过 */ }

        if (count < 0)
        {
            return new TrimCheck
            {
                Id = "cbs-packages",
                Name = "组件包数量",
                Category = "组件",
                Skipped = true,
                Detail = "读不到 Component Based Servicing\\Packages 键，无法统计。",
                Basis = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\Packages"
            };
        }

        var missing = count < 300;

        return new TrimCheck
        {
            Id = "cbs-packages",
            Name = "组件包数量",
            Category = "组件",
            Weight = 3,
            Missing = missing,
            Detail = missing
                ? $"只剩 {count} 个组件包（正常安装通常 2000 个以上）。说明大量系统组件被卸载过，且往后基本装不回功能。"
                : $"{count} 个组件包，在正常范围内。",
            Basis = @"Component Based Servicing\Packages 子键数"
        };
    }

    /// <summary>System32 文件数量：正常约 4500~5500，明显偏少说明被整体裁剪过。</summary>
    private static TrimCheck CheckSystem32Count(string win)
    {
        var dir = Path.Combine(win, "System32");

        if (!Directory.Exists(dir))
        {
            return new TrimCheck
            {
                Id = "system32",
                Name = "System32 文件数量",
                Category = "组件",
                Weight = 3,
                Missing = true,
                Detail = "C:\\Windows\\System32 目录本身不存在——这不是精简，是系统已经损坏。",
                Basis = dir
            };
        }

        int count;
        try
        {
            count = Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly).Length;
        }
        catch (Exception ex)
        {
            return new TrimCheck
            {
                Id = "system32",
                Name = "System32 文件数量",
                Category = "组件",
                Skipped = true,
                Detail = "枚举 System32 失败：" + ex.Message,
                Basis = dir
            };
        }

        var missing = count < 3000;

        return new TrimCheck
        {
            Id = "system32",
            Name = "System32 文件数量",
            Category = "组件",
            Weight = 2,
            Missing = missing,
            Detail = missing
                ? $"顶层只有 {count} 个文件（正常 4500 以上），说明系统文件被批量删除过。"
                : $"{count} 个文件，在正常范围内。",
            Basis = dir
        };
    }

    /// <summary>镜像部署脚本残留：SetupComplete.cmd 是精简镜像跑删除脚本的经典位置。</summary>
    private static TrimCheck CheckSetupScript(string win)
    {
        var path = Path.Combine(win, @"Setup\Scripts\SetupComplete.cmd");
        var exists = File.Exists(path);

        return new TrimCheck
        {
            Id = "setup-script",
            Name = "镜像部署脚本残留",
            Category = "镜像来源",
            Weight = 2,
            Missing = exists,
            Detail = exists
                ? @"发现 C:\Windows\Setup\Scripts\SetupComplete.cmd。这个文件是系统装完后执行的脚本，"
                  + "正常安装会自己删掉；还留着说明是用定制镜像部署的，里面通常就是删除组件的命令。"
                : "没有 SetupComplete.cmd 残留。",
            Basis = path
        };
    }

    /// <summary>常用系统工具：单个缺失不算什么，批量缺失说明被裁过。</summary>
    private static TrimCheck CheckTools(string win)
    {
        var tools = new (string File, string Label)[]
        {
            (@"System32\msinfo32.exe", "系统信息"),
            (@"System32\dxdiag.exe", "DirectX 诊断"),
            (@"System32\resmon.exe", "资源监视器"),
            (@"System32\perfmon.exe", "性能监视器"),
            (@"System32\eventvwr.exe", "事件查看器"),
            (@"System32\cleanmgr.exe", "磁盘清理"),
            (@"System32\sfc.exe", "系统文件检查"),
            (@"System32\dism.exe", "DISM 部署工具"),
            (@"System32\mstsc.exe", "远程桌面"),
            (@"System32\notepad.exe", "记事本")
        };

        var missing = tools.Where(t => !File.Exists(Path.Combine(win, t.File))).Select(t => t.Label).ToList();

        return new TrimCheck
        {
            Id = "system-tools",
            Name = "常用系统工具",
            Category = "组件",
            Weight = 1,
            Missing = missing.Count > 0,
            Detail = missing.Count == 0
                ? "十件常用系统工具都在。"
                : $"缺少 {missing.Count}/{tools.Length} 件：{string.Join("、", missing)}。" +
                  (missing.Count >= 4 ? "批量缺失基本可以确定系统被裁剪过。" : "单个工具缺失也可能是手工删的，只作弱信号。"),
            Basis = @"C:\Windows\System32\*.exe"
        };
    }

    private static TrimCheck CheckUnattend(string win)
    {
        var path = Path.Combine(win, @"Panther\unattend.xml");
        var exists = File.Exists(path);
        var auto = File.Exists(Path.Combine(win, @"Panther\Unattend\unattend.xml"));

        return new TrimCheck
        {
            Id = "unattend",
            Name = "自动应答文件残留",
            Category = "镜像来源",
            Weight = 1,
            Missing = exists || auto,
            Detail = exists || auto
                ? "发现 unattend.xml，说明系统是应答文件自动部署的（自制镜像/common 于精简版），"
                  + "但 OEM 预装机型也会留，所以只作弱信号。"
                : "没有 unattend.xml 残留。",
            Basis = path
        };
    }

    /// <summary>产品名里出现非官方字样——winver 与设置-关于 直接显示的就是这个字符串。</summary>
    private static TrimCheck CheckBrandName()
    {
        var product = RegStr(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName");

        if (product.Length == 0)
        {
            return new TrimCheck
            {
                Id = "brand-name",
                Name = "产品名称是否被改名",
                Category = "品牌",
                Skipped = true,
                Detail = "读不到 ProductName。",
                Basis = @"CurrentVersion\ProductName"
            };
        }

        // 只认「官方不可能出现」的词。注意别把 LTSC / N / Enterprise / Workstations 这些官方版本算进去。
        string[] words =
        {
            "精简", "优化", "纯净", "极速", "装机", "工作室", "汉化", "美化", "定制", "魔改",
            "绿色", "免激活", "破解", "白菜", "萝卜", "雨林", "深度技术", "系统之家",
            "LITE", "TINY", "MINI", "MOD", "GHOST", "ATLAS", "REVI", "SPECTRE", "RYANOS", "XLST"
        };

        var hit = words.Where(w => product.Contains(w, StringComparison.OrdinalIgnoreCase)).ToList();

        // 官方名称都是「Windows 10/11 + 版本」，出现中文括号、竖线、emoji 之类基本就是被人改过
        var oddChars = product.Contains('【') || product.Contains('】') || product.Contains('|') || product.Contains('★');
        var tooLong = product.Length > 40;

        var missing = hit.Count > 0 || oddChars || tooLong;

        return new TrimCheck
        {
            Id = "brand-name",
            Name = "产品名称是否被改名",
            Category = "品牌",
            Weight = 3,
            Missing = missing,
            Detail = missing
                ? $"产品名称是「{product}」" +
                  (hit.Count > 0 ? $"（含非官方字样：{string.Join("、", hit)}）" : "（含特殊符号或长度异常）") +
                  "。这个字符串就是 winver 和「设置 → 系统 → 关于」里显示的系统名称——官方版本只会写「Windows 10/11 + 版本名」。"
                : $"产品名称「{product}」是官方写法。",
            Basis = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProductName"
        };
    }

    /// <summary>
    /// OEMInformation：系统属性里「制造商 / 型号 / 支持链接」的来源。
    /// 魔改镜像爱把作者名写进去，于是「型号」一栏显示的是系统名而不是机型。
    /// </summary>
    private static TrimCheck CheckOemInformation()
    {
        var maker = RegStr(@"SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation", "Manufacturer");
        var model = RegStr(@"SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation", "Model");
        var url = RegStr(@"SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation", "SupportURL");

        if (maker.Length == 0 && model.Length == 0 && url.Length == 0)
        {
            return new TrimCheck
            {
                Id = "oem-info",
                Name = "OEM 制造商与型号",
                Category = "品牌",
                Weight = 3,
                Missing = false,
                Detail = "没有 OEMInformation（DIY 机器正常为空，OEM 预装机才写）。",
                Basis = @"CurrentVersion\OEMInformation"
            };
        }

        // 与 WMI 报告的真实整机厂商比对：真 OEM 机的这两个值必然一致
        var real = "";
        try
        {
            real = EvidenceService.Str(
                EvidenceService.Wmi("SELECT Manufacturer FROM Win32_ComputerSystem").FirstOrDefault(), "Manufacturer");
        }
        catch { /* 读不到就不比对厂商，只看名称像不像系统名 */ }

        var makerMismatch = real.Length > 0 && maker.Length > 0 &&
                            !maker.Contains(real, StringComparison.OrdinalIgnoreCase) &&
                            !real.Contains(maker, StringComparison.OrdinalIgnoreCase);

        // 「型号」里出现 OS / 系统 / 版 / Free / v数字 这类字样，说明填的是系统名
        string[] modelWords = { "OS", "系统", "版", "FREE", "LITE", "MOD", "GHOST", "WIN" };
        var modelLooksLikeOs = model.Length > 0 &&
                               (modelWords.Any(w => model.Contains(w, StringComparison.OrdinalIgnoreCase)) ||
                                System.Text.RegularExpressions.Regex.IsMatch(model, @"v\d+\.\d+"));

        // 支持链接指向个人主页/网盘，基本只可能是改机作者留的
        string[] hostWords = { "bilibili", "weibo", "qq.com", "taobao", "lanzou", "123pan", "aliyundrive", "space.bilibili" };
        var urlLooksPersonal = url.Length > 0 && hostWords.Any(w => url.Contains(w, StringComparison.OrdinalIgnoreCase));

        var missing = makerMismatch || modelLooksLikeOs || urlLooksPersonal;

        var reasons = new List<string>();
        if (makerMismatch) reasons.Add($"制造商写「{maker}」，但 WMI 报告的真实厂商是「{real}」");
        if (modelLooksLikeOs) reasons.Add($"型号写的是「{model}」，看起来是系统名而不是机型");
        if (urlLooksPersonal) reasons.Add($"支持链接指向个人主页/网盘「{url}」");

        return new TrimCheck
        {
            Id = "oem-info",
            Name = "OEM 制造商与型号",
            Category = "品牌",
            Weight = 3,
            Missing = missing,
            Detail = missing
                ? string.Join("；", reasons) +
                  "。这三个值就是「系统属性」与「设置 → 系统 → 关于」里显示的制造商/型号/支持链接——" +
                  "真 OEM 机器写的是厂商自己的信息，这种写法师来自改机作者。"
                : $"OEM 信息与真机一致（制造商 {maker}，型号 {model}）。",
            Basis = @"CurrentVersion\OEMInformation"
        };
    }

    /// <summary>RegisteredOrganization 被写成系统名/作者名。</summary>
    private static TrimCheck CheckRegisteredOrg()
    {
        var org = RegStr(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "RegisteredOrganization");

        if (org.Length == 0)
        {
            return new TrimCheck
            {
                Id = "registered-org",
                Name = "注册组织名称",
                Category = "品牌",
                Weight = 2,
                Missing = false,
                Detail = "没有填写注册组织（正常装机通常为空）。",
                Basis = @"CurrentVersion\RegisteredOrganization"
            };
        }

        string[] words = { "OS", "系统", "版", "FREE", "LITE", "MOD", "GHOST", "精简", "优化", "纯净", "工作室" };
        var looksLikeOs = words.Any(w => org.Contains(w, StringComparison.OrdinalIgnoreCase)) ||
                          System.Text.RegularExpressions.Regex.IsMatch(org, @"v\d+\.\d+");

        return new TrimCheck
        {
            Id = "registered-org",
            Name = "注册组织名称",
            Category = "品牌",
            Weight = 2,
            Missing = looksLikeOs,
            Detail = looksLikeOs
                ? $"注册组织写的是「{org}」，不是公司名而是系统名/版本号。正常装机这里要么为空，要么是公司名。"
                : $"注册组织「{org}」，没有明显异常。",
            Basis = @"CurrentVersion\RegisteredOrganization"
        };
    }

    /// <summary>
    /// 产品名与内核版本矛盾：魔改/汉化镜像最典型的一处破绽。
    /// 正常安装的 Windows 11 会把 ProductName 写成「Windows 11」，从 Win10 升级上来也是一样；
    /// 只有拿 Win11 内核套旧版标识的定制镜像才会出现「Windows 10 + Build 226xx」。
    /// </summary>
    private static TrimCheck CheckVersionMismatch()
    {
        var product = RegStr(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName");
        var build = RegStr(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuild");

        if (product.Length == 0 || !int.TryParse(build, out var buildNo))
        {
            return new TrimCheck
            {
                Id = "version-mismatch",
                Name = "产品名与内核版本",
                Category = "版本",
                Skipped = true,
                Detail = "读不到 ProductName 或 CurrentBuild，无法比对。",
                Basis = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion"
            };
        }

        var mismatch = product.Contains("Windows 10", StringComparison.OrdinalIgnoreCase) && buildNo >= 22000;

        return new TrimCheck
        {
            Id = "version-mismatch",
            Name = "产品名与内核版本",
            Category = "版本",
            Weight = 2,
            Missing = mismatch,
            Detail = mismatch
                ? $"产品名写的是「{product}」，但内核 Build 是 {buildNo}（Windows 11 从 22000 起）。"
                  + "正常安装或从 Win10 升级上来的系统都会把产品名改成 Windows 11——"
                  + "这种组合说明用的是拿 Win11 内核套旧标识的定制镜像。"
                : $"产品名「{product}」与 Build {buildNo} 一致。",
            Basis = "ProductName vs CurrentBuild"
        };
    }

    /// <summary>已知精简项目留下的注册表标记。</summary>
    private static TrimCheck CheckKnownTrimProjects()
    {
        var hits = new List<string>();

        void Probe(string path, string label)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(path);
                if (key != null) hits.Add(label);
            }
            catch { /* 忽略 */ }
        }

        Probe(@"SOFTWARE\AtlasOS", "AtlasOS");
        Probe(@"SOFTWARE\AME", "AME（Atlas 的组件）");
        Probe(@"SOFTWARE\ReviOS", "ReviOS");

        return new TrimCheck
        {
            Id = "known-projects",
            Name = "已知精简项目痕迹",
            Category = "镜像来源",
            Weight = 3,
            Missing = hits.Count > 0,
            Detail = hits.Count > 0
                ? $"注册表里发现精简项目的标记：{string.Join("、", hits)}。"
                : "没有发现 AtlasOS / ReviOS 等精简项目的注册表标记。",
            Basis = @"HKLM\SOFTWARE\AtlasOS 等"
        };
    }

    // =========================================================
    // 报告
    // =========================================================

    public static ReportPayload BuildReport(SystemInfoResult result)
    {
        var doc = new HtmlDoc("系统信息",
            $"{Environment.MachineName}　·　{DateTime.Now:yyyy-MM-dd HH:mm:ss}　·　{result.VerdictText}");

        doc.Kpis(
            (result.VerdictText, "精简判定", result.VerdictColor),
            (result.TrimScore.ToString(), "缺失分", result.TrimScore > 0 ? Palette.WarnHex : Palette.OkHex),
            (result.MissingChecks.Count.ToString(), "缺失项", result.MissingChecks.Count > 0 ? Palette.HintHex : Palette.OkHex),
            (result.TotalItems.ToString(), "信息项", Palette.KnownHex));

        doc.Note($"<b>{HtmlDoc.E(result.Headline)}</b><br/>{HtmlDoc.E(result.VerdictDetail)}");

        doc.H2("精简系统检测");
        doc.Table(
            new[] { "检查项", "分类", "结论", "依据" },
            result.TrimChecks.Select(c => new[]
            {
                $"<b>{HtmlDoc.E(c.Name)}</b><br/><span class=\"muted\">{HtmlDoc.E(c.Detail)}</span>",
                HtmlDoc.E(c.Category),
                c.Skipped
                    ? "<span class=\"muted\">未检查</span>"
                    : c.Missing
                        ? c.Weight >= 3 ? "<span class=\"tag t-warn\">缺失</span>" : "<span class=\"tag t-hint\">有缺失</span>"
                        : "<span class=\"tag t-ok\">正常</span>",
                $"<span class=\"mono\">{HtmlDoc.E(c.Basis)}</span>"
            }),
            new[] { "", "70", "70", "230" });

        foreach (var g in result.Groups)
        {
            doc.H2(g.Name);
            doc.Table(
                new[] { "项", "值" },
                g.Items.Select(i => new[]
                {
                    HtmlDoc.E(i.Name),
                    i.State == InfoState.Ok
                        ? $"<span class=\"mono\">{HtmlDoc.E(i.Value)}</span>"
                        : $"<span class=\"muted\">{HtmlDoc.E(i.Display)}</span>"
                }),
                new[] { "180", "" });
        }

        doc.H2("说明");
        doc.Raw("<div class=\"note\">" +
                "1. <b>精简判定不依赖外部工具</b>：全部通过文件系统、注册表与 WMI 直接判断，任何一台机器打开就能跑。<br/>" +
                "2. <b>强证据（缺失分 3）</b>：Defender 组件、Windows 更新服务、TrustedInstaller、组件存储包、WinRE、DriverStore、事件日志目录——" +
                "正常安装的 Windows 不可能缺这些，删它们是精简镜像的核心动作。<br/>" +
                "3. <b>为什么不是「一缺就判精简」</b>：单个工具被手工删、企业定制镜像关掉 Defender、OEM 预装机型留 unattend.xml 都会造成零星缺失，" +
                "所以要求命中 2 个以上强证据、或总分 ≥ 6 才下「疑似精简」的结论。<br/>" +
                "4. <b>判断不了的情况</b>：权限不足导致大部分项读不到时直接给「无法判断」，不猜。" +
                "</div>");

        var html = doc.Build();
        var path = HtmlReportBuilder.Save(html, "系统信息");

        var data = new ReportData
        {
            Kind = "system-info",
            Title = "系统信息",
            Subtitle = $"{result.VerdictText} · 缺失 {result.MissingChecks.Count} 项 · {result.TotalItems} 项信息",
            GeneratedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            Tool = new ToolInfo(),
            Machine = new MachineInfo
            {
                Name = Environment.MachineName,
                User = Environment.UserName,
                Os = Environment.OSVersion.VersionString,
                Admin = result.Admin
            },
            Verdict = new VerdictInfo
            {
                Score = result.TrimScore,
                Level = result.Verdict switch
                {
                    TrimVerdict.Complete => "低",
                    TrimVerdict.MinorMissing => "中",
                    TrimVerdict.Trimmed => "高",
                    _ => "低"
                },
                Text = result.VerdictText,
                Counts = new ReportCounts
                {
                    Warn = result.StrongMissing,
                    Hint = result.WeakMissing,
                    Ok = result.CheckedCount - result.MissingChecks.Count
                }
            },
            Sections = result.Groups.Select(g => new ReportSection
            {
                Group = g.Name,
                Items = g.Items.Select(i => new ReportItem
                {
                    Title = i.Name,
                    Detail = i.Display,
                    Level = i.Level
                }).ToList()
            }).ToList()
        };

        return new ReportPayload
        {
            Title = "系统信息",
            Subtitle = $"{result.VerdictText} · 缺失 {result.MissingChecks.Count} 项",
            EngineTag = "系统信息",
            HtmlPath = path,
            PlainSummary = BuildPlainSummary(result),
            Data = data
        };
    }

    /// <summary>纯文本摘要（供「复制摘要」与命令行）。</summary>
    public static string BuildPlainSummary(SystemInfoResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"【系统信息】{Environment.MachineName}  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine($"精简判定：{result.VerdictText}（缺失分 {result.TrimScore}，缺失 {result.MissingChecks.Count} 项 / 共检查 {result.CheckedCount} 项）");
        sb.AppendLine(result.Headline);
        sb.AppendLine();

        foreach (var g in result.Groups)
        {
            sb.AppendLine($"[{g.Name}]");
            foreach (var i in g.Items) sb.AppendLine($"  {i.Name,-12} {i.Display}");
            sb.AppendLine();
        }

        sb.AppendLine("[精简系统检测]");
        foreach (var c in result.TrimChecks)
            sb.AppendLine($"  {(c.Skipped ? "[-]" : c.Missing ? "[!]" : "[√]")} {c.Name}：{c.Detail}");

        return sb.ToString();
    }

    // ---------------- 小工具 ----------------

    private static InfoItem Text(string name, string value, string missingNote)
        => string.IsNullOrWhiteSpace(value)
            ? new InfoItem { Name = name, State = InfoState.Unavailable, Note = missingNote.Length > 0 ? missingNote : "没有取到值" }
            : new InfoItem { Name = name, Value = value.Trim() };

    private static string RegStr(string path, string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key?.GetValue(name) as string ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static int RegInt(string path, string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key?.GetValue(name) is int v ? v : 0;
        }
        catch
        {
            return 0;
        }
    }
}
