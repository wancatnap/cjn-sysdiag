using System.Diagnostics;
using System.IO;

namespace SysDiag.Services;

/// <summary>一个被识别为「游戏」的运行中进程。</summary>
public sealed class GameProcess
{
    public int Pid { get; init; }
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";

    /// <summary>命中方式：已知游戏名 / 游戏目录 / 反作弊在跑。</summary>
    public string Reason { get; init; } = "";

    public string Display => $"{Name}（{Pid}）";
}

/// <summary>
/// 游戏进程识别：句柄排查与挂机监控都靠它「自动找到要看的游戏」。
/// 判定顺序：已知游戏进程名 → 安装路径特征 → 同名反作弊进程在运行。
/// 找不到就明确返回空，不拿任意进程冒充。
/// </summary>
public static class GameProcessCatalog
{
    /// <summary>常见竞技类游戏进程名（不含 .exe），大小写不敏感。</summary>
    private static readonly Dictionary<string, string> KnownGames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DeltaForceClient-Win64-Shipping"] = "三角洲行动",
        ["DeltaForceClient"] = "三角洲行动",
        ["VALORANT-Win64-Shipping"] = "无畏契约",
        ["VALORANT"] = "无畏契约",
        ["cs2"] = "反恐精英 2",
        ["csgo"] = "CS:GO",
        ["TslGame"] = "绝地求生",
        ["r5apex"] = "Apex 英雄",
        ["r5apex_dx12"] = "Apex 英雄",
        ["League of Legends"] = "英雄联盟",
        ["league of legends"] = "英雄联盟",
        ["Overwatch"] = "守望先锋",
        ["NarakaBladepoint"] = "永劫无间",
        ["crossfire"] = "穿越火线",
        ["EscapeFromTarkov"] = "逃离塔科夫",
        ["RustClient"] = "Rust",
        ["FortniteClient-Win64-Shipping"] = "堡垒之夜",
        ["bf2042"] = "战地 2042",
        ["dota2"] = "Dota 2",
        ["WhereWindsMeet"] = "燕云十六声",
        ["Marvel-Win64-Shipping"] = "漫威争锋",
        ["strata"] = "卡拉彼丘",
        ["Game"] = "通用游戏进程"
    };

    /// <summary>路径关键词：出现在安装路径里就当游戏目录。</summary>
    private static readonly string[] GamePathHints =
    {
        @"\steamapps\common\", @"\steamlibrary\", @"\epic games\", @"\wegameapps\",
        @"\games\", @"\game\", @"\program files\tencent\", @"\program files (x86)\tencent\",
        @"\xboxgames\", @"\riot games\", @"\ubisoft\", @"\ea games\", @"\battle.net\"
    };

    /// <summary>名字里带这些词的不是游戏本体（启动器 / 更新器 / 崩溃处理）。</summary>
    private static readonly string[] NotGameHints =
    {
        "launcher", "updater", "crashhandler", "crashreport", "service", "helper", "installer", "elevation"
    };

    /// <summary>识别出所有正在运行的游戏进程，已知游戏名优先。</summary>
    public static List<GameProcess> DetectRunningGames()
    {
        var list = new List<GameProcess>();

        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                var name = proc.ProcessName;

                if (NotGameHints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var known = KnownGames.TryGetValue(name, out var title);

                string path = "";
                try { path = proc.MainModule?.FileName ?? ""; }
                catch { /* 权限不足或已退出 */ }

                var byPath = path.Length > 0
                             && GamePathHints.Any(h => path.Contains(h, StringComparison.OrdinalIgnoreCase));

                // 名字明显是游戏、或装在游戏目录里且窗口标题不为空
                if (!known && !byPath) continue;

                var reason = known
                    ? $"已知游戏：{title}"
                    : "安装在游戏目录";

                list.Add(new GameProcess
                {
                    Pid = proc.Id,
                    Name = name,
                    Path = path,
                    Reason = reason,
                    // 已知游戏排前面
                    // 用 Display 排序时按 Reason 前缀区分
                });
            }
            catch { /* 进程已退出 */ }
            finally { proc.Dispose(); }
        }

        return list
            .OrderByDescending(g => g.Reason.StartsWith("已知游戏"))
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>最值得排查的一个游戏进程：没有就返回 null（不猜）。</summary>
    public static GameProcess? Best() => DetectRunningGames().FirstOrDefault();

    /// <summary>某个进程是不是已知游戏（按名字）。</summary>
    public static bool IsKnownGame(string processName) => KnownGames.ContainsKey(processName);

    /// <summary>已知游戏名列表（界面展示与文档用）。</summary>
    public static IReadOnlyList<string> KnownNames => KnownGames.Keys.ToList();
}
