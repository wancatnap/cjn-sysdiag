using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace SysDiag.Services;

/// <summary>
/// 码枪堂那几项里跟游戏性能直接相关的四类：Hyper-V、卓越性能电源、游戏进程优先级、N 卡最高 Boost。
/// （HPET 两种模式在 <see cref="LatencyService"/> 里，和平台时钟放在一起。）
///
/// 三条原则，都是从前面几轮踩坑总结的：
///
/// 1. **能直接改的直接改，改完回读验证**——不靠「返回值成功」下结论。
/// 2. **只能靠驱动 DRS 的不瞎写**：N 卡的「电源管理模式」存在
///    `ProgramData\NVIDIA Corporation\Drs\nvdrsdb0.bin` 二进制里，直接改等于拿用户显卡赌。
///    所以生成 NVIDIA Profile Inspector 的 .nip，导入写的是同一份数据，随时能在控制面板还原。
///    （和 DLSS 那边同一套做法。）
/// 3. **改动有代价就明写**：关 Hyper-V 会废掉 WSL2 / 沙盒 / 虚拟机；卓越性能会顶高温度。
/// </summary>
public static class GameBoostService
{
    public static List<LatencyService.Check> Checks()
    {
        var list = new List<LatencyService.Check>();

        list.Add(HyperVCheck());
        list.Add(UltimatePowerCheck());
        list.Add(GamePriorityCheck());
        list.Add(NvidiaBoostCheck());

        return list;
    }

    // ---------------------------------------------------------------- Hyper-V

    /// <summary>
    /// 关掉 Hyper-V 的收益有争议，但对「老平台 + 竞技游戏」确实有人测出帧时间更稳：
    /// 根 hypervisor 会接管中断与计时，多一层虚拟化开销。代价很硬——WSL2、Windows 沙盒、
    /// 虚拟机、安卓子系统、内存完整性全都不能用。
    /// </summary>
    private static LatencyService.Check HyperVCheck()
    {
        var (bcdOk, output) = LatencyService.RunBcd();
        var mode = bcdOk ? LatencyService.ReadBcdValue(output, "hypervisorlaunchtype") : "";

        var running = false;

        try
        {
            var row = EvidenceService.Wmi("SELECT HypervisorPresent FROM Win32_ComputerSystem").FirstOrDefault();

            if (row != null && row.TryGetValue("HypervisorPresent", out var value) && value is bool present)
                running = present;
        }
        catch { /* 读不到就按 bcd 判断 */ }

        var vbs = ReadDword(@"SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity");
        var memoryIntegrity = ReadDword(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled");

        var isOff = mode.Equals("Off", StringComparison.OrdinalIgnoreCase);

        var detail = !bcdOk
            ? "读不到启动配置（bcdedit 需要管理员权限）。这一项要改内核启动参数，没读到就不猜。"
            : $"hypervisorlaunchtype = {(mode.Length == 0 ? "未设置（默认 Auto）" : mode)}" +
              $"　·　当前 hypervisor 运行中：{(running ? "是" : "否")}" +
              (vbs == 1 ? "\nVBS（基于虚拟化的安全）：已开启" : "") +
              (memoryIntegrity == 1 ? "\n内存完整性（HVCI）：已开启——它依赖 hypervisor，关掉 Hyper-V 会让它失效" : "");

        return new LatencyService.Check
        {
            Group = "虚拟化与安全",
            Title = "Hyper-V / 虚拟化平台（hypervisorlaunchtype）",
            Detail = detail,
            State = !bcdOk ? "unknown" : isOff ? "ok" : running ? "off" : "unknown",
            NeedsAdmin = true,
            NeedsRestart = true,
            Effect = "关掉之后这些全部不能再用：WSL2 / Docker Desktop / Windows 沙盒 / Hyper-V 虚拟机 / " +
                     "安卓子系统 / 内存完整性（内核隔离）。\n" +
                     "如果开着「内存完整性」，系统可能拒绝关闭，需要先在「Windows 安全中心 → 设备安全性」里关掉它。\n" +
                     "对游戏的影响因平台而异，不是必开也不是必关——建议改完自己对比帧时间，不满意再改回来。",
            // 非管理员时也给出按钮：点下去提示提权重启
            Options = new[]
            {
                new LatencyService.Choice
                {
                    Label = "关闭 Hyper-V",
                    Note = "bcdedit /set {current} hypervisorlaunchtype off，重启后生效",
                    Run = () => LatencyService.SetBcd("hypervisorlaunchtype", "off")
                },
                new LatencyService.Choice
                {
                    Label = "恢复开启",
                    Note = "bcdedit /set {current} hypervisorlaunchtype auto，重启后生效",
                    Run = () => LatencyService.SetBcd("hypervisorlaunchtype", "auto")
                }
            }
        };
    }

    // ---------------------------------------------------------------- 卓越性能电源

    private static LatencyService.Check UltimatePowerCheck()
    {
        var plans = PowerPlanService.List();
        var ultra = PowerPlanService.Ultimate();
        var active = plans.FirstOrDefault(p => p.IsActive);

        var activeText = active == null ? "读不到" : active.Name;

        var detail = !ultra.Exists
            ? $"本机没有「卓越性能」方案（微软默认把它藏起来了），当前用的是「{activeText}」。"
            : ultra.Active
                ? $"当前正在使用：{ultra.Name}"
                : $"已存在「{ultra.Name}」，当前用的是「{activeText}」。";

        var options = new List<LatencyService.Choice>();

        if (!ultra.Exists || !ultra.Active)
        {
            options.Add(new LatencyService.Choice
            {
                Label = ultra.Exists ? "切换到卓越性能" : "创建并切换到卓越性能",
                Note = "复制微软的卓越性能模板并设为当前方案",
                Run = () =>
                {
                    var (ok, message, _) = PowerPlanService.EnsureUltimatePerformance();
                    return (ok, message);
                }
            });
        }

        if (ultra.Active)
        {
            options.Add(new LatencyService.Choice
            {
                Label = "切回上一个方案",
                Note = "回到点「卓越性能」之前用的那个电源方案",
                Run = PowerPlanService.RestorePreviousPlan
            });
        }

        return new LatencyService.Check
        {
            Group = "电源与显卡",
            Title = "卓越性能电源方案",
            Detail = detail,
            State = ultra.Active ? "ok" : "off",
            NeedsAdmin = false,   // 实测复制/切换电源方案普通权限就够
            Effect = "卓越性能不做 CPU 主动降频、不关核心休眠，代价是待机功耗和温度明显上升。\n" +
                     "台式机 / 散热好可以用；笔记本长期挂这个方案会烫、风扇吵、电池掉得快。\n" +
                     "注：这台机器上非激活的电源方案会被系统从列表里藏起来，程序靠记住 GUID 认它。",
            Options = options
        };
    }

    // ---------------------------------------------------------------- 游戏优先级

    /// <summary>
    /// 把正在跑的游戏提到「高」优先级。
    /// 只提游戏，不动系统进程——把 csrss / dwm 之类乱提优先级只会让输入更堵。
    /// </summary>
    private static LatencyService.Check GamePriorityCheck()
    {
        var games = SafeGames();

        if (games.Count == 0)
        {
            return new LatencyService.Check
            {
                Group = "电源与显卡",
                Title = "游戏进程优先级",
                Detail = "现在没有检测到游戏在运行。启动游戏后点「重新扫描」，这里会列出进程和当前优先级。",
                State = "unknown",
                Effect = "游戏跑起来之后才能改。改的是进程的 CPU 调度优先级，退出游戏后自动恢复。"
            };
        }

        var rows = games.Select(g =>
        {
            var priority = PriorityOf(g.Pid);
            return $"{g.Name}（{g.Pid}）：{(priority.Length == 0 ? "读不到" : PriorityLabel(priority))}";
        }).ToList();

        var low = games.Where(g =>
        {
            var p = PriorityOf(g.Pid);
            return p.Length > 0 && p != "High" && p != "RealTime";
        }).ToList();

        return new LatencyService.Check
        {
            Group = "电源与显卡",
            Title = "游戏进程优先级",
            Detail = string.Join("\n", rows),
            State = low.Count == 0 ? "ok" : "off",
            ActionLabel = low.Count > 0 ? $"把 {low.Count} 个游戏设为高优先级" : "",
            Apply = low.Count > 0 ? () => Raise(low) : null,
            Effect = "「高」能让游戏更少被后台程序抢 CPU 时间片；不要用「实时」——" +
                     "那会把输入/音频线程饿死，反而卡顿。\n" +
                     "被反作弊保护的进程可能拒绝修改，会如实报出来。"
        };
    }

    private static List<GameProcess> SafeGames()
    {
        try { return GameProcessCatalog.DetectRunningGames(); }
        catch { return new List<GameProcess>(); }
    }

    private static string PriorityOf(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.PriorityClass.ToString();
        }
        catch
        {
            return "";
        }
    }

    private static string PriorityLabel(string priority) => priority switch
    {
        "Idle" => "低",
        "BelowNormal" => "低于正常",
        "Normal" => "正常",
        "AboveNormal" => "高于正常",
        "High" => "高",
        "RealTime" => "实时",
        _ => priority
    };

    private static (bool Ok, string Message) Raise(List<GameProcess> games)
    {
        var done = new List<string>();
        var failed = new List<string>();

        foreach (var game in games)
        {
            try
            {
                using var p = Process.GetProcessById(game.Pid);

                if (p.PriorityClass is ProcessPriorityClass.High or ProcessPriorityClass.RealTime)
                    continue;

                p.PriorityClass = ProcessPriorityClass.High;
                done.Add(game.Name);
            }
            catch (Exception ex)
            {
                failed.Add($"{game.Name}：{ex.Message}");
            }
        }

        var message = done.Count > 0
            ? $"已把 {done.Count} 个游戏进程设为「高」优先级：{string.Join("、", done)}。\n退出游戏后自动失效，不写注册表。"
            : "没有进程被改动。";

        if (failed.Count > 0) message += "\n\n没改成：" + string.Join("；", failed);

        return (failed.Count == 0 || done.Count > 0, message);
    }

    // ---------------------------------------------------------------- N 卡 Boost

    // NVIDIA DRS：电源管理模式（Profile Inspector 里的 Power management mode）
    // 0 = 自适应（默认）、1 = 最高性能优先、2 = 最佳功率
    public const uint PowerManagementModeId = 0x1057EB71;

    private static LatencyService.Check NvidiaBoostCheck()
    {
        var (gpu, driver) = NvidiaGpu();

        if (gpu.Length == 0)
        {
            return new LatencyService.Check
            {
                Group = "电源与显卡",
                Title = "N 卡维持最高 Boost",
                Detail = "没有检测到 NVIDIA 显卡，这一项不适用。",
                State = "unknown",
                Effect = "只有 NVIDIA 显卡才有「电源管理模式」这个设置。"
            };
        }

        var dir = NipDir;
        var latest = Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.nip").OrderByDescending(File.GetLastWriteTime).FirstOrDefault()
            : null;

        var options = new List<LatencyService.Choice>
        {
            new()
            {
                Label = "生成导入文件（.nip）",
                Note = "用 NVIDIA Profile Inspector 导入；写的是同一份 DRS 数据，可随时在控制面板还原",
                Run = () =>
                {
                    var (ok, message, path) = GenerateNvidiaBoost();

                    if (ok && path.Length > 0)
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                                { UseShellExecute = true });
                        }
                        catch { /* 打不开资源管理器不影响生成成功 */ }
                    }

                    return (ok, message);
                }
            },
            new()
            {
                Label = "打开 NVIDIA 控制面板",
                Note = "3D 设置 → 管理 3D 设置 → 电源管理模式 → 优先最高性能",
                Run = OpenNvidiaPanel
            }
        };

        return new LatencyService.Check
        {
            Group = "电源与显卡",
            Title = "N 卡维持最高 Boost",
            Detail = $"{gpu}　·　驱动 {driver}\n" +
                     "「电源管理模式 = 优先最高性能」会让核心不主动降频，Boost 频率更稳定。" +
                     (latest != null ? $"\n上次生成的导入文件：{Path.GetFileName(latest)}" : ""),
            State = "unknown",
            Effect = "代价很直接：空闲也维持高频，待机温度 +5~15℃、风扇更响、功耗更高。\n" +
                     "笔记本不建议长期开。\n\n" +
                     "为什么不直接写：这个设置在 DRS 的二进制库（nvdrsdb0.bin）里，" +
                     "直接改等于拿显卡驱动赌，所以只生成标准 .nip，导入后效果一样、还能随时还原。",
            Options = options
        };
    }

    /// <summary>生成 Boost 的 .nip（不开界面也能用：<c>--nvidia-boost</c>）。</summary>
    public static (bool Ok, string Message, string Path) GenerateNvidiaBoost()
    {
        var (gpu, driver) = NvidiaGpu();

        if (gpu.Length == 0) return (false, "没有检测到 NVIDIA 显卡，这一项不适用。", "");

        return BuildBoostNip(gpu, driver);
    }

    /// <summary>生成只含「电源管理模式 = 最高性能优先」的 .nip。</summary>
    private static (bool Ok, string Message, string Path) BuildBoostNip(string gpu, string driver)
    {
        try
        {
            Directory.CreateDirectory(NipDir);

            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.AppendLine("<ArrayOfProfile xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">");
            sb.AppendLine("  <Profile>");
            sb.AppendLine("    <ProfileName>Base Profile</ProfileName>");
            sb.AppendLine("    <Executeables />");
            sb.AppendLine("    <Settings>");
            sb.AppendLine("      <ProfileSetting>");
            sb.AppendLine("        <SettingNameInfo>Power management mode</SettingNameInfo>");
            sb.AppendLine($"        <SettingID>{PowerManagementModeId}</SettingID>");
            sb.AppendLine("        <SettingValue>1</SettingValue>");
            sb.AppendLine("        <ValueType>Dword</ValueType>");
            sb.AppendLine("      </ProfileSetting>");
            sb.AppendLine("    </Settings>");
            sb.AppendLine("  </Profile>");
            sb.AppendLine("</ArrayOfProfile>");

            var path = Path.Combine(NipDir, $"NVIDIA-Boost-{DateTime.Now:MMdd-HHmm}.nip");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

            return (true,
                $"已生成：{path}\n\n" +
                "用 NVIDIA Profile Inspector 打开 → Import profile(s) → 选这个文件，" +
                "右下角 Apply changes 后生效（这是 Base Profile，对所有程序生效）。\n\n" +
                "想还原：在控制面板里把「电源管理模式」改回「标准」即可。",
                path);
        }
        catch (Exception ex)
        {
            return (false, "生成失败：" + ex.Message, "");
        }
    }

    private static (bool Ok, string Message) OpenNvidiaPanel()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"NVIDIA Corporation\Control Panel Client\nvcplui.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                @"NVIDIA Corporation\Control Panel Client\nvcplui.exe")
        };

        var exe = candidates.FirstOrDefault(File.Exists);

        if (exe == null)
            return (false, "没找到 NVIDIA 控制面板（nvcplui.exe）。\n" +
                           "可以装 Microsoft Store 版的「NVIDIA Control Panel」，或直接用 Profile Inspector。");

        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            return (true, "已打开 NVIDIA 控制面板：3D 设置 → 管理 3D 设置 → 电源管理模式 → 优先最高性能。");
        }
        catch (Exception ex)
        {
            return (false, "打开失败：" + ex.Message);
        }
    }

    private static string NipDir => Path.Combine(AppPaths.OutputDir, "nvidia-boost");

    private static (string Gpu, string Driver) NvidiaGpu()
    {
        try
        {
            foreach (var row in EvidenceService.Wmi("SELECT Name, DriverVersion FROM Win32_VideoController"))
            {
                var name = Str(row, "Name");
                if (name.Length == 0) continue;

                if (!name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("GeForce", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("RTX", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("GTX", StringComparison.OrdinalIgnoreCase))
                    continue;

                return (name, DlssService.NormalizeDriver(Str(row, "DriverVersion")));
            }
        }
        catch { /* 拿不到就当没有 */ }

        return ("", "");
    }

    // ---------------------------------------------------------------- 工具

    private static int ReadDword(string path, string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key?.GetValue(name) is int value ? value : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static string Str(Dictionary<string, object> dict, string key)
        => dict != null && dict.TryGetValue(key, out var v) && v != null ? v.ToString()?.Trim() ?? "" : "";
}
