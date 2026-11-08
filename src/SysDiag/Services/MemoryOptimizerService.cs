using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 系统内存：读数 + 两个有真实作用的释放动作。
///
/// 定位说明（重要）：本模块不做「一键让电脑变快」。Windows 的内存管理里，
/// 待机列表（standby）本身就是缓存，占着物理内存但随时可以让出来；
/// 把它清空只会让下一次读盘变多。所以这里只提供两件事：
///   1. 看清内存到底被谁占着（进程排行 + 构成）；
///   2. 在你确认代价后，执行两个系统真的会响应的动作（清待机列表 / 回收工作集）。
/// 每个动作都会带前后对比数字，不灵就如实显示「没释放」，不编一个漂亮的数字。
/// </summary>
public static class MemoryOptimizerService
{
    // ---------------- 系统信息类常量

    private const int SystemMemoryListInformation = 80;

    /// <summary>MEMORY_LIST_COMMAND 枚举（Windows Internals / RAMMap 使用同一组值）。</summary>
    private const int MemoryEmptyWorkingSets = 2;
    private const int MemoryPurgeStandbyList = 4;

    private const uint StatusSuccess = 0x00000000;
    private const uint StatusPrivilegeNotHeld = 0xC0000061;
    private const uint StatusAccessDenied = 0xC0000022;
    private const uint StatusInfoLengthMismatch = 0xC0000004;

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessSetQuota = 0x0100;

    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;

    private const int ErrorNotAllAssigned = 1300;

    // ---------------- 扫描

    /// <summary>
    /// 读一次内存快照。三路数据源各自独立：某一路失败只让对应字段变成「读不到」，
    /// 不让整块显示失败——精简系统上经常只缺其中一路。
    /// </summary>
    public static MemorySnapshot Snapshot()
    {
        var mem = new MemoryStatusEx();
        var memOk = GlobalMemoryStatusEx(mem);

        var perf = new PerformanceInformation { cb = (uint)Marshal.SizeOf<PerformanceInformation>() };
        var perfOk = GetPerformanceInfo(ref perf, perf.cb);

        var listOk = TryReadPageLists(out var standby, out var modified, out var free, out var zero);

        // GetPerformanceInfo 的 CommitTotal / CommitLimit / SystemCache / KernelPaged / KernelNonpaged
        // 全部以「页」为单位，不是字节——少乘一次页大小会得到小 4096 倍的荒谬数字（实测踩过）。
        var pageSize = perfOk && perf.PageSize > 0 ? perf.PageSize : 4096UL;

        return new MemorySnapshot
        {
            TotalPhys = memOk ? mem.ullTotalPhys : 0,
            AvailPhys = memOk ? mem.ullAvailPhys : 0,
            TotalPageFile = memOk ? mem.ullTotalPageFile : 0,
            AvailPageFile = memOk ? mem.ullAvailPageFile : 0,

            CommitTotal = perfOk ? perf.CommitTotal * pageSize : 0,
            CommitLimit = perfOk ? perf.CommitLimit * pageSize : 0,
            CommitPeak = perfOk ? perf.CommitPeak * pageSize : 0,
            SystemCache = perfOk ? perf.SystemCache * pageSize : 0,
            KernelPaged = perfOk ? perf.KernelPaged * pageSize : 0,
            KernelNonpaged = perfOk ? perf.KernelNonpaged * pageSize : 0,
            PageSize = pageSize,

            StandbyPages = standby,
            ModifiedPages = modified,
            FreePages = free,
            ZeroPages = zero,
            ListInfoAvailable = listOk
        };
    }

    /// <summary>
    /// 进程内存排行。工作集（物理里压着的）、私有（进程独占的已提交）、提交（向系统认领的额度）
    /// 三个数分开给，因为「内存大」在不同列上含义完全不同。
    /// </summary>
    public static List<MemProcessRow> ScanProcesses()
    {
        var rows = new List<MemProcessRow>();

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var pid = p.Id;
                var name = p.ProcessName;

                long workingSet = 0, priv = 0, commit = 0;

                // 自己开句柄，不用 Process.Handle——后者要的权限更宽，容易被拒
                var handle = OpenProcess(
                    ProcessQueryLimitedInformation | ProcessQueryInformation | ProcessSetQuota,
                    false, pid);

                if (handle != IntPtr.Zero)
                {
                    var counters = new ProcessMemoryCountersEx
                    {
                        cb = (uint)Marshal.SizeOf<ProcessMemoryCountersEx>()
                    };
                    if (GetProcessMemoryInfo(handle, ref counters, counters.cb))
                    {
                        workingSet = (long)counters.WorkingSetSize;
                        priv = (long)counters.PrivateUsage;
                        commit = (long)counters.PagefileUsage;
                    }
                    CloseHandle(handle);
                }

                // GetProcessMemoryInfo 拿不到时退回托管口径，至少有一个数能看
                if (workingSet == 0 && priv == 0)
                {
                    try
                    {
                        workingSet = p.WorkingSet64;
                        priv = p.PrivateMemorySize64;
                        commit = priv;
                    }
                    catch { }
                }

                if (workingSet <= 0 && priv <= 0) continue;

                rows.Add(new MemProcessRow
                {
                    Pid = pid,
                    Name = name,
                    WorkingSetMb = workingSet / 1024 / 1024,
                    PrivateMb = priv / 1024 / 1024,
                    CommitMb = commit / 1024 / 1024,
                    IsCritical = IsCriticalProcess(name)
                });
            }
            catch { }
            finally { p.Dispose(); }
        }

        return rows.OrderByDescending(r => r.WorkingSetMb).ToList();
    }

    /// <summary>
    /// 这些进程回收工作集后往往会立刻大面积缺页，甚至拖慢整机，因此不允许勾选。
    /// 与 ProcessInventoryService 的保护名单同源，但这里只关心「能不能回收」，不关心「能不能结束」。
    /// </summary>
    private static bool IsCriticalProcess(string name) => name switch
    {
        "System" or "Idle" or "Registry" or "Memory Compression" => true,
        "smss" or "csrss" or "wininit" or "winlogon" or "services" or "lsass" or "lsm" => true,
        "svchost" or "dwm" or "fontdrvhost" or "audiodg" => true,
        _ => false
    };

    // ---------------- 动作

    /// <summary>
    /// 清空待机列表：这是唯一能让「可用物理内存」立刻涨一截的动作。
    /// 代价是这些缓存页要重新从磁盘读，接下来一段时间读盘变多；系统若开着 SysMain 会慢慢再填回来。
    /// 不减少任何进程的提交量，所以「提交」那一格不会变。
    /// </summary>
    public static MemoryReleaseResult PurgeStandbyList()
    {
        if (!EnablePrivilege("SeProfileSingleProcessPrivilege", out var privError))
            return new MemoryReleaseResult
            {
                Ok = false,
                Message = "需要以管理员身份运行才能清理待机列表。" + privError
            };

        var before = Snapshot();
        var command = MemoryPurgeStandbyList;
        var status = (uint)NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int));
        var after = Snapshot();

        if (status != StatusSuccess)
            return new MemoryReleaseResult
            {
                Ok = false,
                Message = StatusText(status)
            };

        var freedMb = ((long)after.AvailPhys - (long)before.AvailPhys) / 1024 / 1024;
        var standbyMb = (long)before.StandbyBytes / 1024 / 1024;

        var text = freedMb > 0
            ? $"可用内存 {MemorySnapshot.Bytes(before.AvailPhys)} → {MemorySnapshot.Bytes(after.AvailPhys)}，释放约 {freedMb} MB。"
            : $"系统已执行，但可用内存没有增加（{MemorySnapshot.Bytes(before.AvailPhys)} → {MemorySnapshot.Bytes(after.AvailPhys)}）。" +
              "通常是因为待机列表本来就是空的，或者刚清完就被其他程序重新分配了。";

        if (standbyMb > 0) text += $"{Environment.NewLine}清理前待机缓存 {standbyMb} MB。";

        return new MemoryReleaseResult { Ok = true, FreedMb = freedMb, Message = text };
    }

    /// <summary>
    /// 对选定进程回收工作集（EmptyWorkingSet）。
    /// 必须说清楚的实话：页并没有被销毁，只是被挪到待机列表里，进程下次访问就换回来。
    /// 所以「任务管理器里数字变小」几乎完全是账面效果，物理内存占用总量基本不变。
    /// </summary>
    public static MemoryReleaseResult EmptyWorkingSets(IReadOnlyCollection<int> pids)
    {
        if (pids.Count == 0)
            return new MemoryReleaseResult { Ok = false, Message = "没有选中任何进程。" };

        var before = Snapshot();
        var done = 0;
        var failed = 0;

        foreach (var pid in pids)
        {
            var handle = OpenProcess(ProcessQueryLimitedInformation | ProcessSetQuota, false, pid);
            if (handle == IntPtr.Zero)
            {
                failed++;
                continue;
            }

            try
            {
                if (EmptyWorkingSet(handle)) done++;
                else failed++;
            }
            finally { CloseHandle(handle); }
        }

        var after = Snapshot();
        var deltaMb = ((long)after.AvailPhys - (long)before.AvailPhys) / 1024 / 1024;

        var text = $"已回收 {done} 个进程" + (failed > 0 ? $"，{failed} 个失败（权限不足或已退出）" : "") + "。";

        if (deltaMb > 0)
            text += $"可用内存 +{deltaMb} MB。";
        else
            text += "可用内存基本没变——这符合预期：回收工作集只是把页挪进待机列表，不是真的释放。";

        return new MemoryReleaseResult { Ok = done > 0, FreedMb = deltaMb, Message = text };
    }

    /// <summary>
    /// 回收所有可回收进程的工作集（跳过系统关键进程）。
    /// RAMMap 的 Empty Working Sets 是同一件事，效果同样是账面的。
    /// </summary>
    public static MemoryReleaseResult EmptyAllWorkingSets()
    {
        var pids = ScanProcesses()
            .Where(r => !r.IsCritical && r.WorkingSetMb >= 16)
            .Select(r => r.Pid)
            .ToList();

        var result = EmptyWorkingSets(pids);
        return new MemoryReleaseResult
        {
            Ok = result.Ok,
            FreedMb = result.FreedMb,
            Message = result.Message + Environment.NewLine + "已跳过系统关键进程，以及工作集小于 16 MB 的进程。"
        };
    }

    /// <summary>把本软件自己的冷页还给系统。窗口关掉后调一次，免得自己变成「占内存的那个」。</summary>
    public static bool TrimSelf() => MemoryTuner.TrimIfIdle();

    // ---------------- 只读诊断

    /// <summary>
    /// 压缩内存 / SysMain / 页面文件 / LargeSystemCache。
    /// 全部只读：压缩内存关掉在低内存机器上通常更差，SysMain 已经由服务预设模块负责，
    /// 这里重复提供开关会让「改过什么」变得难以追溯。
    /// </summary>
    public static MemoryDiagnostics Diagnostics()
    {
        var (compress, compressMb, compressKnown) = ReadCompression();

        return new MemoryDiagnostics
        {
            MemoryCompression = compress,
            CompressionWorkingSetMb = compressMb,
            CompressionDetectable = compressKnown,
            SysMainState = ReadServiceState("SysMain"),
            PageFileText = ReadPageFile(out var pagePercent),
            PageFilePercent = pagePercent,
            LargeSystemCache = ReadLargeSystemCache()
        };
    }

    /// <summary>
    /// 内存压缩是否启用：以「Memory Compression」进程是否存在为准。
    /// 这个进程只在压缩开启且有内容可压时出现，所以「不存在」不能断定被关掉，
    /// 只报「关」会误导，因此把可判定性单独返回。
    /// </summary>
    private static (bool Enabled, long WorkingSetMb, bool Detectable) ReadCompression()
    {
        try
        {
            var procs = Process.GetProcessesByName("Memory Compression");
            if (procs.Length > 0)
            {
                long mb = 0;
                foreach (var p in procs)
                {
                    try { mb += p.WorkingSet64 / 1024 / 1024; } catch { }
                    p.Dispose();
                }
                return (true, mb, true);
            }

            // 进程不在：可能刚重启还没触发压缩，也可能是关掉了。读注册表确认开关。
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
            var value = key?.GetValue("EnableMemoryCompression");
            if (value is int i) return (i != 0, 0, true);

            return (false, 0, false);
        }
        catch
        {
            return (false, 0, false);
        }
    }

    /// <summary>服务状态走 WMI，与 LoggingService 同一套口径（本项目不引用 System.ServiceProcess）。</summary>
    private static string ReadServiceState(string name)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT State, StartMode FROM Win32_Service WHERE Name='{name}'");

            foreach (var o in searcher.Get())
            {
                using var mo = (ManagementObject)o;
                var state = mo["State"]?.ToString() ?? "";
                return state switch
                {
                    "Running" => "运行中",
                    "Stopped" => "已停止",
                    "Start Pending" => "正在启动",
                    "Stop Pending" => "正在停止",
                    "Paused" => "已暂停",
                    _ => state.Length == 0 ? "未安装" : state
                };
            }
        }
        catch { }

        return "未安装";
    }

    /// <summary>页面文件：总量、当前使用率。WMI 不可用时退回注册表里的配置值。</summary>
    private static string ReadPageFile(out double percent)
    {
        percent = 0;
        try
        {
            var parts = new List<string>();
            ulong totalMb = 0, usedMb = 0;

            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, AllocatedBaseSize, CurrentUsage, PeakUsage FROM Win32_PageFileUsage");

            foreach (ManagementObject mo in searcher.Get())
            {
                var name = mo["Name"]?.ToString() ?? "";
                var size = Convert.ToInt64(mo["AllocatedBaseSize"] ?? 0L);
                var usage = Convert.ToInt64(mo["CurrentUsage"] ?? 0L);
                var peak = Convert.ToInt64(mo["PeakUsage"] ?? 0L);

                totalMb += (ulong)Math.Max(0, size);
                usedMb += (ulong)Math.Max(0, usage);

                parts.Add($"{name} {size} MB（用 {usage} MB，峰值 {peak} MB）");
                mo.Dispose();
            }

            if (parts.Count == 0) return "未设置（系统托管）";

            if (totalMb > 0) percent = usedMb * 100.0 / totalMb;

            // 多家页面文件时只报合计，明细塞进提示更合适；这里给一行能读完的
            return parts.Count == 1
                ? $"{totalMb} MB，已用 {usedMb} MB（{percent:0}%）"
                : $"{parts.Count} 个，合计 {totalMb} MB，已用 {usedMb} MB（{percent:0}%）";
        }
        catch
        {
            return "读不到";
        }
    }

    private static int ReadLargeSystemCache()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
            return key?.GetValue("LargeSystemCache") is int v ? v : 0;
        }
        catch
        {
            return 0;
        }
    }

    // ---------------- 自证文本（--mem-probe）

    /// <summary>
    /// 命令行自证：把每一路数据源的原始值打出来，用来判断「读不到」到底是哪一环。
    /// 默认只读；带 purge 参数才真的清一次待机列表。
    /// </summary>
    public static string Probe(bool purge)
    {
        var sb = new System.Text.StringBuilder();
        var snap = Snapshot();

        sb.AppendLine($"时间 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"管理员 {ElevationService.IsAdmin}");
        sb.AppendLine();
        sb.AppendLine($"[GlobalMemoryStatusEx]  物理 {snap.TotalText} · 可用 {snap.AvailText} · 已用 {snap.UsedPercentText}");
        sb.AppendLine($"[GetPerformanceInfo]   提交 {snap.CommitText}（{snap.CommitPercentText}）· 峰值 {MemorySnapshot.Bytes(snap.CommitPeak)}");
        sb.AppendLine($"                       内核池 {snap.KernelPoolText} · 系统缓存 {snap.CacheText} · 页大小 {(int)snap.PageSize} 字节");
        sb.AppendLine($"                       raw: commit={snap.CommitTotal} limit={snap.CommitLimit} paged={snap.KernelPaged} nonpaged={snap.KernelNonpaged} cache={snap.SystemCache}");
        sb.AppendLine($"[SystemMemoryListInformation] 可读 {snap.ListInfoAvailable}");
        sb.AppendLine($"                       待机 {snap.StandbyText} · 已修改 {snap.ModifiedText} · 空闲 {MemorySnapshot.Bytes(snap.FreeBytes)}");
        sb.AppendLine($"                       占用（已用 − 已修改）{MemorySnapshot.Bytes(snap.LiveBytes)}");

        // 构成条的四段必须加起来等于物理总量，这一行就是给它做的现场校验
        var sum = snap.Segments().Aggregate(0UL, (acc, s) => acc + s.Bytes);
        sb.AppendLine($"                       四段合计 {MemorySnapshot.Bytes(sum)} / 物理 {snap.TotalText}" +
                      $"（差 {((double)sum - snap.TotalPhys) / Math.Max(1.0, snap.TotalPhys) * 100:0.0}%）");
        sb.AppendLine();

        var diag = Diagnostics();
        sb.AppendLine("[只读诊断] " + diag.Summary);
        sb.AppendLine();

        var rows = ScanProcesses();
        sb.AppendLine($"[进程] 共 {rows.Count} 个，按工作集前 12：");
        sb.AppendLine("  PID      工作集      私有       提交     进程");
        foreach (var r in rows.Take(12))
            sb.AppendLine($"  {r.Pid,-6} {r.WorkingSetText,-10} {r.PrivateText,-10} {r.CommitText,-10} {r.Name}");

        if (purge)
        {
            sb.AppendLine();
            var result = PurgeStandbyList();
            sb.AppendLine("[清理待机列表] " + (result.Ok ? "执行成功" : "执行失败"));
            sb.AppendLine("  " + result.Message.Replace(Environment.NewLine, Environment.NewLine + "  "));
        }

        return sb.ToString();
    }

    // ---------------- 内部工具

    private static string StatusText(uint status) => status switch
    {
        StatusPrivilegeNotHeld => "系统拒绝了这次请求：缺少 SeProfileSingleProcessPrivilege，请以管理员身份运行。",
        StatusAccessDenied => "系统拒绝了这次请求：拒绝访问。",
        StatusInfoLengthMismatch => "系统返回信息长度不匹配，本版本的结构体与当前 Windows 不一致。",
        _ => $"系统返回 0x{status:X8}，动作没有执行。"
    };

    /// <summary>
    /// 启用自身令牌里的一个特权。注意 AdjustTokenPrivileges 返回成功也可能「没拿到」，
    /// 必须再查 GetLastError == ERROR_NOT_ALL_ASSIGNED，否则会误判成已提权。
    /// </summary>
    private static bool EnablePrivilege(string name, out string error)
    {
        error = "";
        IntPtr token = IntPtr.Zero;

        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out token))
            {
                error = $"（OpenProcessToken 失败，错误码 {Marshal.GetLastWin32Error()}）";
                return false;
            }

            if (!LookupPrivilegeValue(null, name, out var luid))
            {
                error = $"（LookupPrivilegeValue 失败，错误码 {Marshal.GetLastWin32Error()}）";
                return false;
            }

            var tp = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Privilege = new LuidAndAttributes { Luid = luid, Attributes = SePrivilegeEnabled }
            };

            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
            {
                error = $"（AdjustTokenPrivileges 失败，错误码 {Marshal.GetLastWin32Error()}）";
                return false;
            }

            if (Marshal.GetLastWin32Error() == ErrorNotAllAssigned)
            {
                error = "（当前账户没有这个特权）";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = "（" + ex.Message + "）";
            return false;
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    private static bool TryReadPageLists(out ulong standby, out ulong modified, out ulong free, out ulong zero)
    {
        standby = modified = free = zero = 0;

        // 缓冲区给足：结构在 x64 上是 176 字节，留 256 免得因为版本差异反复失败
        var buffer = new byte[256];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);

        try
        {
            var status = (uint)NtQuerySystemInformation(
                SystemMemoryListInformation,
                handle.AddrOfPinnedObject(),
                buffer.Length,
                out _);

            if (status != StatusSuccess) return false;

            var info = Marshal.PtrToStructure<MemoryListInformation>(handle.AddrOfPinnedObject());

            zero = Pages(info.ZeroPageCount);
            free = Pages(info.FreePageCount);
            modified = Pages(info.ModifiedPageCount);

            // PageCountByPriority[0..7] 全是待机列表，按优先级分桶，求和才是总量
            if (info.PageCountByPriority != null)
                foreach (var bucket in info.PageCountByPriority)
                    standby += Pages(bucket);

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            handle.Free();
        }
    }

    private static ulong Pages(IntPtr value)
    {
        var pages = value.ToInt64();
        return pages > 0 ? (ulong)pages : 0;
    }

    // ---------------- P/Invoke

    [StructLayout(LayoutKind.Sequential)]
    private sealed class MemoryStatusEx
    {
        public uint dwLength = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint cb;
        public ulong CommitTotal;
        public ulong CommitLimit;
        public ulong CommitPeak;
        public ulong PhysicalTotal;
        public ulong PhysicalAvailable;
        public ulong SystemCache;
        public ulong KernelTotal;
        public ulong KernelPaged;
        public ulong KernelNonpaged;
        public ulong PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    /// <summary>
    /// MEMORY_LIST_INFORMATION。5 个页计数 + 两组 8 桶优先级数组 + 末尾一个页文件计数。
    /// 用 IntPtr 而不是 ulong，是为了 32 位进程下也读对。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryListInformation
    {
        public IntPtr ZeroPageCount;
        public IntPtr FreePageCount;
        public IntPtr ModifiedPageCount;
        public IntPtr ModifiedNoWritePageCount;
        public IntPtr BadPageCount;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public IntPtr[] PageCountByPriority;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public IntPtr[] RepurposedPagesByPriority;

        public IntPtr ModifiedPageCountPageFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx
    {
        public uint cb;
        public uint PageFaultCount;
        public ulong PeakWorkingSetSize;
        public ulong WorkingSetSize;
        public ulong QuotaPeakPagedPoolUsage;
        public ulong QuotaPagedPoolUsage;
        public ulong QuotaPeakNonPagedPoolUsage;
        public ulong QuotaNonPagedPoolUsage;
        public ulong PagefileUsage;
        public ulong PrivateUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes
    {
        public Luid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public LuidAndAttributes Privilege;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx lpBuffer);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetPerformanceInfo(ref PerformanceInformation info, uint size);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCountersEx counters, uint size);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr process);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll,
        ref TokenPrivileges newState, int bufferLength, IntPtr previousState, IntPtr returnLength);
}
