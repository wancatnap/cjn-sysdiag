using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 机器码伪造检测：**不需要出厂基线**，直接看当前这台机器的标识符自不自洽、内核环境有没有被改造。
///
/// 为什么需要它：<see cref="MachineIdentityService"/> 是「先登记、后对比」的快照式校验，
/// 如果对方在登记之前就改了机器码，基线记下来的就是改后的值，之后永远显示一致。
/// 这里换一条思路——不跟历史比，只跟「自己应该长什么样」比。
///
/// 参考实现（都在 GitHub 上公开，思路借鉴、代码自己写）：
/// <list type="bullet">
/// <item><b>VMAware</b>（NotRequiem/VMAware，MIT）：评分模型——每项技术按「可靠度 + 特异性」
///       给分，再按误报概率打折，命中累加后过阈值才下结论。本文件的权重与四档结论沿用这个结构，
///       好处是单条弱信号不会直接把机器打成「作弊」，必须组合出现才升级。</item>
/// <item><b>al-khaser</b>（LordNoteworthy/al-khaser）：标识符黑名单的具体取值——
///       BIOS 序列号为 "0" / "A M I"、"To be filled by O.E.M."、"Default string" 这类厂商占位值；
///       厂商/型号里的 VMware、VirtualBox、innotek GmbH、QEMU、HVM domU、Xen；
///       以及「枚举内核驱动服务再比对名单」的做法（Services.cpp 用 EnumServicesStatusEx 只取 SERVICE_DRIVER）。</item>
/// <item><b>pafish</b>（a0rtega/pafish）：MAC 地址 OUI 段与「本地管理地址位」的判断思路。</item>
/// </list>
///
/// 全部只读：不改注册表、不装驱动、不发网络请求。
/// </summary>
public static class HwidIntegrityService
{
    // =========================================================
    // 取值表
    // =========================================================

    /// <summary>
    /// 厂商占位值：主板厂没写序列号时会留这些串。
    /// 取自已公开的检测库里出现频率最高的一批（al-khaser 的 "0" / "A M I" 判断，
    /// 加上常见 OEM 默认串）。命中不算「被改」，但要计入弱信号。
    /// </summary>
    private static readonly string[] Placeholders =
    {
        "", "-", "0", "00", "00000000", "NONE", "NULL", "UNKNOWN", "NOT SPECIFIED",
        "NOT APPLICABLE", "NA", "N/A", "DEFAULT STRING", "TO BE FILLED BY O.E.M.",
        "TO BE FILLED BY OEM", "OEM", "O.E.M.", "SYSTEM SERIAL NUMBER", "SERIAL NUMBER",
        "BASE BOARD SERIAL NUMBER", "CHASSIS SERIAL NUMBER", "SYSTEM MANUFACTURER",
        "SYSTEM PRODUCT NAME", "A M I", "AMI", "XXXXXXX", "XXXXXXXX",
        "0123456789", "123456789", "1234567890",
        "00000000-0000-0000-0000-000000000000",
        "FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF"
    };

    /// <summary>
    /// 已知的主板厂「未写入」默认 UUID。真机出厂没烧 UUID 时常见这两个值，
    /// 所以只算弱信号——但改机工具也很爱直接抄它们。
    /// </summary>
    private static readonly string[] OemDefaultUuids =
    {
        "03000200-0400-0500-0006-000700080009",
        "00020003-0004-0005-0006-000700080009",
        "12345678-1234-1234-1234-123456789012",
        "FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF",
        "00000000-0000-0000-0000-000000000000"
    };

    /// <summary>虚拟化 / 云主机的厂商与型号关键字（取自 al-khaser 的 WMI 检测串）。</summary>
    private static readonly string[] VirtualHints =
    {
        "VMWARE", "VIRTUALBOX", "VBOX", "INNOTEK", "QEMU", "XEN", "BOCHS", "PARALLELS",
        "VIRTUAL MACHINE", "VIRTUAL PC", "HVM DOMU", "KVM", "HYPER-V", "VIRTUALBOX",
        "VIRTIO", "OPENSTACK", "CLOUD", "ALIYUN", "TENCENT", "AZURE", "GOOGLE COMPUTE"
    };

    /// <summary>虚拟机常用的 MAC OUI 前缀（pafish / VMAware 里同一批）。</summary>
    private static readonly string[] VirtualMacPrefixes =
    {
        "00:05:69", "00:0C:29", "00:1C:14", "00:50:56",   // VMware
        "08:00:27",                                        // VirtualBox
        "52:54:00",                                        // QEMU / KVM
        "00:16:3E",                                        // Xen
        "00:15:5D",                                        // Hyper-V
        "00:1C:42",                                        // Parallels
        "00:03:FF"                                         // Virtual PC
    };

    /// <summary>会被判定为「启动配置被改造」的三个开关。</summary>
    private static readonly (string Flag, string Label)[] BootFlags =
    {
        ("testsigning", "测试签名"),
        ("nointegritychecks", "驱动完整性校验"),
        ("disableelamdrivers", "ELAM 早期反恶意驱动")
    };

    // =========================================================
    // 主流程
    // =========================================================

    public static HwidIntegrityResult Run()
    {
        var checks = new List<HwidCheck>();

        var csp = First("SELECT UUID, IdentifyingNumber, Vendor, Name FROM Win32_ComputerSystemProduct");
        var board = First("SELECT Manufacturer, Product, SerialNumber FROM Win32_BaseBoard");
        var bios = First("SELECT Manufacturer, SMBIOSBIOSVersion, SerialNumber, ReleaseDate FROM Win32_BIOS");
        var sys = First("SELECT Manufacturer, Model FROM Win32_ComputerSystem");

        var uuid = EvidenceService.Str(csp, "UUID");
        var systemSn = EvidenceService.Str(csp, "IdentifyingNumber");
        var boardSn = EvidenceService.Str(board, "SerialNumber");
        var biosSn = EvidenceService.Str(bios, "SerialNumber");

        var named = new (string Name, string Value)[]
        {
            ("SMBIOS UUID", uuid),
            ("整机序列号", systemSn),
            ("主板序列号", boardSn),
            ("BIOS 序列号", biosSn)
        };

        checks.Add(CheckPlaceholders(named));
        checks.Add(CheckDuplicates(named));
        checks.Add(CheckUuidFormat(uuid));
        checks.Add(CheckMachineGuid());
        checks.Add(CheckDiskSerial());
        checks.Add(CheckMacAddress());
        checks.Add(CheckVolumeSerial());
        checks.Add(CheckBootFlags());
        checks.Add(CheckDriverPaths());
        checks.Add(CheckVirtualMachine(csp, board, bios, sys));
        checks.Add(CheckBiosDate(bios));
        checks.Add(CheckDiskHistory());

        var score = Math.Min(100, checks.Where(c => c.Triggered).Sum(c => c.Weight));

        return new HwidIntegrityResult
        {
            Checks = checks,
            Score = score,
            Admin = ElevationService.IsAdmin
        };
    }

    // =========================================================
    // 1~4 标识符本身
    // =========================================================

    /// <summary>四个关键标识里有多少是空值 / 厂商占位串。</summary>
    private static HwidCheck CheckPlaceholders((string Name, string Value)[] fields)
    {
        var bad = fields.Where(f => IsPlaceholder(f.Value)).Select(f => f.Name).ToList();

        return new HwidCheck
        {
            Id = "id-placeholder",
            Title = "标识符为空或厂商默认值",
            Category = "标识符",
            // 4 个里缺 2 个以上才升级成强信号：单个空值是低端主板的常态
            Weight = bad.Count >= 2 ? 24 : bad.Count == 1 ? 12 : 0,
            Triggered = bad.Count > 0,
            Detail = bad.Count == 0
                ? "UUID、整机、主板、BIOS 四个序列号都有正常取值。"
                : $"{string.Join("、", bad)} 是空值或厂商占位串（共 {bad.Count}/4 项）。" +
                  (bad.Count >= 2
                      ? "多个关键标识同时缺失，改机工具常见的一键清空特征；也可能是主板厂真的没烧号。"
                      : "单项缺失通常是主板厂没写入，属于弱信号。"),
            Source = "Win32_ComputerSystemProduct / Win32_BaseBoard / Win32_BIOS"
        };
    }

    /// <summary>
    /// 多个字段被写成同一个值——一键伪造的典型特征。
    ///
    /// 例外：OEM 笔电经常让「整机序列号」和「BIOS 序列号」取同一个值（BIOS 里的序列号就是整机号），
    /// 实测在正常机器上很普遍，所以这一对不算证据，其它任意跨字段重复才算。
    /// </summary>
    private static HwidCheck CheckDuplicates((string Name, string Value)[] fields)
    {
        var candidates = fields.Where(f => !IsPlaceholder(f.Value)).ToList();

        var groups = candidates
            .GroupBy(f => f.Value.Trim().ToUpperInvariant())
            .Where(g => g.Count() >= 2)
            .ToList();

        var exempt = new List<string>();
        var evidence = new List<string>();

        foreach (var g in groups)
        {
            var names = g.Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var isOemPair = names.Count == 2 && names.Contains("整机序列号") && names.Contains("BIOS 序列号");

            if (isOemPair) exempt.Add($"整机序列号 = BIOS 序列号 = {Shorten(g.Key, 32)}（OEM 笔电常态）");
            else evidence.Add($"{string.Join(" = ", names)} = {Shorten(g.Key, 40)}");
        }

        var triggered = evidence.Count > 0;

        return new HwidCheck
        {
            Id = "id-dup",
            Title = "多个标识符被写成同一个值",
            Category = "标识符",
            Weight = triggered ? 22 : 0,
            Triggered = triggered,
            Detail = triggered
                ? string.Join("；", evidence) +
                  "。UUID 与各序列号由不同厂商在不同环节写入，跨字段雷同基本只有一种解释：被同一个值覆盖过。" +
                  (exempt.Count > 0 ? $"（已排除正常情况：{string.Join("、", exempt)}）" : "")
                : exempt.Count > 0
                    ? $"只有「整机序列号 = BIOS 序列号」这一对相同，这在 OEM 笔电上是正常写法，不计入。其余标识互不相同。"
                    : "UUID 与三类序列号互不相同，符合真机特征。",
            Source = "同上四个字段的交叉比对"
        };
    }

    /// <summary>UUID 必须是合法 GUID，且不能是全 0 / 全 F / 主板厂默认值。</summary>
    private static HwidCheck CheckUuidFormat(string uuid)
    {
        if (IsPlaceholder(uuid))
        {
            return new HwidCheck
            {
                Id = "uuid-format",
                Title = "UUID 格式与取值",
                Category = "标识符",
                Triggered = false,
                Detail = "UUID 为空或占位值，已在「标识符为空」一项计入，这里不重复扣分。",
                Source = "Win32_ComputerSystemProduct.UUID"
            };
        }

        var legal = Guid.TryParse(uuid, out var guid);
        var flat = uuid.Replace("-", "").ToUpperInvariant();
        var degenerate = flat.Length == 0 || flat.Distinct().Count() <= 1 || flat is "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF" or "00000000000000000000000000000000";
        var oemDefault = OemDefaultUuids.Contains(uuid.ToUpperInvariant());

        if (!legal || degenerate)
        {
            return new HwidCheck
            {
                Id = "uuid-format",
                Title = "UUID 格式与取值",
                Category = "标识符",
                Weight = 26,
                Triggered = true,
                Detail = $"当前值「{Shorten(uuid, 48)}」{(legal ? "是全 0 / 全 F 这类退化值" : "不是合法的 GUID 格式")}。" +
                         "Windows 与绝大多数游戏从这个字段取机器指纹，正常主板烧录后一定是标准 GUID。",
                Source = "Win32_ComputerSystemProduct.UUID"
            };
        }

        return new HwidCheck
        {
            Id = "uuid-format",
            Title = "UUID 格式与取值",
            Category = "标识符",
            Weight = oemDefault ? 12 : 0,
            Triggered = oemDefault,
            Detail = oemDefault
                ? $"当前值「{guid}」是主板厂「未写入」时的默认模板值（多家厂商共用同一个）。" +
                  "有的真机出厂就是这样，改机工具也常直接照抄它，属于弱信号。"
                : $"当前值「{guid}」是合法 GUID，且不是已知的默认模板值。",
            Source = "Win32_ComputerSystemProduct.UUID"
        };
    }

    /// <summary>MachineGuid 被单独改写，是 Spoofer 的常见动作。</summary>
    private static HwidCheck CheckMachineGuid()
    {
        var raw = EvidenceService.RegStr(@"SOFTWARE\Microsoft\Cryptography", "MachineGuid");

        if (string.IsNullOrWhiteSpace(raw))
        {
            return new HwidCheck
            {
                Id = "machine-guid",
                Title = "系统 MachineGuid",
                Category = "标识符",
                Skipped = true,
                Detail = "读不到 MachineGuid（注册表不可读）。",
                Source = @"HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid"
            };
        }

        var flat = raw.Replace("-", "").ToUpperInvariant();
        var legal = Guid.TryParse(raw, out _);
        var degenerate = flat.Length == 0 || flat.Distinct().Count() <= 1;
        var cloned = flat is "11111111222233334444555555555555" or "12345678123456781234567812345678";

        var bad = !legal || degenerate || cloned;

        return new HwidCheck
        {
            Id = "machine-guid",
            Title = "系统 MachineGuid",
            Category = "标识符",
            Weight = bad ? 14 : 0,
            Triggered = bad,
            Detail = bad
                ? $"当前值「{Shorten(raw, 48)}」不是正常安装生成的形式（非法 GUID / 退化值 / 明显的占位串）。" +
                  "装系统时这个值由 Windows 随机生成，被改成固定串说明有人动过。"
                : $"当前值「{Shorten(raw, 40)}」是正常生成的 GUID。",
            Source = @"HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid"
        };
    }

    // =========================================================
    // 5~7 磁盘与网络
    // =========================================================

    /// <summary>磁盘 0 的序列号是否像真盘。</summary>
    private static HwidCheck CheckDiskSerial()
    {
        var disk = First("SELECT Index, Model, SerialNumber FROM Win32_DiskDrive WHERE Index=0");
        var model = EvidenceService.Str(disk, "Model");
        var sn = EvidenceService.Str(disk, "SerialNumber").Trim().TrimEnd('.');

        if (model.Length == 0 && sn.Length == 0)
        {
            return new HwidCheck
            {
                Id = "disk-serial",
                Title = "系统盘序列号",
                Category = "磁盘",
                Skipped = true,
                Detail = "读不到磁盘 0 的信息（可能是权限或驱动问题）。",
                Source = "Win32_DiskDrive"
            };
        }

        var flat = sn.Replace("-", "").Replace(" ", "").Trim();
        var tooShort = sn.Length > 0 && flat.Length < 6;
        var degenerate = flat.Length > 0 && flat.Distinct().Count() <= 1;
        var bad = IsPlaceholder(sn) || tooShort || degenerate;

        return new HwidCheck
        {
            Id = "disk-serial",
            Title = "系统盘序列号",
            Category = "磁盘",
            Weight = bad ? 16 : 0,
            Triggered = bad,
            Detail = bad
                ? $"磁盘 0「{Shorten(model, 40)}」的序列号「{Shorten(sn, 24)}」{(flat.Length == 0 ? "为空" : tooShort ? "过短" : degenerate ? "是重复字符" : "是占位值")}。" +
                  "真盘序列号由厂线写入，长度与字符集都有规律；序列号被抹平后无法再从硬盘本身追到来源。"
                : $"磁盘 0「{Shorten(model, 40)}」序列号「{sn}」格式正常。",
            Source = "Win32_DiskDrive.SerialNumber"
        };
    }

    /// <summary>物理网卡的 MAC 是否被手工设置过（本地管理地址位）。</summary>
    private static HwidCheck CheckMacAddress()
    {
        List<string> laa;
        List<string> virtualOui;

        try
        {
            var nics = PhysicalNics().ToList();
            if (nics.Count == 0)
            {
                return new HwidCheck
                {
                    Id = "mac-laa",
                    Title = "物理网卡 MAC",
                    Category = "网络",
                    Skipped = true,
                    Detail = "没有找到物理网卡。",
                    Source = "NetworkInterface"
                };
            }

            var all = nics.Select(n => (Name: n.Name, Mac: MacOf(n))).ToList();

            // 第一字节的 bit1 = 本地管理地址位：正常出厂网卡是全球管理（UAA），置位说明被改过
            laa = all.Where(x => x.Mac.Length >= 2 && (Convert.ToByte(x.Mac[..2], 16) & 0x02) != 0)
                     .Select(x => $"{x.Name} {x.Mac}")
                     .ToList();

            virtualOui = all.Where(x => VirtualMacPrefixes.Any(p => x.Mac.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                            .Select(x => $"{x.Name} {x.Mac}")
                            .ToList();

            var bad = laa.Count > 0 || virtualOui.Count > 0;

            return new HwidCheck
            {
                Id = "mac-laa",
                Title = "物理网卡 MAC",
                Category = "网络",
                Weight = bad ? 18 : 0,
                Triggered = bad,
                Detail = !bad
                    ? $"{all.Count} 块物理网卡的 MAC 都是全球管理地址（UAA），没有被手工设置过。"
                    : (laa.Count > 0
                        ? $"本地管理地址（LAA）位被置位：{string.Join("；", laa.Take(3))}。IEEE 规定这一位由本地管理员设置，" +
                          "出厂网卡不会带这一位——正常网卡出现它，只有「有人手工改了 MAC」一种解释。"
                        : "") +
                      (virtualOui.Count > 0
                          ? $"MAC 落在虚拟机厂商的 OUI 段：{string.Join("；", virtualOui.Take(3))}。"
                          : ""),
                Source = "NetworkInterface.GetAllNetworkInterfaces + IEEE OUI 判断"
            };
        }
        catch (Exception ex)
        {
            return new HwidCheck
            {
                Id = "mac-laa",
                Title = "物理网卡 MAC",
                Category = "网络",
                Skipped = true,
                Detail = "枚举网卡失败：" + ex.Message,
                Source = "NetworkInterface"
            };
        }
    }

    /// <summary>两个不同卷的序列号相同——系统盘来自同一份克隆镜像。</summary>
    private static HwidCheck CheckVolumeSerial()
    {
        try
        {
            var vols = EvidenceService
                .Wmi("SELECT DeviceID, VolumeSerialNumber FROM Win32_LogicalDisk WHERE DriveType=3")
                .Select(v => (Drive: EvidenceService.Str(v, "DeviceID"), Serial: EvidenceService.Str(v, "VolumeSerialNumber").Trim()))
                .Where(v => v.Serial.Length > 0)
                .ToList();

            var dup = vols
                .GroupBy(v => v.Serial, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Select(x => x.Drive).Distinct().Count() >= 2)
                .ToList();

            return new HwidCheck
            {
                Id = "vol-serial",
                Title = "卷序列号是否重复",
                Category = "磁盘",
                Weight = dup.Count > 0 ? 12 : 0,
                Triggered = dup.Count > 0,
                Detail = vols.Count == 0
                    ? "没有可读的本地卷。"
                    : dup.Count == 0
                        ? $"{vols.Count} 个本地卷的序列号互不相同（格式化时随机生成）。"
                        : $"以下卷序列号重复：{string.Join("；", dup.Select(g => $"{string.Join("/", g.Select(x => x.Drive))} = {g.Key}"))}。" +
                          "卷序列号是格式化时随机生成的，两块独立的盘不可能相同；重复通常意味着它们是从同一份克隆镜像还原的。",
                Source = "Win32_LogicalDisk.VolumeSerialNumber"
            };
        }
        catch (Exception ex)
        {
            return new HwidCheck
            {
                Id = "vol-serial",
                Title = "卷序列号是否重复",
                Category = "磁盘",
                Skipped = true,
                Detail = "读取卷序列号失败：" + ex.Message,
                Source = "Win32_LogicalDisk"
            };
        }
    }

    // =========================================================
    // 8~9 内核环境
    // =========================================================

    /// <summary>
    /// 启动配置里的签名/完整性开关——改机驱动几乎都要先关掉它们。
    ///
    /// 主来源是注册表 <c>Control\SystemStartOptions</c>（免管理员，实测可信）；
    /// bcdedit 只在能跑通时作为交叉验证，跑不通不影响结论。
    /// </summary>
    private static HwidCheck CheckBootFlags()
    {
        var startOptions = "";
        try
        {
            startOptions = EvidenceService.RegStr(@"SYSTEM\CurrentControlSet\Control", "SystemStartOptions") ?? "";
        }
        catch { /* 读不到就走 bcdedit */ }

        var (bcdOk, bcdText) = RunBcdEdit();
        var text = startOptions + "\n" + (bcdOk ? bcdText : "");

        if (startOptions.Trim().Length == 0 && !bcdOk)
        {
            return new HwidCheck
            {
                Id = "boot-flags",
                Title = "驱动签名与完整性校验",
                Category = "内核",
                Skipped = true,
                Detail = "读不到启动配置（注册表与 bcdedit 都不可用）。这一项在判断改机时最有分量，建议以管理员身份重跑。",
                Source = @"Control\SystemStartOptions / bcdedit /enum {current}"
            };
        }

        var hits = BootFlags
            .Where(f => text.Contains(f.Flag, StringComparison.OrdinalIgnoreCase) &&
                        Regex.IsMatch(text, $@"{f.Flag}[= \t]*(\?|Yes|ON|1)\b", RegexOptions.IgnoreCase))
            .Select(f => f.Label + $"（{f.Flag}）")
            .ToList();

        return new HwidCheck
        {
            Id = "boot-flags",
            Title = "驱动签名与完整性校验",
            Category = "内核",
            Weight = hits.Count > 0 ? 30 : 0,
            Triggered = hits.Count > 0,
            Detail = hits.Count == 0
                ? "测试签名、驱动完整性校验、ELAM 三个开关都处于默认的强制状态。"
                : $"以下开关被关闭：{string.Join("、", hits)}。" +
                  "这是加载未签名内核驱动的常规前置步骤，而改机工具（伪造 SMBIOS / 磁盘序列号）绝大多数都是未签名驱动。" +
                  "普通玩家没有任何理由关掉它们。",
            Source = bcdOk
                ? "Control\\SystemStartOptions + bcdedit（两处一致）"
                : "Control\\SystemStartOptions"
        };
    }

    /// <summary>内核驱动从临时目录 / 用户目录加载（或指向不存在的文件）。</summary>
    private static HwidCheck CheckDriverPaths()
    {
        try
        {
            using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (services == null)
            {
                return new HwidCheck
                {
                    Id = "driver-path",
                    Title = "内核驱动加载路径",
                    Category = "内核",
                    Skipped = true,
                    Detail = "读不到服务注册表。",
                    Source = @"HKLM\SYSTEM\CurrentControlSet\Services"
                };
            }

            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var odd = new List<string>();
            var total = 0;

            foreach (var name in services.GetSubKeyNames())
            {
                using var key = services.OpenSubKey(name);
                if (key == null) continue;

                // 只关心内核驱动（1）与文件系统驱动（2），用户态服务不参与判断
                if (key.GetValue("Type") is not int type || (type & 0xFF) is not (1 or 2)) continue;

                var image = key.GetValue("ImagePath") as string;
                if (string.IsNullOrWhiteSpace(image)) continue;

                total++;
                var full = NormalizeImagePath(image, windows);
                if (full.Length == 0) continue;

                if (IsDropZone(full)) odd.Add($"{name} → {Shorten(image, 64)}");
            }

            return new HwidCheck
            {
                Id = "driver-path",
                Title = "内核驱动加载路径",
                Category = "内核",
                Weight = odd.Count > 0 ? 22 : 0,
                Triggered = odd.Count > 0,
                Detail = odd.Count == 0
                    ? $"共 {total} 个内核驱动，都不在临时目录或用户目录里（System32\\drivers、DriverStore、Program Files 均属正常）。"
                    : $"{odd.Count} 个内核驱动从临时目录 / 用户目录加载（共 {total} 个）：{string.Join("；", odd.Take(4))}。" +
                      "正常驱动由安装程序放进 System32\\drivers；放在 Temp、用户目录、下载目录，通常是「临时加载、用完即删」的改机/外挂驱动。",
                Source = @"HKLM\SYSTEM\CurrentControlSet\Services\*\ImagePath"
            };
        }
        catch (Exception ex)
        {
            return new HwidCheck
            {
                Id = "driver-path",
                Title = "内核驱动加载路径",
                Category = "内核",
                Skipped = true,
                Detail = "枚举驱动失败：" + ex.Message,
                Source = @"HKLM\SYSTEM\CurrentControlSet\Services"
            };
        }
    }

    // =========================================================
    // 10~12 环境与时间线
    // =========================================================

    /// <summary>整机厂商 / 型号 / 主板 / BIOS 里是否出现虚拟化关键字。</summary>
    private static HwidCheck CheckVirtualMachine(
        Dictionary<string, object> csp,
        Dictionary<string, object> board,
        Dictionary<string, object> bios,
        Dictionary<string, object> sys)
    {
        var pairs = new (string Field, string Value)[]
        {
            ("整机厂商", EvidenceService.Str(csp, "Vendor")),
            ("整机型号", EvidenceService.Str(csp, "Name")),
            ("系统厂商", EvidenceService.Str(sys, "Manufacturer")),
            ("系统型号", EvidenceService.Str(sys, "Model")),
            ("主板厂商", EvidenceService.Str(board, "Manufacturer")),
            ("BIOS 厂商", EvidenceService.Str(bios, "Manufacturer"))
        };

        var hits = pairs
            .Where(p => p.Value.Length > 0 &&
                        VirtualHints.Any(h => p.Value.Contains(h, StringComparison.OrdinalIgnoreCase)))
            .Select(p => $"{p.Field}「{Shorten(p.Value, 40)}」")
            .ToList();

        return new HwidCheck
        {
            Id = "vm-env",
            Title = "是否运行在虚拟机里",
            Category = "环境",
            // 权重压低：虚拟机不是作弊证据，但它意味着机器码本来就不固定
            Weight = hits.Count > 0 ? 8 : 0,
            Triggered = hits.Count > 0,
            Detail = hits.Count == 0
                ? "厂商与型号都是实体机特征，没有命中虚拟化关键字。"
                : $"命中虚拟化关键字：{string.Join("、", hits)}。" +
                  "在虚拟机 / 云主机里，SMBIOS 与磁盘都是虚拟设备，机器码本就不固定——这类环境下的机器码不具备「唯一标识一台物理机」的意义，需要单独说明。",
            Source = "Win32_ComputerSystem / BaseBoard / BIOS 的厂商与型号字段"
        };
    }

    /// <summary>BIOS 发布日期晚于系统安装日期：刷过 BIOS（正常升级，也可能是刷了改过的）。</summary>
    private static HwidCheck CheckBiosDate(Dictionary<string, object> bios)
    {
        var biosDate = EvidenceService.WmiDate(EvidenceService.Str(bios, "ReleaseDate"));
        var install = InstallDate();

        if (biosDate.Length == 0 || install == null)
        {
            return new HwidCheck
            {
                Id = "bios-date",
                Title = "BIOS 与系统安装时间线",
                Category = "环境",
                Skipped = true,
                Detail = "取不到 BIOS 日期或系统安装日期。",
                Source = "Win32_BIOS.ReleaseDate / InstallDate"
            };
        }

        var parsed = DateTime.TryParse(biosDate, out var bd) ? bd : DateTime.MinValue;
        var newer = parsed > install.Value.AddDays(30);

        return new HwidCheck
        {
            Id = "bios-date",
            Title = "BIOS 与系统安装时间线",
            Category = "环境",
            Weight = newer ? 8 : 0,
            Triggered = newer,
            Detail = newer
                ? $"BIOS 日期 {parsed:yyyy-MM-dd} 晚于系统安装日期 {install:yyyy-MM-dd}。" +
                  "装完系统之后刷过 BIOS。正常升级 BIOS 也会这样，但「为了改 SMBIOS 而重刷」同样是这个特征，属于弱信号。"
                : $"BIOS 日期 {parsed:yyyy-MM-dd}，系统安装于 {install:yyyy-MM-dd}，时间线正常。",
            Source = "Win32_BIOS.ReleaseDate vs HKLM\\...\\CurrentVersion\\InstallDate"
        };
    }

    /// <summary>Windows 记得历史上挂过的内置盘——多于一块说明换过盘。</summary>
    private static HwidCheck CheckDiskHistory()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Disk\Enum");
            if (key == null)
            {
                return new HwidCheck
                {
                    Id = "disk-history",
                    Title = "历史上挂过的磁盘",
                    Category = "磁盘",
                    Skipped = true,
                    Detail = "读不到磁盘枚举历史。",
                    Source = @"HKLM\SYSTEM\CurrentControlSet\Services\Disk\Enum"
                };
            }

            var models = new List<string>();
            foreach (var name in key.GetValueNames())
            {
                if (key.GetValue(name) is not string instance) continue;

                // 只看内置盘（SCSI / IDE 前缀）；USBSTOR 是移动硬盘，插拔属于正常使用
                if (!instance.StartsWith("SCSI\\", StringComparison.OrdinalIgnoreCase) &&
                    !instance.StartsWith("IDE\\", StringComparison.OrdinalIgnoreCase)) continue;

                var m = Regex.Match(instance, @"VEN_([^&\\]+)&PROD_([^\\]+)", RegexOptions.IgnoreCase);
                var label = m.Success ? $"{m.Groups[1].Value} {m.Groups[2].Value}".Replace('_', ' ') : Shorten(instance, 48);
                if (!models.Contains(label, StringComparer.OrdinalIgnoreCase)) models.Add(label);
            }

            var many = models.Count > 1;

            return new HwidCheck
            {
                Id = "disk-history",
                Title = "历史上挂过的磁盘",
                Category = "磁盘",
                Weight = many ? 8 : 0,
                Triggered = many,
                Detail = models.Count == 0
                    ? "没有读到内置盘的枚举记录。"
                    : $"{models.Count} 个内置盘设备：{string.Join(" / ", models.Take(4))}。" +
                      (many
                          ? "多于一块，说明这台机器换过（或加过）内置硬盘。换盘本身很常见，但如果当事人声称「从没动过硬件」，这就是矛盾点。"
                          : "只有一块，与当前配置一致。"),
                Source = @"HKLM\SYSTEM\CurrentControlSet\Services\Disk\Enum"
            };
        }
        catch (Exception ex)
        {
            return new HwidCheck
            {
                Id = "disk-history",
                Title = "历史上挂过的磁盘",
                Category = "磁盘",
                Skipped = true,
                Detail = "读取磁盘枚举历史失败：" + ex.Message,
                Source = @"HKLM\SYSTEM\CurrentControlSet\Services\Disk\Enum"
            };
        }
    }

    // =========================================================
    // 报告
    // =========================================================

    public static ReportPayload BuildReport(HwidIntegrityResult result)
    {
        var doc = new HtmlDoc("机器码伪造检测",
            $"{Environment.MachineName}　·　{DateTime.Now:yyyy-MM-dd HH:mm:ss}　·　{result.VerdictText}　·　风险分 {result.Score}");

        doc.Kpis(
            (result.Score.ToString(), "风险分", result.VerdictColor),
            (result.HitCount.ToString(), "命中项", result.HitCount > 0 ? Palette.WarnHex : Palette.OkHex),
            (result.CheckedCount.ToString(), "已检查", Palette.KnownHex),
            (result.SkippedCount.ToString(), "未检查", result.SkippedCount > 0 ? Palette.HintHex : Palette.NoneHex));

        doc.Note($"<b>{HtmlDoc.E(result.Headline)}</b><br/>{HtmlDoc.E(result.Detail)}");

        doc.H2("检查项");
        doc.Table(
            new[] { "检查项", "分类", "结论", "分数", "依据" },
            result.Checks.Select(c => new[]
            {
                $"<b>{HtmlDoc.E(c.Title)}</b><br/><span class=\"muted\">{HtmlDoc.E(c.Detail)}</span>",
                HtmlDoc.E(c.Category),
                c.Skipped
                    ? "<span class=\"muted\">未检查</span>"
                    : c.Triggered
                        ? c.Weight >= 22 ? "<span class=\"tag t-warn\">可疑</span>" : "<span class=\"tag t-hint\">提示</span>"
                        : "<span class=\"tag t-ok\">通过</span>",
                c.ScoreText,
                $"<span class=\"mono\">{HtmlDoc.E(c.Source)}</span>"
            }),
            new[] { "", "66", "66", "44", "220" });

        doc.H2("怎么读这个结果");
        doc.Raw("<div class=\"note\">" +
                "1. <b>这个检测不需要任何登记。</b>它只判断当前机器的标识符是否自洽、内核环境是否被改造，换到任何一台机器都能直接跑。<br/>" +
                "2. <b>弱信号（+8 ~ +18）</b>：厂商没写序列号、USB 网卡带本地管理地址、换过硬盘等，单个出现很常见，不能直接下结论。<br/>" +
                "3. <b>强信号（+22 以上）</b>：多个标识符被写成同一个值、UUID 非法、驱动签名被关、驱动从临时目录加载——这些是程序化改写的典型特征。<br/>" +
                "4. <b>分数怎么来的</b>：每项按「可靠性 + 对改机的特异性」给权重，命中即累加，满分封顶 100；<24 需留意，24~54 可疑，≥55 高度疑似。" +
                "权重结构参考 VMAware 的评分方式（可靠度 + 特异性，按误报概率打折）。<br/>" +
                "5. <b>检测不到的情况</b>：直接改 BIOS/UEFI 里的 SMBIOS、或者在更底层伪造（例如虚拟机 + 全套硬改），用户态程序看不到，" +
                "这类只能靠现场比对、序列号登记表或厂商后台核实。" +
                "</div>");

        var html = doc.Build();
        var path = HtmlReportBuilder.Save(html, "机器码伪造检测");

        var data = new ReportData
        {
            Kind = "hwid-integrity",
            Title = "机器码伪造检测",
            Subtitle = $"{result.VerdictText} · 风险分 {result.Score} · 命中 {result.HitCount}/{result.CheckedCount}",
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
                Score = result.Score,
                Level = result.Verdict switch
                {
                    HwidVerdict.Clean => "低",
                    HwidVerdict.Notice => "低",
                    HwidVerdict.Suspicious => "中",
                    _ => "高"
                },
                Text = result.VerdictText,
                Counts = new ReportCounts
                {
                    Warn = result.StrongCount,
                    Hint = result.WeakCount,
                    Ok = result.Checks.Count(c => !c.Triggered && !c.Skipped)
                }
            },
            Sections = new List<ReportSection>
            {
                new()
                {
                    Group = "检查项",
                    Items = result.Checks.Select(c => new ReportItem
                    {
                        Title = c.Title,
                        Detail = c.Detail,
                        Basis = c.Source,
                        Level = c.Level
                    }).ToList()
                }
            }
        };

        return new ReportPayload
        {
            Title = "机器码伪造检测",
            Subtitle = $"{result.VerdictText} · 风险分 {result.Score} · 命中 {result.HitCount} 项",
            EngineTag = "伪造检测",
            HtmlPath = path,
            PlainSummary = $"机器码伪造检测\n结论：{result.VerdictText}（风险分 {result.Score}）\n" +
                           $"已检查 {result.CheckedCount} 项 / 未检查 {result.SkippedCount} 项 / 命中 {result.HitCount} 项\n\n" +
                           string.Join("\n", result.Checks.Select(c =>
                               $"{(c.Skipped ? "[-]" : c.Triggered ? "[!]" : "[√]")} {c.Title}（{c.ScoreText}）{c.Detail}")),
            Data = data
        };
    }

    // =========================================================
    // 小工具
    // =========================================================

    private static Dictionary<string, object> First(string query)
        => EvidenceService.Wmi(query).FirstOrDefault() ?? new Dictionary<string, object>();

    /// <summary>
    /// 驱动落在「临时目录 / 用户目录 / 回收站」才算可疑。
    /// 实测教训：一开始写成「不在 System32\drivers 就报」，结果本机 40 多个正常驱动
    /// （DriverStore 里的 OEM 驱动、Program Files 里的 ACE 反作弊）全被误报，一天就被自检抓出来。
    /// </summary>
    private static bool IsDropZone(string path)
    {
        string[] markers = { @"\temp\", @"\users\", @"\appdata\", @"\downloads\", @"\$recycle", @"\desktop\" };
        return markers.Any(m => path.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPlaceholder(string value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) return true;
        return Placeholders.Contains(v, StringComparer.OrdinalIgnoreCase);
    }

    private static string Shorten(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..(max - 1)] + "…";

    private static string MacOf(NetworkInterface nic)
        => string.Join(":", nic.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));

    /// <summary>物理网卡：排除虚拟网卡、VPN、蓝牙 PAN 这些 MAC 不固定的适配器。</summary>
    private static IEnumerable<NetworkInterface> PhysicalNics()
    {
        string[] hints = { "Virtual", "Wi-Fi Direct", "Hyper-V", "Bluetooth", "VPN", "TAP-", "Npcap", "Loopback", "Tunnel", "VMware", "VirtualBox" };

        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.GetPhysicalAddress().GetAddressBytes().Length == 6)
            .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet
                or NetworkInterfaceType.GigabitEthernet
                or NetworkInterfaceType.Wireless80211
                or NetworkInterfaceType.Ethernet3Megabit)
            .Where(n => !hints.Any(h => (n.Description ?? "").Contains(h, StringComparison.OrdinalIgnoreCase)))
            .Where(n => !hints.Any(h => (n.Name ?? "").Contains(h, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(n => n.Name);
    }

    /// <summary>把 ImagePath 归一成绝对路径；无法判断时返回空串。</summary>
    private static string NormalizeImagePath(string image, string windows)
    {
        var p = image.Trim().Trim('"');
        if (p.Length == 0) return "";

        if (p.StartsWith(@"\??\", StringComparison.Ordinal)) p = p[4..];
        if (p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(windows, p[@"\SystemRoot\".Length..]);
        if (p.StartsWith(@"SystemRoot\", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(windows, p[@"SystemRoot\".Length..]);
        if (p.StartsWith(@"\System32\", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(windows, p[1..]);
        if (p.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(windows, p);
        if (p.StartsWith(@"drivers\", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(windows, "System32", p);

        return Path.IsPathRooted(p) ? p : "";
    }

    private static (bool Ok, string Text) RunBcdEdit()
    {
        try
        {
            var psi = new ProcessStartInfo("bcdedit.exe", "/enum {current}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };

            using var proc = Process.Start(psi);
            if (proc == null) return (false, "");

            var text = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(5000))
            {
                try { proc.Kill(); } catch { }
                return (false, text);
            }

            return (proc.ExitCode == 0, text);
        }
        catch
        {
            return (false, "");
        }
    }

    private static DateTime? InstallDate()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key?.GetValue("InstallDate") is not int seconds || seconds <= 0) return null;
            return DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime;
        }
        catch
        {
            return null;
        }
    }
}
