using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using Microsoft.Win32;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 外挂体检的深度采集层。
///
/// 为什么单独一层：原有 11 项采集偏「历史痕迹」（Prefetch、AmCache、USN、事件日志），
/// 能证明「某程序跑过」，但对**当前正被加载的东西**覆盖不足。外挂想生效必须落到三处之一：
/// <list type="number">
/// <item>内核驱动（读游戏内存、模拟输入）——查驱动来源与签名</item>
/// <item>注入到进程里的模块——原有「模块完整性」已覆盖</item>
/// <item>无文件持久化（WMI 订阅、AppInit_DLLs、IFEO）——本轮新增</item>
/// </list>
///
/// 判定原则：**只有「结构性异常」才升级为线索**。签名主体不是微软这类，合法软件也大量存在，
/// 顶多算提示；而「驱动文件在 Temp 目录」「服务镜像在用户目录」这种正常软件几乎不会出现，
/// 才配得上线索。误报比漏报更糟——一次误判会让人不再信这份报告。
/// </summary>
public static class CheatDeepProbeService
{
    public sealed class ProbeResult
    {
        public List<CheatFinding> Findings { get; } = new();
        public List<CheatStage> Stages { get; } = new();
    }

    /// <summary>已知被滥用做内存读写的合法驱动（BYOVD）。命中不等于作弊，只说明存在滥用面。</summary>
    private static readonly string[] KnownAbusedDrivers =
    {
        "winring0.sys", "winring0x64.sys", "winio64.sys", "winio32.sys",
        "gdrv.sys", "gdrv2.sys", "iqvw64e.sys", "iqvw64.sys",
        "rtcore64.sys", "eneio64.sys", "eneio.sys",
        "msio64.sys", "msio32.sys", "capcom.sys", "physmem.sys",
        "dbutil.sys", "asrdrv101.sys", "asrdrv106.sys", "asrdrv109.sys",
        "kprocesshacker.sys", "nvflash.sys", "atillk64.sys"
    };

    public static ProbeResult Run(IProgress<string> progress)
    {
        var result = new ProbeResult();

        // 先把「本机是不是精简系统」摆出来。
        // 这一步必须放在最前面：精简系统会删掉 Defender 平台、部分 WMI 提供程序、WinRE，
        // 导致后面几项「读不到」。如果报告里不先说明，用户看到的是一堆「无法判断」，
        // 会以为是软件坏了、或者更糟——把「没检查成」当成「没问题」。
        result.Findings.Add(BuildTrimFinding());
        result.Stages.Add(new CheatStage
        {
            Name = "系统完整性",
            State = "done",
            Items = TrimAwareness.CheckedCount,
            Note = TrimAwareness.Summary
        });

        progress?.Report("正在检查服务与驱动的来源…");
        CollectServiceImages(result);

        progress?.Report("正在检查无文件持久化…");
        CollectFilelessPersistence(result);

        progress?.Report("正在检查隐藏进程…");
        CollectHiddenProcesses(result);

        progress?.Report("正在检查可执行文件的行为特征…");
        var peScan = PeFeatureScanner.Scan(progress);
        result.Findings.AddRange(PeFeatureScanner.ToFindings(peScan));
        result.Stages.Add(new CheatStage
        {
            Name = "文件行为特征",
            State = "done",
            Items = peScan.ScannedFiles,
            Note = peScan.Note
        });

        progress?.Report("正在清点网络过滤驱动…");
        result.Findings.AddRange(NetworkFilterProbeService.Probe());
        result.Stages.Add(new CheatStage
        {
            Name = "网络过滤层",
            State = "done",
            Items = NetworkFilterProbeService.ReadBindings().Count,
            Note = "网卡绑定组件清点完成"
        });

        return result;
    }

    // ---------------------------------------------------------------- 服务与驱动来源

    /// <summary>
    /// 枚举 <c>HKLM\SYSTEM\CurrentControlSet\Services</c> 下所有内核驱动（Type=1/2）与服务（Type=16/32/272），
    /// 只报**结构上不该出现**的镜像位置。
    ///
    /// 这一项在开发过程中返工过一次，原因值得记下来：
    /// 最初用「镜像不在 System32\\drivers」+「X509Certificate 读不到证书 = 未签名」两条判定，
    /// 在本机跑出 **206 条异常**——`AcpiDev`、`acpipagr`、`1394ohci` 这些系统自带驱动全被标成未签名，
    /// ACE 反作弊装在 Program Files 的驱动被标成「不在系统目录」。一份 206 条假异常的报告等于没有报告。
    ///
    /// 两个错误：
    /// ① <c>X509Certificate.CreateFromSignedFile</c> 解析不了嵌入式签名的 .sys，读不到 ≠ 没签名；
    /// ② 大量合法驱动（反作弊、虚拟化、安全软件）本来就装在 Program Files 下。
    ///
    /// 所以现在只保留两条真正可靠的判据：**镜像在临时/下载/回收站目录**、**系统目录下登记了但文件已不存在**。
    /// 宁可少报，也不制造假异常。
    /// </summary>
    /// <summary>把精简状态作为第一条结论，后面所有「读不到」都能在这里找到原因。</summary>
    private static CheatFinding BuildTrimFinding()
    {
        if (!TrimAwareness.IsTrimmed)
        {
            return new CheatFinding
            {
                Group = "系统完整性",
                Title = "系统文件与组件完整",
                Detail = "关键组件（Defender 平台、Edge、WinRE、组件存储、DriverStore 等）都在位，"
                         + "各项检测的数据源应该都能读到。",
                Level = "ok"
            };
        }

        var names = string.Join("、", TrimAwareness.MissingNames.Take(6));

        return new CheatFinding
        {
            Group = "系统完整性",
            Title = "本机是精简/魔改系统",
            Detail = $"缺失分 {TrimAwareness.Score}，检查 {TrimAwareness.CheckedCount} 项，缺 {TrimAwareness.MissingNames.Count} 项：" +
                     names +
                     "。构件被移除会让部分检测永远读不到数据——**那不是权限问题，重装本软件也解决不了**。" +
                     "受影响的典型项：Defender 详细状态（平台组件被删）、网卡过滤驱动清点（WMI 类可能被删）、" +
                     "部分 PCIe 属性、恢复环境相关。这些项在报告里显示「未检查」或「无法判断」，" +
                     "不能当作「没有异常」。" +
                     "另外，精简镜像常同时改 hosts 与 DNS 做去广告，那些条目会出现在「网络隐蔽手法」里，属于预期而非异常。",
            Basis = TrimAwareness.Summary,
            Level = "hint",
            Weight = 0
        };
    }

    private static void CollectServiceImages(ProbeResult result)
    {
        var stage = new CheatStage { Name = "服务与驱动来源" };

        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (root == null)
            {
                stage.State = "failed";
                stage.Note = "读不到 Services 注册表根键。";
                result.Stages.Add(stage);
                return;
            }

            var total = 0;
            var drivers = 0;
            var suspicious = new List<(string Name, string Path, string Why)>();
            var missing = new List<(string Name, string Path)>();
            var abused = new List<(string Name, string File)>();

            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                if (key == null) continue;

                var type = key.GetValue("Type") as int? ?? 0;
                var isDriver = type is 1 or 2;
                var isService = type is 16 or 32 or 272;
                if (!isDriver && !isService) continue;

                var raw = key.GetValue("ImagePath") as string;
                if (string.IsNullOrWhiteSpace(raw)) continue;

                total++;
                if (isDriver) drivers++;

                var path = ExpandImagePath(raw);
                if (path.Length == 0) continue;

                var fileName = Path.GetFileName(path);
                var lower = path.ToLowerInvariant();

                if (KnownAbusedDrivers.Contains(fileName.ToLowerInvariant()))
                    abused.Add((name, fileName));

                // 判据一：镜像落在临时、下载、回收站、公共目录——正常软件不会这么装
                var inThrowaway = lower.Contains(@"\temp\")
                                  || lower.Contains(@"\$recycle.bin\")
                                  || lower.Contains(@"\downloads\")
                                  || lower.Contains(@"\users\public\");

                if (inThrowaway)
                {
                    suspicious.Add((name, path, "镜像位于临时/下载/回收站目录"));
                    continue;
                }

                // 判据二：系统目录下登记了但文件已不存在
                // 只对系统目录判断——第三方目录的驱动可能延迟创建，误报率高
                var inSystem = lower.Contains(@"\windows\system32\") || lower.Contains(@"\windows\syswow64\");
                if (inSystem && !File.Exists(path))
                    missing.Add((name, path));
            }

            stage.Items = total;

            if (suspicious.Count == 0)
            {
                stage.Note = $"{total} 个驱动/服务（含 {drivers} 个驱动），镜像位置正常";
                result.Findings.Add(new CheatFinding
                {
                    Group = "服务与驱动来源",
                    Title = "驱动与服务镜像没有可疑位置",
                    Detail = $"检查了 {total} 个内核驱动与服务的镜像路径（其中 {drivers} 个驱动）。"
                             + "内核驱动能直接读写任意进程内存、模拟输入，是外挂最常见的内核态落地方式。"
                             + "这里没有发现装在临时目录、下载目录或回收站里的镜像。",
                    Level = "ok"
                });
            }
            else
            {
                stage.State = "limited";
                stage.Note = $"{suspicious.Count} 个镜像位置异常";

                foreach (var (name, path, why) in suspicious.Take(10))
                {
                    result.Findings.Add(new CheatFinding
                    {
                        Group = "服务与驱动来源",
                        Title = $"{name}：{why}",
                        Detail = "正常软件绝不会把驱动或服务镜像放在临时目录、下载目录或回收站里。"
                                 + "这是外挂/加载器的典型摆放方式：用完即删、不留在 Program Files。"
                                 + "如果这个服务名你能对上某个软件，值得点开它的路径看看到底是什么。",
                        Basis = path,
                        Level = "warn",
                        Weight = 16
                    });
                }
            }

            // 已丢失的镜像：降级为提示——卸载不干净、驱动动态加载都会造成这种残留
            if (missing.Count > 0)
            {
                result.Findings.Add(new CheatFinding
                {
                    Group = "服务与驱动来源",
                    Title = $"{missing.Count} 个驱动/服务登记了但文件已不存在",
                    Detail = "常见于软件卸载不干净、或驱动是按需释放的。"
                             + "单独看它说明不了什么，但如果同时出现「路径在临时目录」或「无文件持久化」，"
                             + "说明有人在反复装卸同类组件。",
                    Basis = string.Join("、", missing.Take(8).Select(m => m.Name)),
                    Level = "hint",
                    Weight = 4
                });
            }

            // 已知被滥用的驱动：只提示，因为灯效/主板/监控工具也会装它们
            foreach (var (name, file) in abused.Take(8))
            {
                result.Findings.Add(new CheatFinding
                {
                    Group = "服务与驱动来源",
                    Title = $"装有一个常被滥用的驱动：{file}",
                    Detail = $"{file} 是公开的硬件访问驱动，多数由主板/灯效/监控工具安装，本身合法。"
                             + "它同时也能被用来读写进程内存，所以是外挂作者偏爱的「合法外壳」。"
                             + "出现不代表作弊，但说明这台机器具备内核级读写能力，值得结合其他项一起看。",
                    Basis = name,
                    Level = "hint",
                    Weight = 6
                });
            }

            result.Stages.Add(stage);
        }
        catch (Exception ex)
        {
            stage.State = "failed";
            stage.Note = ex.Message;
            result.Stages.Add(stage);
        }
    }

    // ---------------------------------------------------------------- 无文件持久化

    /// <summary>
    /// 三个经典的「不落盘」持久化点。共同特点：正常系统里要么为空，要么只有极少条目，
    /// 一旦出现就是明确的「有人在这里挂了东西」。
    /// </summary>
    private static void CollectFilelessPersistence(ProbeResult result)
    {
        var stage = new CheatStage { Name = "无文件持久化" };
        var hits = 0;

        try
        {
            // ---------- 1. WMI 事件订阅 ----------
            var filters = new List<string>();

            try
            {
                using var searcher = new ManagementObjectSearcher(
                    new ManagementScope(@"\\.\root\subscription"),
                    new ObjectQuery("SELECT Name, Query FROM __EventFilter"));

                foreach (ManagementObject mo in searcher.Get())
                {
                    using (mo)
                    {
                        var name = mo["Name"]?.ToString() ?? "(未命名)";
                        var query = mo["Query"]?.ToString() ?? "";
                        filters.Add($"{name}：{query}");
                    }
                }
            }
            catch
            {
                // root\subscription 需要管理员；读不到不算异常，stage 标受限
                stage.State = "limited";
                stage.Note = "WMI 订阅需要管理员权限，未能读取。";
            }

            if (filters.Count > 0)
            {
                // 系统自带的订阅必须排除，否则这台机器每条都会报一条假线索。
                // SCM Event Log Filter 是服务控制管理器把服务状态写进事件日志用的，
                // 几乎所有 Windows 都有；把它当异常等于给每台机器都判可疑。
                var builtIn = new[] { "SCM Event Log Filter", "SCM Event Log Consumer" };

                var unknown = filters
                    .Where(f => !builtIn.Any(b => f.Contains(b, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                if (unknown.Count > 0)
                {
                    hits++;
                    result.Findings.Add(new CheatFinding
                    {
                        Group = "无文件持久化",
                        Title = $"存在 {unknown.Count} 条非系统的 WMI 事件订阅",
                        Detail = "WMI 永久事件订阅能在不写文件、不建服务的情况下，在指定事件发生时执行命令。"
                                 + "Windows 自带的「SCM Event Log Filter」已排除；剩下的都不是系统自带的。"
                                 + "外挂用它做无文件启动：重启后仍在，且不出现在任何启动项列表里。",
                        Basis = string.Join("　", unknown.Take(5)),
                        Level = "warn",
                        Weight = 14
                    });
                }
                else
                {
                    result.Findings.Add(new CheatFinding
                    {
                        Group = "无文件持久化",
                        Title = "只有系统自带的那条 WMI 订阅",
                        Detail = $"{filters.Count} 条订阅全部是 Windows 自带的服务控制管理器事件记录器"
                                 + "（SCM Event Log Filter，把服务启停写进事件日志），属于正常配置。"
                                 + "没有发现第三方建立的持久化订阅。",
                        Basis = string.Join("　", filters.Take(3)),
                        Level = "ok"
                    });
                }
            }
            else if (stage.State != "limited")
            {
                result.Findings.Add(new CheatFinding
                {
                    Group = "无文件持久化",
                    Title = "没有 WMI 事件订阅",
                    Detail = "WMI 永久订阅是无文件持久化的首选手段，这里没有发现。",
                    Level = "ok"
                });
            }

            // ---------- 2. AppInit_DLLs ----------
            foreach (var (view, path) in new[]
                     {
                         ("64 位", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows"),
                         ("32 位", @"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Windows")
                     })
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(path);
                    var dlls = key?.GetValue("AppInit_DLLs") as string ?? "";
                    var load = key?.GetValue("LoadAppInit_DLLs") as int? ?? 0;

                    if (load != 0 && dlls.Trim().Length > 0)
                    {
                        hits++;
                        result.Findings.Add(new CheatFinding
                        {
                            Group = "无文件持久化",
                            Title = $"AppInit_DLLs 已启用（{view}）",
                            Detail = "AppInit_DLLs 会让系统把指定 DLL 加载进**每一个**加载了 user32.dll 的进程。"
                                     + "这是老牌注入手法，微软默认关闭且不推荐。启用并填了路径，说明有东西想全局注入。",
                            Basis = dlls,
                            Level = "warn",
                            Weight = 16
                        });
                    }
                }
                catch { }
            }

            // ---------- 3. IFEO 映像劫持 ----------
            var ifeoHits = new List<string>();

            try
            {
                using var ifeo = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options");

                if (ifeo != null)
                {
                    foreach (var name in ifeo.GetSubKeyNames())
                    {
                        using var sub = ifeo.OpenSubKey(name);
                        var debugger = sub?.GetValue("Debugger") as string;

                        if (!string.IsNullOrWhiteSpace(debugger))
                            ifeoHits.Add($"{name} → {debugger}");
                    }
                }
            }
            catch { }

            if (ifeoHits.Count > 0)
            {
                hits++;
                result.Findings.Add(new CheatFinding
                {
                    Group = "无文件持久化",
                    Title = $"存在 {ifeoHits.Count} 条 IFEO 映像劫持",
                    Detail = "IFEO 的 Debugger 值会让指定程序启动时先运行另一个程序。"
                             + "正常系统里只有极少数调试工具会用；外挂用它劫持游戏启动器，或劫持任务管理器、杀毒软件让它起不来。"
                             + "**这一项既是作弊信号，也是「系统被别人动过」的证据**，不要直接删，先备份。",
                    Basis = string.Join("　", ifeoHits.Take(6)),
                    Level = "warn",
                    Weight = 18
                });
            }

            if (hits == 0 && stage.State != "limited")
            {
                stage.Note = "WMI 订阅 / AppInit_DLLs / IFEO 均无异常";
            }
            else if (hits > 0)
            {
                stage.Note = $"{hits} 类持久化点存在条目";
            }

            stage.Items = filters.Count + ifeoHits.Count;
            result.Stages.Add(stage);
        }
        catch (Exception ex)
        {
            stage.State = "failed";
            stage.Note = ex.Message;
            result.Stages.Add(stage);
        }
    }

    // ---------------------------------------------------------------- 隐藏进程

    /// <summary>
    /// 用三种互不依赖的方式枚举 PID，比对差异。
    ///
    /// 原理：外挂常 hook 掉某一套 API 让自己从任务管理器里消失。但要让三套机制同时说谎很难：
    /// Toolhelp32 走的是内核快照，WMI 走 WMI 服务，Process.GetProcesses 走 PSAPI/EnumProcesses。
    /// 差集里出现的 PID 说明「有一方看不到它」，值得人工确认。
    ///
    /// **为什么判提示而不是线索**：权限差异也会造成差集。系统进程、受保护进程在不同 API 下
    /// 可见性本来就不同，所以这条只作提示，且要求差集里的 PID 能被定位到具体进程名才列出。
    /// </summary>
    private static void CollectHiddenProcesses(ProbeResult result)
    {
        var stage = new CheatStage { Name = "隐藏进程比对" };

        try
        {
            var psapi = new HashSet<int>();
            foreach (var p in Process.GetProcesses())
            {
                using (p) psapi.Add(p.Id);
            }

            var wmi = new HashSet<int>();
            try
            {
                foreach (var row in EvidenceService.Wmi("SELECT ProcessId, Name FROM Win32_Process"))
                {
                    if (row.TryGetValue("ProcessId", out var v) && v != null
                        && int.TryParse(v.ToString(), out var pid))
                        wmi.Add(pid);
                }
            }
            catch { }

            if (wmi.Count == 0)
            {
                stage.State = "limited";
                stage.Note = "WMI 进程枚举失败，无法比对（通常是被安全软件拦截）。";
                result.Stages.Add(stage);
                return;
            }

            var toolhelp = NativeProcessList();

            stage.Items = psapi.Count;

            // 只报「Toolhelp 看得到、PSAPI 看不到」——反向差异多半是权限，不是隐藏
            var hidden = toolhelp.Except(psapi).ToList();

            if (hidden.Count == 0)
            {
                stage.Note = $"三套枚举结果一致（{psapi.Count} / {wmi.Count} / {toolhelp.Count}）";
                result.Findings.Add(new CheatFinding
                {
                    Group = "隐藏进程比对",
                    Title = "没有发现 API 之间的进程可见性差异",
                    Detail = $"用 PSAPI（{psapi.Count} 个）、WMI（{wmi.Count} 个）、内核快照（{toolhelp.Count} 个）"
                             + "三套互不依赖的机制枚举进程，没有出现「一方看不到」的进程。"
                             + "外挂若 hook 了其中一套 API 来隐藏自己，这里会出现差集。",
                    Level = "ok"
                });
            }
            else
            {
                stage.State = "limited";
                stage.Note = $"{hidden.Count} 个 PID 只在部分枚举中可见";

                var names = hidden.Take(10).Select(pid =>
                {
                    try
                    {
                        using var p = Process.GetProcessById(pid);
                        return $"{p.ProcessName}（PID {pid}）";
                    }
                    catch
                    {
                        return $"PID {pid}（无法打开进程名）";
                    }
                }).ToList();

                result.Findings.Add(new CheatFinding
                {
                    Group = "隐藏进程比对",
                    Title = $"{hidden.Count} 个进程只被部分枚举方式看到",
                    Detail = "这件事有两种解释，报告只负责指出差异，不替你下结论：\n"
                             + "① 外挂 hook 了某个进程枚举 API 让自己消失；\n"
                             + "② 该进程受保护或有特殊权限，不同 API 的可见性本来就不同。\n"
                             + "判断方法：看这些进程能不能对应到已知软件；对不上再深挖。",
                    Basis = string.Join("、", names),
                    Level = "hint",
                    Weight = 8
                });
            }

            result.Stages.Add(stage);
        }
        catch (Exception ex)
        {
            stage.State = "failed";
            stage.Note = ex.Message;
            result.Stages.Add(stage);
        }
    }

    // ---------------------------------------------------------------- 辅助

    /// <summary>把服务 ImagePath 展开成真实文件路径。解析不出来返回空串（不猜）。</summary>
    public static string ExpandImagePath(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var path = raw.Trim();

        // 带参数的镜像（如 "C:\x\a.exe" -k something）先剥出路径部分
        if (path.StartsWith("\"", StringComparison.Ordinal))
        {
            var end = path.IndexOf('"', 1);
            if (end > 1) path = path[1..end];
        }
        else
        {
            // 无引号但含空格+参数：只在明显有 .exe/.sys 后缀时截断
            var lower = path.ToLowerInvariant();
            var idx = lower.IndexOf(".sys ", StringComparison.Ordinal);
            if (idx < 0) idx = lower.IndexOf(".exe ", StringComparison.Ordinal);
            if (idx > 0) path = path[..(idx + 4)];
        }

        // 常见前缀写法
        if (path.StartsWith(@"\??\", StringComparison.Ordinal)) path = path[4..];
        if (path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                path[@"\SystemRoot\".Length..]);
        else if (path.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), path);
        else if (path.StartsWith(@"\SystemRoot", StringComparison.OrdinalIgnoreCase))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                path[@"\SystemRoot".Length..].TrimStart('\\'));

        // 环境变量（REG_EXPAND_SZ 已被注册表 API 展开，这里兜底）
        if (path.Contains('%'))
        {
            try { path = Environment.ExpandEnvironmentVariables(path); }
            catch { return ""; }
        }

        if (!path.Contains(':') && !path.StartsWith(@"\\", StringComparison.Ordinal))
            return "";

        return path;
    }

    /// <summary>用 Toolhelp32 快照取 PID 集合。这是最底层的一路枚举，不容易被用户态 hook 影响。</summary>
    private static HashSet<int> NativeProcessList()
    {
        var set = new HashSet<int>();

        var snapshot = CreateToolhelp32Snapshot(0x00000002 /* TH32CS_SNAPPROCESS */, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return set;

        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<PROCESSENTRY32>() };

            if (Process32First(snapshot, ref entry))
            {
                do
                {
                    if (entry.th32ProcessID > 0) set.Add((int)entry.th32ProcessID);
                }
                while (Process32Next(snapshot, ref entry));
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return set;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
