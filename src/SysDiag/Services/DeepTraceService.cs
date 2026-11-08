using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 深度硬件痕迹：检查两类“不碰游戏内存也能作弊”的链路。
/// <para>
/// 一、DMA（Direct Memory Access）：板卡经 PCIe/Thunderbolt 直接读写物理内存，绕过 CPU 与操作系统。
/// 二、采集卡链路：主机画面经采集卡进 PC，PC 上做视觉识别，再用输入模拟器把指令送回主机。
/// </para>
/// <para>
/// 设计约束：全部只读，不改任何设置；快速项 1 秒内出结果，重活放在深度扫描里；
/// 单项只给“正常 / 提示”，只有当多个特征同时出现才升级为“线索”，避免把 Arduino 板子当成外挂。
/// </para>
/// </summary>
public static class DeepTraceService
{
    // ---------- 公开规则表（自检会核对，界面不直接展示） ----------

    /// <summary>常见单片机 / 串口桥接芯片的厂商号。任何人都可能插着开发板，命中只算提示。</summary>
    public static IReadOnlyList<(string Vid, string Label)> BridgeVids { get; } = new[]
    {
        ("2341", "Arduino"),
        ("16C0", "Teensy / PJRC"),
        ("2E8A", "Raspberry Pi（RP2040）"),
        ("1A86", "WCH CH340 串口"),
        ("10C4", "Silicon Labs CP210x"),
        ("0403", "FTDI 串口"),
        ("1209", "pid.codes 开源硬件")
    };

    /// <summary>采集卡厂商与型号关键字，匹配设备名。</summary>
    public static IReadOnlyList<string> CaptureKeywords { get; } = new[]
    {
        "elgato", "avermedia", "magewell", "ripsaw", "cam link",
        "live gamer", "capture", "采集", "game capture"
    };

    /// <summary>采集/推流软件进程名（精确匹配，避免 obs 误伤 obsidian 之类）。</summary>
    public static IReadOnlyList<string> CaptureProcesses { get; } = new[]
    {
        "obs", "obs32", "obs64", "streamlabs", "elgatogamecapture",
        "recentral", "avermedia", "vmix", "xsplit", "camengine"
    };

    private const string PciEnum = @"SYSTEM\CurrentControlSet\Enum\PCI";
    private const string UsbEnum = @"SYSTEM\CurrentControlSet\Enum\USB";

    // ---------- 对外入口 ----------

    /// <summary>快速体检：只读注册表与进程表，通常 1 秒内完成。</summary>
    public static DeepTraceResult Scan(bool deep, IProgress<string> progress = null)
    {
        var sw = Stopwatch.StartNew();
        var items = new List<DeepTraceItem>();

        var pci = ReadPciDevices();
        var usb = ReadUsbDevicesSafe();
        var captureApps = RunningCaptureApps();

        items.AddRange(BuildDmaItems(pci, deep, progress));
        items.AddRange(BuildDmaPolicyItems(pci));

        // DMA 伪装检测：固件把厂商 ID 改成真实网卡后，靠「查厂商清单」就失效了。
        // 这一层改用结构性矛盾（同一 ID 对应不同设备类、链路宽度与设备类不符）。
        progress?.Report("正在检查 PCIe 设备的伪装迹象…");
        items.AddRange(DmaSpoofProbeService.Analyze(DmaSpoofProbeService.ReadDevices()));
        items.AddRange(BuildCaptureItems(pci, usb, captureApps, deep));
        items.AddRange(BuildInputItems(usb, pci));

        // 组合判定：单个特征都不算异常，只有链路凑齐了才升级为线索
        var captureDevice = items.Any(i => i.Name.StartsWith("采集设备", StringComparison.Ordinal) && i.Level != "ok");
        var captureRunning = captureApps.Count > 0;
        var bridge = items.Any(i => i.Name.StartsWith("输入模拟设备", StringComparison.Ordinal) && i.Level != "ok");

        if (captureDevice && captureRunning)
        {
            items.Add(new DeepTraceItem
            {
                Group = DeepGroup.Capture,
                Name = "采集链路：设备与软件同时存在",
                Detail = "检测到采集设备，且采集/推流软件正在运行。正常直播也是这个组合；只有在“主机游戏 + 电脑视觉识别”场景下才构成作弊链路。",
                Level = "warn",
                Basis = "设备枚举 + 进程表"
            });
        }

        if (bridge && (captureDevice || captureRunning))
        {
            items.Add(new DeepTraceItem
            {
                Group = DeepGroup.Input,
                Name = "输入模拟 + 采集：主机串流链路的组合特征",
                Detail = "同时存在单片机/串口桥接设备与采集链路。这类组合常见于“画面进电脑、指令回主机”的外置方案，也可能只是开发板加直播。",
                Level = "warn",
                Basis = "USB 设备枚举 + 采集设备 / 采集软件"
            });
        }

        sw.Stop();

        return new DeepTraceResult
        {
            Items = items,
            IsDeep = deep,
            ElapsedMs = sw.ElapsedMilliseconds
        };
    }

    /// <summary>把结果拼成 HTML 报告并落盘，返回报告载荷。</summary>
    public static ReportPayload BuildReport(DeepTraceResult r)
    {
        AppPaths.Ensure();

        var doc = new HtmlDoc("深度硬件痕迹", 
            $"扫描方式：{r.ScopeText}　|　耗时 {r.ElapsedMs} ms　|　" +
            $"生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        doc.Kpis(
            (r.WarnCount.ToString(), "需要人工确认", "#C92A2A"),
            (r.HintCount.ToString(), "提示项", "#E8590C"),
            (r.OkCount.ToString(), "正常项", "#2F9E44"));

        doc.Raw($"<div class=\"verdict\" style=\"border-left-color:{r.VerdictColor}\">" +
                $"<b>{HtmlDoc.E(r.Verdict)}</b><br>{HtmlDoc.E(r.VerdictDetail)}</div>");

        doc.Note("<b>这不是作弊判定。</b>本页只列出“这台机器上存在哪些可能被用于外部辅助的硬件与软件面”，" +
                 "命中不等于用过、未命中不等于没用过。是否违规以游戏反作弊系统的判定为准。");

        foreach (var group in new[] { DeepGroup.Dma, DeepGroup.Capture, DeepGroup.Input })
        {
            var rows = r.Of(group).ToList();
            doc.H2($"{GroupName(group)}（{rows.Count} 项）");

            if (rows.Count == 0)
            {
                doc.Empty("本次未采集到该类信息。");
                continue;
            }

            doc.Table(new[] { "状态", "检查项", "结果", "依据" },
                rows.Select(i => new[]
                {
                    i.LevelText, i.Name, i.Detail,
                    string.IsNullOrEmpty(i.Basis) ? "—" : i.Basis
                }),
                new[] { "62px", "30%", "", "22%" });
        }

        doc.H2("检查方法与边界");
        doc.Raw("<div class=\"note\" style=\"line-height:1.9\">" +
                "<b>DMA 面</b>：枚举 <code>HKLM\\SYSTEM\\CurrentControlSet\\Enum\\PCI</code> 下的全部实例，" +
                "读取设备描述、驱动服务与配置标志；查 Thunderbolt / USB4 控制器是否存在；" +
                "查内存完整性（HVCI）与 DMA 白名单（<code>DmaSecurity\\AllowedBuses</code>）状态。" +
                "深度扫描额外读取近 30 天的 Kernel-PnP 设备事件，并对非微软驱动的 .sys 逐个体检签名。<br>" +
                "<b>采集卡链路</b>：按厂商与型号关键字匹配已记录过的设备，并检查采集/推流软件是否在运行。<br>" +
                "<b>输入模拟</b>：按常见单片机与串口桥接芯片的厂商号匹配 USB 设备。<br>" +
                "<b>边界</b>：本工具读不到 PCIe 配置空间的原始数据，也不检测 DMA 板卡是否真的发起过内存访问；" +
                "USB 记录来自注册表历史条目，拔掉的设备同样会出现在清单里；" +
                "驱动签名只说明文件带有效签名，不代表该驱动安全。" +
                "</div>");

        var html = doc.Build();
        var path = HtmlReportBuilder.Save(html, "深度硬件痕迹");

        var sb = new StringBuilder();
        sb.AppendLine($"【深度硬件痕迹】{DateTime.Now:yyyy-MM-dd HH:mm:ss}　{r.ScopeText}");
        sb.AppendLine($"结论：{r.Verdict}");
        sb.AppendLine($"线索 {r.WarnCount} · 提示 {r.HintCount} · 正常 {r.OkCount}　耗时 {r.ElapsedMs} ms");
        sb.AppendLine();
        foreach (var i in r.Items)
            sb.AppendLine($"[{i.LevelText}] {i.GroupText} / {i.Name}：{i.Detail}");

        return new ReportPayload
        {
            Title = "深度硬件痕迹",
            Subtitle = $"{r.ScopeText} · {r.Verdict}",
            HtmlPath = path,
            PlainSummary = sb.ToString()
        };
    }

    /// <summary>从 "PCI\VEN_10EE&amp;DEV_7024..." 或 "VID_2341&amp;PID_0043" 里取 4 位厂商号。</summary>
    public static string ParseVid(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return "";
        var m = Regex.Match(instanceId, @"(?:VEN|VID)_([0-9A-Fa-f]{4})");
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : "";
    }

    public static string GroupName(DeepGroup g) => g switch
    {
        DeepGroup.Dma => "DMA 攻击面",
        DeepGroup.Capture => "采集卡链路",
        _ => "输入模拟设备"
    };

    // ---------- DMA ----------

    private static IEnumerable<DeepTraceItem> BuildDmaItems(List<PciDevice> pci, bool deep, IProgress<string> progress)
    {
        yield return new DeepTraceItem
        {
            Group = DeepGroup.Dma,
            Name = "PCIe 设备总览",
            Detail = pci.Count == 0 ? "未能读取 PCI 设备树。" : $"共 {pci.Count} 个 PCI 设备实例。",
            Level = pci.Count == 0 ? "hint" : "ok",
            Basis = @"Enum\PCI"
        };

        // 没有驱动接管的 PCI 设备：DMA 板卡常常就是“插上去没装驱动”的状态。
        // 芯片组自带的 SMBus / SPI / SRAM / Host Bridge 本来就没有独立服务，先排除，避免刷一堆噪音。
        var orphan = pci.Where(d => string.IsNullOrEmpty(d.Service) && !IsSystemBridge(d.Desc)).ToList();
        yield return new DeepTraceItem
        {
            Group = DeepGroup.Dma,
            Name = "无驱动接管的 PCI 设备",
            Detail = orphan.Count == 0
                ? "所有 PCI 设备都有对应驱动服务。"
                : $"{orphan.Count} 个：" + string.Join("；", orphan.Take(5).Select(d =>
                      $"{Truncate(d.Desc.Length > 0 ? d.Desc : d.Instance, 46)}（VEN_{d.Vid}）")) +
                  (orphan.Count > 5 ? $" 等 {orphan.Count} 个" : ""),
            Level = orphan.Count == 0 ? "ok" : "hint",
            Basis = "Service 值为空"
        };

        var tb = pci.Where(d => d.Desc.Contains("Thunderbolt", StringComparison.OrdinalIgnoreCase)
                             || d.Desc.Contains("USB4", StringComparison.OrdinalIgnoreCase)).ToList();
        yield return new DeepTraceItem
        {
            Group = DeepGroup.Dma,
            Name = "Thunderbolt / USB4 控制器",
            Detail = tb.Count == 0
                ? "未发现雷电或 USB4 控制器。外置 PCIe 设备没有入口。"
                : $"发现 {tb.Count} 个：{string.Join("；", tb.Take(3).Select(d => Truncate(d.Desc, 46)))}。雷电接口是最常见的外置 DMA 入口。",
            Level = tb.Count == 0 ? "ok" : "hint",
            Basis = "设备描述匹配 Thunderbolt / USB4"
        };

        var hvci = ReadInt(Registry.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled");
        yield return new DeepTraceItem
        {
            Group = DeepGroup.Dma,
            Name = "内存完整性（HVCI）",
            Detail = hvci switch
            {
                1 => "已开启。内核代码完整性校验在运行，DMA 写入的内核结构更容易被发现。",
                0 => "已关闭。关闭 HVCI 能提升些许游戏性能，但也削弱了对内核篡改的发现能力。",
                _ => "未配置（等同关闭）。这项需要系统支持虚拟化并重启后才生效。"
            },
            Level = hvci == 1 ? "ok" : "hint",
            Basis = @"DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity"
        };

        var dmaGuard = ReadSubKeyNames(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\DmaSecurity\AllowedBuses");
        yield return new DeepTraceItem
        {
            Group = DeepGroup.Dma,
            Name = "DMA 白名单（AllowedBuses）",
            Detail = dmaGuard.Count == 0
                ? "未配置白名单。系统没有限制哪些总线可以做 DMA。"
                : $"已配置 {dmaGuard.Count} 条允许规则。",
            Level = dmaGuard.Count == 0 ? "hint" : "ok",
            Basis = @"DmaSecurity\AllowedBuses"
        };

        if (!deep) yield break;

        progress?.Report("回溯设备事件…");
        var pnp = ReadPnpEventsSafe(30);
        var pciEvents = pnp.Where(e =>
            e.Device.Contains("PCI", StringComparison.OrdinalIgnoreCase) ||
            e.Message.Contains("PCI", StringComparison.OrdinalIgnoreCase)).ToList();

        yield return new DeepTraceItem
        {
            Group = DeepGroup.Dma,
            Name = "近 30 天 PCIe 设备事件",
            Detail = pnp.Count == 0
                ? "读不到 Kernel-PnP 事件（可能需要管理员权限，或该通道未启用）。"
                : pciEvents.Count == 0
                    ? $"近 30 天共 {pnp.Count} 条设备事件，其中没有 PCI 相关记录。"
                    : $"{pciEvents.Count} 条 PCI 相关事件，最近一次：{pciEvents[0].TimeText} {Truncate(pciEvents[0].Device, 40)}。",
            Level = pciEvents.Count == 0 ? "ok" : "hint",
            Basis = "Microsoft-Windows-Kernel-PnP/Configuration",
            DeepOnly = true
        };

        progress?.Report("核对驱动加载策略…");

        var startOptions = ReadString(Registry.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control", "SystemStartOptions") ?? "";

        var testSigning = startOptions.Contains("TESTSIGNING", StringComparison.OrdinalIgnoreCase);
        var noIntegrity = startOptions.Contains("DISABLE_INTEGRITY_CHECKS", StringComparison.OrdinalIgnoreCase);

        yield return new DeepTraceItem
        {
            Group = DeepGroup.Dma,
            Name = "测试签名与完整性检查",
            Detail = testSigning || noIntegrity
                ? $"启动参数里有 {(testSigning ? "TESTSIGNING" : "")}{(testSigning && noIntegrity ? " + " : "")}" +
                  $"{(noIntegrity ? "DISABLE_INTEGRITY_CHECKS" : "")}，未签名驱动可以加载。开发者常用，但也会放宽外挂驱动的门槛。"
                : "未开启测试签名，驱动签名强制在生效。",
            Level = testSigning || noIntegrity ? "hint" : "ok",
            Basis = @"Control\SystemStartOptions",
            DeepOnly = true
        };

        // 这个值在不同版本里落在两个位置，优先 Config，再退回 Policy
        var blocklist = ReadInt(Registry.LocalMachine,
                             @"SYSTEM\CurrentControlSet\Control\CI\Config", "VulnerableDriverBlocklistEnable")
                        ?? ReadInt(Registry.LocalMachine,
                             @"SYSTEM\CurrentControlSet\Control\CI\Policy", "VulnerableDriverBlocklistEnable");

        yield return new DeepTraceItem
        {
            Group = DeepGroup.Dma,
            Name = "易受攻击驱动黑名单",
            Detail = blocklist switch
            {
                1 => "已启用。微软的易受攻击驱动黑名单在拦截已知可被利用的驱动（BYOVD）。",
                0 => "已被关闭。BYOVD 类手法依赖的正是这些驱动，关闭后拦不住。",
                _ => "未找到该策略项。Windows 10 旧版本与部分精简系统没有这一项。"
            },
            Level = blocklist == 1 ? "ok" : "hint",
            Basis = @"CI\Config\VulnerableDriverBlocklistEnable",
            DeepOnly = true
        };
    }

    // ---------- DMA 平台防护（IOMMU / FPGA / VBS） ----------

    /// <summary>
    /// DMA 外挂检测里唯一能在用户态做的事：看**平台允不允许 DMA 穿透**，以及**总线上有没有 FPGA**。
    ///
    /// 背景：DMA 外挂（PCILeech / LeetDMA 一类）用一块 FPGA 板插在 PCIe 或雷电口上，
    /// 由硬件直接读物理内存、完全绕过进程与 API hook。游戏进程本身干干净净，所以任何
    /// 「进程内检测」都抓不到它。可查的只剩三条：
    ///
    /// 1. **IOMMU 是否在保护**：Windows 的 Kernel DMA Protection 依赖固件在 ACPI 的
    ///    DMAR（Intel）/ IVRS（AMD）表里 opt-in。表里没开这个位，系统就没有 DMA 重映射，
    ///    一块随便什么 FPGA 都能全内存读。这一项查的是「平台本身是否失守」。
    /// 2. **总线上有没有 FPGA**：Xilinx / Lattice / Altera 的厂商 ID 出现在消费级机器上很少见。
    ///    高级固件会把 ID 伪装成真实网卡，所以「没查到」不等于没有。
    /// 3. **VBS/HVCI 是否在跑**：虚拟化层能拦住一部分内核级篡改（包括用 BYOVD 关 IOMMU 的手法）。
    ///
    /// 明确不做的：不解析 PCIe 配置空间的能力链、不比对 BAR 布局与 link width。
    /// 那些要在内核态读配置空间，用户态读不到，硬做只会给出一堆猜测。
    /// </summary>
    private static IEnumerable<DeepTraceItem> BuildDmaPolicyItems(List<PciDevice> pci)
    {
        var list = new List<DeepTraceItem>();

        // ---------- 1. 平台 DMA 保护状态 ----------
        // 读 Windows 自己的策略，而不是解析 ACPI 的 DMAR 表：
        // 固件表只能说明「固件声明了什么」，注册表才能说明「Windows 实际采用了什么策略」。
        // 而且 DMAR 的 opt-in 位极易搞错（bit0 是中断重映射，bit2 才是 DMA 保护 opt-in），
        // 搞错方向会把「保护关闭」报成「保护开启」，比不报更糟。
        list.AddRange(DmaSpoofProbeService.ReadPlatformProtection());

        // ---------- 2. 总线上的 FPGA ----------
        var fpgaVendors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["10EE"] = "Xilinx",
            ["1204"] = "Lattice",
            ["1172"] = "Altera (Intel FPGA)"
        };

        var fpgaHits = pci
            .Where(p => fpgaVendors.ContainsKey(p.Vid))
            .Select(p => $"{fpgaVendors[p.Vid]}（VEN_{p.Vid}）{p.Desc}")
            .Distinct()
            .ToList();

        if (fpgaHits.Count > 0)
        {
            list.Add(new DeepTraceItem
            {
                Group = DeepGroup.Dma,
                Name = "总线上有 FPGA 设备",
                Detail = "检测到 FPGA 厂商的 PCIe 设备。常见 DMA 外挂（PCILeech / LeetDMA 一类）就是 FPGA 板，"
                         + "插在 PCIe 槽或雷电扩展坞里，由硬件直接读物理内存，游戏进程本身不会被碰。"
                         + "但很多正当用途也会装 FPGA：开发板、科研采集、信号处理、部分专业声卡。"
                         + "所以这一条只说明「具备 DMA 硬件条件」，要结合用途判断。",
                Level = "warn",
                Basis = string.Join("、", fpgaHits),
                DeepOnly = false
            });
        }
        else
        {
            list.Add(new DeepTraceItem
            {
                Group = DeepGroup.Dma,
                Name = "总线上没有 FPGA 厂商设备",
                Detail = "没有发现 Xilinx / Lattice / Altera 厂商 ID 的 PCIe 设备。"
                         + "注意：高级 DMA 固件会把厂商 ID 伪装成真实网卡或声卡，"
                         + "因此「没查到」只能说明没有使用最粗糙的那类板子，不能证明不存在 DMA 设备。",
                Level = "ok",
                Basis = $"{pci.Count} 个 PCIe 设备",
                DeepOnly = false
            });
        }

        // ---------- 3. VBS / HVCI ----------
        var (vbsStatus, services) = ReadDeviceGuard();

        if (vbsStatus < 0)
        {
            list.Add(new DeepTraceItem
            {
                Group = DeepGroup.Dma,
                Name = "VBS 状态：读不到",
                Detail = "查询 Win32_DeviceGuard 失败（常见于被安全软件接管或系统被精简）。",
                Level = "hint",
                Basis = "root\\Microsoft\\Windows\\DeviceGuard",
                DeepOnly = true
            });
        }
        else
        {
            var running = services.Contains("2");   // 2 = HVCI

            list.Add(new DeepTraceItem
            {
                Group = DeepGroup.Dma,
                Name = vbsStatus == 2
                    ? (running ? "VBS 正在运行，HVCI 已启用" : "VBS 正在运行，HVCI 未启用")
                    : "VBS 未运行",
                Detail = vbsStatus == 2
                    ? "虚拟化安全层在跑。它能拦住一部分内核级篡改——包括用合法但易受攻击的驱动（BYOVD）"
                      + "去运行时关闭 IOMMU 表这类手法，而后者正是让 DMA 板子「重新可用」的关键一步。"
                    : "VBS 未运行。这不代表有问题（很多游戏机器为了性能关掉了它），"
                      + "但意味着内核对 DMA 重映射表的保护更弱，BYOVD 关闭 IOMMU 的成功率更高。",
                Level = vbsStatus == 2 ? "ok" : "hint",
                Basis = $"VirtualizationBasedSecurityStatus={vbsStatus}　运行中的服务={string.Join(",", services)}",
                DeepOnly = false
            });
        }

        return list;
    }

    /// <summary>VBS 状态与运行中的安全服务。返回 (-1, 空) 表示读不到。</summary>
    private static (int Status, List<string> Services) ReadDeviceGuard()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                new System.Management.ManagementScope(@"\\.\root\Microsoft\Windows\DeviceGuard"),
                new System.Management.ObjectQuery(
                    "SELECT VirtualizationBasedSecurityStatus, SecurityServicesRunning FROM Win32_DeviceGuard"));

            foreach (System.Management.ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    var status = int.TryParse(mo["VirtualizationBasedSecurityStatus"]?.ToString(), out var s) ? s : -1;

                    var services = new List<string>();
                    if (mo["SecurityServicesRunning"] is ushort[] arr)
                        services.AddRange(arr.Select(x => x.ToString()));
                    else if (mo["SecurityServicesRunning"] is uint[] arr2)
                        services.AddRange(arr2.Select(x => x.ToString()));

                    return (status, services);
                }
            }
        }
        catch { }

        return (-1, new List<string>());
    }

    // ---------- 采集卡 ----------

    private static IEnumerable<DeepTraceItem> BuildCaptureItems(
        List<PciDevice> pci, List<UsbDeviceInfo> usb, List<string> running, bool deep)
    {
        var hits = new List<string>();

        foreach (var u in usb)
        {
            if (CaptureKeywords.Any(k => u.Name.Contains(k, StringComparison.OrdinalIgnoreCase)
                                      || u.VidPid.Contains(k, StringComparison.OrdinalIgnoreCase)))
                hits.Add($"{Truncate(u.Name, 40)}（{u.VidPid}）");
        }

        foreach (var d in pci)
        {
            if (CaptureKeywords.Any(k => d.Desc.Contains(k, StringComparison.OrdinalIgnoreCase)))
                hits.Add($"{Truncate(d.Desc, 40)}（VEN_{d.Vid}）");
        }

        yield return new DeepTraceItem
        {
            Group = DeepGroup.Capture,
            Name = "采集设备",
            Detail = hits.Count == 0
                ? "未发现采集卡类设备。"
                : $"{hits.Count} 个：{string.Join("；", hits.Distinct().Take(5))}",
            Level = hits.Count == 0 ? "ok" : "hint",
            Basis = "设备名关键字（Elgato / AVerMedia / Magewell 等）"
        };

        var media = usb.Count(u => u.Service.Contains("ksthunk", StringComparison.OrdinalIgnoreCase)
                                 || u.Service.Contains("usbvideo", StringComparison.OrdinalIgnoreCase));
        yield return new DeepTraceItem
        {
            Group = DeepGroup.Capture,
            Name = "视频捕获类驱动",
            Detail = media == 0
                ? "没有注册为视频捕获的 USB 设备。"
                : $"{media} 个 USB 设备挂了捕获/视频驱动（ksthunk / usbvideo），摄像头也会命中。",
            Level = media == 0 ? "ok" : "hint",
            Basis = "USB 驱动服务名"
        };

        yield return new DeepTraceItem
        {
            Group = DeepGroup.Capture,
            Name = "采集 / 推流软件",
            Detail = running.Count == 0
                ? "当前没有采集或推流软件在运行。"
                : $"正在运行：{string.Join("、", running)}。正常直播也属于这个状态。",
            Level = running.Count == 0 ? "ok" : "hint",
            Basis = "进程名精确匹配"
        };

        if (!deep) yield break;

        yield return new DeepTraceItem
        {
            Group = DeepGroup.Capture,
            Name = "画面回传方向",
            Detail = "本工具无法判断采集卡的信号来自主机还是摄像头，也无法判断电脑是否把识别结果回传给了主机。" +
                     "这一层只能靠人工核对设备接线与实际用途。",
            Level = "hint",
            Basis = "能力边界说明",
            DeepOnly = true
        };
    }

    // ---------- 输入模拟 ----------

    private static IEnumerable<DeepTraceItem> BuildInputItems(List<UsbDeviceInfo> usb, List<PciDevice> pci)
    {
        var found = new List<string>();

        foreach (var u in usb)
        {
            var vid = ParseVid(u.VidPid);
            var rule = BridgeVids.FirstOrDefault(b => b.Vid.Equals(vid, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(rule.Vid))
                found.Add($"{rule.Label}（{u.VidPid}）{Truncate(u.Name, 30)}");
        }

        yield return new DeepTraceItem
        {
            Group = DeepGroup.Input,
            Name = "输入模拟设备",
            Detail = found.Count == 0
                ? "未发现常见单片机或串口桥接设备。"
                : $"{found.Count} 个：{string.Join("；", found.Distinct().Take(5))}。开发板、3D 打印机、串口调试器都会命中。",
            Level = found.Count == 0 ? "ok" : "hint",
            Basis = "USB 厂商号对照表"
        };

        var hid = usb.Count(u => u.Service.Equals("hidusb", StringComparison.OrdinalIgnoreCase)
                              || u.Service.Equals("kbdhid", StringComparison.OrdinalIgnoreCase)
                              || u.Service.Equals("mouhid", StringComparison.OrdinalIgnoreCase));
        yield return new DeepTraceItem
        {
            Group = DeepGroup.Input,
            Name = "HID 输入设备",
            Detail = hid == 0
                ? "没有注册为 HID 的 USB 设备。"
                : $"{hid} 个 HID 设备（键鼠、手柄、部分模拟器都走这条通道）。",
            Level = hid == 0 ? "ok" : "hint",
            Basis = "USB 驱动服务名（hidusb / kbdhid / mouhid）"
        };

        var vhid = ReadSubKeyNames(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Services")
            .Where(n => n.Contains("kmbox", StringComparison.OrdinalIgnoreCase)
                     || n.Contains("interception", StringComparison.OrdinalIgnoreCase)
                     || n.Equals("vgamepad", StringComparison.OrdinalIgnoreCase)
                     || n.Equals("scpvbus", StringComparison.OrdinalIgnoreCase))
            .ToList();

        yield return new DeepTraceItem
        {
            Group = DeepGroup.Input,
            Name = "输入注入类驱动服务",
            Detail = vhid.Count == 0
                ? "注册表服务列表里没有常见的输入注入驱动。"
                : $"发现 {vhid.Count} 个：{string.Join("、", vhid)}。这些是模拟输入的通用组件，虚拟手柄软件也会安装。",
            Level = vhid.Count == 0 ? "ok" : "hint",
            Basis = @"Services 键名匹配（kmbox / interception / vgamepad / scpvbus）"
        };
    }

    // ---------- 底层读取 ----------

    private sealed class PciDevice
    {
        public string Instance { get; init; } = "";
        public string Vid { get; init; } = "";
        public string Desc { get; init; } = "";
        public string Service { get; init; } = "";
        public string ClassGuid { get; init; } = "";
    }

    private static List<PciDevice> ReadPciDevices()
    {
        var list = new List<PciDevice>();

        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(PciEnum);
            if (root == null) return list;

            foreach (var key in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(key);
                if (k == null) continue;

                var vid = ParseVid(key);

                foreach (var inst in k.GetSubKeyNames())
                {
                    using var ik = k.OpenSubKey(inst);
                    if (ik == null) continue;

                    list.Add(new PciDevice
                    {
                        Instance = key + @"\" + inst,
                        Vid = vid,
                        Desc = CleanDesc(ik.GetValue("DeviceDesc") as string),
                        Service = (ik.GetValue("Service") as string ?? "").Trim(),
                        ClassGuid = (ik.GetValue("ClassGUID") as string ?? "").Trim()
                    });
                }
            }
        }
        catch
        {
            // 读不到就返回空表，界面给“未能读取”的提示，不影响其他检查
        }

        return list;
    }

    private static List<UsbDeviceInfo> ReadUsbDevicesSafe()
    {
        try { return EvidenceService.ReadUsbDevices(); }
        catch { return new List<UsbDeviceInfo>(); }
    }

    private static List<PnpEventRow> ReadPnpEventsSafe(int days)
    {
        try { return EvidenceService.ReadPnpEvents(days, 400); }
        catch { return new List<PnpEventRow>(); }
    }

    private static List<string> RunningCaptureApps()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in Process.GetProcesses())
        {
            try { names.Add(p.ProcessName); }
            catch { /* 进程已退出 */ }
            finally { p.Dispose(); }
        }

        return CaptureProcesses
            .Where(k => names.Contains(k))
            .Select(k => k)
            .ToList();
    }

    /// <summary>设备描述常是 "@oem12.inf,%xxx%;真实名称" 这种资源引用，取分号后的可读部分。</summary>
    private static string CleanDesc(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var s = raw.Trim();
        var i = s.LastIndexOf(';');
        if (i >= 0 && i < s.Length - 1) s = s[(i + 1)..];
        if (s.StartsWith('@')) return "";
        return s.Trim();
    }

    /// <summary>芯片组内置设备：本来就没有独立驱动服务，不能当成“裸设备”报出来。</summary>
    private static bool IsSystemBridge(string desc)
    {
        if (string.IsNullOrEmpty(desc)) return true;

        string[] builtin =
        {
            "SMBus", "SPI", "SRAM", "CPU bridge", "Host Bridge", "ISA bridge",
            "PCI standard", "LPC", "Memory Controller", "Thermal", "Management Engine",
            "Serial IO", "GPIO", "Root Port", "PCI-to-PCI", "Switch"
        };

        return builtin.Any(k => desc.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadString(RegistryKey root, string path, string name)
    {
        try
        {
            using var k = root.OpenSubKey(path);
            return k?.GetValue(name) as string;
        }
        catch { return null; }
    }

    private static int? ReadInt(RegistryKey root, string path, string name)
    {
        try
        {
            using var k = root.OpenSubKey(path);
            var v = k?.GetValue(name);
            return v is int i ? i : v is long l ? (int)l : null;
        }
        catch { return null; }
    }

    private static List<string> ReadSubKeyNames(RegistryKey root, string path)
    {
        try
        {
            using var k = root.OpenSubKey(path);
            return k?.GetSubKeyNames().ToList() ?? new List<string>();
        }
        catch { return new List<string>(); }
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";
}
