using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>校验结论。</summary>
public enum MachineCheckState
{
    /// <summary>还没登记过出厂记录。</summary>
    NoBaseline,

    /// <summary>与出厂记录完全一致。</summary>
    Match,

    /// <summary>部分关键字段变化（换硬件、装了新硬盘等）。</summary>
    Partial,

    /// <summary>核心标识全部变化（疑似换机、虚拟机克隆或伪造）。</summary>
    Mismatch
}

/// <summary>一个稳定字段的比对结果。</summary>
public sealed class MachineFieldDiff
{
    public string Group { get; init; } = "";
    public string Name { get; init; } = "";
    public string CurrentValue { get; init; } = "";
    public bool Changed { get; init; }
    public bool IsCore { get; init; }
}

/// <summary>机器码校验结果。</summary>
public sealed class MachineCheck
{
    public MachineCheckState State { get; init; } = MachineCheckState.NoBaseline;
    public string CurrentCode { get; init; } = "";
    public string BaselineCode { get; init; } = "";
    public DateTime? SavedAt { get; init; }
    public string Computer { get; init; } = "";
    public List<MachineFieldDiff> Fields { get; init; } = new();

    public List<MachineFieldDiff> Changed => Fields.Where(f => f.Changed).ToList();

    public bool NeedsAttention => State is MachineCheckState.Partial or MachineCheckState.Mismatch;

    public string StateText => State switch
    {
        MachineCheckState.NoBaseline => "未登记",
        MachineCheckState.Match => "一致",
        MachineCheckState.Partial => "部分变化",
        _ => "不一致"
    };

    public string StateColor => State switch
    {
        MachineCheckState.Match => Palette.OkHex,
        MachineCheckState.Partial => Palette.HintHex,
        MachineCheckState.Mismatch => Palette.WarnHex,
        _ => Palette.NoneHex
    };

    public string Title => State switch
    {
        MachineCheckState.NoBaseline => "还没有出厂记录",
        MachineCheckState.Match => "机器码与出厂记录一致",
        MachineCheckState.Partial => $"机器码有 {Changed.Count} 项变化",
        _ => "机器码与出厂记录不一致"
    };

    public string Detail => State switch
    {
        MachineCheckState.NoBaseline =>
            "第一次使用的机器建议点「登记为出厂记录」，之后每次打开都能校验这台机器有没有被换过或改过硬件。",

        MachineCheckState.Match =>
            $"当前机器码 {CurrentCode} 与 {SavedAt:yyyy-MM-dd HH:mm} 登记的一致。",

        MachineCheckState.Partial =>
            $"变化的字段：{string.Join("、", Changed.Select(c => c.Name))}。" +
            "这通常是更换了硬盘、内存或主板等硬件。如果本机最近没有动过硬件，需要核实是否被改机或换了机器。",

        _ =>
            "核心标识（SMBIOS UUID / 主板序列号 / 磁盘 0）全部与出厂记录不同。" +
            "可能是换了整机、重装系统后主板被刷写，或在虚拟机里运行；也可能是伪造环境。建议导出报告后人工核实。"
    };
}

/// <summary>
/// 机器码出厂记录：登记一次，之后每次校验「当前机器码是否与出厂记录一致」。
/// 只存字段值的哈希，不落盘明文；不一致只提示、不禁用功能（正常升级硬件不该被误伤）。
/// </summary>
public static class MachineIdentityService
{
    /// <summary>稳定字段（换硬盘/内存会变，换 CPU 会变；计算机名、产品 ID 这些会随重装变化的不纳入）。</summary>
    private static readonly (string Key, bool Core)[] StableKeys =
    {
        ("SMBIOS UUID", true),
        ("整机序列号", true),
        ("主板序列号", true),
        ("BIOS 序列号", false),
        ("ProcessorId", false),
        ("磁盘 0", true)   // 名称含「磁盘 0」，后缀是型号
    };

    public static string DefaultPath => Path.Combine(AppPaths.DataDir, "machine-baseline.json");

    private static string PathOf(string? file) => file ?? DefaultPath;

    /// <summary>当前所有硬件标识字段（原值，供界面显示）。</summary>
    public static List<HwidField> CaptureFields() => EvidenceService.CollectHwid();

    /// <summary>从字段里挑出稳定字段（磁盘按前缀匹配）。</summary>
    private static List<(string Group, string Name, string Value, bool Core)> PickStable(List<HwidField> fields)
    {
        var picked = new List<(string, string, string, bool)>();

        foreach (var (key, core) in StableKeys)
        {
            var hit = fields.FirstOrDefault(f =>
                string.Equals(f.Name, key, StringComparison.OrdinalIgnoreCase)
                || (key == "磁盘 0" && f.Name.StartsWith("磁盘 0", StringComparison.OrdinalIgnoreCase)));

            if (hit == null) continue;
            if (string.IsNullOrWhiteSpace(hit.Value)) continue;

            picked.Add((hit.Group, hit.Name, hit.Value, core));
        }

        return picked;
    }

    /// <summary>机器码：稳定字段值拼接后 SHA256，取前 16 位分组显示。</summary>
    public static string CodeOf(IEnumerable<(string Name, string Value)> fields)
    {
        var joined = string.Join("\n", fields.Select(f => f.Value.Trim().ToUpperInvariant()));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        var hex = Convert.ToHexString(hash)[..16];
        return $"{hex[..4]}-{hex[4..8]}-{hex[8..12]}-{hex[12..16]}";
    }

    /// <summary>
    /// 直接算当前机器的特征码，不需要先登记基线。
    ///
    /// 只用稳定字段（UUID / 主板 / BIOS / CPU / 系统盘），换内存条、加硬盘、改机器名都不会让它变。
    /// 外挂体检的报告需要「这份报告来自哪台机器」，用这个就够，不依赖用户是否做过硬件基线登记。
    /// </summary>
    public static string CurrentCode()
    {
        try
        {
            var picked = PickStable(CaptureFields());
            if (picked.Count == 0) return "";

            return CodeOf(picked.Select(p => (p.Name, p.Value)));
        }
        catch
        {
            // 读不到就返回空串，报告里显示「读不到」，不编一个假码
            return "";
        }
    }

    private static string HashOf(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim().ToUpperInvariant()));
        return Convert.ToHexString(hash)[..16];
    }

    /// <summary>登记（或重登记）出厂记录。返回本次登记的快照。</summary>
    public static MachineBaseline Register(string? file = null)
    {
        var fields = PickStable(CaptureFields());

        var baseline = new MachineBaseline
        {
            SavedAt = DateTime.Now,
            Computer = Environment.MachineName,
            Code = CodeOf(fields.Select(f => (f.Name, f.Value))),
            Fields = fields.Select(f => new MachineBaselineField
            {
                Group = f.Group,
                Name = f.Name,
                Hash = HashOf(f.Value),
                Core = f.Core
            }).ToList()
        };

        AppPaths.Ensure();
        Directory.CreateDirectory(Path.GetDirectoryName(PathOf(file))!);
        File.WriteAllText(PathOf(file),
            JsonSerializer.Serialize(baseline, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        return baseline;
    }

    public static MachineBaseline? Load(string? file = null)
    {
        try
        {
            var path = PathOf(file);
            if (!File.Exists(path)) return null;

            var baseline = JsonSerializer.Deserialize<MachineBaseline>(File.ReadAllText(path));
            return baseline?.Fields.Count > 0 ? baseline : null;
        }
        catch
        {
            return null;
        }
    }

    public static bool HasBaseline(string? file = null) => Load(file) != null;

    /// <summary>删除出厂记录（重装/转让机器时用）。</summary>
    public static void Clear(string? file = null)
    {
        try
        {
            var path = PathOf(file);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* 删不掉就算了，下次登记会覆盖 */ }
    }

    /// <summary>校验当前机器码与出厂记录是否一致。</summary>
    public static MachineCheck Verify(string? file = null)
    {
        var current = PickStable(CaptureFields());
        var baseline = Load(file);

        if (baseline == null)
        {
            return new MachineCheck
            {
                State = MachineCheckState.NoBaseline,
                CurrentCode = CodeOf(current.Select(f => (f.Name, f.Value))),
                Fields = current.Select(f => new MachineFieldDiff
                {
                    Group = f.Group,
                    Name = f.Name,
                    CurrentValue = f.Value,
                    IsCore = f.Core
                }).ToList()
            };
        }

        var map = baseline.Fields.ToDictionary(f => f.Name, f => f, StringComparer.OrdinalIgnoreCase);
        var diffs = new List<MachineFieldDiff>();

        foreach (var f in current)
        {
            map.TryGetValue(f.Name, out var baseField);
            var changed = baseField != null && !string.Equals(baseField.Hash, HashOf(f.Value), StringComparison.OrdinalIgnoreCase);

            diffs.Add(new MachineFieldDiff
            {
                Group = f.Group,
                Name = f.Name,
                CurrentValue = f.Value,
                Changed = changed,
                IsCore = f.Core
            });
        }

        // 基线里有、当前读不到的（例如磁盘被拆掉）也算变化
        foreach (var b in baseline.Fields)
        {
            if (diffs.Any(d => string.Equals(d.Name, b.Name, StringComparison.OrdinalIgnoreCase))) continue;

            diffs.Add(new MachineFieldDiff
            {
                Group = b.Group,
                Name = b.Name,
                CurrentValue = "(当前读取不到)",
                Changed = true,
                IsCore = b.Core
            });
        }

        var coreKeys = baseline.Fields.Where(f => f.Core).Select(f => f.Name).ToList();
        var coreChanged = diffs.Where(d => d.Changed && coreKeys.Contains(d.Name, StringComparer.OrdinalIgnoreCase)).ToList();

        var state = diffs.All(d => !d.Changed)
            ? MachineCheckState.Match
            : coreChanged.Count == coreKeys.Count && coreKeys.Count > 0
                ? MachineCheckState.Mismatch
                : MachineCheckState.Partial;

        return new MachineCheck
        {
            State = state,
            CurrentCode = CodeOf(current.Select(f => (f.Name, f.Value))),
            BaselineCode = baseline.Code,
            SavedAt = baseline.SavedAt,
            Computer = baseline.Computer,
            Fields = diffs
        };
    }

    // ---------------- 报告 ----------------

    public static ReportPayload BuildReport(MachineCheck check)
    {
        var doc = new HtmlDoc("机器码校验",
            $"{Environment.MachineName}　·　{DateTime.Now:yyyy-MM-dd HH:mm:ss}　·　{check.StateText}");

        doc.Kpis(
            (check.CurrentCode, "当前机器码", "#4C6EF5"),
            (check.BaselineCode.Length > 0 ? check.BaselineCode : "—", "出厂机器码", "#8B5CF6"),
            (check.Changed.Count.ToString(), "变化字段", check.Changed.Count > 0 ? Palette.WarnHex : Palette.OkHex),
            (check.SavedAt?.ToString("MM-dd HH:mm") ?? "—", "登记时间", "#34D399"));

        doc.Note($"<b>{HtmlDoc.E(check.Title)}</b><br/>{HtmlDoc.E(check.Detail)}");

        doc.H2("稳定字段比对");
        doc.Table(
            new[] { "字段", "分组", "当前值", "结论" },
            check.Fields.Select(f => new[]
            {
                $"<b>{HtmlDoc.E(f.Name)}</b>" + (f.IsCore ? " <span class=\"tag t-hint\">核心</span>" : ""),
                HtmlDoc.E(f.Group),
                $"<span class=\"mono\">{HtmlDoc.E(f.CurrentValue)}</span>",
                f.Changed
                    ? "<span class=\"tag t-warn\">已变化</span>"
                    : check.State == MachineCheckState.NoBaseline
                        ? "<span class=\"muted\">未登记</span>"
                        : "<span class=\"tag t-ok\">一致</span>"
            }),
            new[] { "200", "100", "", "78" });

        doc.H2("怎么处理这个结果");
        doc.Raw("<div class=\"note\">" +
                "1. <b>一致</b>：这台机器就是登记时那台，硬件没被动过。<br/>" +
                "2. <b>部分变化</b>：通常换了硬盘/内存/主板。先确认当事人是否升级过硬件；没有的话要核实是否被改机。<br/>" +
                "3. <b>不一致</b>：核心标识全变，可能是换了整机、虚拟机克隆或伪造环境。建议导出报告后人工核实，必要时让对方现场拍照比对。<br/>" +
                "4. <b>本功能只提示、不禁用任何功能</b>——正常升级硬件很常见，误伤代价比漏报大。换机或重装后可点「重新登记」更新出厂记录。" +
                "</div>");

        var html = doc.Build();
        var path = HtmlReportBuilder.Save(html, "机器码校验");

        var data = new ReportData
        {
            Kind = "machine-check",
            Title = "机器码校验",
            Subtitle = $"{check.StateText} · 当前 {check.CurrentCode}",
            GeneratedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            Tool = new ToolInfo(),
            Machine = new MachineInfo
            {
                Name = Environment.MachineName,
                User = Environment.UserName,
                Os = Environment.OSVersion.VersionString,
                Admin = ElevationService.IsAdmin
            },
            Verdict = new VerdictInfo
            {
                Score = -1,
                Level = check.State switch
                {
                    MachineCheckState.Match => "低",
                    MachineCheckState.Partial => "中",
                    MachineCheckState.Mismatch => "高",
                    _ => "低"
                },
                Text = check.Title,
                Counts = new ReportCounts
                {
                    Warn = check.Changed.Count(f => f.IsCore),
                    Hint = check.Changed.Count(f => !f.IsCore),
                    Ok = check.Fields.Count - check.Changed.Count
                }
            },
            Sections = new List<ReportSection>
            {
                new()
                {
                    Group = "字段比对",
                    Items = check.Fields.Select(f => new ReportItem
                    {
                        Title = f.Name + (f.IsCore ? "（核心）" : ""),
                        Detail = f.CurrentValue,
                        Basis = f.Changed ? "已变化" : check.State == MachineCheckState.NoBaseline ? "未登记" : "一致",
                        Level = f.Changed ? "warn" : "ok"
                    }).ToList()
                }
            }
        };

        return new ReportPayload
        {
            Title = "机器码校验",
            Subtitle = $"{check.StateText} · 当前 {check.CurrentCode} · 变化 {check.Changed.Count} 项",
            EngineTag = "出厂校验",
            HtmlPath = path,
            PlainSummary = $"机器码校验\n状态：{check.StateText}\n" +
                           $"当前 {check.CurrentCode}\n出厂 {check.BaselineCode}\n" +
                           $"变化字段：{(check.Changed.Count > 0 ? string.Join("、", check.Changed.Select(c => c.Name)) : "无")}",
            Data = data
        };
    }
}

/// <summary>出厂记录文件结构（不存明文，只存哈希）。</summary>
public sealed class MachineBaseline
{
    public DateTime SavedAt { get; set; }
    public string Computer { get; set; } = "";
    public string Code { get; set; } = "";
    public List<MachineBaselineField> Fields { get; set; } = new();
}

public sealed class MachineBaselineField
{
    public string Group { get; set; } = "";
    public string Name { get; set; } = "";
    public string Hash { get; set; } = "";
    public bool Core { get; set; }
}
