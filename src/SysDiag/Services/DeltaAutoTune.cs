using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 游戏「全自动调优」。
///
/// 原来的「一键优化」只做三件事（GPU 高性能、关全屏优化、关鼠标加速），
/// 而实际让游戏跑得稳的那些开关散在「系统调优」和「系统优化」两个窗口里。
/// 这里把它们排成一条流水线：勾选 → 依次执行 → 每一步都有结果 → 一键回滚。
///
/// 关于回滚：三类改动各有各的备份，这里只是按顺序调用它们的还原：
/// - 游戏级（GPU 首选项 / 全屏优化 / 鼠标）→ <see cref="GameOptimizeService.Rollback"/>
/// - 系统调优（Game Bar / GPU 调度 / 服务 / CPU / 鼠标）→ <see cref="SystemTuningService.RestoreAll"/>
/// - 系统优化条目（小组件 / 更新 / 休眠…）→ <see cref="TweakService.RestoreAll"/>
///
/// 刻意没做的事：不把「优化系统服务」默认勾上。它会停掉打印后台与搜索索引，
/// 属于「为了游戏牺牲日常使用」的选择，必须由用户自己勾。
/// </summary>
public static class DeltaAutoTune
{
    public sealed class Step
    {
        public string Id { get; init; } = "";
        public string Title { get; init; } = "";
        public string Desc { get; init; } = "";

        /// <summary>代价 / 副作用，界面用橙色显示。</summary>
        public string Effect { get; init; } = "";

        public string Risk { get; init; } = "安全";

        /// <summary>默认是否勾选。</summary>
        public bool DefaultOn { get; init; } = true;

        /// <summary>需要先选定游戏 exe。</summary>
        public bool NeedsGame { get; init; }

        /// <summary>需要游戏正在运行（进程类操作）。</summary>
        public bool NeedsRunning { get; init; }

        /// <summary>影响整个系统，而不只影响这个游戏。</summary>
        public bool SystemWide { get; init; }

        public Func<string, IProgress<string>, Task<(bool Ok, string Message)>> Run { get; init; }
    }

    public sealed class StepResult
    {
        public string Title { get; init; } = "";
        public bool Ok { get; init; }
        public string Message { get; init; } = "";
        public bool Skipped { get; init; }
    }

    public static List<Step> Plan() => new()
    {
        new Step
        {
            Id = "game-gpu",
            Title = "游戏设为高性能 GPU",
            Desc = "写显卡首选项 GpuPreference=2，等于「设置 → 显示 → 图形」里手动设成高性能。",
            NeedsGame = true,
            Run = (exe, _) => Task.FromResult(GameTweakService.SetGpuPreference(exe, true))
        },
        new Step
        {
            Id = "game-fullscreen",
            Title = "禁用全屏优化",
            Desc = "只在游戏 exe 的兼容性标记上加 DISABLEDXMAXIMIZEDWINDOWEDMODE，不动 RUNASADMIN / 高 DPI 等其它标记。",
            NeedsGame = true,
            Run = (exe, _) => Task.FromResult(GameTweakService.SetFullscreenOptimization(exe, true))
        },
        new Step
        {
            Id = "game-mouse",
            Title = "关闭鼠标加速（全局）",
            Desc = "移除「提高指针精确度」带来的轨迹修正，让移动贴合鼠标原始输入。",
            Effect = "影响整个系统：桌面手感会变「快而直」，FPS 游戏通常反而更准。",
            Risk = "谨慎",
            SystemWide = true,
            Run = (_, _) => Task.FromResult(MouseTweakService.DisableAcceleration())
        },
        new Step
        {
            Id = "game-priority",
            Title = "游戏进程设为「高」优先级 + 全核亲和性",
            Desc = "把正在运行的游戏进程提到「高」，并把 CPU 亲和性放开到全部核心。",
            Effect = "只在本次运行有效，退出游戏后自动失效；反作弊若拦截会明确报错。",
            Risk = "谨慎",
            DefaultOn = false,
            NeedsRunning = true,
            Run = (_, _) => Task.FromResult(ApplyGameProcessTuning())
        },
        new Step
        {
            Id = "sys-gamebar",
            Title = "关闭 Game Bar",
            Desc = "关掉游戏栏与后台录制组件，减少覆盖层与常驻开销。",
            Effect = "Win+G 失效，Xbox 录屏/后台录制不可用；要录屏先别关。",
            SystemWide = true,
            Run = (_, _) => Task.FromResult(SystemTuningService.SetGameBar(true))
        },
        new Step
        {
            Id = "sys-hwsched",
            Title = "开启 GPU 硬件加速调度",
            Desc = "HwSchMode=2，把显存管理与调度交给显卡驱动。",
            Effect = "中低端配置可能增加帧时间波动；需要重启系统生效。",
            Risk = "谨慎",
            SystemWide = true,
            Run = (_, _) => Task.FromResult(SystemTuningService.SetHwScheduling(true))
        },
        new Step
        {
            Id = "sys-cpu",
            Title = "CPU 调度档位 → 激进一",
            Desc = "短时间片 + 可变 + 前台 2 倍加成（Windows「程序」预设），前台游戏更容易抢到 CPU 时间。",
            Effect = "后台（录制、下载、语音）更容易卡；需要重启系统生效。",
            Risk = "谨慎",
            SystemWide = true,
            Run = (_, _) => Task.FromResult(SystemTuningService.SetCpuProfile(38))
        },
        new Step
        {
            Id = "sys-services",
            Title = "优化系统服务（关搜索/遥测/下载/打印/SysMain）",
            Desc = "停用 WSearch、DiagTrack、DoSvc、Spooler、SysMain 并设为禁用。",
            Effect = "会关掉打印后台与开始菜单搜索索引，属于为游戏牺牲日常使用；事件日志保留不动。",
            Risk = "谨慎",
            DefaultOn = false,
            SystemWide = true,
            Run = (_, _) => Task.FromResult(SystemTuningService.SetServices(true))
        },
        new Step
        {
            Id = "tweak-clutter",
            Title = "关掉后台杂项（小组件 / 搜索热点 / CEIP / 驱动自动更新）",
            Desc = "四条策略键一次写完，减少后台推送与内容拉取。",
            SystemWide = true,
            Run = (_, _) => Task.FromResult(ApplyTweaks("tweak-clutter",
                "common-widgets", "common-search-hotspot", "common-ceip", "common-wu-drivers"))
        },
        new Step
        {
            Id = "tweak-delivery",
            Title = "关闭更新传递优化（P2P 上传）",
            Desc = "不再把更新分片上传给别的电脑，也不再从别人那儿下载。",
            Effect = "更新下载可能慢一点；需要管理员。",
            Risk = "谨慎",
            SystemWide = true,
            Run = (_, _) => Task.FromResult(ApplyTweaks("tweak-delivery", "pv-delivery"))
        },
        new Step
        {
            Id = "tweak-update",
            Title = "暂停 Windows 更新（长期）",
            Desc = "把暂停截止时间写到 2042 年，避免打游戏当天被更新打断。",
            Effect = "安全补丁也一起停；用完记得在「系统优化」里还原。",
            Risk = "谨慎",
            DefaultOn = false,
            SystemWide = true,
            Run = (_, _) => Task.FromResult(ApplyTweaks("tweak-update", "pv-pause-update"))
        },
        new Step
        {
            Id = "tweak-hibernate",
            Title = "关闭休眠（删 hiberfil.sys，腾 C 盘）",
            Desc = "通常能腾出与内存等大的空间。",
            Effect = "休眠与依赖它的快速启动一起失效。",
            Risk = "谨慎",
            DefaultOn = false,
            SystemWide = true,
            Run = (_, _) => Task.FromResult(ApplyTweaks("tweak-hibernate", "common-hibernate"))
        }
    };

    /// <summary>按勾选顺序执行。备份失败就整体中止，不带着「没法回滚」的风险继续。</summary>
    public static async Task<List<StepResult>> RunAsync(
        string exePath, IEnumerable<string> selectedIds, IProgress<string> log,
        CancellationToken token = default)
    {
        var results = new List<StepResult>();
        var selected = new HashSet<string>(selectedIds, StringComparer.OrdinalIgnoreCase);
        var steps = Plan().Where(s => selected.Contains(s.Id)).ToList();

        if (steps.Count == 0)
        {
            return results;
        }

        // 先备份游戏级设置。系统级的三套服务各自会在写入前备份自己的原值，
        // 但没有「统一备份点」，所以这里补一份游戏侧的，回滚时不至于漏。
        if (!string.IsNullOrWhiteSpace(exePath))
        {
            var (backupOk, backupMsg) = GameOptimizeService.CreateBackup(exePath);
            log?.Report((backupOk ? "① 备份：" : "① 备份失败，已中止：") + backupMsg);

            if (!backupOk)
            {
                results.Add(new StepResult
                {
                    Title = "备份当前设置",
                    Ok = false,
                    Message = backupMsg
                });

                return results;
            }
        }
        else
        {
            log?.Report("① 未选定游戏 exe，跳过游戏侧备份（系统级条目仍会各自备份原值）");
        }

        var index = 1;

        foreach (var step in steps)
        {
            token.ThrowIfCancellationRequested();

            index++;
            log?.Report($"{index:00} {step.Title} …");

            if (step.NeedsGame && string.IsNullOrWhiteSpace(exePath))
            {
                results.Add(new StepResult { Title = step.Title, Ok = false, Skipped = true, Message = "没有选定游戏 exe" });
                log?.Report($"   跳过：没有选定游戏 exe");
                continue;
            }

            if (step.NeedsRunning && !GameProcessRunning(exePath))
            {
                results.Add(new StepResult { Title = step.Title, Ok = false, Skipped = true, Message = "游戏没在运行" });
                log?.Report("   跳过：游戏没在运行");
                continue;
            }

            try
            {
                var (ok, message) = await step.Run(exePath ?? "", log);
                results.Add(new StepResult { Title = step.Title, Ok = ok, Message = message });
                log?.Report($"   {(ok ? "✓" : "✗")} {message}");
            }
            catch (Exception ex)
            {
                results.Add(new StepResult { Title = step.Title, Ok = false, Message = ex.Message });
                log?.Report("   ✗ " + ex.Message);
            }

            await Task.Delay(120, token);
        }

        return results;
    }

    /// <summary>把三套备份一起还原。</summary>
    public static (bool Ok, string Message) Rollback()
    {
        var parts = new List<string>();
        var anyOk = false;
        var anyFailed = false;

        void Take((bool Ok, string Message) result, string label)
        {
            parts.Add($"{label}：{result.Message}");
            if (result.Ok) anyOk = true;
            else anyFailed = true;
        }

        try
        {
            if (GameOptimizeService.HasBackup)
                Take(GameOptimizeService.Rollback(), "游戏设置");
            else
                parts.Add("游戏设置：没有备份记录");
        }
        catch (Exception ex)
        {
            parts.Add("游戏设置：还原失败（" + ex.Message + "）");
            anyFailed = true;
        }

        try
        {
            var (ok, message) = SystemTuningService.RestoreAll();
            parts.Add("系统调优：" + message);
            if (ok) anyOk = true; else anyFailed = true;
        }
        catch (Exception ex)
        {
            parts.Add("系统调优：还原失败（" + ex.Message + "）");
            anyFailed = true;
        }

        try
        {
            if (TweakService.BackupCount > 0)
            {
                var (ok, message) = TweakService.RestoreAll();
                parts.Add("系统优化：" + message);
                if (ok) anyOk = true; else anyFailed = true;
            }
            else
            {
                parts.Add("系统优化：没有备份记录");
            }
        }
        catch (Exception ex)
        {
            parts.Add("系统优化：还原失败（" + ex.Message + "）");
            anyFailed = true;
        }

        return (anyOk && !anyFailed, string.Join("\n", parts));
    }

    // ---------------------------------------------------------------- 内部

    private static (bool Ok, string Message) ApplyTweaks(string label, params string[] ids)
    {
        var applied = new List<string>();
        var failed = new List<string>();

        foreach (var id in ids)
        {
            var item = TweakCatalog.ById(id);
            if (item == null)
            {
                failed.Add(id + "（条目不存在）");
                continue;
            }

            var (ok, message) = TweakService.Apply(item, true);
            if (ok) applied.Add(item.Title);
            else failed.Add(item.Title + "：" + message);
        }

        if (failed.Count == 0)
        {
            return (true, $"已应用 {applied.Count} 项：{string.Join("、", applied)}");
        }

        return (applied.Count > 0,
            $"成功 {applied.Count} 项、失败 {failed.Count} 项 —— {string.Join("；", failed)}");
    }

    /// <summary>游戏进程在跑才改优先级——进程退出后设置就没了，跑之前改没有意义。</summary>
    private static (bool Ok, string Message) ApplyGameProcessTuning()
    {
        var processes = ProcessInventoryService.Scan();
        var target = FindGameProcess(processes);

        if (target == null)
            return (false, "没有找到正在运行的游戏进程（反作弊可能隐藏了进程信息）。");

        var notes = new List<string>();

        var priority = ProcessInventoryService.SetPriority(target, "High");
        notes.Add(priority.Ok ? "优先级已设为「高」" : "优先级：" + priority.Message);

        // 放开到全部逻辑核心：有些工具会把它锁在单核上，那才是真的掉帧
        var cores = Environment.ProcessorCount;
        var mask = cores >= 64 ? -1L : (1L << cores) - 1;
        var affinity = ProcessInventoryService.SetAffinity(target, mask);
        notes.Add(affinity.Ok
            ? $"亲和性已放开到 {cores} 个逻辑处理器"
            : "亲和性：" + affinity.Message);

        return (priority.Ok || affinity.Ok, $"{target.Name}.exe（PID {target.Pid}）—— " + string.Join("；", notes));
    }

    private static bool GameProcessRunning(string exePath)
        => FindGameProcess(ProcessInventoryService.Scan()) != null;

    private static ProcessInventoryService.ProcessEntry FindGameProcess(
        IEnumerable<ProcessInventoryService.ProcessEntry> processes)
    {
        var names = new[] { "deltaforce", "deltaforceclient", "deltaforceclient-win64-shipping", "df" };

        return processes.FirstOrDefault(p =>
            names.Any(n => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase)));
    }
}
