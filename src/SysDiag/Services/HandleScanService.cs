using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 进程句柄排查：枚举系统全表句柄，找出「谁打开了目标进程」。
/// 归属方式：先在本进程句柄表里定位「我们刚打开的目标进程句柄」对应的内核对象地址，
/// 再用同一地址匹配其他进程的句柄——对象地址相等即同一对象，不需要对每条句柄做查询，也绕开了
/// 目标句柄缺少查询权限时无法归属的问题。目标进程打不开时降级为复制句柄归属（受权限限制）。
/// </summary>
public static class HandleScanService
{
    private const int SystemExtendedHandleInformation = 64;
    private const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    private const uint DUPLICATE_SAME_ACCESS = 2;

    // 进程对象访问掩码（Win32 PROCESS_* 位）
    private const uint PROCESS_TERMINATE = 0x0001;
    private const uint PROCESS_CREATE_THREAD = 0x0002;
    private const uint PROCESS_VM_OPERATION = 0x0008;
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_VM_WRITE = 0x0020;
    private const uint PROCESS_DUP_HANDLE = 0x0040;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_SUSPEND_RESUME = 0x0800;
    private const uint PROCESS_QUERY_LIMITED = 0x1000;

    // 补齐 System Informer（phnt）里进程对象访问掩码的其余位：
    // 之前只认了内存/线程/终止这几项，0x0080 / 0x0100 / 0x0200 / 0x00100000 全被漏成「—」。
    private const uint PROCESS_CREATE_PROCESS = 0x0080;
    private const uint PROCESS_SET_QUOTA = 0x0100;
    private const uint PROCESS_SET_INFORMATION = 0x0200;
    private const uint SYNCHRONIZE = 0x00100000;
    private const uint PROCESS_ALL_ACCESS = 0x001FFFFF;

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int cls, IntPtr info, int len, out int returnLen);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr hSrcProcess, IntPtr hSrcHandle, IntPtr hDstProcess,
        out IntPtr hDstHandle, uint desiredAccess, bool inheritHandle, uint options);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessId(IntPtr hObject);

    /// <summary>
    /// 系统级组件白名单。它们打开任意进程的句柄都是操作系统正常工作的一部分：
    /// csrss 管每个进程、explorer 读窗口、Defender 扫内存。未列进来的照样显示，只是不给「可信」标签。
    /// </summary>
    private static readonly HashSet<string> KnownOwners = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "memcompression", "csrss", "smss", "wininit", "winlogon",
        "services", "lsass", "svchost", "explorer", "dwm", "conhost", "dllhost", "sihost",
        "ctfmon", "fontdrvhost", "taskhostw", "runtimebroker", "searchindexer", "searchapplication",
        "wmiprvse", "spoolsv", "audiodg", "wudfhost", "msmpeng", "securityhealthservice",
        "securityhealthsystray", "nissrv", "smartscreen", "werfault", "wermgr", "usocore", "cmd"
    };

    /// <summary>识别一个进程名是否已知组件。返回标签，空串表示未知。</summary>
    internal static string IdentifyOwner(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return "";

        // 反作弊组件优先：ACE、EAC、BattlEye 是环境可信的正面信号
        var antiCheat = AntiCheatCatalog.Identify(processName);
        if (!string.IsNullOrEmpty(antiCheat)) return antiCheat;

        var name = Path.GetFileNameWithoutExtension(processName);
        return KnownOwners.Contains(name) ? "系统组件" : "";
    }

    /// <summary>
    /// 把权限掩码转成中文说明。掩码里一个已知位都没有时给「—」。
    ///
    /// 具体解码在 <see cref="AccessMaskTables"/>：先把 GENERIC_* 展开再判断，
    /// 否则「只带 GENERIC_WRITE」的句柄会显示成「—」，把写内存的能力藏了。
    /// </summary>
    internal static string DescribeAccess(uint access)
    {
        // 全部权限单独说，否则会列出十来个词，反而看不出重点
        if ((access & PROCESS_ALL_ACCESS) == PROCESS_ALL_ACCESS) return "全部权限（含注入能力）";

        var flags = AccessMaskTables.DescribeProcess(access);
        if (AccessMaskTables.HasGeneric(access)) flags.Add("（泛用权限位）");

        return flags.Count > 0 ? string.Join(" · ", flags) : "—";
    }

    /// <summary>
    /// 技术等级：能写内存或建线程 = 高危；能读内存 / 复制句柄 / 挂起 / 创建子进程 = 提示；其余 = 正常。
    /// （复制句柄与创建子进程本身不能改内存，但它们是注入链路上的常用手段，所以不归到「正常」。）
    /// </summary>
    internal static string ClassifyRisk(uint access)
    {
        if (AccessMaskTables.CanInject(access)) return "warn";
        if (AccessMaskTables.CanProbe(access)) return "hint";
        return "ok";
    }

    /// <summary>能力档位（一句人话），供列表与报告的第一列使用。</summary>
    internal static string DescribeCapability(uint access)
    {
        var expanded = AccessMaskTables.ExpandGeneric(access);

        if (AccessMaskTables.CanInject(expanded)) return "可注入";
        if ((expanded & PROCESS_VM_READ) != 0) return "可读内存";
        if ((expanded & PROCESS_DUP_HANDLE) != 0) return "可窃取句柄";
        if ((expanded & (PROCESS_SUSPEND_RESUME | PROCESS_TERMINATE)) != 0) return "可控制进程";
        if ((expanded & (PROCESS_QUERY_INFORMATION | PROCESS_QUERY_LIMITED)) != 0) return "仅查询";
        return "低权限";
    }

    /// <summary>复用的大缓冲：稳态下每次扫描零分配，避免 4~8MB 反复进 LOH。</summary>
    private static readonly object BufferLock = new();

    private static byte[] _buffer = new byte[0x800000];

    /// <summary>按需扩容（只增不减），返回可用的共享缓冲。</summary>
    private static byte[] RentBuffer(int size)
    {
        if (_buffer.Length < size)
            _buffer = new byte[Math.Max(size, _buffer.Length * 2)];

        return _buffer;
    }

    /// <summary>执行扫描。targetPid 传本进程自己也可以（排查谁持有对自己的句柄）。</summary>
    public static HandleScanResult Scan(int targetPid, string targetName)
    {
        // 共享缓冲 + 全表枚举，串行执行（并发时排队，单次 0.1 秒级）
        lock (BufferLock)
        {
            return ScanCore(targetPid, targetName);
        }
    }

    private static HandleScanResult ScanCore(int targetPid, string targetName)
    {
        var sw = Stopwatch.StartNew();
        int ourPid = Environment.ProcessId;

        var note = "";
        long totalHandles = 0;
        short processIndex = -1;
        ulong targetObject = 0;
        byte[] data = null;

        // 1. 造一个「指向自己进程的真句柄」，到句柄表里反查它的类型索引——
        //    SystemExtendedHandleInformation 里的 ObjectTypeIndex 是本进程视角的类型缓存索引，
        //    不同 Windows 版本不同，不能写死。
        var hSelf = IntPtr.Zero;
        DuplicateHandle(GetCurrentProcess(), GetCurrentProcess(), GetCurrentProcess(),
            out hSelf, 0, false, DUPLICATE_SAME_ACCESS);

        // 2. 打开目标进程：拿得到句柄才能在表里定位目标的内核对象地址。
        var hTarget = OpenProcess(PROCESS_QUERY_LIMITED | PROCESS_DUP_HANDLE, false, targetPid);

        try
        {
            // 3. 枚举全表，4MB 起步按 STATUS_INFO_LENGTH_MISMATCH 翻倍，上限 256MB
            var size = 0x800000;
            while (size <= 0x10000000)
            {
                var buf = Marshal.AllocHGlobal(size);
                try
                {
                    var status = NtQuerySystemInformation(SystemExtendedHandleInformation, buf, size, out _);
                    if (status == 0)
                    {
                        data = RentBuffer(size);
                        Marshal.Copy(buf, data, 0, size);
                        break;
                    }
                    if (status != STATUS_INFO_LENGTH_MISMATCH)
                    {
                        note = $"句柄表读取失败（NtStatus 0x{status:X8}）";
                        break;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
                size *= 2;
            }

            if (data != null)
            {
                totalHandles = BitConverter.ToInt64(data, 0);
                const int stride = 40;   // SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX（x64，含尾部对齐）
                const int baseOff = 16;  // SYSTEM_HANDLE_INFORMATION_EX 头部两个指针

                var hSelfValue = unchecked((uint)hSelf.ToInt64());
                var hTargetValue = hTarget != IntPtr.Zero ? unchecked((uint)hTarget.ToInt64()) : 0;

                for (long i = 0; i < totalHandles; i++)
                {
                    var off = (int)(baseOff + i * stride);
                    if (off + stride > data.Length) break;

                    int pid = (int)BitConverter.ToInt64(data, off + 8);
                    if (pid != ourPid) continue;

                    uint handle = BitConverter.ToUInt32(data, off + 16);
                    if (handle == hSelfValue)
                        processIndex = BitConverter.ToInt16(data, off + 26);
                    else if (hTargetValue != 0 && handle == hTargetValue)
                        targetObject = BitConverter.ToUInt64(data, off);

                    if (processIndex >= 0 && (hTargetValue == 0 || targetObject != 0)) break;
                }

                if (processIndex < 0)
                    note = "无法定位进程对象类型索引（罕见，句柄表快照期间进程退出？）";
            }
            else if (note.Length == 0)
            {
                note = "句柄表读取失败";
            }
        }
        finally
        {
            if (hTarget != IntPtr.Zero) CloseHandle(hTarget);
            if (hSelf != IntPtr.Zero) CloseHandle(hSelf);
        }

        var owners = new Dictionary<int, uint>();
        int unknown = 0;
        var precise = targetObject != 0 && processIndex >= 0;

        if (precise)
        {
            // 4a. 精确归属：内核对象地址相同 = 同一个进程对象。不需要对每条句柄开查询权限。
            const int stride = 40;
            const int baseOff = 16;

            for (long i = 0; i < totalHandles; i++)
            {
                var off = (int)(baseOff + i * stride);
                if (off + stride > data!.Length) break;

                var obj = BitConverter.ToUInt64(data, off);
                if (obj != targetObject) continue;

                int pid = (int)BitConverter.ToInt64(data, off + 8);
                if (pid == ourPid || pid == targetPid) continue;

                uint access = BitConverter.ToUInt32(data, off + 20);
                owners[pid] = owners.TryGetValue(pid, out var acc) ? acc | access : access;
            }
        }
        else if (processIndex >= 0)
        {
            // 4b. 降级：目标进程打不开（受保护），复制候选句柄用 GetProcessId 归属，
            //     有查询权限的能归属成功，其余计入「无法归属」——不猜。
            note = "无法打开目标进程，改用复制句柄归属（部分句柄缺查询权限，归属不全）";

            const int stride = 40;
            const int baseOff = 16;
            var attempts = 0;

            for (long i = 0; i < totalHandles && attempts < 4000; i++)
            {
                var off = (int)(baseOff + i * stride);
                if (off + stride > data!.Length) break;

                if (BitConverter.ToInt16(data, off + 26) != processIndex) continue;

                int pid = (int)BitConverter.ToInt64(data, off + 8);
                if (pid == ourPid || pid == targetPid || pid <= 0) continue;

                attempts++;
                uint access = BitConverter.ToUInt32(data, off + 20);

                var dup = IntPtr.Zero;
                try
                {
                    if (!OpenSourceAndDuplicate(pid, (IntPtr)BitConverter.ToInt64(data, off + 16), ref dup))
                    {
                        unknown++;
                        continue;
                    }

                    var actual = GetProcessId(dup);
                    if (actual == (uint)targetPid)
                        owners[pid] = owners.TryGetValue(pid, out var acc) ? acc | access : access;
                }
                finally
                {
                    if (dup != IntPtr.Zero) CloseHandle(dup);
                }
            }
        }

        // 5. 汇总：合并出进程名、标签、等级；高危在前，白名单在后。
        var findings = new List<HandleOwner>();
        foreach (var kv in owners)
        {
            string name;
            try { name = Process.GetProcessById(kv.Key).ProcessName; }
            catch { name = "进程" + kv.Key; }

            findings.Add(new HandleOwner
            {
                Pid = kv.Key,
                Name = name,
                Tag = IdentifyOwner(name),
                Access = kv.Value,
                FlagsText = DescribeAccess(kv.Value),
                Capability = DescribeCapability(kv.Value),
                Risk = ClassifyRisk(kv.Value)
            });
        }

        findings.Sort((a, b) =>
        {
            int Rank(HandleOwner x) => x.Known ? 3 : x.Risk switch { "warn" => 0, "hint" => 1, _ => 2 };
            var c = Rank(a).CompareTo(Rank(b));
            return c != 0 ? c : a.Pid.CompareTo(b.Pid);
        });

        if (hTarget == IntPtr.Zero && processIndex >= 0 && !precise && note.Length == 0)
            note = "目标进程无法打开";

        return new HandleScanResult
        {
            TargetPid = targetPid,
            TargetName = targetName,
            TotalHandles = totalHandles,
            Findings = findings,
            UnknownCount = unknown,
            Precise = precise,
            Note = note,
            ElapsedText = $"{sw.ElapsedMilliseconds / 1000.0:0.0} 秒"
        };
    }

    private static bool OpenSourceAndDuplicate(int ownerPid, long handle, ref IntPtr dup)
    {
        var src = OpenProcess(PROCESS_DUP_HANDLE, false, ownerPid);
        if (src == IntPtr.Zero) return false;

        try
        {
            return DuplicateHandle(src, (IntPtr)handle, GetCurrentProcess(), out dup, 0, false, DUPLICATE_SAME_ACCESS);
        }
        finally
        {
            CloseHandle(src);
        }
    }

    // ---------------- 报告 ----------------

    public static ReportPayload BuildReport(HandleScanResult r)
    {
        var doc = new HtmlDoc("进程句柄排查",
            $"目标 {r.TargetName}（{r.TargetPid}） · 全表 {r.TotalHandles:N0} 条句柄 · 外部进程 {r.Findings.Count} 个 · 耗时 {r.ElapsedText}");

        var known = r.Findings.Where(x => x.Known).ToList();
        var warn = r.Findings.Where(x => x.Risk == "warn" && !x.Known).ToList();
        var hint = r.Findings.Where(x => x.Risk == "hint" && !x.Known).ToList();

        doc.Kpis(
            (r.Findings.Count.ToString(), "外部进程", "#4C6EF5"),
            (warn.Count.ToString(), "可写内存 / 建线程", warn.Count > 0 ? "#C92A2A" : "#2F9E44"),
            (hint.Count.ToString(), "仅可读内存", hint.Count > 0 ? "#F08C00" : "#2F9E44"),
            (known.Count.ToString(), "已知组件", "#8B5CF6"));

        doc.Note(r.Precise
            ? "归属方式：按内核对象地址精确匹配，全部条目可信。"
            : "归属方式：复制句柄降级归属（目标进程无法打开），缺查询权限的句柄无法确认，未计入列表。"
              + (r.UnknownCount > 0 ? $" 无法确认的候选 {r.UnknownCount} 条。" : ""));

        doc.H2($"谁打开了 {r.TargetName}（{r.TargetPid}）");

        if (r.Findings.Count == 0)
            doc.Empty("没有外部进程持有目标进程的句柄。");
        else
        {
            doc.Table(
                new[] { "进程", "权限", "掩码", "等级" },
                r.Findings.Select(x => new[]
                {
                    $"<b>{HtmlDoc.E(x.Header)}</b>" +
                    (x.Known ? $" <span class=\"tag t-ok\">{HtmlDoc.E(x.Tag)}</span>" : ""),
                    $"<span class=\"{(x.Risk == "warn" && !x.Known ? "t-bad" : x.Risk == "hint" && !x.Known ? "t-warn" : "")}\">" +
                    $"<b>{HtmlDoc.E(x.Capability)}</b>　{HtmlDoc.E(x.FlagsText)}</span>",
                    $"<span class=\"mono\">{x.AccessHex}</span>",
                    $"<span class=\"{(x.Known || x.Risk == "ok" ? "t-ok" : x.Risk == "warn" ? "t-bad" : "t-warn")}\">{x.RiskText}</span>"
                }),
                new[] { "220", "", "100", "78" });
        }

        doc.H2("怎么读这份结果");
        doc.Raw("<div class=\"note\">" +
                "1. <b>可写入内存</b>（PROCESS_VM_WRITE / CREATE_THREAD）是注入类外挂的必要条件，" +
                "普通软件极少需要这两个权限——出现未知名词值得查。<br/>" +
                "2. <b>可读取内存</b>（PROCESS_VM_READ）单独出现很常见：调试器、监控工具、任务管理器级软件都会要，" +
                "结合进程名判断。<br/>" +
                "3. <b>已知组件</b>（系统组件、反作弊）打开进程是正常工作，不算风险，但照常列出——不隐藏证据。<br/>" +
                "4. 需要管理员权限才能看到全部归属；普通权限下系统进程的句柄查不全，结果会偏少。<br/>" +
                "5. 句柄是<b>瞬时状态</b>：扫描那一刻谁开着，不等于一直开着。要反复确认就多扫几次。" +
                "</div>");

        var html = doc.Build();
        var path = HtmlReportBuilder.Save(html, "进程句柄排查");

        var data = new ReportData
        {
            Kind = "handle-scan",
            Title = "进程句柄排查",
            Subtitle = $"目标 {r.TargetName}（{r.TargetPid}） · 外部进程 {r.Findings.Count} 个",
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
                Level = warn.Count > 0 ? "中" : "低",
                Text = warn.Count > 0
                    ? $"{warn.Count} 个未知进程可写入目标内存"
                    : r.Findings.Count == 0
                        ? "没有外部进程持有目标句柄"
                        : "没有未知进程持有高危权限",
                Counts = new ReportCounts
                {
                    Warn = warn.Count,
                    Hint = hint.Count,
                    Ok = r.OkCount
                }
            },
            Sections = new List<ReportSection>
            {
                new()
                {
                    Group = "外部句柄",
                    Items = r.Findings.Select(x => new ReportItem
                    {
                        Title = x.Header + (x.Known ? "（" + x.Tag + "）" : ""),
                        Detail = x.FlagsText,
                        Basis = x.Known ? "已知组件，正常工作" : "未知来源，请核对",
                        Level = x.Known ? "ok" : x.Risk switch { "warn" => "warn", "hint" => "hint", _ => "ok" }
                    }).ToList()
                }
            }
        };

        return new ReportPayload
        {
            Title = "进程句柄排查",
            Subtitle = $"目标 {r.TargetName}（{r.TargetPid}） · 外部进程 {r.Findings.Count} 个 · {r.ElapsedText}",
            EngineTag = "句柄全表",
            HtmlPath = path,
            PlainSummary = $"进程句柄排查\n目标 {r.TargetName}（{r.TargetPid}）\n" +
                           $"外部进程 {r.Findings.Count} 个 / 可写 {warn.Count} / 仅可读 {hint.Count} / 已知组件 {known.Count}",
            Data = data
        };
    }
}
