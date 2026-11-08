using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 直播伴侣优化：降优先级、限制 CPU 核心、切换高性能电源计划。
/// 逻辑对齐桌面上的「直播伴侣优化器」，但改为直接调 API，不依赖 psutil / powercfg 外部命令。
/// </summary>
public static class LiveOptimizerService
{
    public const string ProcessName = "直播伴侣";

    /// <summary>Windows 内置电源计划 GUID。</summary>
    private static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    private static long _lastIdle, _lastKernel, _lastUser;
    private static bool _hasLastSample;

    public static Process FindProcess()
    {
        try
        {
            return Process.GetProcessesByName(ProcessName).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>一次采样：CPU / 内存 / 温度 / 进程状态。</summary>
    public static LiveSnapshot Snapshot()
    {
        var p = FindProcess();
        return new LiveSnapshot
        {
            CpuPercent = ReadCpuPercent(),
            MemoryPercent = ReadMemoryPercent(),
            TemperatureText = ReadTemperature(),
            Running = p != null,
            ProcessId = p?.Id ?? 0
        };
    }

    /// <summary>优化：降优先级 + 跳过 0 号核 + 高性能电源。</summary>
    public static (bool Ok, string Message) Optimize()
    {
        var p = FindProcess();
        if (p == null)
            return (false, "未找到直播伴侣进程，请先启动它再优化。");

        var log = new List<string>();

        try
        {
            p.PriorityClass = ProcessPriorityClass.BelowNormal;
            log.Add("已把直播伴侣优先级降到「低于正常」，减少它和游戏抢 CPU。");
        }
        catch (Exception ex)
        {
            log.Add("优先级调整失败：" + ex.Message);
        }

        try
        {
            var cores = Environment.ProcessorCount;
            if (cores > 1)
            {
                p.ProcessorAffinity = (IntPtr)BuildAffinityMask(cores);
                log.Add($"已把直播伴侣限制到 {cores - 1} 个核心（跳过 0 号核，留给游戏/系统）。");
            }
            else
            {
                log.Add("只有 1 个逻辑核心，跳过核心限制。");
            }
        }
        catch (Exception ex)
        {
            log.Add("CPU 核心限制失败：" + ex.Message);
        }

        if (TrySetPowerScheme(HighPerformance, out var powerError))
            log.Add("已切换到「高性能」电源计划。");
        else
            log.Add("电源计划切换失败（通常需要管理员权限）：" + powerError);

        // GPU 图形首选项：Windows 会把「后台」程序的 GPU 调度降级，
        // 直播软件的画布合成可能被丢到核显，编码器吃不到独显。只对这一个 exe 生效。
        try
        {
            var exe = p.MainModule?.FileName ?? "";
            var (gpuOk, gpuMsg) = LiveStreamTweaks.ApplyGpuPreference(exe);
            log.Add(gpuOk ? "GPU 首选项：" + gpuMsg : "GPU 首选项未设置（" + gpuMsg + "）");
        }
        catch (Exception ex)
        {
            log.Add("读不到直播软件路径，跳过 GPU 首选项：" + ex.Message);
        }

        // MMCSS 捕获任务：直播软件注册采集线程时用这组配置。
        var (capOk, capMsg) = LiveStreamTweaks.ApplyCaptureProfile();
        log.Add(capOk ? "捕获任务：" + capMsg : "捕获任务未调整（" + capMsg + "）");

        // 内存：编码器要的是连续的物理页，清一次待机缓存是本轮唯一真能腾出物理内存的动作。
        // 不做「回收工作集」——那只是把页挪个位置，反而让直播软件下次采集时缺页。
        var mem = MemoryOptimizerService.PurgeStandbyList();
        log.Add(mem.Ok
            ? "内存：" + OneLine(mem.Message)
            : "内存：没有清理待机列表（" + OneLine(mem.Message) + "）");

        log.Add("手动建议：关掉 Discord/GeForce/RTSS 的覆盖层；游戏内帧率封顶到显示器刷新率；直播伴侣适当降低码率/分辨率。");

        return (true, string.Join(Environment.NewLine, log));
    }

    /// <summary>把多行结果压成一行，塞进优化记录里不撑高行高。</summary>
    private static string OneLine(string text) =>
        text.Replace("\r\n", " ").Replace("\n", " ").Trim();

    /// <summary>还原：优先级恢复正常、恢复全部核心、电源回到平衡。</summary>
    public static (bool Ok, string Message) Restore()
    {
        var p = FindProcess();
        var log = new List<string>();

        if (p != null)
        {
            try
            {
                p.PriorityClass = ProcessPriorityClass.Normal;
                log.Add("直播伴侣优先级已恢复「正常」。");
            }
            catch (Exception ex)
            {
                log.Add("优先级恢复失败：" + ex.Message);
            }

            try
            {
                var cores = Environment.ProcessorCount;
                p.ProcessorAffinity = (IntPtr)((1L << cores) - 1);
                log.Add("已恢复使用全部 CPU 核心。");
            }
            catch (Exception ex)
            {
                log.Add("核心恢复失败：" + ex.Message);
            }
        }
        else
        {
            log.Add("直播伴侣当前未运行，跳过进程设置。");
        }

        if (TrySetPowerScheme(Balanced, out var powerError))
            log.Add("已切回「平衡」电源计划。");
        else
            log.Add("电源计划恢复失败：" + powerError);

        try
        {
            var exe = p?.MainModule?.FileName ?? "";
            if (exe.Length > 0)
            {
                var (gpuOk, gpuMsg) = LiveStreamTweaks.RestoreGpuPreference(exe);
                log.Add(gpuOk ? "GPU 首选项已撤回。" : "GPU 首选项撤回失败：" + gpuMsg);
            }
        }
        catch { }

        var (capOk, capMsg) = LiveStreamTweaks.RestoreCaptureProfile();
        log.Add(capOk ? "捕获任务：" + capMsg : "捕获任务还原失败（" + capMsg + "）");

        // 内存动作是一次性的（清完就没了），没有可还原的设置——写出来，免得用户以为漏做了什么
        log.Add("内存：清理待机列表是一次性动作，没有需要还原的设置。");

        return (true, string.Join(Environment.NewLine, log));
    }

    /// <summary>除 0 号核以外的掩码；自检直接验证这个纯函数。</summary>
    public static long BuildAffinityMask(int cores)
    {
        if (cores <= 1) return 1;
        long mask = 0;
        for (var i = 1; i < cores && i < 64; i++) mask |= 1L << i;
        return mask;
    }

    private static bool TrySetPowerScheme(Guid scheme, out string error)
    {
        error = "";
        try
        {
            var result = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
            if (result == 0) return true;

            error = result == 5
                ? "拒绝访问，请以管理员身份运行。"
                : "错误码 " + result;
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static double ReadCpuPercent()
    {
        try
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user)) return -1;

            if (!_hasLastSample)
            {
                _lastIdle = idle; _lastKernel = kernel; _lastUser = user;
                _hasLastSample = true;
                return -1;
            }

            var idleDelta = idle - _lastIdle;
            var totalDelta = (kernel - _lastKernel) + (user - _lastUser);
            _lastIdle = idle; _lastKernel = kernel; _lastUser = user;

            if (totalDelta <= 0) return 0;
            var busy = 1.0 - (double)idleDelta / totalDelta;
            return Math.Round(Math.Clamp(busy * 100.0, 0, 100), 1);
        }
        catch
        {
            return -1;
        }
    }

    private static double ReadMemoryPercent()
    {
        try
        {
            var status = new MemoryStatusEx();
            if (!GlobalMemoryStatusEx(status)) return -1;
            return Math.Round((double)status.ullTotalPhys > 0
                ? (1.0 - (double)status.ullAvailPhys / status.ullTotalPhys) * 100.0
                : 0, 1);
        }
        catch
        {
            return -1;
        }
    }

    private static string ReadTemperature()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\wmi"),
                new ObjectQuery("SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"));

            foreach (ManagementObject mo in searcher.Get())
            {
                var raw = Convert.ToDouble(mo["CurrentTemperature"]);
                var celsius = raw / 10.0 - 273.15;
                if (celsius > -40 && celsius < 150)
                    return celsius.ToString("0.0") + " °C";
            }

            return "未检测到传感器";
        }
        catch
        {
            return "未检测到（需管理员）";
        }
    }

    // ---------- P/Invoke ----------

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx lpBuffer);

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
}
