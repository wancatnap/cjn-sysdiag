using System.Diagnostics;
using System.IO;
using System.Runtime;
using System.Runtime.InteropServices;

namespace SysDiag.Services;

/// <summary>
/// 内存占用控制：空闲时回收工作集、提供实测快照。
/// 目标是「挂着不影响打游戏」——空闲工作集 150 MB 以内（见 README 实测表），
/// 启动完成后与子窗口关掉后各回收一次冷页，不做常驻轮询。
/// </summary>
public static class MemoryTuner
{
    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    private static DateTime _lastTrim = DateTime.MinValue;

    /// <summary>两次回收的最小间隔，避免频繁把页面换出又换入（反而更卡）。</summary>
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(90);

    /// <summary>自检读取：累计回收次数。</summary>
    public static int TrimCount { get; private set; }

    /// <summary>自检读取：上次回收时间。</summary>
    public static DateTime? LastTrim => TrimCount > 0 ? _lastTrim : null;

    public static long WorkingSetMB
    {
        get
        {
            try
            {
                using var p = Process.GetCurrentProcess();
                return p.WorkingSet64 / (1024 * 1024);
            }
            catch { return 0; }
        }
    }

    public static long PrivateMB
    {
        get
        {
            try
            {
                using var p = Process.GetCurrentProcess();
                return p.PrivateMemorySize64 / (1024 * 1024);
            }
            catch { return 0; }
        }
    }

    public static int HandleCount
    {
        get
        {
            try
            {
                using var p = Process.GetCurrentProcess();
                return p.HandleCount;
            }
            catch { return 0; }
        }
    }

    public static string Snapshot() => $"工作集 {WorkingSetMB} MB · 私有 {PrivateMB} MB · 句柄 {HandleCount}";

    /// <summary>
    /// 回收内存：先把托管堆压缩（含大对象堆），再把冷页换出物理内存。
    /// 借鉴 revanapriyandi/jemur 的 MEMORY.md：WPF 应用「compact GC + SetProcessWorkingSetSize」后，
    /// 私有字节能降一大截，因为 GC 段的空闲部分会真正还给系统。
    /// 间隔太短会更卡，所以有 90 秒节流。
    /// </summary>
    public static bool TrimIfIdle()
    {
        if (DateTime.UtcNow - _lastTrim < MinInterval) return false;

        try
        {
            // 1) 压缩托管堆：LOH 平时不压缩，这里强制压一次，把空闲段归还
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

            // 2) 换出冷页：把不再访问的页还给 Windows（下次访问从 standby 列表取回，不读盘）
            using var p = Process.GetCurrentProcess();
            var ok = EmptyWorkingSet(p.Handle);

            if (ok)
            {
                _lastTrim = DateTime.UtcNow;
                TrimCount++;
                LastHeapMb = GC.GetTotalMemory(false) / (1024 * 1024);
            }

            return ok;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>自检读取：最近一次回收后的托管堆大小（MB）。</summary>
    public static long LastHeapMb { get; private set; }

    /// <summary>
    /// 写一份内存明细到 outputs/mem-report.txt（--mem-report 自证用）。
    /// 记录真实启动路径下的托管堆、程序集清单、模块数——用来判断哪部分还能压。
    /// </summary>
    public static void WriteMemReport()
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            var asms = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetName().Name ?? "?")
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            using var proc = Process.GetCurrentProcess();

            sb.AppendLine($"时间 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"版本 {AppInfo.Version}");
            sb.AppendLine($"工作集 {WorkingSetMB} MB · 私有 {PrivateMB} MB · 句柄 {HandleCount}");
            sb.AppendLine($"托管堆 {GC.GetTotalMemory(false) / 1024 / 1024} MB · GC 已提交 " +
                          $"{GC.GetGCMemoryInfo().TotalCommittedBytes / 1024 / 1024} MB");
            sb.AppendLine($"程序集 {asms.Count} · 模块 {proc.Modules.Count} · 线程 {proc.Threads.Count}");
            sb.AppendLine();
            sb.AppendLine("程序集清单：");
            foreach (var a in asms) sb.AppendLine("  " + a);

            AppPaths.Ensure();
            File.WriteAllText(Path.Combine(AppPaths.OutputDir, "mem-report.txt"), sb.ToString(),
                new System.Text.UTF8Encoding(false));
        }
        catch { /* 自证失败不影响主流程 */ }
    }
}
