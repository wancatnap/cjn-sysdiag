using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using Microsoft.Win32;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 防护状态审查。
///
/// 为什么不做「关闭 Defender」：那件事只有三种实现路径，本软件一种都不走——
/// <list type="number">
/// <item><b>官方开关</b>：实时保护与篡改防护（Tamper Protection）在消费版 Windows 上只能由用户
///   在「Windows 安全中心」里手动关闭。注册表 <c>DisableAntiSpyware</c> 在 1903 之后对消费版无效。
///   所以「脚本一键关」在官方路径上本来就做不到。</item>
/// <item><b>滥用系统机制</b>：注册一个假杀软到 Windows 安全中心（WSC），让 Defender 按冲突逻辑自己退让。
///   DefendNot 就是这么做的，且必须把 DLL 注入到微软签名的 Taskmgr.exe 里绕过 PPL 校验。
///   这属于 ATT&CK T1562.001，微软已将其作为恶意软件查杀。这是攻击技术，不是功能。</item>
/// <item><b>内核驱动 / 打补丁</b>：需要签名驱动或利用漏洞，代价是系统完整性被破坏。</item>
/// </list>
///
/// 所以本功能做的是相反且更有价值的事：**判断防护有没有被别人悄悄关掉**。
/// 这正是取证场景的真实需求——「Defender 显示已关闭，但我没关过，谁干的」。
/// </summary>
public static class DefenderReviewService
{
    /// <summary>安全中心里注册的一个安全产品。</summary>
    public sealed class AvProvider
    {
        public string Name { get; init; } = "";
        public string StateRaw { get; init; } = "";
        public bool RealTimeOn { get; init; }
        public bool SignatureOutdated { get; init; }
        public string Exe { get; init; } = "";
        public string Guid { get; init; } = "";
        public bool IsMicrosoft =>
            Name.Contains("Defender", StringComparison.OrdinalIgnoreCase) ||
            Guid.StartsWith("D68DDC3A", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 详细状态读不到时为 true，界面显示「状态未知」。
        ///
        /// 为什么不直接用 SecurityCenter2 的 productState：本机实测出现过
        /// 五个指标全部「读不到」、产品条却显示「实时保护：开」——两个数据源打架。
        /// Defender 平台被移除后，安全中心里可能是残留条目，那个「开」不可信。
        /// 这种时候能给的只有「未知」，给「开」就是替系统说谎。
        /// </summary>
        public bool StateUnknown { get; set; }

        public string StateText => StateUnknown ? "状态未知"
            : RealTimeOn ? "实时保护：开" : "实时保护：关";
    }

    /// <summary>一条审查结论。</summary>
    public sealed class Finding
    {
        /// <summary>ok / info / warn / risk。界面按这个上色。</summary>
        public string Level { get; init; } = "info";
        public string Title { get; init; } = "";
        public string Detail { get; init; } = "";
    }

    public sealed class Review
    {
        public bool WscReadOk { get; init; }
        public List<AvProvider> Providers { get; init; } = new();

        public bool DefenderPresent { get; init; }
        public bool? AntivirusEnabled { get; init; }
        public bool? RealTimeEnabled { get; init; }
        public bool? TamperProtected { get; init; }
        public int? SignatureAgeDays { get; init; }
        public string SignatureVersion { get; init; } = "";

        /// <summary>被动模式：Defender 主动让位给第三方杀软。这个字段是「被削弱」的核心信号。</summary>
        public bool? PassiveMode { get; init; }

        public List<string> ExclusionPaths { get; init; } = new();
        public List<string> ExclusionProcesses { get; init; } = new();

        public List<Finding> Findings { get; init; } = new();

        /// <summary>ok / weak / suspicious / unknown。</summary>
        public string Verdict { get; init; } = "unknown";

        public string VerdictText => Verdict switch
        {
            "ok" => "防护正常",
            "weak" => "防护被削弱",
            "suspicious" => "疑似被第三方关闭",
            _ => "无法判断"
        };

        public string Summary { get; init; } = "";
    }

    // ---------------------------------------------------------------- 扫描

    public static Review Scan()
    {
        var findings = new List<Finding>();

        var providers = ReadProviders(out var wscOk);
        var defenderPresent = DirectoryExistsSafe(@"C:\ProgramData\Microsoft\Windows Defender\Platform")
                              || DirectoryExistsSafe(@"C:\Program Files\Windows Defender");

        var avEnabled = ReadDefenderBool("AntivirusEnabled");
        var rtEnabled = ReadDefenderBool("RealTimeProtectionEnabled");
        var tamper = ReadDefenderBool("IsTamperProtected");
        var sigAge = ReadDefenderInt("AntivirusSignatureAge");
        var sigVersion = ReadDefenderString("AntivirusSignatureVersion");
        var passive = ReadPassiveMode();

        var exclPaths = ReadExclusions("ExclusionPath");
        var exclProcs = ReadExclusions("ExclusionProcess");

        // 详细状态读不到时，产品条的「实时保护：开/关」一律降级为「状态未知」。
        // 否则会出现「五个指标全读不到、却显示实时保护开」这种自相矛盾的界面。
        if (!rtEnabled.HasValue)
            foreach (var p in providers) p.StateUnknown = true;

        var review = new Review
        {
            WscReadOk = wscOk,
            Providers = providers,
            DefenderPresent = defenderPresent,
            AntivirusEnabled = avEnabled,
            RealTimeEnabled = rtEnabled,
            TamperProtected = tamper,
            SignatureAgeDays = sigAge,
            SignatureVersion = sigVersion,
            PassiveMode = passive,
            ExclusionPaths = exclPaths,
            ExclusionProcesses = exclProcs
        };

        var thirdParty = providers.Where(p => !p.IsMicrosoft).ToList();
        var msProvider = providers.FirstOrDefault(p => p.IsMicrosoft);

        // 四个状态全都读不到时，只能说「不知道」，不能说「正常」。
        // 本机实测过：普通权限下 MSFT_MpComputerStatus 为空，早期版本会因此输出「防护正常」，
        // 和安全中心里「实时保护：关」的产品条目直接矛盾。
        var defenderStateKnown = avEnabled.HasValue || rtEnabled.HasValue
                                 || passive.HasValue || tamper.HasValue;

        if (!defenderStateKnown)
        {
            findings.Add(new Finding
            {
                Level = "warn",
                Title = "读不到 Defender 的详细状态",
                Detail = "root\\Microsoft\\Windows\\Defender 查询为空。这个命名空间依赖 Defender 平台组件，" +
                         "组件被精简、服务未运行、或防护被第三方接管时都会查不到。" +
                         "本机实测：以管理员身份运行同样为空，所以不是权限问题。" +
                         TrimAwareness.AttributionFor("WinDefend", "Defender") +
                         "结论只能依据安全中心里注册的产品，详细状态无法判断——本软件不会因为读不到就判「正常」。"
            });
        }

        // ---------- 1. 安全中心本身读不到 ----------
        if (!wscOk)
        {
            findings.Add(new Finding
            {
                Level = "warn",
                Title = "读不到安全中心的注册信息",
                Detail = "root\\SecurityCenter2 查询失败。常见原因是「安全中心」服务（wscsvc）被停用，" +
                         "或系统被精简过。这种情况无法判断当前是哪个产品在提供实时保护。"
            });
        }
        else if (providers.Count == 0)
        {
            findings.Add(new Finding
            {
                Level = "risk",
                Title = "安全中心里没有任何安全产品注册",
                Detail = "正常的 Windows 至少会注册 Defender。一条都没有，说明注册信息被删过。" +
                         "有些「一键关 Defender」工具会把 WSC 里的条目连同注册痕迹一起清掉。"
            });
        }

        // ---------- 2. 核心判定：Defender 关着，但没别的杀软接管 ----------
        var defenderOff = avEnabled == false || rtEnabled == false || passive == true;

        if (defenderOff && thirdParty.Count == 0)
        {
            findings.Add(new Finding
            {
                Level = "risk",
                Title = "Defender 未在防护，且没有任何第三方杀软接管",
                Detail = BuildDefenderOffDetail(avEnabled, rtEnabled, passive) +
                         "这是最需要警惕的组合：系统看起来「有安全中心」，实际没有任何实时防护在跑。" +
                         "「一键关闭 Defender」类工具造成的正是这个状态。"
            });
        }
        else if (defenderOff && thirdParty.Count > 0)
        {
            findings.Add(new Finding
            {
                Level = "info",
                Title = "Defender 已让位给第三方杀软",
                Detail = "检测到 " + string.Join("、", thirdParty.Select(p => p.Name)) +
                         "。Windows 在装有第三方杀软时会自动让 Defender 退到被动模式，这是正常设计，不是被入侵。"
            });
        }

        // ---------- 3. 被动模式却没有第三方：DefendNot 式伪造注册的典型残留 ----------
        if (passive == true && thirdParty.Count == 0)
        {
            findings.Add(new Finding
            {
                Level = "risk",
                Title = "被动模式已开启，但安全中心里没有第三方产品",
                Detail = "被动模式本来只在第三方杀软注册后才出现。现在它开着却没有第三方产品，" +
                         "典型原因是「假杀软注册」被撤销了但被动状态没恢复——" +
                         "这正是 DefendNot 一类工具留下的痕迹。建议在 Windows 安全中心手动把实时保护打开，并做一次全盘扫描。"
            });
        }

        // ---------- 4. 篡改防护 ----------
        if (tamper == false)
        {
            findings.Add(new Finding
            {
                Level = "warn",
                Title = "篡改防护（Tamper Protection）已关闭",
                Detail = "篡改防护关闭后，任何管理员权限的程序都能改 Defender 的设置，" +
                         "包括实时保护、排除项和云保护。微软把它作为默认开启的基线，建议在安全中心里打开。"
            });
        }
        else if (tamper == true)
        {
            findings.Add(new Finding
            {
                Level = "ok",
                Title = "篡改防护已开启",
                Detail = "关键设置受保护，脚本与普通程序改不动它。这是「为什么本软件不提供关闭按钮」的直接原因——" +
                         "开着它的时候，正规途径本来就关不掉。"
            });
        }

        // ---------- 5. 排除项 ----------
        var totalExcl = exclPaths.Count + exclProcs.Count;
        if (totalExcl > 0)
        {
            findings.Add(new Finding
            {
                Level = totalExcl >= 5 ? "warn" : "info",
                Title = $"存在 {totalExcl} 条排除项",
                Detail = "排除的路径与进程不会被扫描。恶意软件常把自己加进排除项来长期驻留，" +
                         "所以排除项是排查「为什么没查出来」的第一站。列表见下方。"
            });
        }

        // ---------- 6. 签名过期 ----------
        if (sigAge is > 7)
        {
            findings.Add(new Finding
            {
                Level = sigAge > 30 ? "warn" : "info",
                Title = $"病毒库已 {sigAge} 天未更新",
                Detail = "签名过期意味着新出现的威胁认不出来。更新服务（wuauserv / BITS）被停用是常见原因。"
            });
        }

        // ---------- 7. Defender 组件缺失 ----------
        if (!defenderPresent && avEnabled == null)
        {
            findings.Add(new Finding
            {
                Level = "risk",
                Title = "Defender 组件目录不存在，状态接口也读不到",
                Detail = "正常安装的 Windows 一定同时有组件目录和 WMI 状态接口。" +
                         "两者都没有，通常是精简镜像删掉了 Defender——" +
                         "删组件和关开关不是一回事：关掉可以再打开，删掉只能重装系统或修复安装。"
            });
        }

        if (findings.Count == 0)
        {
            findings.Add(new Finding
            {
                Level = "ok",
                Title = "未发现异常",
                Detail = "Defender 在正常防护，篡改防护开着，没有排除项。"
            });
        }

        var verdict = Classify(
            wscOk,
            providers.Count,
            defenderOff,
            thirdParty.Count,
            findings.Any(f => f.Level == "warn"),
            findings.Any(f => f.Level == "risk"),
            defenderStateKnown);

        return new Review
        {
            WscReadOk = wscOk,
            Providers = providers,
            DefenderPresent = defenderPresent,
            AntivirusEnabled = avEnabled,
            RealTimeEnabled = rtEnabled,
            TamperProtected = tamper,
            SignatureAgeDays = sigAge,
            SignatureVersion = sigVersion,
            PassiveMode = passive,
            ExclusionPaths = exclPaths,
            ExclusionProcesses = exclProcs,
            Findings = findings,
            Verdict = verdict,
            Summary = findings.FirstOrDefault(f => f.Level is "risk" or "warn")?.Title ?? "未发现异常"
        };
    }

    private static string BuildDefenderOffDetail(bool? av, bool? rt, bool? passive)
    {
        var parts = new List<string>();
        if (av == false) parts.Add("防病毒总开关已关");
        if (rt == false) parts.Add("实时保护已关");
        if (passive == true) parts.Add("处于被动模式");
        return parts.Count > 0 ? $"当前状态：{string.Join("、", parts)}。" : "";
    }

    /// <summary>
    /// 结论等级判定。抽成纯函数是为了能构造各种组合去验证——
    /// 总不能让自检真的把本机 Defender 关掉来测这一条。
    ///
    /// 判定顺序刻意如此：
    /// 读不到 → 无法判断（不猜）；一个产品都没注册 → 可疑；
    /// 防护关着且没人接管 → 可疑；有风险项 → 可疑；有警告项 → 削弱；否则正常。
    /// </summary>
    public static string Classify(bool wscOk, int providerCount, bool defenderOff,
        int thirdPartyCount, bool hasWarn, bool hasRisk, bool defenderStateKnown = true)
    {
        if (!wscOk) return "unknown";
        if (providerCount == 0) return "suspicious";
        if (defenderOff && thirdPartyCount == 0) return "suspicious";
        if (hasRisk) return "suspicious";

        // 「读不到」必须排在「有警告」之前：否则读不到状态会被归成「防护被削弱」，
        // 那是在没有证据的情况下下结论。信息不足就说信息不足。
        if (!defenderStateKnown) return "unknown";

        if (hasWarn) return "weak";
        return "ok";
    }

    // ---------------------------------------------------------------- WSC

    /// <summary>
    /// 读安全中心注册的安全产品。productState 是个三字节状态字，
    /// 中间字节 0x10 表示实时保护开着、0x00 表示关着；低字节非 0 表示签名过期。
    /// 这个编码没有正式文档，取值来自长期稳定的实测惯例，所以界面上只展示「开/关」两态，不展示原始码。
    /// </summary>
    private static List<AvProvider> ReadProviders(out bool ok)
    {
        var list = new List<AvProvider>();
        ok = false;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\SecurityCenter2"),
                new ObjectQuery("SELECT displayName, productState, pathToSignedProductExe, instanceGuid FROM AntiVirusProduct"));

            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    var stateText = mo["productState"]?.ToString() ?? "";
                    var state = 0;
                    int.TryParse(stateText, out state);

                    // productState 是三字节状态字，没有正式文档，取值来自长期稳定的实测惯例：
                    //   0x061100 = Defender 开启且签名最新
                    //   0x060000 = 关闭
                    // 判据用 bit12（0x1000）：这两种状态下它分别是 1 和 0，比逐字节比对稳。
                    // 早期版本按「中间字节 == 0x10」判断，遇到 0x11 会误报成关闭——本机实测发现后改正。
                    var rtOn = (state & 0x1000) != 0;
                    var outdated = (state & 0x0F) != 0 && (state & 0x1000) != 0;

                    list.Add(new AvProvider
                    {
                        Name = mo["displayName"]?.ToString() ?? "(未命名)",
                        StateRaw = state == 0 ? "" : $"0x{state:X6}",
                        RealTimeOn = rtOn,
                        SignatureOutdated = outdated,
                        Exe = mo["pathToSignedProductExe"]?.ToString() ?? "",
                        Guid = mo["instanceGuid"]?.ToString() ?? ""
                    });
                }
            }

            ok = true;
        }
        catch
        {
            ok = false;
        }

        return list;
    }

    // ---------------------------------------------------------------- Defender WMI

    private static object ReadDefenderValue(string property)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\Microsoft\Windows\Defender"),
                new ObjectQuery("SELECT " + property + " FROM MSFT_MpComputerStatus"));

            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    return mo[property];
                }
            }
        }
        catch { }

        return null;
    }

    private static bool? ReadDefenderBool(string property)
    {
        var v = ReadDefenderValue(property);
        return v == null ? null : (bool.TryParse(v.ToString(), out var b) ? b : null);
    }

    private static int? ReadDefenderInt(string property)
    {
        var v = ReadDefenderValue(property);
        return v == null ? null : (int.TryParse(v.ToString(), out var i) ? i : null);
    }

    private static string ReadDefenderString(string property)
        => ReadDefenderValue(property)?.ToString() ?? "";

    /// <summary>
    /// 被动模式。优先读 WMI 的 AMRunningMode（"Normal" / "Passive"），
    /// 读不到再退回注册表 <c>ForcePassiveMode</c>。
    /// </summary>
    private static bool? ReadPassiveMode()
    {
        try
        {
            var mode = ReadDefenderString("AMRunningMode");
            if (mode.Length > 0)
                return mode.Contains("Passive", StringComparison.OrdinalIgnoreCase);
        }
        catch { }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows Defender");
            if (key?.GetValue("ForcePassiveMode") is int force)
                return force != 0;
        }
        catch { }

        return null;
    }

    /// <summary>读排除项。需要管理员权限，读不到就返回空列表（界面会说明「读不到」而不是「没有」）。</summary>
    private static List<string> ReadExclusions(string property)
    {
        var list = new List<string>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\Microsoft\Windows\Defender"),
                new ObjectQuery("SELECT " + property + " FROM MSFT_MpPreference"));

            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    if (mo[property] is string[] arr)
                        list.AddRange(arr.Where(x => !string.IsNullOrWhiteSpace(x)));
                }
            }
        }
        catch { }

        return list;
    }

    private static bool DirectoryExistsSafe(string path)
    {
        try { return System.IO.Directory.Exists(path); }
        catch { return false; }
    }

    /// <summary>
    /// 给用户看的「为什么本软件不提供关闭按钮」说明。写成常量而不是散落在窗口里，
    /// 保证界面和自检引用的是同一份措辞。
    /// </summary>
    public const string WhyNoDisableButton =
        "为什么不提供「关闭 Defender」按钮：\n\n" +
        "1. 官方路径关不掉。实时保护与篡改防护只能由用户在 Windows 安全中心手动关闭；" +
        "注册表 DisableAntiSpyware 从 Windows 10 1903 起对消费版不再生效。\n\n" +
        "2. 能「一键关」的工具走的是滥用路径：向 Windows 安全中心注册一个假杀软，" +
        "让 Defender 按冲突逻辑自己退让。这需要把 DLL 注入微软签名的系统进程来绕过防护，" +
        "属于 ATT&CK T1562.001（削弱防御），微软已将其按恶意软件查杀。\n\n" +
        "3. 换个角度：防护被悄悄关掉，恰恰是需要被查出来的事。本页做的就是这件事——" +
        "告诉你它现在是什么状态、是不是有人动过，而不是帮你关掉它。\n\n" +
        "如果你确实需要临时降低扫描干扰（例如跑大型编译或基准测试），" +
        "正确做法是在安全中心里把工作目录加进排除项，或临时暂停实时保护，用完立刻恢复。";
}
