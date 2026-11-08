using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// PE 行为特征扫描：判断一个可执行文件「有没有外挂需要的能力」。
///
/// 为什么不用 YARA：YARA 的强项是匹配**已知样本的字节签名**，那需要持续维护规则库；
/// 规则一旧就漏新挂，而对普通用户来说「看起来像不像外挂」比「是不是某个样本」更有用。
/// 所以这里走另一条路——**看导入表**：一个程序要读写别的进程内存、要注入线程、要监听键盘，
/// 就必须导入对应的系统函数。这个能力集是**外挂逃不掉**的，而正常软件很少同时具备。
///
/// 三条设计约束：
/// <list type="number">
/// <item>**只扫候选文件**，不全盘扫描。候选来自 Temp / 下载 / 公共目录里最近的可执行文件——
/// 外挂的落地习惯是把文件丢在这些地方，装在 Program Files 且带安装器的反而少见。</item>
/// <item>**组合才升级**。单个 <c>ReadProcessMemory</c> 在调试器、备份工具、屏幕阅读器里都有；
/// 「读写内存 + 远程线程」同时出现才叫注入器特征。</item>
/// <item>**位置决定级别**。同样一组 API，在 Program Files 里报提示，在 Temp 里报线索——
/// 因为正常软件不会把自己装在 Temp。</item>
/// </list>
///
/// 明确不做：不做字符串签名匹配、不判断是否恶意、不给出「这是外挂」的结论。
/// </summary>
public static class PeFeatureScanner
{
    /// <summary>一组能力特征：命中全部 Required 才算这条规则成立。</summary>
    private sealed class Rule
    {
        public string Name { get; init; } = "";
        public string Explain { get; init; } = "";
        public string[] Required { get; init; } = Array.Empty<string>();
        public int Weight { get; init; }
    }

    private static readonly Rule[] Rules =
    {
        new()
        {
            Name = "跨进程内存读写",
            Explain = "能读取并写入其他进程的内存。这是外部读取游戏状态（透视/雷达）最基础的一步。",
            Required = new[] { "ReadProcessMemory", "WriteProcessMemory" },
            Weight = 18
        },
        new()
        {
            Name = "远程线程注入",
            Explain = "能把代码塞进另一个进程执行。外挂注入游戏进程的标准做法。",
            Required = new[] { "CreateRemoteThread", "VirtualAllocEx" },
            Weight = 22
        },
        new()
        {
            Name = "线程上下文改写",
            Explain = "改写别的线程的执行上下文（寄存器/入口），比远程线程更隐蔽的注入方式。",
            Required = new[] { "SetThreadContext", "OpenProcess" },
            Weight = 16
        },
        new()
        {
            Name = "键盘状态轮询",
            Explain = "轮询全局按键状态。输入类外挂（自动压枪、连点）常用；也可能是热键工具。",
            Required = new[] { "GetAsyncKeyState" },
            Weight = 8
        },
        new()
        {
            Name = "全局键盘钩子",
            Explain = "装全局键盘钩子，能收到所有程序的按键。键盘记录器的特征，也可能是快捷键工具。",
            Required = new[] { "SetWindowsHookEx", "CallNextHookEx" },
            Weight = 12
        },
        new()
        {
            Name = "屏幕采集",
            Explain = "抓取屏幕或窗口画面。画面识别型外挂的第一步；录屏软件也会命中。",
            Required = new[] { "BitBlt", "GetDC" },
            Weight = 6
        },
        new()
        {
            Name = "内核驱动加载",
            Explain = "能在运行时加载内核驱动。多数正常程序不需要这个能力。",
            Required = new[] { "NtLoadDriver" },
            Weight = 20
        },
        new()
        {
            Name = "服务创建",
            Explain = "能注册系统服务。常与驱动加载一起构成持久化，也可能是正常安装器。",
            Required = new[] { "OpenSCManager", "CreateService" },
            Weight = 8
        }
    };

    public sealed class FileHit
    {
        public string Path { get; init; } = "";
        public string FileName { get; init; } = "";
        public long SizeBytes { get; init; }
        public DateTime Modified { get; init; }
        public List<string> Rules { get; init; } = new();
        public int Score { get; init; }

        /// <summary>是否位于临时/下载这类「正常软件不该待」的目录。</summary>
        public bool InThrowaway { get; init; }

        public string SizeText => SizeBytes >= 1024 * 1024
            ? $"{SizeBytes / 1024.0 / 1024.0:0.0} MB"
            : $"{SizeBytes / 1024.0:0} KB";
    }

    /// <summary>扫描结果。</summary>
    public sealed class ScanResult
    {
        public List<FileHit> Hits { get; } = new();
        public int ScannedFiles { get; set; }
        public int CandidateFiles { get; set; }
        public string Note { get; set; } = "";
    }

    /// <summary>自检用：规则条数与名称。</summary>
    public static IReadOnlyList<string> RuleNames => Rules.Select(r => r.Name).ToList();

    /// <summary>
    /// 自检用：给定一组导入函数名，返回命中的规则名。
    /// 抽出来是为了能构造场景验证「组合才成立」——不必真去找一个外挂样本。
    /// </summary>
    public static List<string> MatchRuleNames(IEnumerable<string> imports)
    {
        var set = new HashSet<string>(imports ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        return Rules.Where(r => r.Required.All(set.Contains)).Select(r => r.Name).ToList();
    }

    private const int MaxFiles = 240;
    private const long MaxSizeBytes = 64L * 1024 * 1024;

    /// <summary>
    /// 扫描候选目录里的可执行文件。只读文件，不执行、不加载。
    /// </summary>
    public static ScanResult Scan(IProgress<string> progress = null)
    {
        var result = new ScanResult();

        var roots = new[]
        {
            Path.GetTempPath(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) is { Length: > 0 } la
                ? Path.Combine(la, "Temp") : "",
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) is { Length: > 0 } pd
                ? Path.Combine(pd, "Temp") : "",
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) is { Length: > 0 }
                ? Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)) ?? "" : ""
        }
        .Where(r => r.Length > 0 && Directory.Exists(r))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

        if (roots.Count == 0)
        {
            result.Note = "没有可扫描的目录。";
            return result;
        }

        var cutoff = DateTime.Now.AddDays(-120);
        var candidates = new List<FileInfo>();

        foreach (var root in roots)
        {
            try
            {
                var dir = new DirectoryInfo(root);

                foreach (var file in dir.EnumerateFiles("*.*", SearchOption.AllDirectories)
                             .Where(f => f.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                                         || f.Extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
                             .Where(f => f.Length > 4096 && f.Length <= MaxSizeBytes)
                             .Where(f => f.LastWriteTime >= cutoff)
                             .OrderByDescending(f => f.LastWriteTime))
                {
                    candidates.Add(file);
                    if (candidates.Count >= MaxFiles * 3) break;
                }
            }
            catch
            {
                // 目录不可读（权限或路径异常）就跳过，不影响其他目录
            }
        }

        result.CandidateFiles = candidates.Count;

        if (candidates.Count == 0)
        {
            result.Note = "Temp 与下载目录里没有最近修改的可执行文件。";
            return result;
        }

        var throwawayRoots = roots.Select(r => r.ToLowerInvariant()).ToList();
        var index = 0;

        foreach (var file in candidates)
        {
            if (result.ScannedFiles >= MaxFiles) break;

            index++;
            if (index % 20 == 0)
                progress?.Report($"正在检查文件特征… {result.ScannedFiles}/{Math.Min(candidates.Count, MaxFiles)}");

            try
            {
                var imports = ReadImports(file.FullName);
                if (imports.Count == 0) continue;

                result.ScannedFiles++;

                var hitRules = Rules
                    .Where(r => r.Required.All(imp => imports.Contains(imp)))
                    .ToList();

                if (hitRules.Count == 0) continue;

                var lower = file.FullName.ToLowerInvariant();
                var inThrowaway = throwawayRoots.Any(t => lower.StartsWith(t));

                var score = hitRules.Sum(r => r.Weight);

                // 位置加权：正常软件不把自己装在 Temp / 下载目录
                if (inThrowaway) score += 10;

                // 只保留有实际意义的命中，避免把「只有键盘轮询」的普通工具列满一屏
                if (score < 18 && hitRules.Count < 2) continue;

                result.Hits.Add(new FileHit
                {
                    Path = file.FullName,
                    FileName = file.Name,
                    SizeBytes = file.Length,
                    Modified = file.LastWriteTime,
                    Rules = hitRules.Select(r => r.Name).ToList(),
                    Score = Math.Min(score, 100),
                    InThrowaway = inThrowaway
                });
            }
            catch
            {
                // 单个文件解析失败不影响整体
            }
        }

        result.Note = result.ScannedFiles == 0
            ? "候选文件都没有可解析的导入表。"
            : $"检查了 {result.ScannedFiles} 个可执行文件";

        return result;
    }

    // ---------------------------------------------------------------- 报告

    /// <summary>把扫描结果转成体检发现。判级规则：Temp/下载里命中注入类能力才算线索。</summary>
    public static List<CheatFinding> ToFindings(ScanResult scan)
    {
        var findings = new List<CheatFinding>();

        if (scan.Hits.Count == 0)
        {
            findings.Add(new CheatFinding
            {
                Group = "文件行为特征",
                Title = "临时目录与下载目录里没有可疑的可执行文件",
                Detail = scan.Note + "。这里查的是「程序具备哪些能力」——"
                         + "读写其他进程内存、远程线程注入、全局键盘钩子这些，正常软件很少同时具备。"
                         + "需要说明：外挂装在 Program Files 或用安装器部署时不在扫描范围内。",
                Level = "ok"
            });

            return findings;
        }

        foreach (var hit in scan.Hits.OrderByDescending(h => h.Score).Take(10))
        {
            // 组合里含注入或驱动加载，且文件在临时目录 → 线索；否则只提示
            var serious = hit.InThrowaway && hit.Score >= 40;

            findings.Add(new CheatFinding
            {
                Group = "文件行为特征",
                Title = $"{hit.FileName}：{string.Join("、", hit.Rules)}",
                Detail = "这个文件导入了上述系统函数，意味着它具备对应能力："
                         + string.Join("；", Rules.Where(r => hit.Rules.Contains(r.Name)).Select(r => r.Explain))
                         + (hit.InThrowaway
                             ? "\n它就放在临时/下载目录里——正常软件装完之后不会留在这种地方。"
                             : "\n它在普通程序目录下，可能是正常软件或开发工具。"),
                Basis = $"{hit.Path}　{hit.SizeText}　修改于 {hit.Modified:yyyy-MM-dd}",
                Level = serious ? "warn" : "hint",
                Weight = serious ? Math.Min(hit.Score / 2, 20) : 4
            });
        }

        return findings;
    }

    // ---------------------------------------------------------------- PE 解析

    /// <summary>
    /// 读导入表里的函数名集合。解析不了（不是 PE、加壳后导入表被抹、.NET 单文件）返回空集。
    ///
    /// 只读文件头与导入表，不执行、不加载、不映射到内存。
    /// </summary>
    public static HashSet<string> ReadImports(string path)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        byte[] data;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 64 || info.Length > MaxSizeBytes) return names;

            data = File.ReadAllBytes(path);
        }
        catch
        {
            return names;
        }

        try
        {
            if (data.Length < 0x40) return names;

            var peOffset = BitConverter.ToInt32(data, 0x3C);
            if (peOffset <= 0 || peOffset + 24 >= data.Length) return names;

            // "PE\0\0"
            if (data[peOffset] != 'P' || data[peOffset + 1] != 'E') return names;

            var coff = peOffset + 4;
            var sectionCount = BitConverter.ToUInt16(data, coff + 2);
            var optionalSize = BitConverter.ToUInt16(data, coff + 16);

            var optional = coff + 20;
            if (optional + optionalSize > data.Length || optionalSize < 96) return names;

            var magic = BitConverter.ToUInt16(data, optional);
            var isPe32Plus = magic == 0x20B;

            // DataDirectory 起始：PE32 = optional+96，PE32+ = optional+112
            var dataDir = optional + (isPe32Plus ? 112 : 96);
            if (dataDir + 16 > data.Length) return names;

            // 第 2 项是导入表（index 1），每项 8 字节（RVA + Size）
            var importRva = BitConverter.ToInt32(data, dataDir + 8);
            if (importRva <= 0) return names;

            var sections = ReadSections(data, optional, optionalSize, sectionCount);
            if (sections.Count == 0) return names;

            var importOffset = RvaToOffset(importRva, sections);
            if (importOffset <= 0 || importOffset >= data.Length) return names;

            // 遍历 IMAGE_IMPORT_DESCRIPTOR（每项 20 字节），直到全 0
            for (var i = 0; i < 512; i++)
            {
                var desc = importOffset + i * 20;
                if (desc + 20 > data.Length) break;

                var nameRva = BitConverter.ToInt32(data, desc + 12);   // Name
                var thunkRva = BitConverter.ToInt32(data, desc);       // OriginalFirstThunk

                if (nameRva == 0 && thunkRva == 0) break;

                if (thunkRva == 0) continue;

                var thunkOffset = RvaToOffset(thunkRva, sections);
                if (thunkOffset <= 0 || thunkOffset >= data.Length) continue;

                var step = isPe32Plus ? 8 : 4;

                for (var j = 0; j < 4096; j++)
                {
                    var entry = thunkOffset + j * step;
                    if (entry + step > data.Length) break;

                    var value = isPe32Plus
                        ? BitConverter.ToInt64(data, entry)
                        : BitConverter.ToInt32(data, entry);

                    if (value == 0) break;

                    var ordinalFlag = isPe32Plus ? (1L << 63) : (1L << 31);
                    if ((value & ordinalFlag) != 0) continue;   // 按序号导入，没有名字

                    var hintRva = (int)(value & 0x7FFFFFFF);
                    var hintOffset = RvaToOffset(hintRva, sections);
                    if (hintOffset <= 0 || hintOffset + 2 >= data.Length) continue;

                    // 前 2 字节是 Hint，之后是函数名（以 \0 结尾）
                    var end = hintOffset + 2;
                    while (end < data.Length && data[end] != 0) end++;

                    if (end <= hintOffset + 2) continue;

                    var name = System.Text.Encoding.ASCII.GetString(data, hintOffset + 2, end - hintOffset - 2);
                    if (name.Length > 0 && name.Length < 128) names.Add(name);
                }
            }
        }
        catch
        {
            // 解析失败返回已收集到的部分
        }

        return names;
    }

    private sealed class Section
    {
        public uint VirtualAddress { get; init; }
        public uint VirtualSize { get; init; }
        public uint RawPointer { get; init; }
        public uint RawSize { get; init; }
    }

    private static List<Section> ReadSections(byte[] data, int optional, int optionalSize, int count)
    {
        var list = new List<Section>();

        var table = optional + optionalSize;
        if (table <= 0 || table >= data.Length || count > 96) return list;

        for (var i = 0; i < count; i++)
        {
            var entry = table + i * 40;
            if (entry + 40 > data.Length) break;

            list.Add(new Section
            {
                VirtualSize = BitConverter.ToUInt32(data, entry + 8),
                VirtualAddress = BitConverter.ToUInt32(data, entry + 12),
                RawSize = BitConverter.ToUInt32(data, entry + 16),
                RawPointer = BitConverter.ToUInt32(data, entry + 20)
            });
        }

        return list;
    }

    private static int RvaToOffset(int rva, List<Section> sections)
    {
        foreach (var s in sections)
        {
            var size = Math.Max(s.VirtualSize, s.RawSize);
            if (rva >= s.VirtualAddress && rva < s.VirtualAddress + size)
                return (int)(rva - s.VirtualAddress + s.RawPointer);
        }

        // 落在头部（没有 section 覆盖）时，RVA 就是文件偏移
        return rva;
    }
}
