namespace SysDiag.Services;

/// <summary>
/// 已知反作弊组件识别。
/// <para>
/// 反作弊驱动同样会「注册内核服务」「加载 .sys」，如果按普通驱动去提示，
/// 每台装了游戏反作弊的机器都会多出一堆噪音。
/// 更关键的是语义：反作弊驱动是**正面信号**——它会主动拦截内存读写与输入模拟。
/// </para>
/// </summary>
public static class AntiCheatCatalog
{
    /// <summary>token 匹配名；prefixOnly 表示必须出现在名字开头，避免短词误伤。</summary>
    private static readonly (string Token, string Name, bool PrefixOnly)[] Table =
    {
        ("ACE-", "腾讯 ACE", false),            // ACE-BASE / ACE-SSC-DRV64 / ACE-GAME
        ("ACEBase", "腾讯 ACE", false),
        ("ACEService", "腾讯 ACE", false),
        ("AntiCheatExpert", "腾讯 ACE", false),
        ("SGuard", "腾讯 ACE", true),
        ("TenProtect", "腾讯 TenProtect", false),
        ("TenSafe", "腾讯 TenProtect", false),
        ("EasyAntiCheat", "Easy Anti-Cheat", false),
        ("BEService", "BattlEye", false),
        ("BEDaisy", "BattlEye", false),
        ("BEClient", "BattlEye", false),
        ("vgk", "Riot Vanguard", true),
        ("vgc", "Riot Vanguard", true),
        ("nProtect", "nProtect GameGuard", false),
        ("GameMon", "nProtect GameGuard", false),
        ("npgg", "nProtect GameGuard", false),
        ("xhunter", "XignCode3", false),
        ("XignCode", "XignCode3", false),
        ("Neac", "网易 NEAC", false),
        ("NetEaseAntiCheat", "网易 NEAC", false),
        ("PnkBstr", "PunkBuster", false),
        ("5EAntiCheat", "5E 反作弊", false),
        ("ESEA", "ESEA 反作弊", false),
        ("FACEIT", "FACEIT 反作弊", false)
    };

    /// <summary>识别文本里的反作弊组件名；识别不出返回 null。</summary>
    public static string Identify(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        foreach (var (token, name, prefixOnly) in Table)
        {
            if (prefixOnly)
            {
                if (text.StartsWith(token, StringComparison.OrdinalIgnoreCase)
                    || text.Contains(@"\" + token, StringComparison.OrdinalIgnoreCase))
                    return name;

                continue;
            }

            if (text.Contains(token, StringComparison.OrdinalIgnoreCase)) return name;
        }

        return null;
    }

    public static bool IsAntiCheat(string text) => Identify(text) != null;

    /// <summary>当前表里覆盖的组件名，供界面与报告展示。</summary>
    public static IReadOnlyList<string> Names =>
        Table.Select(x => x.Name).Distinct().ToList();
}
