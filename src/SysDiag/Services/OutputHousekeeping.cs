using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SysDiag.Services;

/// <summary>
/// outputs 目录的整理：报告只保留最近若干份。
///
/// 为什么需要：每点一次「报告」就落一份 HTML + 一张 PNG，用久了目录里会堆几百个文件
/// （实测一次开发周期就攒了 345 个），想找最新那份得往下翻。
///
/// **只动本程序自己生成的东西**：靠文件名模式识别——
/// <c>yyyyMMdd-HHmmss-标题.html/.png</c>。用户自己放进去的图片（截图、图标）
/// 不匹配这个模式，一律不碰。
///
/// 删除的是可再生产物（重新点一次「报告」就有），不是原始数据；
/// 证据包 zip、latency-experiments.json 这类累积型记录**不在清理范围内**。
/// </summary>
public static class OutputHousekeeping
{
    /// <summary>保留最近多少份报告（按时间前缀算，HTML 与 PNG 成对计一份）。</summary>
    public const int KeepReports = 40;

    // 只认「8 位日期-6 位时间-标题.扩展名」，且扩展名限定在报告会用的那两种。
    private static readonly Regex ReportName =
        new(@"^(\d{8}-\d{6})-.+\.(html|png)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 从一批文件名里挑出该删的。抽成纯函数是为了能自检——
    /// 这种「按模式删文件」的逻辑，最怕正则写宽了把用户文件带走。
    /// </summary>
    public static List<string> CollectPrunable(IEnumerable<string> fileNames, int keep = KeepReports)
    {
        var list = (fileNames ?? Enumerable.Empty<string>()).ToList();

        // 按时间前缀分组：同一批 HTML 与 PNG 属于同一次报告，一起留或一起删
        var stamped = list
            .Select(n => new { Name = n, Match = ReportName.Match(n) })
            .Where(x => x.Match.Success)
            .Select(x => new { x.Name, Stamp = x.Match.Groups[1].Value })
            .ToList();

        if (stamped.Count == 0) return new List<string>();

        var keepStamps = stamped
            .Select(x => x.Stamp)
            .Distinct()
            .OrderByDescending(s => s, StringComparer.Ordinal)   // 时间前缀可直接按字符串比
            .Take(Math.Max(1, keep))
            .ToHashSet(StringComparer.Ordinal);

        return stamped
            .Where(x => !keepStamps.Contains(x.Stamp))
            .Select(x => x.Name)
            .ToList();
    }

    /// <summary>
    /// 按保留策略清理 outputs。返回 (删除数量, 释放字节)。
    /// 任何异常都吞掉——整理目录失败不该影响正在做的事。
    /// </summary>
    public static (int Removed, long Freed) Prune(int keep = KeepReports)
    {
        try
        {
            AppPaths.Ensure();

            var dir = AppPaths.OutputDir;
            var names = Directory.EnumerateFiles(dir)
                .Select(Path.GetFileName)
                .Where(n => n != null)
                .ToList();

            var victims = CollectPrunable(names!, keep);
            if (victims.Count == 0) return (0, 0);

            long freed = 0;
            var removed = 0;

            foreach (var name in victims)
            {
                try
                {
                    var full = Path.Combine(dir, name!);
                    var size = new FileInfo(full).Length;
                    File.Delete(full);
                    freed += size;
                    removed++;
                }
                catch
                {
                    // 单个文件删不掉（被占用）就跳过，下次再说
                }
            }

            return (removed, freed);
        }
        catch
        {
            return (0, 0);
        }
    }
}
