using System;
using System.Collections.Generic;
using System.Linq;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 「本机是不是精简系统」的共享判断。
///
/// 为什么需要它：精简系统会把 Defender 平台、Edge、WinRE、部分 WMI 提供程序连同服务键一起删掉。
/// 于是本程序里很多地方会「读不到」——Read 不到不等于没问题，也不等于权限不足。
/// 如果检测模块不知道这件事，就只能给出「无法判断」，用户看到一堆「读不到」会以为软件坏了。
///
/// 有了这个类，那些地方就能说清原因：
/// 「本机 Defenders 平台组件已被移除（WinDefend 服务键不存在），所以这个接口永远读不到——
/// 换管理员、重装软件都没用」。
///
/// 判定逻辑**复用** <see cref="SystemInfoService.DetectTrimmedOnly"/>，不另写一套，
/// 否则「系统信息」里说精简、外挂体检里说完整，自相矛盾。
///
/// 只在第一次访问时算一遍并缓存：检查项都是读文件与注册表，不跑 WMI，开销小；
/// 但没必要每个窗口都重算。
/// </summary>
public static class TrimAwareness
{
    private static readonly object Sync = new();

    private static bool _loaded;
    private static bool _trimmed;
    private static int _score;
    private static int _checked;
    private static int _strong;
    private static List<TrimCheck> _missing = new();

    /// <summary>强制重算（自检与「刷新」时用）。</summary>
    public static void Reset()
    {
        lock (Sync)
        {
            _loaded = false;
            _missing = new List<TrimCheck>();
        }
    }

    private static void Ensure()
    {
        if (_loaded) return;

        lock (Sync)
        {
            if (_loaded) return;

            try
            {
                var checks = SystemInfoService.DetectTrimmedOnly();

                _score = checks.Where(c => c.Missing).Sum(c => c.Weight);
                _strong = checks.Count(c => c.Missing && c.Weight >= 3);
                _checked = checks.Count(c => !c.Skipped);
                _missing = checks.Where(c => c.Missing).ToList();

                // 与 SystemInfoService 里的判定规则保持一致：
                // 检查项太少不下结论；命中 2 个以上强证据、或总分 ≥ 6 才算精简。
                _trimmed = _checked >= 6 && (_strong >= 2 || _score >= 6);
            }
            catch
            {
                _trimmed = false;
                _missing = new List<TrimCheck>();
            }

            _loaded = true;
        }
    }

    /// <summary>是否判定为精简/魔改系统。</summary>
    public static bool IsTrimmed
    {
        get { Ensure(); return _trimmed; }
    }

    public static int Score
    {
        get { Ensure(); return _score; }
    }

    public static int CheckedCount
    {
        get { Ensure(); return _checked; }
    }

    /// <summary>缺失项的名称列表。</summary>
    public static IReadOnlyList<string> MissingNames
    {
        get { Ensure(); return _missing.Select(c => c.Name).ToList(); }
    }

    /// <summary>一句话摘要，用于报告与提示。</summary>
    public static string Summary
    {
        get
        {
            Ensure();
            if (!_trimmed) return "完整系统";

            var names = string.Join("、", _missing.OrderByDescending(c => c.Weight).Take(4).Select(c => c.Name));
            return $"精简/魔改系统（缺失分 {_score}，缺 {_missing.Count} 项：{names}）";
        }
    }

    /// <summary>
    /// 某个组件是否被本机移除。按名称关键字匹配，找不到匹配项返回 false。
    ///
    /// 用途：判断「这个接口读不到，是因为组件被删了」还是「本来就没这东西」。
    /// 例：<c>IsMissing("WinDefend")</c> → true 说明 Defender 平台被移除，
    /// 那么 Defender 的 WMI 查询永远为空，与权限无关。
    /// </summary>
    public static bool IsMissing(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return false;

        Ensure();
        return _missing.Any(c =>
            c.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
            c.Id.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
            c.Detail.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 给「读不到」补一句归因。
    ///
    /// 这是本类存在的核心目的：把「无法判断」变成
    /// 「因为 X 被移除，所以无法判断，且换权限也解决不了」。
    /// 组件没缺失时返回空串，调用方保持原文案。
    /// </summary>
    /// <param name="componentKeywords">相关组件关键字，命中任一即认为原因成立。</param>
    public static string AttributionFor(params string[] componentKeywords)
    {
        if (componentKeywords == null || componentKeywords.Length == 0) return "";

        Ensure();
        if (!_trimmed) return "";

        var hit = componentKeywords.Where(IsMissing).ToList();
        if (hit.Count == 0) return "";

        return $" 本机判定为{Summary}，相关组件已被移除——这是该接口读不到的原因，" +
               "换管理员身份或重装本软件都无法改变。";
    }
}
