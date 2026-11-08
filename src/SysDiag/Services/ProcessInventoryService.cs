using System.Diagnostics;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 进程清单（任务管理器 / 资源监视器的软件内版本）。
///
/// CPU 占用需要两次采样求差：进程的 TotalProcessorTime 是累计值，
/// 单次读出来的只是"开机以来用了多少"，不是"现在占多少"。
/// </summary>
public static class ProcessInventoryService
{
    public sealed class ProcessEntry
    {
        public int Pid { get; set; }
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Company { get; set; } = "";
        public string Path { get; set; } = "";
        public double CpuPercent { get; set; }
        public long WorkingSetMb { get; set; }
        public int Handles { get; set; }
        public int Threads { get; set; }
        public bool Responding { get; set; } = true;
        public DateTime? StartTime { get; set; }
        public bool Critical { get; set; }

        /// <summary>优先级类：Idle / BelowNormal / Normal / AboveNormal / High / RealTime；读不到为空。</summary>
        public string Priority { get; set; } = "";

        /// <summary>CPU 亲和性掩码。0 表示读不到。</summary>
        public long AffinityMask { get; set; }

        /// <summary>「正常」「高」「CPU 0-3」这种给人看的摘要。</summary>
        public string PriorityText => Priority.Length == 0 ? "读不到" : PriorityLabel(Priority);

        public string AffinityText => AffinityMask == 0
            ? "读不到"
            : MaskText(AffinityMask);

        public string Level => Critical ? "warn" : !Responding ? "warn" : CpuPercent >= 25 ? "hint" : "ok";

        public string LevelText => Critical ? "系统进程" : !Responding ? "无响应" : CpuPercent >= 25 ? "占用高" : "";
    }

    /// <summary>绝不允许在这里结束的进程（杀了会立刻蓝屏或掉登录）。</summary>
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "Memory Compression", "smss", "csrss", "wininit",
        "winlogon", "services", "lsass", "lsm", "svchost", "fontdrvhost", "dwm", "audiodg"
    };

    private static Dictionary<int, (DateTime Stamp, TimeSpan Cpu)> _last = new();

    /// <summary>采样一次。第二次之后 CPU% 才有意义（第一次全是 0）。</summary>
    public static List<ProcessEntry> Scan()
    {
        var now = DateTime.UtcNow;
        var snapshot = new Dictionary<int, (DateTime, TimeSpan)>();
        var cores = Math.Max(1, Environment.ProcessorCount);
        var list = new List<ProcessEntry>();

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var entry = new ProcessEntry
                {
                    Pid = p.Id,
                    Name = p.ProcessName,
                    WorkingSetMb = p.WorkingSet64 / 1024 / 1024,
                    Threads = p.Threads.Count,
                    Handles = p.HandleCount,
                    Critical = Protected.Contains(p.ProcessName)
                };

                try { entry.StartTime = p.StartTime; } catch { }

                try
                {
                    entry.Path = p.MainModule?.FileName ?? "";
                    entry.Description = p.MainModule?.FileVersionInfo.FileDescription ?? "";
                    entry.Company = p.MainModule?.FileVersionInfo.CompanyName ?? "";
                }
                catch
                {
                    // 受保护进程（System / 反作弊保护的游戏）读不到模块信息，属正常
                }

                try { entry.Responding = p.Responding; } catch { }

                // 优先级与亲和性是「能不能改」的前提：受保护进程读不到就不显示可改
                try { entry.Priority = p.PriorityClass.ToString(); } catch { }
                try { entry.AffinityMask = p.ProcessorAffinity.ToInt64(); } catch { }

                var cpu = p.TotalProcessorTime;
                snapshot[p.Id] = (now, cpu);

                if (_last.TryGetValue(p.Id, out var before))
                {
                    var elapsed = (now - before.Stamp).TotalMilliseconds;
                    if (elapsed > 50)
                    {
                        var used = (cpu - before.Cpu).TotalMilliseconds;
                        entry.CpuPercent = Math.Round(Math.Clamp(used / (elapsed * cores) * 100, 0, 100), 1);
                    }
                }

                list.Add(entry);
            }
            catch
            {
                // 进程在枚举过程中退出
            }
            finally
            {
                p.Dispose();
            }
        }

        _last = snapshot;

        return list
            .OrderByDescending(x => x.CpuPercent)
            .ThenByDescending(x => x.WorkingSetMb)
            .ToList();
    }

    /// <summary>优先级类 → 中文。顺序与任务管理器一致。</summary>
    public static string PriorityLabel(string priority) => priority switch
    {
        "Idle" => "低",
        "BelowNormal" => "低于正常",
        "Normal" => "正常",
        "AboveNormal" => "高于正常",
        "High" => "高",
        "RealTime" => "实时",
        _ => priority
    };

    /// <summary>优先级下拉框的选项（值 = ProcessPriorityClass 的名称）。</summary>
    public static readonly (string Value, string Label, string Note)[] PriorityOptions =
    {
        ("Idle", "低", "只在系统空闲时运行"),
        ("BelowNormal", "低于正常", "让出 CPU 给前台程序"),
        ("Normal", "正常", "默认值"),
        ("AboveNormal", "高于正常", "优先获得 CPU"),
        ("High", "高", "会明显抢占前台程序，游戏加速常用"),
        ("RealTime", "实时", "最高，可能让输入和界面卡死，慎用")
    };

    /// <summary>把掩码翻译成「CPU 0、CPU 2-3」。</summary>
    public static string MaskText(long mask)
    {
        if (mask <= 0) return "无";

        var parts = new List<string>();
        var start = -1;

        for (var i = 0; i < 64; i++)
        {
            var on = (mask & (1L << i)) != 0;

            if (on && start < 0) start = i;
            if (on || start < 0) continue;

            parts.Add(start == i - 1 ? $"CPU {start}" : $"CPU {start}-{i - 1}");
            start = -1;
        }

        if (start >= 0) parts.Add(start == 63 ? $"CPU {start}" : $"CPU {start}-63");

        return string.Join("、", parts);
    }

    /// <summary>改优先级。实时优先级需要提升调度权限，普通权限通常会被拒绝。</summary>
    public static (bool Ok, string Message) SetPriority(ProcessEntry entry, string priority)
    {
        if (Protected.Contains(entry.Name))
            return (false, $"{entry.Name} 是系统关键进程，不允许改优先级。");

        try
        {
            using var p = Process.GetProcessById(entry.Pid);

            if (!Enum.TryParse<ProcessPriorityClass>(priority, true, out var value))
                return (false, "未知的优先级：" + priority);

            p.PriorityClass = value;

            return (true, $"{entry.Name} 的优先级已改为「{PriorityLabel(priority)}」。");
        }
        catch (ArgumentException)
        {
            return (false, "进程已经退出了。");
        }
        catch (Exception ex)
        {
            return (false, $"{entry.Name} 的优先级改不了：{ex.Message}\n\n" +
                           "反作弊保护的游戏进程、系统关键进程（PPL）会拒绝这类修改，" +
                           (ElevationService.IsAdmin ? "即使是管理员也一样。" : "以管理员身份重启可以改更多进程。"));
        }
    }

    /// <summary>
    /// 改 CPU 亲和性。
    /// 注意：只对当前运行实例有效，进程退出后重置；且只覆盖当前处理器组（64 核以上会拆组）。
    /// </summary>
    public static (bool Ok, string Message) SetAffinity(ProcessEntry entry, long mask)
    {
        if (Protected.Contains(entry.Name))
            return (false, $"{entry.Name} 是系统关键进程，不允许改 CPU 亲和性。");

        var cores = Environment.ProcessorCount;

        if (mask <= 0)
            return (false, "至少要勾选一个 CPU。");

        if ((mask & ((1L << cores) - 1)) == 0)
            return (false, $"这台机器只有 {cores} 个逻辑处理器，勾选的 CPU 都不在其中。");

        try
        {
            using var p = Process.GetProcessById(entry.Pid);
            p.ProcessorAffinity = (IntPtr)mask;

            return (true, $"{entry.Name} 已绑定到 {MaskText(mask)}。");
        }
        catch (ArgumentException)
        {
            return (false, "进程已经退出了。");
        }
        catch (Exception ex)
        {
            return (false, $"绑定 CPU 失败：{ex.Message}\n\n" +
                           "受保护进程与反作弊进程会拒绝；另外这个设置只对当前实例有效。");
        }
    }

    /// <summary>结束进程。系统关键进程直接拒绝，不做"尽力而为"。</summary>
    public static (bool Ok, string Message) Kill(ProcessEntry entry)
    {
        if (Protected.Contains(entry.Name))
            return (false, $"{entry.Name} 是系统关键进程，结束它会导致系统立即崩溃或掉登录，已拒绝。");

        try
        {
            using var p = Process.GetProcessById(entry.Pid);
            p.Kill();

            return (true, $"已结束 {entry.Name}（PID {entry.Pid}）。");
        }
        catch (ArgumentException)
        {
            return (false, "进程已经退出了。");
        }
        catch (Exception ex)
        {
            return (false, $"结束失败：{ex.Message}" +
                           (ElevationService.IsAdmin ? "" : "\n\n以管理员身份重启后可以结束更多受保护进程。"));
        }
    }

    /// <summary>内存 / 句柄 / 线程的总量，给 KPI 用。</summary>
    public static (int Count, long MemoryMb, double CpuSum) Summary(List<ProcessEntry> list)
        => (list.Count, list.Sum(x => x.WorkingSetMb), Math.Round(list.Sum(x => x.CpuPercent), 1));
}
