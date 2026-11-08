using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// DMA 外挂的「伪装」检测。
///
/// 背景：DMA 外挂把 FPGA 板插在 PCIe/雷电口上，用硬件直接读物理内存，游戏进程本身干净，
/// 任何进程内的注入/句柄/模块检测都抓不到。高级固件还会把厂商 ID、类代码改成一块真实网卡，
/// 让「查 Xilinx 0x10EE」这种清单式检测完全失效。
///
/// 公开资料（PCILeech 生态、Clubhouse AC 的 PCIe 指纹研究、gmh5225/awesome-game-security）
/// 给出的判据分三档：
/// <list type="number">
/// <item><b>配置空间层</b>：能力链完整性、BAR mask 尺寸、保留字节非零、Xilinx IP 残留签名、
/// DSN 能力出现性。这是最有效的一档——但**全部需要内核态读配置空间**。</item>
/// <item><b>行为层</b>：链路宽度/速率与宣称能力矛盾、MSI 中断是否真的产生、BAR 读回的寄存器内容、
/// 完成延迟分布（KS / Hill 估计）。前两项用户态可做，后两项需要发 TLP，做不到。</item>
/// <item><b>平台层</b>：IOMMU 是否真的在重映射、设备是否落在重映射域内、VBS 是否拦住 BYOVD 关表。</item>
/// </list>
///
/// 本类只做用户态确实能拿到的部分，并且**明确不假装能做配置空间层**。
///
/// 用户态能拿到的四个可靠信号：
/// <list type="bullet">
/// <item>PCIe 当前/最大链路宽度与速率（pci.sys 通过 DEVPKEY_PciDevice_* 暴露，已实测可读）</item>
/// <item>设备类代码（BaseClass/SubClass）</item>
/// <item>同一 VID/DID 是否对应了不同的设备类——这是**逻辑矛盾**，不是概率判断</item>
/// <item>Windows 自己的 DMA 保护状态（DmaSecurity 策略 + VBS 状态）</item>
/// </list>
/// </summary>
public static class DmaSpoofProbeService
{
    // ------------------------------------------------------------------ 模型

    /// <summary>一个 PCIe 设备在用户态能读到的全部事实。</summary>
    public sealed class DeviceFacts
    {
        public string InstanceId { get; init; } = "";
        public string Vid { get; init; } = "";
        public string Did { get; init; } = "";
        public string Subsys { get; init; } = "";
        public string Name { get; init; } = "";

        /// <summary>PCI 类代码。0x02 网络、0x03 显示、0x01 存储、0x0C 串行总线。</summary>
        public int BaseClass { get; init; } = -1;
        public int SubClass { get; init; } = -1;

        /// <summary>当前链路宽度（x1 / x2 / x4 …）与最大支持宽度。读不到为 0。</summary>
        public int CurrentLinkWidth { get; init; }
        public int MaxLinkWidth { get; init; }

        /// <summary>当前链路速率档位与最大支持档位。1=2.5GT/s … 5=32GT/s。</summary>
        public int CurrentLinkSpeed { get; init; }
        public int MaxLinkSpeed { get; init; }

        /// <summary>pci.sys 报的设备类型。语义跨版本不稳，只用于展示，不参与判定。</summary>
        public int DeviceType { get; init; } = -1;

        public string Service { get; init; } = "";

        /// <summary>链路信息是否读到了。读不到说明属性编号不对或该设备没有 PCIe 能力结构。</summary>
        public bool HasLinkInfo => CurrentLinkWidth > 0 && MaxLinkWidth > 0;

        public string ClassText => BaseClass switch
        {
            0x00 => "未定义",
            0x01 => "存储控制器",
            0x02 => "网络控制器",
            0x03 => "显示控制器",
            0x04 => "多媒体设备",
            0x05 => "内存控制器",
            0x06 => "桥/端口",
            0x07 => "通信控制器",
            0x08 => "系统外设",
            0x09 => "输入设备",
            0x0A => "对接站",
            0x0B => "处理器",
            0x0C => "串行总线控制器",
            0x0D => "无线控制器",
            0x0E => "智能 IO",
            0x0F => "卫星设备",
            0x10 => "加密控制器",
            0x11 => "信号处理",
            0xFF => "其他",
            _ => "未知"
        };

        public string WidthText => HasLinkInfo ? $"x{CurrentLinkWidth}/x{MaxLinkWidth}" : "读不到";

        public string SpeedText => HasLinkInfo
            ? $"{SpeedName(CurrentLinkSpeed)}/{SpeedName(MaxLinkSpeed)}"
            : "读不到";
    }

    private static string SpeedName(int level) => level switch
    {
        1 => "2.5GT",
        2 => "5GT",
        3 => "8GT",
        4 => "16GT",
        5 => "32GT",
        6 => "64GT",
        0 => "—",
        _ => $"?{level}"
    };

    /// <summary>
    /// 诊断用：把某个设备上**所有** PCI 属性键连同值列出来。
    ///
    /// 为什么需要这个：DEVPKEY_PciDevice_* 的属性编号在不同 Windows SDK 版本里改过，
    /// 凭记忆写编号会出现「全部读成 -1」或「读出完全无关的数字」。有了这个枚举，
    /// 编号可以直接从本机反推出来，而不是猜。
    /// </summary>
    public static string DumpPropertyKeys(string instanceFilter)
    {
        var sb = new StringBuilder();
        var set = SetupDiGetClassDevsW(IntPtr.Zero, "PCI", IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return "打开设备集失败";

        try
        {
            var data = new SP_DEVINFO_DATA();
            data.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));

            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                var idBuffer = new StringBuilder(512);
                if (!SetupDiGetDeviceInstanceIdW(set, ref data, idBuffer, 512, out _)) continue;

                var instance = idBuffer.ToString();
                if (instanceFilter.Length > 0 &&
                    !instance.Contains(instanceFilter, StringComparison.OrdinalIgnoreCase)) continue;

                sb.AppendLine("实例 " + instance);

                SetupDiGetDevicePropertyKeys(set, ref data, null, 0, out var count, 0);
                if (count <= 0) { sb.AppendLine("  （没有属性键）"); continue; }

                var keys = new DEVPROPKEY[count];
                if (!SetupDiGetDevicePropertyKeys(set, ref data, keys, count, out _, 0))
                {
                    sb.AppendLine("  枚举属性键失败，错误 " + Marshal.GetLastWin32Error());
                    continue;
                }

                foreach (var key in keys)
                {
                    var local = key;   // foreach 变量不能按 ref 传出
                    if (local.fmtid != PciDevicePropSet) continue;

                    var buffer = new byte[512];
                    if (SetupDiGetDevicePropertyW(set, ref data, ref local, out var type,
                            buffer, buffer.Length, out var need, 0))
                    {
                        sb.AppendLine($"  pid={local.pid} type={type} need={need} = {Describe(type, buffer, need)}");
                    }
                }
                sb.AppendLine();
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return sb.ToString();
    }

    private static string Describe(uint type, byte[] buffer, int need)
    {
        try
        {
            return type switch
            {
                0x00000003 when need >= 1 => buffer[0].ToString(),
                0x00000004 when need >= 2 => BitConverter.ToUInt16(buffer, 0).ToString(),
                0x00000007 when need >= 4 => BitConverter.ToUInt32(buffer, 0).ToString(),
                0x00000008 when need >= 8 => BitConverter.ToUInt64(buffer, 0).ToString(),
                0x00000012 => Encoding.Unicode.GetString(buffer, 0, need).TrimEnd('\0'),
                0x00000013 => Encoding.Unicode.GetString(buffer, 0, need).TrimEnd('\0'),
                0x0000100B or 0x0000000F => "（字符串列表 " + need + " 字节）",
                _ => "type=0x" + type.ToString("X") + " need=" + need + " " +
                     (need > 0 && need <= 16 ? BitConverter.ToString(buffer, 0, need) : "")
            };
        }
        catch
        {
            return $"type=0x{type:X} need={need}（无法解释）";
        }
    }


    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public int DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public int pid;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string enumerator,
        IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA data,
        StringBuilder buffer, int size, out int required);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA data,
        ref DEVPROPKEY key, out uint type, byte[] buffer, int size, out int required, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiGetDevicePropertyKeys(IntPtr set, ref SP_DEVINFO_DATA data,
        [Out] DEVPROPKEY[] keys, int keyCount, out int required, uint flags);

    private const uint DIGCF_PRESENT = 0x02;
    private const uint DIGCF_ALLCLASSES = 0x04;

    /// <summary>DEVPKEY_PciDevice_* 的属性集 GUID（Windows SDK devpkey.h）。</summary>
    private static readonly Guid PciDevicePropSet = new("3ab22e31-8264-4b4e-9af5-a8d2d8e33e62");

    // 属性编号。
    //
    // 这些编号不是从文档抄的，是**在本机反推出来的**：用 --dma-keys 枚举设备上
    // 全部 PCI 属性，再拿 Get-PnpDeviceProperty 的已知值去对号——真实网卡那条设备上
    // pid=3 读 2（网络控制器）、pid=4 读 0（以太网）、pid=9..12 全读 1（链路 x1/2.5GT）、
    // pid=16 读 513（BarTypes），逐项吻合。
    //
    // 为什么必须实测：最初按记忆写的是 12/13/14/15/17/18/20，结果**全部读成 -1**，
    // 界面上表现为「所有设备都读不到链路信息」。差一点就把「读不到」当成
    // 「这台机器没有异常」报出去，那是最危险的一种错——用空白冒充结论。
    private const int PidCurrentLinkSpeed = 9;
    private const int PidCurrentLinkWidth = 10;
    private const int PidMaxLinkSpeed = 11;
    private const int PidMaxLinkWidth = 12;
    private const int PidDeviceType = 13;
    private const int PidBaseClass = 3;
    private const int PidSubClass = 4;

    private static int ReadIntProperty(IntPtr set, ref SP_DEVINFO_DATA data, int pid)
    {
        var key = new DEVPROPKEY { fmtid = PciDevicePropSet, pid = pid };
        var buffer = new byte[64];

        if (!SetupDiGetDevicePropertyW(set, ref data, ref key, out var type,
                buffer, buffer.Length, out var required, 0))
            return -1;

        if (required <= 0) return -1;

        // DEVPROP_TYPE_BYTE=3 / UINT16=4? 实际这几项都是 UINT32 或 UINT16。
        // 统一按需要长度解释，避免依赖具体 type 常量。
        return required switch
        {
            1 => buffer[0],
            2 => BitConverter.ToUInt16(buffer, 0),
            4 => (int)BitConverter.ToUInt32(buffer, 0),
            8 => (int)BitConverter.ToUInt64(buffer, 0),
            _ => -1
        };
    }

    /// <summary>
    /// 枚举全部 PCI 设备并读属性。
    /// 读不到链路信息的设备照样返回（链路信息为 0），因为「读不到」本身是结论的一部分。
    /// </summary>
    public static List<DeviceFacts> ReadDevices()
    {
        var list = new List<DeviceFacts>();

        var set = SetupDiGetClassDevsW(IntPtr.Zero, "PCI", IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return list;

        try
        {
            var data = new SP_DEVINFO_DATA();
            data.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));

            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                var idBuffer = new StringBuilder(512);
                if (!SetupDiGetDeviceInstanceIdW(set, ref data, idBuffer, 512, out _)) continue;

                var instance = idBuffer.ToString();
                var (vid, did, subsys) = ParseInstanceId(instance);

                list.Add(new DeviceFacts
                {
                    InstanceId = instance,
                    Vid = vid,
                    Did = did,
                    Subsys = subsys,
                    Name = ReadFriendlyName(instance),
                    Service = ReadService(instance),
                    BaseClass = ReadIntProperty(set, ref data, PidBaseClass),
                    SubClass = ReadIntProperty(set, ref data, PidSubClass),
                    CurrentLinkWidth = ReadIntProperty(set, ref data, PidCurrentLinkWidth),
                    MaxLinkWidth = ReadIntProperty(set, ref data, PidMaxLinkWidth),
                    CurrentLinkSpeed = ReadIntProperty(set, ref data, PidCurrentLinkSpeed),
                    MaxLinkSpeed = ReadIntProperty(set, ref data, PidMaxLinkSpeed),
                    DeviceType = ReadIntProperty(set, ref data, PidDeviceType)
                });
            }
        }
        catch
        {
            // 枚举失败就返回已读到的部分，不抛给上层。
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return list;
    }

    /// <summary>从实例路径里拆 VID / DID / 子系统 ID。</summary>
    public static (string Vid, string Did, string Subsys) ParseInstanceId(string instance)
    {
        var vid = ExtractToken(instance, "VEN_");
        var did = ExtractToken(instance, "DEV_");
        var subsys = ExtractToken(instance, "SUBSYS_");
        return (vid, did, subsys);
    }

    private static string ExtractToken(string text, string prefix)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var at = text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return "";

        var start = at + prefix.Length;
        var length = 0;

        while (start + length < text.Length && Uri.IsHexDigit(text[start + length]) && length < 8)
            length++;

        return text.Substring(start, length).ToUpperInvariant();
    }

    private static string ReadFriendlyName(string instance)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\" + instance);
            return key?.GetValue("DeviceDesc") as string is { Length: > 0 } desc
                ? desc.Split(';')[^1]
                : "";
        }
        catch { return ""; }
    }

    private static string ReadService(string instance)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\" + instance);
            return key?.GetValue("Service") as string ?? "";
        }
        catch { return ""; }
    }

    // ------------------------------------------------------------ 判定规则

    /// <summary>
    /// 「这个设备所声称的厂商，在现实中会不会做这一类设备」。
    ///
    /// 只收少数几家消费级主板上最常见、产品线边界清晰的厂商。之所以敢用这条判据，
    /// 是因为这些厂商的 PCIe 产品谱系很窄：Realtek 只做网卡/声卡/读卡器，
    /// 不会出现在显示控制器类里。伪装固件改了 VID 却忘了改类代码时会被这条抓到。
    ///
    /// **不做全量白名单**：把几百个 VID 的合法类都列出来维护成本高、过期快，
    /// 而且一旦漏了合法组合就变成误报。宁可少判。
    /// </summary>
    /// <summary>
    /// 「这家厂商会不会做这一类设备」。
    ///
    /// **刻意不收 Intel 与 AMD**：它们从 CPU、芯片组、网卡、显卡、存储一直做到信号处理和加密引擎，
    /// 几乎覆盖所有设备类，把它们放进白名单等于没有信息量，只会制造误报
    /// （本机实测就误报了一条：Intel 的 Innovation Platform Framework 属于「信号处理」类）。
    ///
    /// 只留 PCIe 产品谱系很窄的厂商：Realtek 做网卡/声卡/读卡器，NVIDIA 做显卡与显卡上的音频，
    /// ASMedia 做 USB/SATA 桥，Broadcom 与 Atheros 做网卡。改过 VID 却忘改类代码的伪装固件，
    /// 会落在这几个矩阵之外。
    /// </summary>
    private static readonly Dictionary<string, (string Vendor, int[] Allowed)> VendorClassMatrix =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["10EC"] = ("Realtek", new[] { 0x01, 0x02, 0x04, 0x08, 0x0C, 0xFF }),
            ["10DE"] = ("NVIDIA", new[] { 0x03, 0x04, 0x06, 0x08, 0x0B, 0xFF }),
            ["1B21"] = ("ASMedia", new[] { 0x01, 0x02, 0x06, 0x0C, 0xFF }),
            ["14E4"] = ("Broadcom", new[] { 0x01, 0x02, 0x06, 0x0C, 0x0D, 0xFF }),
            ["1969"] = ("Atheros", new[] { 0x02, 0x0D, 0xFF })
        };

    /// <summary>已知的 FPGA 厂商（原厂 ID）。伪装时会改掉，所以只报提示、不当结论。</summary>
    private static readonly Dictionary<string, string> FpgaVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["10EE"] = "Xilinx",
        ["1204"] = "Lattice",
        ["1172"] = "Altera / Intel FPGA",
        ["11AA"] = "Xilinx (legacy)"
    };

    /// <summary>
    /// 对一批设备做伪装分析。返回可直接并入深度痕迹报告的条目。
    /// </summary>
    public static List<DeepTraceItem> Analyze(List<DeviceFacts> devices)
    {
        var items = new List<DeepTraceItem>();
        var readable = devices.Where(d => d.HasLinkInfo).ToList();

        items.Add(new DeepTraceItem
        {
            Group = DeepGroup.Dma,
            Name = "PCIe 设备属性读取",
            Detail = devices.Count == 0
                ? "没有枚举到任何 PCI 设备。"
                : $"共 {devices.Count} 个 PCIe 设备，其中 {readable.Count} 个读到了链路宽度与速率。" +
                  (readable.Count == 0
                      ? "读不到通常意味着设备没有 PCIe 能力结构（芯片组内部设备常见），不构成异常。"
                      : ""),
            Level = devices.Count == 0 ? "hint" : "ok",
            Basis = "SetupAPI + DEVPKEY_PciDevice_*"
        });

        // ---------- 规则 1：同一 VID/DID 对应了不同设备类 ----------
        // 这是逻辑矛盾：同一颗芯片不可能既是网卡又是显示控制器。伪装固件复用 DID 时几乎必犯。
        var contradictions = devices
            .Where(d => d.Vid.Length > 0 && d.Did.Length > 0 && d.BaseClass >= 0)
            .GroupBy(d => d.Vid + ":" + d.Did)
            .Where(g => g.Select(d => d.BaseClass).Distinct().Count() > 1)
            .ToList();

        if (contradictions.Count == 0)
        {
            items.Add(new DeepTraceItem
            {
                Group = DeepGroup.Dma,
                Name = "设备标识一致性",
                Detail = "没有发现「同一组 VID/DID 却报告不同设备类」的情况。",
                Level = "ok",
                Basis = $"{devices.Count} 个设备的 VID/DID/类代码交叉比对"
            });
        }
        else
        {
            foreach (var g in contradictions.Take(5))
            {
                var classes = string.Join("、", g.Select(d => d.ClassText).Distinct());
                items.Add(new DeepTraceItem
                {
                    Group = DeepGroup.Dma,
                    Name = $"设备标识矛盾：{g.Key} 同时声明为 {classes}",
                    Detail = "同一个厂商+设备 ID 对应了两种不同的设备类。真实硬件不会这样——" +
                             "同一颗芯片的类代码是固定的。出现这种情况通常是固件只改了 ID 没改类代码，" +
                             "或者系统里存在被克隆的标识。这是逻辑矛盾，不是概率判断。",
                    Level = "warn",
                    Basis = string.Join("　", g.Select(d => d.InstanceId).Take(3))
                });
            }
        }

        // ---------- 规则 2：网络控制器的链路宽度远超真实网卡 ----------
        // 消费级与绝大多数服务器网卡最大 x8（100G 双口）；x16 的网卡几乎不存在。
        var wideNics = readable
            .Where(d => d.BaseClass == 0x02 && d.MaxLinkWidth >= 8)
            .ToList();

        if (wideNics.Count > 0)
        {
            foreach (var d in wideNics.Take(5))
            {
                items.Add(new DeepTraceItem
                {
                    Group = DeepGroup.Dma,
                    Name = $"网络控制器的链路宽度异常：{Display(d)} 声明 x{d.MaxLinkWidth}",
                    Detail = $"这张网卡声称最大 x{d.MaxLinkWidth}，当前 x{d.CurrentLinkWidth}、" +
                             $"{SpeedName(d.CurrentLinkSpeed)}。真实网卡里 x8 已是 100G 双口的规格，" +
                             "x16 的网卡在消费与主流服务器平台上都极罕见。" +
                             "伪装固件直接把 FPGA 的 x8/x16 链路能力报上来时会出现这种组合。" +
                             "但也可能是识别错误或多口网卡，需要人工确认型号。",
                    Level = "hint",
                    Basis = d.InstanceId
                });
            }
        }

        // ---------- 规则 3：显示控制器只有 x1 链路 ----------
        // 真显卡至少 x4（极少），正常 x8/x16。x1 常见于矿卡延长线、转接卡，以及伪装板。
        var narrowGpus = readable
            .Where(d => d.BaseClass == 0x03 && d.MaxLinkWidth >= 8 && d.CurrentLinkWidth > 0 && d.CurrentLinkWidth <= 1)
            .ToList();

        foreach (var d in narrowGpus.Take(5))
        {
            items.Add(new DeepTraceItem
            {
                Group = DeepGroup.Dma,
                Name = $"显示控制器跑在 x{d.CurrentLinkWidth}：{Display(d)}",
                Detail = $"这块显示控制器最大支持 x{d.MaxLinkWidth}，但当前只有 x{d.CurrentLinkWidth}。" +
                         "显卡在空闲时会降速省电，但**链路宽度通常不降**（降的是速率）。" +
                         "宽度掉到 x1 一般是插在转接/延长线上，或者设备本身不是真显卡。",
                Level = "hint",
                Basis = d.InstanceId
            });
        }

        // ---------- 规则 4：厂商与设备类矛盾 ----------
        foreach (var d in devices.Where(d => d.BaseClass >= 0 && VendorClassMatrix.ContainsKey(d.Vid)))
        {
            var (vendor, allowed) = VendorClassMatrix[d.Vid];
            if (allowed.Length == 0 || allowed.Contains(d.BaseClass)) continue;

            items.Add(new DeepTraceItem
            {
                Group = DeepGroup.Dma,
                Name = $"{vendor} 的设备出现在「{d.ClassText}」类：{Display(d)}",
                Detail = $"{vendor}（VID {d.Vid}）的 PCIe 产品线不含「{d.ClassText}」。" +
                         "伪装固件把厂商 ID 改成常见品牌、却保留了原来的类代码时，会出现这种不匹配。" +
                         "注意：厂家产品线会变，这条只是提示，需要你核对设备型号是否真实存在。",
                Level = "hint",
                Basis = d.InstanceId
            });
        }

        // ---------- 汇总：链路信息可读率 ----------
        if (devices.Count > 0 && readable.Count == 0)
        {
            items.Add(new DeepTraceItem
            {
                Group = DeepGroup.Dma,
                Name = "链路信息全部读不到（判定能力受限）",
                Detail = "一个设备的链路宽度都没读到。这意味着上面几条基于链路的判据实际没有生效——" +
                         "不是「没问题」，而是「没法判断」。常见原因：运行在虚拟机里、驱动未提供该属性、" +
                         "或系统版本不支持这些属性键。" +
                         TrimAwareness.AttributionFor("组件包", "驱动", "WMI"),
                Level = "hint",
                Basis = "DEVPKEY_PciDevice_CurrentLinkWidth 全部为空"
            });
        }

        return items;
    }

    private static string Display(DeviceFacts d) =>
        string.IsNullOrWhiteSpace(d.Name) ? $"{d.Vid}:{d.Did}" : d.Name;

    // ------------------------------------------------------ 平台保护状态

    /// <summary>
    /// 读 Windows 自己的 DMA 保护配置。
    ///
    /// 为什么读注册表而不解析 ACPI 的 DMAR 表：
    /// <list type="bullet">
    /// <item>解析 DMAR 只能知道「固件声明了什么」，读注册表能知道「Windows 实际采用了什么策略」。</item>
    /// <item>DMAR 里的 opt-in 位含义容易搞错（bit0 是中断重映射，bit2 才是 DMA 保护 opt-in），
    /// 一旦搞错方向，会把「保护关闭」报成「保护开启」，比不报更糟。</item>
    /// </list>
    ///
    /// 已实测的键：<c>HKLM\SYSTEM\CurrentControlSet\Control\DmaSecurity</c>。
    /// 该键存在表示系统具备 Kernel DMA Protection 机制；其中：
    /// <list type="bullet">
    /// <item><c>AllowAllBuses = 1</c>：所有总线放行，等于没有保护。</item>
    /// <item><c>AllowedBuses</c>：被豁免的 PCI 段列表，豁免的总线不受保护。</item>
    /// <item>键为空（无 AllowAllBuses）：按设备逐个策略，属正常默认。</item>
    /// </list>
    /// </summary>
    public static List<DeepTraceItem> ReadPlatformProtection()
    {
        var items = new List<DeepTraceItem>();

        bool keyExists = false;
        bool allowAll = false;
        var allowedBuses = new List<string>();

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\DmaSecurity");

            if (key != null)
            {
                keyExists = true;

                if (key.GetValue("AllowAllBuses") is int all)
                    allowAll = all != 0;

                if (key.GetValue("AllowedBuses") is string[] buses)
                    allowedBuses.AddRange(buses);
                else if (key.GetValue("AllowedBuses") is string single && single.Length > 0)
                    allowedBuses.Add(single);
            }
        }
        catch { /* 读不到按「未知」处理 */ }

        if (!keyExists)
        {
            items.Add(new DeepTraceItem
            {
                Group = DeepGroup.Dma,
                Name = "平台未提供 DMA 保护机制",
                Detail = "系统里没有 DmaSecurity 配置键。这通常意味着固件/平台不支持 Kernel DMA Protection，" +
                         "或该功能被关闭。此时插在 PCIe / 雷电口的设备默认可直接访问全物理内存，" +
                         "唯一的拦阻只剩 BIOS 里的 VT-d / AMD-Vi。",
                Level = "hint",
                Basis = @"HKLM\SYSTEM\CurrentControlSet\Control\DmaSecurity 不存在"
            });
        }
        else if (allowAll)
        {
            items.Add(new DeepTraceItem
            {
                Group = DeepGroup.Dma,
                Name = "DMA 保护被设为「全部总线放行」",
                Detail = "DmaSecurity\\AllowAllBuses = 1 表示所有 PCI 段都豁免 DMA 重映射。" +
                         "这等于关掉了 Kernel DMA Protection 的保护作用——任何插上的 DMA 设备都能直读内存。",
                Level = "warn",
                Basis = @"DmaSecurity\AllowAllBuses = 1"
            });
        }
        else
        {
            // 注意这里判 hint 而不是 ok。
            // 「策略键存在且没有整段放行」只能说明 Windows 没有被配置成放行全部总线，
            // **不能说明 DMA 重映射真的在运行**——运行与否取决于固件是否 opt-in、
            // BIOS 里 VT-d / AMD-Vi 是否真开。那些信息用户态读不到（固件表接口在部分环境不可用），
            // 所以这里不给「保护正常」的结论，只给「策略层没有放开」。
            items.Add(new DeepTraceItem
            {
                Group = DeepGroup.Dma,
                Name = allowedBuses.Count > 0
                    ? $"DMA 保护策略：{allowedBuses.Count} 个总线被豁免"
                    : "DMA 保护策略：没有整段放行",
                Detail = allowedBuses.Count > 0
                    ? "平台按设备做 DMA 重映射，但下面这些总线被显式豁免：" +
                      string.Join("、", allowedBuses.Take(8)) +
                      "。被豁免总线上的设备不受 IOMMU 隔离，如果里面有你不认识的口，需要查一下。"
                    : "系统里没有把任何总线整段放行，说明没有被配置成「关闭保护」。" +
                      "但这只代表策略层正常：重映射是否真的在跑，还取决于固件 opt-in 与 BIOS 里 VT-d / AMD-Vi 是否启用，" +
                      "这两项用户态读不到，所以不能据此认为「已受保护」。",
                Level = "hint",
                Basis = @"HKLM\SYSTEM\CurrentControlSet\Control\DmaSecurity"
            });
        }

        return items;
    }

    /// <summary>
    /// 自检用：属性编号是不是真的对应到了正确的语义。
    ///
    /// 做法：拿已知设备验证内部关系——最大链路宽度必须 ≥ 当前宽度，且都落在 PCIe 允许的集合里。
    /// 如果属性编号写错了，读出来的会是完全无关的数字（比如把 DeviceType 当成宽度），
    /// 这种关系立刻被破坏。
    /// </summary>
    public static (bool Ok, string Note) VerifyPropertyIds()
    {
        var devices = ReadDevices();
        if (devices.Count == 0)
            return (false, "没枚举到 PCI 设备，无法校验");

        var withLink = devices.Where(d => d.HasLinkInfo).ToList();
        if (withLink.Count == 0)
            return (true, $"枚举 {devices.Count} 个设备，但都读不到链路信息（虚拟机或驱动不提供），跳过关系校验");

        var bad = withLink.Where(d =>
            d.CurrentLinkWidth > d.MaxLinkWidth ||
            d.CurrentLinkWidth > 32 ||
            d.MaxLinkWidth > 32 ||
            d.CurrentLinkSpeed > 6 ||
            d.MaxLinkSpeed > 6).ToList();

        if (bad.Count > 0)
            return (false, $"{bad.Count} 个设备读出矛盾值（当前宽度 > 最大宽度等），属性编号可能错位");

        return (true, $"{withLink.Count}/{devices.Count} 个设备链路数据自洽");
    }

    /// <summary>命令行探针：把读到的原始事实打出来，便于排查属性读取问题。</summary>
    public static string Probe()
    {
        var sb = new StringBuilder();
        var devices = ReadDevices();

        sb.AppendLine($"枚举到 PCI 设备：{devices.Count}");
        sb.AppendLine();

        foreach (var d in devices.OrderBy(x => x.BaseClass).ThenBy(x => x.InstanceId))
        {
            sb.AppendLine($"{Display(d)}");
            sb.AppendLine($"  实例   {d.InstanceId}");
            sb.AppendLine($"  标识   VID={d.Vid} DID={d.Did} SUBSYS={d.Subsys}");
            sb.AppendLine($"  类     {d.ClassText}（0x{d.BaseClass:X2}/0x{d.SubClass:X2}）");
            sb.AppendLine($"  链路   {d.WidthText}  {d.SpeedText}");
            sb.AppendLine($"  类型   DeviceType={d.DeviceType}  驱动={d.Service}");
            sb.AppendLine();
        }

        sb.AppendLine("---- 判定 ----");
        foreach (var item in Analyze(devices).Concat(ReadPlatformProtection()))
        {
            sb.AppendLine($"[{item.Level}] {item.Name}");
            sb.AppendLine($"        {item.Detail}");
        }

        return sb.ToString();
    }
}
