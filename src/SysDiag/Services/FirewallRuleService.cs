using Microsoft.Win32;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 防火墙规则清单：直接读注册表里的 <c>FirewallRules</c> 值。
///
/// 控制面板的 firewall.cpl 只是这份数据的图形前端，规则本身以
/// <c>v2.10|Action=Allow|Dir=In|Protocol=6|LPort=80|Name=…|</c> 这种竖线分隔的
/// 字符串存在注册表里，解析出来就能在软件内看。
/// 新增/修改规则仍要用原生工具——那部分会写策略并校验规则语义，不适合自己实现。
/// </summary>
public static class FirewallRuleService
{
    public sealed class RuleEntry
    {
        public string Name { get; set; } = "";
        public string Group { get; set; } = "";
        public string Action { get; set; } = "";
        public string Direction { get; set; } = "";
        public string Protocol { get; set; } = "";
        public string Ports { get; set; } = "";
        public string Program { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public string Profile { get; set; } = "";

        public bool Blocking => Action.Equals("Block", StringComparison.OrdinalIgnoreCase);

        public string Level => !Enabled ? "none" : Blocking ? "hint" : "ok";

        public string LevelText => !Enabled ? "已停用" : Blocking ? "阻止" : "允许";
    }

    /// <summary>三个配置文件的开关状态：域 / 专用 / 公用。</summary>
    public static List<(string Profile, bool Enabled, string DefaultInbound, string DefaultOutbound)> Profiles()
    {
        var list = new List<(string, bool, string, string)>();

        foreach (var (key, label) in new[]
                 {
                     (@"DomainProfile", "域网络"),
                     (@"StandardProfile", "专用网络"),
                     (@"PublicProfile", "公用网络")
                 })
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\" + key);

                if (k == null) continue;

                list.Add((
                    label,
                    k.GetValue("EnableFirewall") is int on && on == 1,
                    k.GetValue("DefaultInboundAction") is int inbound ? (inbound == 1 ? "阻止" : "允许") : "未知",
                    k.GetValue("DefaultOutboundAction") is int outbound ? (outbound == 1 ? "阻止" : "允许") : "未知"));
            }
            catch { /* 读不到跳过 */ }
        }

        return list;
    }

    /// <summary>解析全部规则。读不到（权限不足）时返回空表，由页面提示。</summary>
    public static List<RuleEntry> Scan()
    {
        var list = new List<RuleEntry>();

        foreach (var path in new[]
                 {
                     @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules",
                     @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\RestrictedServices\Static\System"
                 })
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(path);
                if (key == null) continue;

                foreach (var valueName in key.GetValueNames())
                {
                    if (key.GetValue(valueName) is not string raw || raw.Length == 0) continue;

                    var rule = Parse(raw);
                    if (rule.Name.Length == 0) continue;

                    list.Add(rule);
                }
            }
            catch { /* 某一支读不到跳过 */ }
        }

        return list
            .GroupBy(r => r.Name + "|" + r.Direction + "|" + r.Action)
            .Select(g => g.First())
            .OrderByDescending(r => r.Enabled)
            .ThenByDescending(r => r.Blocking)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>解析一条规则字符串。格式无版本号约定，未知键直接忽略。</summary>
    internal static RuleEntry Parse(string raw)
    {
        var entry = new RuleEntry();
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in raw.Split('|'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;

            var key = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim();

            if (key.Length > 0) fields[key] = value;
        }

        fields.TryGetValue("Name", out var name);
        fields.TryGetValue("Desc", out var desc);
        fields.TryGetValue("Group", out var group);
        fields.TryGetValue("Action", out var action);
        fields.TryGetValue("Dir", out var dir);
        fields.TryGetValue("Protocol", out var protocol);
        fields.TryGetValue("LPort", out var lport);
        fields.TryGetValue("RPort", out var rport);
        fields.TryGetValue("App", out var app);
        fields.TryGetValue("Active", out var active);
        fields.TryGetValue("Profile", out var profile);

        // 规则名与组名大量是资源引用（@FirewallAPI.dll,-23304），必须解引用后再显示
        var resolvedName = MuiString.Resolve(name ?? "");
        var resolvedDesc = resolvedName.Length > 0 ? "" : MuiString.Resolve(desc ?? "");
        var resolvedGroup = MuiString.Resolve(group ?? "");

        entry.Name = resolvedName.Length > 0 ? resolvedName : resolvedDesc;
        if (entry.Name.Length == 0) entry.Name = resolvedGroup;
        if (entry.Name.Length == 0) entry.Name = "（没有名称的规则）";
        entry.Group = resolvedGroup;
        entry.Action = action ?? "";
        // 规则里的大小写并不统一（既有 Dir=In 也有 Dir=in），必须忽略大小写
        entry.Direction = (dir ?? "").ToLowerInvariant() switch
        {
            "in" => "入站",
            "out" => "出站",
            _ => dir ?? ""
        };
        entry.Protocol = protocol switch
        {
            "6" => "TCP",
            "17" => "UDP",
            "1" => "ICMPv4",
            "58" => "ICMPv6",
            "256" => "任意",
            _ => protocol ?? ""
        };

        var ports = new List<string>();
        if (!string.IsNullOrWhiteSpace(lport)) ports.Add("本地 " + lport);
        if (!string.IsNullOrWhiteSpace(rport)) ports.Add("远程 " + rport);
        entry.Ports = ports.Count > 0 ? string.Join(" / ", ports) : "任意";

        entry.Program = app ?? "";
        entry.Enabled = !string.Equals(active, "FALSE", StringComparison.OrdinalIgnoreCase);
        entry.Profile = profile switch
        {
            "1" => "域",
            "2" => "专用",
            "4" => "公用",
            "7" or null => "全部",
            _ => profile
        };

        return entry;
    }

    /// <summary>统计：启用 / 阻止 / 入站。</summary>
    public static (int Total, int Enabled, int Block, int Inbound) Count(List<RuleEntry> rules)
        => (rules.Count,
            rules.Count(r => r.Enabled),
            rules.Count(r => r.Blocking && r.Enabled),
            rules.Count(r => r.Direction == "入站" && r.Enabled));
}
