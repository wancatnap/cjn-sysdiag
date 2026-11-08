using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;

namespace SysDiag.Services;

/// <summary>
/// 内核级 DPC / 中断采集。
///
/// 为什么能做：Windows 自带 ETW 的 NT Kernel Logger 会话，开
/// <c>EVENT_TRACE_FLAG_DPC | EVENT_TRACE_FLAG_INTERRUPT</c> 就能拿到内核 DPC 的
/// PERFINFO_DPC 事件，事件里带**单次执行时长**。这条路不需要装驱动、不需要签名、
/// 不需要 WDK，只要管理员权限。LatencyMon 装驱动是为了采集更多细节（ISR 归因、
/// 驱动名解析、watchdog），不是拿到时长本身的必要条件。
///
/// 边界（界面上必须原样写出来，不许含糊）：
/// - **需要管理员**：NT Kernel Logger 是特权会话，普通权限直接拒绝。
/// - **只做时长分布，不做模块归因**：把 DPC 归因到某个 .sys 需要符号解析与地址区间匹配，
///   做不好会给出错误结论，本版本明确不做，不猜。
/// - **中断（ISR）只计数，不算时长**：PERFINFO 的 ISR 事件结构里没有可靠的单次时长字段，
///   强行配对进出事件在丢事件时会算出离谱值，因此不报 ISR 时长。
/// - **同机全局唯一**：一台机器同时只能有一个 NT Kernel Logger 会话。若 WPR / xperf /
///   LatencyMon 正在录制，这里会启动失败，此时如实报错并让用户先停掉对方。
/// </summary>
public static class KernelTraceService
{
    // ---------------------------------------------------------------- 常量

    /// <summary>NT Kernel Logger 的会话名，固定值，不能改。</summary>
    private const string LoggerName = "NT Kernel Logger";

    private const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
    private const uint EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
    private const uint EVENT_TRACE_FLAG_DPC = 0x00000020;
    private const uint EVENT_TRACE_FLAG_INTERRUPT = 0x00000040;
    private const uint EVENT_TRACE_CONTROL_STOP = 1;

    private const uint PROCESS_TRACE_MODE_REAL_TIME = 0x00000100;
    private const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;

    /// <summary>PERFINFO 事件类型：DPC。Mof 里的 type 字段。</summary>
    private const byte TypeDpc = 11;

    /// <summary>PERFINFO 事件类型：中断服务例程。</summary>
    private const byte TypeIsr = 12;

    // EVENT_TRACE_PROPERTIES 在 x64 上的字段偏移。整个结构 128 字节，
    // LoggerName 的宽字符串紧跟在结构之后。
    // 因为有这些偏移，启动失败时会把错误码原样报出来，便于定位。
    private const int OffWnodeFlags = 52;
    private const int OffBufferSize = 56;
    private const int OffLogFileMode = 72;
    private const int OffFlushTimer = 76;
    private const int OffEnableFlags = 80;
    private const int OffLogFileNameOffset = 120;
    private const int OffLoggerNameOffset = 124;
    private const int PropsSize = 128;

    /// <summary>
    /// 与 EVENT_TRACE_PROPERTIES 一一对应的托管结构。
    /// 用结构体而不是纯手算偏移，是为了让 CLR 负责对齐——手算错一个字段就是
    /// ERROR_BAD_LENGTH(24)，且很难从错误码反推是哪一项。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct WnodeHeader
    {
        public uint BufferSize;
        public uint ProviderId;
        public ulong HistoricalContext;
        public IntPtr KernelHandle;
        public long TimeStamp;
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TraceProperties
    {
        public WnodeHeader Wnode;
        public uint BufferSize;
        public uint MinimumBuffers;
        public uint MaximumBuffers;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint FlushTimer;
        public uint EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers;
        public uint FreeBuffers;
        public uint EventsLost;
        public uint BuffersWritten;
        public uint LogBuffersLost;
        public uint RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset;
        public uint LoggerNameOffset;
    }

    // EVENT_TRACE_LOGFILE 的字段偏移（x64，结构 456 字节）。我们只写需要的那几个，
    // 其余保持 0。缓冲区按 1024 字节分配，留足余量，避免 ETW 回填头部时越界。
    private const int OffLoggerName = 8;
    private const int OffProcessTraceMode = 28;
    private const int OffEventRecordCallback = 432;
    private const int OffContext = 448;
    private const int LogFileSize = 1024;

    // EVENT_RECORD 的字段偏移（x64，结构 112 字节）
    private const int OffHeaderTypeLegacy = 4;    // legacy 事件的 Type 低字节
    private const int OffHeaderId = 40;           // manifest 事件的 Id
    private const int OffUserData = 96;

    // PERFINFO_DPC：Routine(8) + InitialTime(8) + Duration(8，单位 100ns)
    private const int OffDpcDuration = 16;

    /// <summary>单次捕获最多保留的 DPC 时长样本数，超出后只计数不分位数。</summary>
    private const int MaxSamples = 500_000;

    // ---------------------------------------------------------------- P/Invoke

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint StartTraceW(out ulong sessionHandle, string sessionName, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint ControlTraceW(ulong sessionHandle, string sessionName,
        IntPtr properties, uint controlCode);

    // TRACEHANDLE 是 ULONG64：x64 下写成 uint 会截断高 32 位，
    // 后面 CloseTrace 拿到错句柄，会话就停不掉。
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern ulong OpenTraceW(IntPtr logfile);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint ProcessTrace([In] ulong[] handleArray, uint handleCount,
        IntPtr startTime, IntPtr endTime);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint CloseTrace(ulong traceHandle);

    // ---------------------------------------------------------------- 特权

    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x0002;

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

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValueW(string systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges,
        ref TokenPrivileges newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// 启用 <c>SeSystemProfilePrivilege</c>（配置文件系统性能）。
    ///
    /// 管理员组成员默认**持有**这个特权，但进程令牌里它是禁用状态，
    /// 必须显式 AdjustTokenPrivileges 打开才能开内核跟踪会话。
    /// tracelog / logman / WPR 都会自己做这一步，直接调 StartTrace 的代码如果漏了，
    /// 表现是各种奇怪的错误码而不是明确的「权限不足」。
    /// </summary>
    private static bool TryEnableProfilePrivilege(out string note)
    {
        note = "";
        IntPtr token = IntPtr.Zero;

        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out token))
            {
                note = "打开进程令牌失败：" + Marshal.GetLastWin32Error();
                return false;
            }

            if (!LookupPrivilegeValueW(null, "SeSystemProfilePrivilege", out var luid))
            {
                note = "查不到 SeSystemProfilePrivilege：" + Marshal.GetLastWin32Error();
                return false;
            }

            var tp = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Privilege = new LuidAndAttributes { Luid = luid, Attributes = SePrivilegeEnabled }
            };

            if (!AdjustTokenPrivileges(token, false, ref tp,
                    (uint)Marshal.SizeOf<TokenPrivileges>(), IntPtr.Zero, IntPtr.Zero))
            {
                note = "启用特权失败：" + Marshal.GetLastWin32Error();
                return false;
            }

            // AdjustTokenPrivileges 成功但一个特权都没改时不会报错，必须再看 LastError。
            var err = Marshal.GetLastWin32Error();
            if (err != 0)
            {
                note = "特权未生效（可能策略禁止）：" + err;
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            note = "启用特权异常：" + ex.Message;
            return false;
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void EventRecordCallbackDelegate(IntPtr eventRecord);

    // 委托必须由静态字段持有，否则会被 GC 回收，回调时进程直接崩。
    private static readonly EventRecordCallbackDelegate CallbackKeepAlive = OnEventRecord;
    private static readonly IntPtr CallbackPtr = Marshal.GetFunctionPointerForDelegate(CallbackKeepAlive);

    // ---------------------------------------------------------------- 采集状态

    private static readonly object Sync = new();
    private static List<double> _dpcDurationsUs = new();
    private static int _dpcCount;
    private static int _isrCount;
    private static double _dpcMaxUs;
    private static int _oddDurationCount;
    private static double _firstDurationUs = -1;

    // ---------------------------------------------------------------- 结果

    public sealed class KernelResult
    {
        public bool Ok { get; init; }
        public string Message { get; init; } = "";
        public int DpcCount { get; init; }
        public int IsrCount { get; init; }
        public double MeanUs { get; init; }
        public double P99Us { get; init; }
        public double MaxUs { get; init; }
        public double ElapsedSeconds { get; init; }

        public static KernelResult Fail(string message) => new() { Ok = false, Message = message };
    }

    /// <summary>当前进程是否以管理员身份运行。</summary>
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>内核记录器会话的当前状态。</summary>
    public enum SessionState
    {
        /// <summary>没有会话在跑，可以直接开。</summary>
        NotRunning,

        /// <summary>已有会话占用。内核记录器全系统只有一个，此时无法再开。</summary>
        Running,

        /// <summary>查询本身失败（权限不足等），无法判断。</summary>
        Unknown
    }

    /// <summary>
    /// 查询内核记录器是否已被占用。
    ///
    /// 这不是可选的优化项：NT Kernel Logger 全系统只能存在一个，被 WPR / xperf /
    /// LatencyMon / 部分监控与反作弊组件占用时，本软件就开不了。
    /// 提前查一次可以把「错误码 161」变成一句人能看懂的话。
    /// </summary>
    public static (SessionState State, string Note) QueryKernelSession()
    {
        if (!IsElevated)
            return (SessionState.Unknown, "需要管理员权限才能查询内核会话。");

        var size = 8192;
        var ptr = IntPtr.Zero;

        try
        {
            ptr = Marshal.AllocHGlobal(size);
            for (var i = 0; i < size; i++) Marshal.WriteByte(ptr, i, 0);

            var nameBytes = System.Text.Encoding.Unicode.GetBytes(LoggerName + "\0");
            var structSize = Marshal.SizeOf<TraceProperties>();

            var tp = new TraceProperties
            {
                // QUERY 会把会话属性写回来，缓冲区要给足，否则返回 234(ERROR_MORE_DATA)
                Wnode = new WnodeHeader { BufferSize = (uint)size, Flags = WNODE_FLAG_TRACED_GUID },
                LogFileMode = EVENT_TRACE_REAL_TIME_MODE,
                LoggerNameOffset = (uint)structSize
            };

            Marshal.StructureToPtr(tp, ptr, false);
            Marshal.Copy(nameBytes, 0, ptr + structSize, nameBytes.Length);

            var rc = ControlTraceW(0, LoggerName, ptr, 0 /* EVENT_TRACE_CONTROL_QUERY */);

            // 0 = 查到；234 = 查到但缓冲不足（同样说明存在）
            if (rc == 0 || rc == 234) return (SessionState.Running, "");
            if (rc == 4201) return (SessionState.NotRunning, "");   // ERROR_WMI_INSTANCE_NOT_FOUND

            return (SessionState.Unknown, $"查询返回 {rc}");
        }
        catch (Exception ex)
        {
            return (SessionState.Unknown, ex.Message);
        }
        finally
        {
            if (ptr != IntPtr.Zero) Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>
    /// 停掉正在占用内核记录器的会话。
    ///
    /// 这会打断对方正在进行的录制或监控，所以**只能由用户明确确认后调用**，
    /// 绝不在 Capture 里自动做。返回是否真的停掉了。
    /// </summary>
    public static bool StopExistingSession()
    {
        if (!IsElevated) return false;

        var size = 8192;
        var ptr = IntPtr.Zero;

        try
        {
            ptr = Marshal.AllocHGlobal(size);
            for (var i = 0; i < size; i++) Marshal.WriteByte(ptr, i, 0);

            var nameBytes = System.Text.Encoding.Unicode.GetBytes(LoggerName + "\0");
            var structSize = Marshal.SizeOf<TraceProperties>();

            var tp = new TraceProperties
            {
                Wnode = new WnodeHeader { BufferSize = (uint)size, Flags = WNODE_FLAG_TRACED_GUID },
                LogFileMode = EVENT_TRACE_REAL_TIME_MODE,
                LoggerNameOffset = (uint)structSize
            };

            Marshal.StructureToPtr(tp, ptr, false);
            Marshal.Copy(nameBytes, 0, ptr + structSize, nameBytes.Length);

            var rc = ControlTraceW(0, LoggerName, ptr, EVENT_TRACE_CONTROL_STOP);
            return rc == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (ptr != IntPtr.Zero) Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>
    /// 采集指定秒数的内核 DPC/ISR。阻塞调用，必须在后台线程跑。
    /// 任何一步失败都返回 Ok=false 并说明原因，不返回半截数据。
    /// </summary>
    public static KernelResult Capture(int seconds, CancellationToken token,
        IProgress<string> progress = null, bool takeoverExisting = false)
    {
        var budget = Math.Clamp(seconds, 3, 60);

        if (!IsElevated)
            return KernelResult.Fail("需要管理员权限。ETW 的内核记录器会话是特权会话，普通权限无法开启。");

        // 先查占用。这一步把「错误码 161」翻译成人话：
        // 内核记录器全系统只有一个，被别的程序占着时本软件开不了，且这不是本软件的 bug。
        var (state, stateNote) = QueryKernelSession();

        if (state == SessionState.Running && !takeoverExisting)
            return KernelResult.Fail(
                "内核记录器正被其他程序占用。\n\n" +
                "NT Kernel Logger 是全局唯一资源，同一时刻只能有一个程序使用。" +
                "常见占用者：Windows Performance Recorder、xperf、LatencyMon，" +
                "以及部分带监控/反作弊组件的软件。\n\n" +
                "请先关闭它们再采集；或在这个窗口里选择「接管内核记录器」——" +
                "那会中断对方正在进行的录制。");

        if (state == SessionState.Running && takeoverExisting)
        {
            progress?.Report("正在接管内核记录器…");
            if (!StopExistingSession())
                return KernelResult.Fail(
                    "检测到内核记录器被占用，但停止失败。可能被系统保护或有更高权限的组件持有。\n" +
                    (stateNote.Length > 0 ? "查询信息：" + stateNote : ""));
        }

        lock (Sync)
        {
            _dpcDurationsUs = new List<double>(capacity: 65536);
            _dpcCount = 0;
            _isrCount = 0;
            _dpcMaxUs = 0;
            _oddDurationCount = 0;
            _firstDurationUs = -1;
        }

        IntPtr props = IntPtr.Zero;
        IntPtr logFile = IntPtr.Zero;
        IntPtr loggerNamePtr = IntPtr.Zero;
        ulong sessionHandle = 0;
        ulong traceHandle = 0;
        var started = false;
        Thread pump = null;

        try
        {
            // ---------- 0. 先启用内核跟踪所需的特权 ----------
            TryEnableProfilePrivilege(out var privNote);

            progress?.Report("正在开启内核记录器…");

            // ---------- 1. StartTrace：开实时内核会话 ----------
            var nameBytes = System.Text.Encoding.Unicode.GetBytes(LoggerName + "\0");
            var structSize = Marshal.SizeOf<TraceProperties>();
            var totalSize = structSize + nameBytes.Length;

            props = Marshal.AllocHGlobal(totalSize + 32);
            for (var i = 0; i < totalSize + 32; i++) Marshal.WriteByte(props, i, 0);

            var tp = new TraceProperties
            {
                Wnode = new WnodeHeader
                {
                    // WNODE_HEADER.BufferSize = 整个结构的字节数，含紧跟其后的会话名。
                    // 漏填这个字段 StartTrace 返回 24（ERROR_BAD_LENGTH）；
                    // 名字位置写错返回 161（ERROR_BAD_PATHNAME）。两者是不同阶段的校验。
                    BufferSize = (uint)totalSize,
                    Flags = WNODE_FLAG_TRACED_GUID
                },
                BufferSize = 64,                 // 每个缓冲 64 KB
                MinimumBuffers = 4,
                MaximumBuffers = 64,
                LogFileMode = EVENT_TRACE_REAL_TIME_MODE,
                FlushTimer = 1,                  // 每秒刷新一次
                EnableFlags = EVENT_TRACE_FLAG_DPC | EVENT_TRACE_FLAG_INTERRUPT,
                LoggerNameOffset = (uint)structSize
            };

            Marshal.StructureToPtr(tp, props, false);
            Marshal.Copy(nameBytes, 0, props + structSize, nameBytes.Length);

            var rc = StartTraceW(out sessionHandle, LoggerName, props);

            if (rc == 183) // ERROR_ALREADY_EXISTS
            {
                return KernelResult.Fail(
                    "内核记录器会话已被占用：这台机器上已有别的程序在用 NT Kernel Logger" +
                    "（Windows Performance Recorder、xperf、LatencyMon 或另一份本软件）。" +
                    "请先停掉它再采集。");
            }

            if (rc != 0)
            {
                // 失败时把关键布局参数一并报出来：这些数字能直接判断是结构问题还是环境问题。
                var readBackName = Marshal.PtrToStringUni(props + structSize) ?? "";

                if (rc == 161)
                {
                    // 161 在官方文档里只对应两种原因：LogFileMode 与 LogFileNameOffset 同时为 0，
                    // 或同名会话被占用。本程序已把这两个字段都设对（回读校验过），
                    // 也确认了没有别的会话在跑，因此只剩「本机机制拒绝」这一种解释。
                    return KernelResult.Fail(
                        "内核记录器被本机环境拒绝（错误码 161）。\n\n" +
                        "参数本身没有问题：结构、会话名、LogFileMode、缓冲都已逐项校验。" +
                        "官方对这个错误码的定义只有两种原因，两种都已排除。\n\n" +
                        "已知会造成这种情况的：安全软件或反作弊组件 hook 了 ETW、" +
                        "组策略禁用内核日志、系统里存在异常的内核会话。\n" +
                        "可运行 logman query -ets 查看当前会话列表。\n\n" +
                        "用户态测量不受影响，可以继续用它做对照。需要内核级数据时，" +
                        "建议用 Windows Performance Recorder 或 LatencyMon 交叉验证。");
                }

                return KernelResult.Fail(
                    $"开启内核记录器失败，错误码 {rc}（{new System.ComponentModel.Win32Exception((int)rc).Message}）。" +
                    $"诊断：结构 {structSize} 字节、总长 {totalSize}、名字偏移 {structSize}、" +
                    $"读回名字「{readBackName}」、" +
                    (privNote.Length > 0 ? "特权：" + privNote : "特权已启用") + "。" +
                    (rc == 5 ? "这通常是权限不足或被安全软件拦截。" : ""));
            }

            started = true;

            // ---------- 2. OpenTrace：把会话接成实时消费 ----------
            logFile = Marshal.AllocHGlobal(LogFileSize);
            for (var i = 0; i < LogFileSize; i++) Marshal.WriteByte(logFile, i, 0);

            loggerNamePtr = Marshal.StringToHGlobalUni(LoggerName);
            Marshal.WriteIntPtr(logFile, OffLoggerName, loggerNamePtr);
            Marshal.WriteInt32(logFile, OffProcessTraceMode,
                unchecked((int)(PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD)));
            Marshal.WriteIntPtr(logFile, OffEventRecordCallback, CallbackPtr);
            Marshal.WriteIntPtr(logFile, OffContext, IntPtr.Zero);

            traceHandle = OpenTraceW(logFile);

            if (traceHandle == ulong.MaxValue)
            {
                var err = Marshal.GetLastWin32Error();
                return KernelResult.Fail(
                    $"接入内核记录器失败，错误码 {err}（{new System.ComponentModel.Win32Exception(err).Message}）。");
            }

            // ---------- 3. ProcessTrace：在独立线程里阻塞消费 ----------
            var pumpHandle = traceHandle;
            pump = new Thread(() =>
            {
                try
                {
                    ProcessTrace(new[] { pumpHandle }, 1, IntPtr.Zero, IntPtr.Zero);
                }
                catch
                {
                    // 停止时 CloseTrace 会让 ProcessTrace 抛错，属正常收尾。
                }
            })
            {
                IsBackground = true,
                Name = "SysDiag-EtwPump"
            };
            pump.Start();

            progress?.Report($"内核采集中… 0 / {budget} 秒");
            var sw = Stopwatch.StartNew();

            while (sw.Elapsed.TotalSeconds < budget)
            {
                token.ThrowIfCancellationRequested();
                Thread.Sleep(200);

                var elapsed = (int)sw.Elapsed.TotalSeconds;
                progress?.Report($"内核采集中… {elapsed} / {budget} 秒");
            }

            // ---------- 4. 停止会话 ----------
            progress?.Report("正在停止内核记录器…");
            StopSession(sessionHandle, props, ref traceHandle);

            if (pump.IsAlive) pump.Join(3000);

            SessionStopped = true;

            // ---------- 5. 汇总 ----------
            List<double> durations;
            int dpcCount, isrCount, odd;
            double maxUs, firstUs;

            lock (Sync)
            {
                durations = _dpcDurationsUs;
                dpcCount = _dpcCount;
                isrCount = _isrCount;
                maxUs = _dpcMaxUs;
                odd = _oddDurationCount;
                firstUs = _firstDurationUs;
            }

            if (dpcCount == 0)
            {
                return KernelResult.Fail(
                    "会话开启成功，但没收到 DPC 事件。可能这台机器/虚拟机没有暴露 DPC 计数" +
                    "（部分虚拟机与云主机如此），或会话被安全软件过滤。");
            }

            // 合理性校验：读错偏移会得到离谱值，宁可报失败也不给假数据。
            if (odd > dpcCount / 2)
            {
                return KernelResult.Fail(
                    $"解析出的 DPC 时长有 {odd}/{dpcCount} 条超出合理范围（首条 {firstUs:0.##} µs），" +
                    "判定为事件结构不匹配。本版本不以此版本输出结论，请反馈这个问题。");
            }

            durations.Sort();
            var mean = durations.Count > 0 ? durations.Average() : 0;
            var p99 = LatencyProbeService.Percentile(durations, 0.99);

            return new KernelResult
            {
                Ok = true,
                DpcCount = dpcCount,
                IsrCount = isrCount,
                MeanUs = mean,
                P99Us = p99,
                MaxUs = maxUs,
                ElapsedSeconds = sw.Elapsed.TotalSeconds,
                Message = $"采集 {sw.Elapsed.TotalSeconds:0} 秒：DPC {dpcCount:N0} 次、" +
                          $"中断 {isrCount:N0} 次。"
            };
        }
        catch (OperationCanceledException)
        {
            return KernelResult.Fail("已取消。");
        }
        catch (Exception ex)
        {
            return KernelResult.Fail("内核采集异常：" + ex.Message);
        }
        finally
        {
            if (started && !SessionStopped)
                StopSession(sessionHandle, props, ref traceHandle);

            if (props != IntPtr.Zero) Marshal.FreeHGlobal(props);
            if (logFile != IntPtr.Zero) Marshal.FreeHGlobal(logFile);
            if (loggerNamePtr != IntPtr.Zero) Marshal.FreeHGlobal(loggerNamePtr);

            SessionStopped = false;
        }
    }

    private static bool SessionStopped;

    private static void StopSession(ulong sessionHandle, IntPtr props, ref ulong traceHandle)
    {
        try
        {
            if (props != IntPtr.Zero)
                ControlTraceW(sessionHandle, LoggerName, props, EVENT_TRACE_CONTROL_STOP);
        }
        catch { }

        try
        {
            if (traceHandle != 0 && traceHandle != ulong.MaxValue)
                CloseTrace(traceHandle);
        }
        catch { }

        traceHandle = 0;
    }

    // ---------------------------------------------------------------- 回调

    /// <summary>
    /// ETW 回调。只做两件事：认事件类型、累加时长。
    /// 这里绝不能做任何阻塞操作（写文件、查库、调 UI），否则会话缓冲会被拖爆并丢事件。
    /// </summary>
    private static void OnEventRecord(IntPtr record)
    {
        if (record == IntPtr.Zero) return;

        try
        {
            var type = ReadEventType(record);
            if (type != TypeDpc && type != TypeIsr) return;

            if (type == TypeIsr)
            {
                Interlocked.Increment(ref _isrCount);
                return;
            }

            var userData = Marshal.ReadIntPtr(record, OffUserData);
            if (userData == IntPtr.Zero) return;

            // Duration 是 ULONG_PTR，单位 100ns。换成微秒。
            var raw = (ulong)Marshal.ReadInt64(userData, OffDpcDuration);
            var us = raw / 10.0;

            Interlocked.Increment(ref _dpcCount);

            lock (Sync)
            {
                if (_firstDurationUs < 0) _firstDurationUs = us;

                // 超过 1 秒的单次 DPC 不可能是真的，说明读错了字段，计为异常样本。
                if (us <= 0 || us > 1_000_000)
                {
                    _oddDurationCount++;
                    return;
                }

                if (us > _dpcMaxUs) _dpcMaxUs = us;
                if (_dpcDurationsUs.Count < MaxSamples) _dpcDurationsUs.Add(us);
            }
        }
        catch
        {
            // 单条事件解析失败不影响整体，继续消费。
        }
    }

    /// <summary>
    /// 读事件类型。legacy 内核事件把 Type 放在 EVENT_TRACE_HEADER 的 Class.Type，
    /// 在 EVENT_RECORD 里对应偏移 4 的低字节；manifest 事件则放在偏移 40 的 Id。
    /// 两处都看一遍，命中 DPC/ISR 才处理——避免读错位置把垃圾数据当成 DPC。
    /// </summary>
    private static byte ReadEventType(IntPtr record)
    {
        var legacy = Marshal.ReadByte(record, OffHeaderTypeLegacy);
        if (legacy == TypeDpc || legacy == TypeIsr) return legacy;

        var id = Marshal.ReadInt16(record, OffHeaderId);
        var low = (byte)(id & 0xFF);
        if (low == TypeDpc || low == TypeIsr) return low;

        return 0;
    }

    /// <summary>自检用：把一段原始 Duration（100ns）换算成微秒，验证单位换算正确。</summary>
    public static double RawToMicroseconds(ulong raw) => raw / 10.0;

    /// <summary>自检用：合理性判据，单次 DPC 超过 1 秒视为解析异常。</summary>
    public static bool IsPlausibleDpcUs(double us) => us > 0 && us <= 1_000_000;

    // ---------------------------------------------------------------- 启动诊断

    /// <summary>
    /// 穷举几种参数组合，找出本机能接受的 StartTrace 形式。
    ///
    /// 这不是产品功能，只是排错工具：StartTrace 的失败码（24 长度错、161 路径无效、5 拒绝访问）
    /// 很难反推是哪一项参数，只能逐项试。每次试成功都立即 ControlTrace STOP，避免影响下一项。
    /// </summary>
    public static string DiagnoseStart()
    {
        var sb = new System.Text.StringBuilder();
        var systemTraceGuid = new Guid("9e814aad-3204-11d2-9a82-006008a86939");

        sb.AppendLine($"管理员：{IsElevated}");
        sb.AppendLine($"结构大小：{Marshal.SizeOf<TraceProperties>()}");
        sb.AppendLine($"字段偏移：Wnode={Marshal.OffsetOf<TraceProperties>("Wnode")} " +
                      $"BufferSize={Marshal.OffsetOf<TraceProperties>("BufferSize")} " +
                      $"LogFileMode={Marshal.OffsetOf<TraceProperties>("LogFileMode")} " +
                      $"EnableFlags={Marshal.OffsetOf<TraceProperties>("EnableFlags")} " +
                      $"LoggerThreadId={Marshal.OffsetOf<TraceProperties>("LoggerThreadId")} " +
                      $"LogFileNameOffset={Marshal.OffsetOf<TraceProperties>("LogFileNameOffset")} " +
                      $"LoggerNameOffset={Marshal.OffsetOf<TraceProperties>("LoggerNameOffset")}");
        sb.AppendLine($"Wnode 内：Flags={Marshal.OffsetOf<WnodeHeader>("Flags")} " +
                      $"ClientContext={Marshal.OffsetOf<WnodeHeader>("ClientContext")}");
        TryEnableProfilePrivilege(out var privNote);
        sb.AppendLine($"特权：{(privNote.Length > 0 ? privNote : "已启用")}");
        sb.AppendLine();

        void Try(string name, string sessionName, uint minBuf, uint maxBuf,
            uint logMode, uint flushTimer, Guid wnodeGuid, uint clientContext,
            int sizePad, bool alignSize, bool emptyLogFileName = false)
        {
            var bytes = System.Text.Encoding.Unicode.GetBytes(LoggerName + "\0");

            // emptyLogFileName：把 LogFileNameOffset 指向结构之后的一个空 WCHAR，
            // 会话名排在它后面。用来排除「LogFileNameOffset=0 被某些实现判为非法」的情况。
            var logNameBytes = emptyLogFileName
                ? System.Text.Encoding.Unicode.GetBytes("\0")
                : Array.Empty<byte>();

            var structSize = Marshal.SizeOf<TraceProperties>();
            var raw = structSize + logNameBytes.Length + bytes.Length + sizePad;
            var total = alignSize ? ((raw + 7) / 8 * 8) : raw;

            var ptr = Marshal.AllocHGlobal(total + 64);

            try
            {
                for (var i = 0; i < total + 64; i++) Marshal.WriteByte(ptr, i, 0);

                var nameOffset = structSize + logNameBytes.Length;

                var tp = new TraceProperties
                {
                    Wnode = new WnodeHeader
                    {
                        BufferSize = (uint)total,
                        Flags = WNODE_FLAG_TRACED_GUID,
                        Guid = wnodeGuid,
                        ClientContext = clientContext
                    },
                    BufferSize = 64,
                    MinimumBuffers = minBuf,
                    MaximumBuffers = maxBuf,
                    LogFileMode = logMode,
                    FlushTimer = flushTimer,
                    EnableFlags = EVENT_TRACE_FLAG_DPC | EVENT_TRACE_FLAG_INTERRUPT,
                    LogFileNameOffset = emptyLogFileName ? (uint)structSize : 0,
                    LoggerNameOffset = (uint)nameOffset
                };

                Marshal.StructureToPtr(tp, ptr, false);

                if (logNameBytes.Length > 0)
                    Marshal.Copy(logNameBytes, 0, ptr + structSize, logNameBytes.Length);

                Marshal.Copy(bytes, 0, ptr + nameOffset, bytes.Length);

                var rc = StartTraceW(out var handle, sessionName, ptr);

                if (rc == 0)
                {
                    ControlTraceW(handle, LoggerName, ptr, EVENT_TRACE_CONTROL_STOP);
                    sb.AppendLine($"[成功] {name}");
                }
                else
                {
                    // 回读关键字段：如果 LogFileMode 读回 0，说明是结构写入的问题，
                    // 而不是 ETW 的参数校验问题。
                    var rbWnodeSize = Marshal.ReadInt32(ptr, 0);
                    var rbLogFileMode = Marshal.ReadInt32(ptr, OffLogFileMode);
                    var rbLogNameOff = Marshal.ReadInt32(ptr, OffLogFileNameOffset);
                    var rbLoggerNameOff = Marshal.ReadInt32(ptr, OffLoggerNameOffset);

                    sb.AppendLine($"[失败 {rc}] {name}　{new System.ComponentModel.Win32Exception((int)rc).Message}");
                    sb.AppendLine($"        回读：Wnode.BufferSize={rbWnodeSize} LogFileMode=0x{rbLogFileMode:X} " +
                                  $"LogFileNameOffset={rbLogNameOff} LoggerNameOffset={rbLoggerNameOff}");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"[异常] {name}　{ex.Message}");
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        var realtime = EVENT_TRACE_REAL_TIME_MODE;
        var sysGuid = systemTraceGuid;

        Try("A 基线", LoggerName, 4, 64, realtime, 1, Guid.Empty, 0, 0, false);
        Try("G BufferSize 多留 8 字节", LoggerName, 4, 64, realtime, 1, Guid.Empty, 0, 8, false);
        Try("H BufferSize 对齐到 8", LoggerName, 4, 64, realtime, 1, Guid.Empty, 0, 0, true);
        Try("I ClientContext=1（QPC 时间戳）", LoggerName, 4, 64, realtime, 1, Guid.Empty, 1, 0, false);
        Try("J ClientContext=1 + 对齐", LoggerName, 4, 64, realtime, 1, Guid.Empty, 1, 0, true);
        Try("K 缓冲 24/128", LoggerName, 24, 128, realtime, 1, Guid.Empty, 1, 0, false);
        Try("L SystemTraceControlGuid + ClientContext=1",
            LoggerName, 4, 64, realtime, 1, sysGuid, 1, 0, false);
        Try("M LogFileNameOffset 指向空串",
            LoggerName, 4, 64, realtime, 1, Guid.Empty, 1, 0, false, true);
        Try("N 空串 + 对齐 + 缓冲 24/128",
            LoggerName, 24, 128, realtime, 1, Guid.Empty, 1, 0, true, true);

        // ---------- O：先 ControlTrace 停掉可能残留的内核会话，再启动 ----------
        // 内核会话是系统级、进程退出也不自动关；之前调用失败可能留下半开会话。
        // 这一步同时也验证「是否有别的程序正占着 NT Kernel Logger」。
        try
        {
            var bytes = System.Text.Encoding.Unicode.GetBytes(LoggerName + "\0");
            var structSize = Marshal.SizeOf<TraceProperties>();
            var total = structSize + bytes.Length;
            var ptr = Marshal.AllocHGlobal(total + 64);

            for (var i = 0; i < total + 64; i++) Marshal.WriteByte(ptr, i, 0);

            var tp = new TraceProperties
            {
                Wnode = new WnodeHeader { BufferSize = (uint)total, Flags = WNODE_FLAG_TRACED_GUID },
                BufferSize = 64,
                MinimumBuffers = 4,
                MaximumBuffers = 64,
                LogFileMode = realtime,
                FlushTimer = 1,
                EnableFlags = EVENT_TRACE_FLAG_DPC | EVENT_TRACE_FLAG_INTERRUPT,
                LoggerNameOffset = (uint)structSize
            };

            Marshal.StructureToPtr(tp, ptr, false);
            Marshal.Copy(bytes, 0, ptr + structSize, bytes.Length);

            // 先查询：0 = 没有该会话在跑
            var queryRc = ControlTraceW(0, LoggerName, ptr, 0 /* QUERY */);
            sb.AppendLine($"[查询] 内核会话查询返回 {queryRc}" +
                          (queryRc == 0 ? "（存在，可能被别的程序占用）" : "（不存在）"));

            if (queryRc == 0)
            {
                var stopRc = ControlTraceW(0, LoggerName, ptr, EVENT_TRACE_CONTROL_STOP);
                sb.AppendLine($"[清理] 停止残留会话返回 {stopRc}");
            }

            // 重新填一遍，ControlTrace 会改结构里的统计字段
            Marshal.StructureToPtr(tp, ptr, false);
            Marshal.Copy(bytes, 0, ptr + structSize, bytes.Length);

            var rc = StartTraceW(out var handle, LoggerName, ptr);

            if (rc == 0)
            {
                ControlTraceW(handle, null, ptr, EVENT_TRACE_CONTROL_STOP);
                sb.AppendLine("[成功] O 先清理再启动");
            }
            else
            {
                sb.AppendLine($"[失败 {rc}] O 先清理再启动　{new System.ComponentModel.Win32Exception((int)rc).Message}");
            }

            Marshal.FreeHGlobal(ptr);
        }
        catch (Exception ex)
        {
            sb.AppendLine("[异常] O " + ex.Message);
        }

        return sb.ToString();
    }
}
