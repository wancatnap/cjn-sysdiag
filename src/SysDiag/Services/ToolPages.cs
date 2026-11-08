using System.Diagnostics;
using System.Windows;
using SysDiag.Controls;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 九张「系统工具」卡片的页面定义：怎么采、KPI 显示什么、能对选中项做什么。
///
/// 界面骨架统一在 <see cref="SysDiag.ToolListWindow"/>，这里只描述差异部分——
/// 新增同类工具只要再加一个方法，不用再写一套 XAML。
/// </summary>
public static class ToolPages
{
    /// <summary>卡片 target → 页面。MainWindow 与 --open-tool 都走这里。</summary>
    public static ToolPage ByKey(string key) => key switch
    {
        "devices" => Devices(),
        "services" => Services(),
        "power-plans" => PowerPlans(),
        "reliability" => Reliability(),
        "installed-apps" => InstalledApps(),
        "processes" => Processes(),
        "adapters" => Adapters(),
        "firewall" => Firewall(),
        "disks" => Disks(),
        _ => null
    };

    // ---------------- 设备管理器 ----------------

    public static ToolPage Devices()
    {
        var page = new ToolPage
        {
            Key = "devices",
            Title = "设备管理器",
            Badge = "软件内查看",
            Subtitle = "直接读 WMI 的设备与驱动信息，列出类别、状态、问题代码与驱动版本。精简系统删掉 mmc / 设备管理器组件时，这里照样能用。",
            LaunchArg = "--open-tool=devices",
            Note = "驱动安装、回滚、禁用设备要写驱动存储并调用 SetupAPI，仍只能用系统自带的设备管理器（右下角可打开）。本页会把「哪些设备有问题、问题代码是什么」先定位出来。",
            EmptyText = "没有读到任何设备（WMI 的 Win32_PnPEntity 不可用？）",
            NativeFallbackLabel = "打开系统设备管理器",
            NativeFallback = _ => OpenNative("devmgmt.msc")
        };

        page.Options = new LogViewOptions
        {
            DefaultSort = "level",
            ShowTimeline = false,
            LevelLabels = new Dictionary<string, string>
            {
                ["warn"] = "故障", ["hint"] = "已禁用", ["ok"] = "正常"
            }
        };

        page.SelectionKey = r => ((DeviceEntry)r.Payload).DeviceId;

        page.Load = () => DeviceInventoryService.Scan()
            .Select(d => new LogRow
            {
                Title = d.Name,
                Detail = DeviceDetail(d),
                Basis = d.DeviceId,
                Tag = d.Category,
                Level = d.Level,
                LevelText = d.LevelText,
                MetricText = d.DriverVersion,
                Payload = d
            })
            .ToList();

        page.Kpis = () =>
        {
            var c = DeviceInventoryService.Count(page.Rows.Select(r => (DeviceEntry)r.Payload).ToList());
            return new List<ToolKpi>
            {
                new(c.Total.ToString(), "设备总数", Palette.KnownHex),
                new(c.Problem.ToString(), "故障", c.Problem > 0 ? Palette.WarnHex : Palette.OkHex),
                new(c.Disabled.ToString(), "已禁用", c.Disabled > 0 ? Palette.HintHex : Palette.OkHex),
                new(c.NoDriver.ToString(), "缺驱动", c.NoDriver > 0 ? Palette.HintHex : Palette.OkHex)
            };
        };

        page.RowActions.Add(new ToolAction
        {
            Label = "复制设备 ID",
            Tip = "设备实例 ID 用于在厂商官网查驱动、或在系统工具里定位同一台设备。",
            Run = ctx =>
            {
                if (ctx.First?.Payload is not DeviceEntry d) return ToolActionResult.Fail("请先选中一台设备。");

                Clipboard.SetText(d.DeviceId);
                return new ToolActionResult(true, "设备 ID 已复制。", false);
            }
        });

        page.RowActions.Add(new ToolAction
        {
            Label = "复制问题摘要",
            Run = ctx =>
            {
                if (ctx.First?.Payload is not DeviceEntry d) return ToolActionResult.Fail("请先选中一台设备。");

                Clipboard.SetText(
                    $"{d.Name}\n类别：{d.Category}\n状态：{d.LevelText}" +
                    (d.ProblemText.Length > 0 ? $"（{d.ProblemText}，代码 {d.ErrorCode}）" : "") +
                    $"\n驱动：{d.DriverVersion}　{d.DriverProvider}　{d.DriverDate}\n设备 ID：{d.DeviceId}");

                return new ToolActionResult(true, "问题摘要已复制。", false);
            }
        });

        return page;
    }

    private static string DeviceDetail(DeviceEntry d)
    {
        var parts = new List<string>();

        if (d.HasProblem || d.IsDisabled)
            parts.Add(d.ProblemText + $"（代码 {d.ErrorCode}）");

        if (d.Manufacturer.Length > 0) parts.Add(d.Manufacturer);

        if (d.DriverVersion.Length > 0)
        {
            var driver = $"驱动 {d.DriverVersion}";
            if (d.DriverProvider.Length > 0) driver += $" · {d.DriverProvider}";
            if (d.DriverDate.Length > 0) driver += $" · {d.DriverDate}";
            parts.Add(driver);
        }
        else
        {
            parts.Add("没有登记驱动信息");
        }

        if (d.ServiceName.Length > 0) parts.Add("服务 " + d.ServiceName);

        return string.Join("　|　", parts);
    }

    // ---------------- 服务 ----------------

    public static ToolPage Services()
    {
        var page = new ToolPage
        {
            Key = "services",
            Title = "服务",
            Badge = "软件内启停",
            Subtitle = "列出本机全部 Win32 服务：状态、启动类型、登录身份与描述。可以直接启动、停止、重启，或改启动类型（自动 / 手动 / 禁用），不需要打开 services.msc。",
            LaunchArg = "--open-tool=services",
            NeedsAdmin = true,
            Note = "启停与改启动类型需要管理员权限。标「自动」的服务被停掉后，下次开机会自己回来；要长期关闭请改启动类型为「禁用」。内核驱动不在这里，见「设备管理器」。",
            EmptyText = "没有读到服务（注册表 Services 键不可访问？）",
            NativeFallbackLabel = "打开系统服务管理器",
            NativeFallback = _ => OpenNative("services.msc")
        };

        page.Options = new LogViewOptions
        {
            DefaultSort = "level",
            ShowTimeline = false,
            LevelLabels = new Dictionary<string, string>
            {
                ["warn"] = "状态异常", ["hint"] = "处理中", ["ok"] = "运行中",
                ["none"] = "已停止", ["known"] = "按用户启动"
            }
        };

        page.SelectionKey = r => ((ServiceEntry)r.Payload).Name;

        page.Load = () => ServiceInventoryService.Scan()
            .Select(s => new LogRow
            {
                Title = s.DisplayName,
                Detail = s.IsPerUserTemplate
                    ? "按用户服务模板：SCM 里没有常驻实例，每个用户登录后各启动一份。" +
                      (s.Description.Length > 0 ? "　" + Shorten(s.Description, 130) : "")
                    : s.Description.Length > 0
                        ? Shorten(s.Description, 160)
                        : $"可执行文件：{Shorten(s.ImagePath, 110)}",
                Basis = $"{s.Name}　·　启动 {s.StartTypeText}　·　身份 {AccountText(s.Account)}" +
                        (s.ProcessId > 0 ? $"　·　PID {s.ProcessId}" : "") +
                        (s.Dependencies.Length > 0 ? $"　·　依赖 {Shorten(s.Dependencies, 60)}" : ""),
                Tag = s.StartTypeText,
                Level = s.Level,
                LevelText = s.LevelText,
                MetricText = s.Running ? "运行" : "停止",
                Payload = s
            })
            .ToList();

        page.Kpis = () =>
        {
            var list = page.Rows.Select(r => (ServiceEntry)r.Payload).ToList();
            return new List<ToolKpi>
            {
                new(list.Count.ToString(), "服务总数", Palette.KnownHex),
                new(list.Count(x => x.Running).ToString(), "运行中", Palette.OkHex),
                new(list.Count(x => x.State == "Stopped").ToString(), "已停止", list.Any(x => x.State == "Stopped") ? Palette.HintHex : Palette.OkHex),
                new(list.Count(x => x.StartRaw == 2).ToString(), "自动启动", Palette.KnownHex)
            };
        };

        page.Actions.Add(new ToolAction
        {
            Label = "启动",
            NeedsAdmin = true,
            Run = ctx => RunService(ctx, "启动", (e) => ServiceInventoryService.Start(e.Name))
        });

        page.Actions.Add(new ToolAction
        {
            Label = "停止",
            NeedsAdmin = true,
            Run = ctx =>
            {
                if (ctx.First?.Payload is not ServiceEntry s) return ToolActionResult.Fail("请先选中一个服务。");

                var confirm = UiDialog.Show(ctx.Owner,
                    $"停止「{s.DisplayName}」？\n\n" +
                    $"服务名：{s.Name}\n当前状态：{s.LevelText}\n\n" +
                    "停止服务会立刻中断它提供的功能。系统关键服务（如 RpcSs、Dhcp）停止后界面可能卡住甚至需要重启。\n" +
                    "如果不确定，先改启动类型为「手动」，下次开机它就不会自动起来。",
                    "确认停止服务", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);

                if (confirm != System.Windows.MessageBoxResult.OK)
                    return new ToolActionResult(true, "已取消。", false);

                return RunService(ctx, "停止", (e) => ServiceInventoryService.Stop(e.Name));
            }
        });

        page.Actions.Add(new ToolAction
        {
            Label = "重启",
            NeedsAdmin = true,
            Run = ctx =>
            {
                if (ctx.First?.Payload is not ServiceEntry s) return ToolActionResult.Fail("请先选中一个服务。");

                var stop = ServiceInventoryService.Stop(s.Name);
                if (!stop.Ok && !stop.Message.Contains("已经是停止状态")) return ToolActionResult.Fail(stop.Message);

                var start = ServiceInventoryService.Start(s.Name);
                return new ToolActionResult(start.Ok, $"{s.DisplayName} 已重启。{(start.Ok ? "" : start.Message)}");
            }
        });

        page.Actions.Add(new ToolAction
        {
            Label = "暂停",
            NeedsAdmin = true,
            Tip = "只有注册了暂停控制的服务才支持（例如打印队列）。",
            Run = ctx => RunService(ctx, "暂停", s => ServiceInventoryService.Pause(s.Name))
        });

        page.Actions.Add(new ToolAction
        {
            Label = "继续",
            NeedsAdmin = true,
            Run = ctx => RunService(ctx, "继续", s => ServiceInventoryService.Continue(s.Name))
        });

        page.RowActions.Add(StartTypeAction("设为自动", 2));
        page.RowActions.Add(StartTypeAction("设为手动", 3));
        page.RowActions.Add(StartTypeAction("设为禁用", 4));

        page.RowActions.Add(new ToolAction
        {
            Label = "设为自动（延迟）",
            NeedsAdmin = true,
            Tip = "开机后延迟约 2 分钟启动，能把开机时间让给更要紧的服务。",
            Run = ctx =>
            {
                if (ctx.First?.Payload is not ServiceEntry s) return ToolActionResult.Fail("请先选中一个服务。");

                if (s.StartRaw != 2 && s.StartRaw != 3)
                {
                    var set = ServiceInventoryService.SetStartType(s.Name, 2);
                    if (!set.Ok) return ToolActionResult.Fail(set.Message);
                }

                var (ok, message) = ServiceInventoryService.SetDelayedAutoStart(s.Name, true);
                return new ToolActionResult(ok, message);
            }
        });

        page.RowActions.Add(new ToolAction
        {
            Label = "失败恢复…",
            NeedsAdmin = true,
            Tip = "服务异常退出后要不要自动重启。对应 services.msc 属性里的「恢复」页。",
            Run = ctx =>
            {
                if (ctx.First?.Payload is not ServiceEntry s) return ToolActionResult.Fail("请先选中一个服务。");

                var dialog = new SysDiag.ServiceRecoveryWindow(s) { Owner = ctx.Owner };

                return dialog.ShowDialog() == true
                    ? new ToolActionResult(true, dialog.ApplyMessage, true)
                    : new ToolActionResult(true, "已取消。", false);
            }
        });

        return page;
    }

    private static ToolAction StartTypeAction(string label, int startRaw) => new()
    {
        Label = label,
        NeedsAdmin = true,
        Run = ctx =>
        {
            if (ctx.First?.Payload is not ServiceEntry s) return ToolActionResult.Fail("请先选中一个服务。");

            if (startRaw == 4)
            {
                var confirm = UiDialog.Show(ctx.Owner,
                    $"把「{s.DisplayName}」设为禁用？\n\n" +
                    "禁用之后这个服务无法再启动（手动也不行），部分系统功能会永久失效，直到改回「手动」或「自动」。",
                    "确认禁用服务", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);

                if (confirm != System.Windows.MessageBoxResult.OK)
                    return new ToolActionResult(true, "已取消。", false);
            }

            var (ok, message) = ServiceInventoryService.SetStartType(s.Name, startRaw);
            return new ToolActionResult(ok, message);
        }
    };

    private static ToolActionResult RunService(ToolActionContext ctx, string verb,
        Func<ServiceEntry, (bool Ok, string Message)> action)
    {
        if (ctx.First?.Payload is not ServiceEntry s) return ToolActionResult.Fail("请先选中一个服务。");

        var (ok, message) = action(s);
        return new ToolActionResult(ok, message);
    }

    private static string AccountText(string account) => account switch
    {
        "LocalSystem" => "LocalSystem（本地系统）",
        "NT AUTHORITY\\LocalService" => "LocalService（本地服务）",
        "NT AUTHORITY\\NetworkService" => "NetworkService（网络服务）",
        var a when a.Length == 0 => "未知",
        _ => account
    };

    // ---------------- 电源计划 ----------------

    public static ToolPage PowerPlans()
    {
        var page = new ToolPage
        {
            Key = "power-plans",
            Title = "电源选项",
            Badge = "软件内切换",
            Subtitle = "直接调电源管理接口（powrprof）读取全部电源计划，并显示每个计划的关键设置。可以一键切到高性能，不需要 powercfg.exe 或控制面板。",
            LaunchArg = "--open-tool=power-plans",
            Note = "这里改的是「整套电源计划」。处理器频率上限、PCIe 链路电源管理等细项仍在系统的高级电源设置里；本页会把处理器最小/最大状态一并读出来供对照。",
            EmptyText = "没有读到电源计划（powrprof 不可用？）",
            NativeFallbackLabel = "打开系统电源设置",
            NativeFallback = _ => OpenNative("control.exe", "powercfg.cpl")
        };

        page.Options = new LogViewOptions { DefaultSort = "level", ShowTimeline = false, ShowLevelChips = false };
        page.SelectionKey = r => ((PowerPlanEntry)r.Payload).Guid.ToString("D");

        page.Load = () => PowerPlanService.List()
            .Select(p => new LogRow
            {
                Title = p.Name,
                Detail = p.SettingsText,
                Basis = p.Guid.ToString("D") + (p.IsBuiltIn ? "　·　系统内置" : "　·　自定义方案"),
                Tag = p.Kind,
                Level = p.Level,
                LevelText = p.LevelText,
                MetricText = p.IsActive ? "使用中" : "　　　",
                Payload = p
            })
            .ToList();

        page.Kpis = () =>
        {
            var list = page.Rows.Select(r => (PowerPlanEntry)r.Payload).ToList();
            var active = list.FirstOrDefault(p => p.IsActive);

            return new List<ToolKpi>
            {
                new(active?.Name ?? "未知", "当前计划", active != null && active.Kind == "高性能" ? Palette.OkHex : Palette.KnownHex),
                new(list.Count.ToString(), "方案数", Palette.KnownHex),
                new(active?.ProcessorMin >= 0 ? active.ProcessorMin + "%" : "—", "处理器下限", Palette.HintHex),
                new(active?.ProcessorMax >= 0 ? active.ProcessorMax + "%" : "—", "处理器上限", Palette.HintHex)
            };
        };

        page.Actions.Add(new ToolAction
        {
            Label = "切换到该计划",
            Run = ctx =>
            {
                if (ctx.First?.Payload is not PowerPlanEntry p) return ToolActionResult.Fail("请先选中一个电源计划。");

                var (ok, message) = PowerPlanService.SetActive(p.Guid);
                return new ToolActionResult(ok, ok ? $"已切换到「{p.Name}」。" : message);
            }
        });

        page.Actions.Add(new ToolAction
        {
            Label = "创建并切换高性能",
            Tip = "现代笔记本默认只保留「平衡」，高性能方案被系统隐藏；这里可以现场复制一份。",
            Run = _ =>
            {
                var (ok, message, _) = PowerPlanService.EnsureHighPerformance();
                return new ToolActionResult(ok, message);
            }
        });

        page.RowActions.Add(new ToolAction
        {
            Label = "复制计划 GUID",
            Tip = "在高级电源设置或脚本里指定方案时要用它。",
            Run = ctx =>
            {
                if (ctx.First?.Payload is not PowerPlanEntry p) return ToolActionResult.Fail("请先选中一个电源计划。");

                Clipboard.SetText(p.Guid.ToString("D"));
                return new ToolActionResult(true, "GUID 已复制。", false);
            }
        });

        return page;
    }

    // ---------------- 可靠性监视器 ----------------

    public static ToolPage Reliability()
    {
        var page = new ToolPage
        {
            Key = "reliability",
            Title = "可靠性监视器",
            Badge = "软件内查看",
            Subtitle = "读系统的可靠性与问题历史（Win32_ReliabilityRecords / StabilityMetrics），与「可靠性和问题历史」面板同源：稳定性指数 + 崩溃、挂起、服务失败、驱动与更新记录。",
            LaunchArg = "--open-tool=reliability",
            Note = "稳定性指数 10 为满分，掉分来自崩溃、挂起与服务异常退出。数据由系统自己记录（RACWMI 提供程序），精简镜像删除该组件时会提示不可用。",
            EmptyText = "近 30 天没有可靠性记录——这通常说明系统很稳定，也可能是可靠性数据提供程序被删。",
            NativeFallbackLabel = "打开事件查看器",
            NativeFallback = w =>
            {
                if (w is SysDiag.ToolListWindow) OpenNative("eventvwr.msc");
            }
        };

        page.Options = new LogViewOptions
        {
            DefaultSort = "time-desc",
            ShowTimeline = true,
            LevelLabels = new Dictionary<string, string>
            {
                ["warn"] = "异常", ["hint"] = "变更", ["ok"] = "记录"
            }
        };

        page.NoteProvider = () =>
        {
            var data = ReliabilityService.Collect();
            if (!data.Available) return data.Reason;

            var worst = data.Timeline.OrderBy(d => d.Min).FirstOrDefault();

            return $"近 {data.Days} 天平均稳定性 {data.AverageIndex:0.00}／10" +
                   (worst != null ? $"，最低 {worst.Min:0.00}（{worst.Day:MM-dd}）" : "") +
                   $"　·　崩溃 {data.CrashCount} 次、变更 {data.ChangeCount} 条" +
                   "　·　数据来自系统可靠性提供程序（与 perfmon /rel 同源）";
        };

        page.Load = () =>
        {
            var data = ReliabilityService.Collect();

            if (!data.Available)
                return new List<LogRow>();

            return data.Records.Select(r => new LogRow
            {
                Title = r.Product.Length > 0 ? r.Product : r.Source,
                Detail = r.Message.Length > 0 ? r.Message : r.Source,
                Basis = $"{r.Source}　·　事件 {r.EventId}　·　{r.Category}",
                Tag = r.Category,
                Level = r.Level,
                LevelText = r.LevelText,
                Time = r.Time,
                TimeText = r.Time.ToString("MM-dd HH:mm"),
                Payload = r
            }).ToList();
        };

        page.Kpis = () =>
        {
            var data = ReliabilityService.Collect();

            if (!data.Available)
                return new List<ToolKpi>
                {
                    new("不可用", "可靠性数据", Palette.WarnHex),
                    new("—", "平均指数", ""),
                    new("—", "崩溃", ""),
                    new("—", "变更", "")
                };

            return new List<ToolKpi>
            {
                new(data.AverageIndex.ToString("0.00"), "平均指数", data.AverageIndex >= 8 ? Palette.OkHex : Palette.HintHex),
                new(data.LowestIndex.ToString("0.00"), "最低指数", data.LowestIndex < 5 ? Palette.WarnHex : Palette.HintHex),
                new(data.CrashCount.ToString(), "崩溃/挂起", data.CrashCount > 0 ? Palette.WarnHex : Palette.OkHex),
                new(data.ChangeCount.ToString(), "系统变更", Palette.KnownHex)
            };
        };

        page.RowActions.Add(new ToolAction
        {
            Label = "复制这条记录",
            Run = ctx =>
            {
                if (ctx.First?.Payload is not ReliabilityRecord r) return ToolActionResult.Fail("请先选中一条记录。");

                Clipboard.SetText($"{r.Time:yyyy-MM-dd HH:mm:ss}　{r.Category}　{r.Product}　{r.Source}({r.EventId})\n{r.Message}");
                return new ToolActionResult(true, "记录已复制。", false);
            }
        });

        page.RowActions.Add(new ToolAction
        {
            Label = "看相关事件",
            Tip = "在本软件的事件查看器里继续查（本软件自建，不调 eventvwr.msc）。",
            Run = ctx =>
            {
                if (ctx.First?.Payload is not ReliabilityRecord r) return ToolActionResult.Fail("请先选中一条记录。");
                if (ctx.Owner is not SysDiag.ToolListWindow win) return ToolActionResult.Fail("无法定位窗口。");

                win.OpenChildWindow(new SysDiag.EvtxViewerWindow(_ => { }));

                return new ToolActionResult(true, $"已打开事件查看器，可在里面按来源「{r.Source}」筛选。", false);
            }
        });

        return page;
    }

    // ---------------- 程序和功能 ----------------

    public static ToolPage InstalledApps()
    {
        var page = new ToolPage
        {
            Key = "installed-apps",
            Title = "程序和功能",
            Badge = "软件内查看与卸载",
            Subtitle = "从注册表 Uninstall 键读出全部已安装软件：版本、发行商、安装日期、占用空间。可以在这里直接唤起它自己的卸载程序。",
            LaunchArg = "--open-tool=installed-apps",
            Note = "卸载会调用软件自己登记的卸载程序（可能出现它自己的窗口）。系统补丁与运行库条目已按惯例剔除，避免列表被几百个 KB 编号填满。",
            EmptyText = "没有读到已安装软件。",
            NativeFallbackLabel = "打开系统「程序和功能」",
            NativeFallback = _ => OpenNative("control.exe", "appwiz.cpl")
        };

        page.Options = new LogViewOptions { DefaultSort = "title", ShowTimeline = false, ShowLevelChips = false };

        page.Load = () => InstalledAppsService.Scan()
            .Select(a => new LogRow
            {
                Title = a.Name,
                Detail = AppDetail(a),
                Basis = a.Uninstall.Length > 0 ? Shorten(a.Uninstall, 120) : "没有登记卸载命令",
                Tag = a.PerUser ? "当前用户" : "全机",
                Level = "ok",
                LevelText = a.SizeKb >= 2 * 1024 * 1024 ? "占空间大" : "",
                MetricText = a.SizeText,
                Payload = a
            })
            .ToList();

        page.Kpis = () =>
        {
            var list = page.Rows.Select(r => (InstalledAppsService.AppEntry)r.Payload).ToList();
            var totalKb = list.Sum(x => x.SizeKb);

            return new List<ToolKpi>
            {
                new(list.Count.ToString(), "软件数", Palette.KnownHex),
                new(totalKb >= 1024 * 1024 ? $"{totalKb / 1024.0 / 1024:0.#} GB" : $"{totalKb / 1024.0:0} MB", "登记占用", Palette.HintHex),
                new(list.Count(x => x.SizeKb >= 1024 * 1024).ToString(), "大于 1 GB", Palette.HintHex),
                new(list.Count(x => x.PerUser).ToString(), "仅当前用户", Palette.KnownHex)
            };
        };

        page.Actions.Add(new ToolAction
        {
            Label = "卸载",
            Run = ctx =>
            {
                if (ctx.First?.Payload is not InstalledAppsService.AppEntry app)
                    return ToolActionResult.Fail("请先选中一个软件。");

                var confirm = UiDialog.Show(ctx.Owner,
                    $"卸载「{app.Name}」？\n\n版本：{app.Version}\n发行商：{app.Publisher}\n\n" +
                    "这会启动该软件自己的卸载程序，后续步骤按它的提示走。",
                    "确认卸载", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);

                if (confirm != System.Windows.MessageBoxResult.OK)
                    return new ToolActionResult(true, "已取消。", false);

                var (ok, message) = InstalledAppsService.Uninstall(app);
                return new ToolActionResult(ok, message, false);
            }
        });

        page.RowActions.Add(new ToolAction
        {
            Label = "打开安装位置",
            Run = ctx =>
            {
                if (ctx.First?.Payload is not InstalledAppsService.AppEntry app) return ToolActionResult.Fail("请先选中一个软件。");

                if (app.Location.Length == 0 || !System.IO.Directory.Exists(app.Location))
                    return ToolActionResult.Fail("这个软件没有登记安装目录，或目录已被删除。");

                LauncherService.RevealInExplorer(app.Location);
                return new ToolActionResult(true, "已在资源管理器中打开安装目录。", false);
            }
        });

        page.RowActions.Add(new ToolAction
        {
            Label = "复制卸载命令",
            Run = ctx =>
            {
                if (ctx.First?.Payload is not InstalledAppsService.AppEntry app) return ToolActionResult.Fail("请先选中一个软件。");

                Clipboard.SetText(app.Uninstall.Length > 0 ? app.Uninstall : app.Name);
                return new ToolActionResult(true, "卸载命令已复制。", false);
            }
        });

        return page;
    }

    private static string AppDetail(InstalledAppsService.AppEntry a)
    {
        var parts = new List<string>();

        if (a.Version.Length > 0) parts.Add("版本 " + a.Version);
        if (a.Publisher.Length > 0) parts.Add(a.Publisher);
        if (a.InstallDate.Length > 0) parts.Add("安装于 " + a.InstallDate);
        if (a.SizeText.Length > 0) parts.Add(a.SizeText);
        if (a.Location.Length > 0) parts.Add(Shorten(a.Location, 60));

        return string.Join("　·　", parts);
    }

    // ---------------- 任务管理器 / 资源监视器 ----------------

    public static ToolPage Processes()
    {
        var page = new ToolPage
        {
            Key = "processes",
            Title = "任务管理器",
            Badge = "软件内实时",
            Subtitle = "进程列表：CPU、内存、句柄、线程、公司、完整路径。默认每 2 秒自动刷新，可以对选中进程结束任务或打开文件位置。",
            LaunchArg = "--open-tool=processes",
            Note = "结束进程（TerminateProcess）、优先级、CPU 亲和性都能改；优先级与亲和性只对当前实例有效，进程重启后恢复默认。系统关键进程列入保护名单，直接拒绝。",
            EmptyText = "没有读到进程。",
            NativeFallbackLabel = "打开系统任务管理器",
            NativeFallback = _ => OpenNative("taskmgr.exe")
        };

        page.Options = new LogViewOptions { DefaultSort = "title", ShowTimeline = false, ShowLevelChips = false };
        page.AutoRefreshSeconds = 2;
        page.SelectionKey = r => ((ProcessInventoryService.ProcessEntry)r.Payload).Pid.ToString();

        // CPU 与内存必须进 Detail：卡片模式下 MetricText 只在紧凑列表里显示，
        // 而「哪个进程在吃 CPU」正是这一页最该一眼看到的信息。
        page.Load = () => ProcessInventoryService.Scan()
            .Select(p => new LogRow
            {
                Title = p.Name + ".exe",
                Detail = $"CPU {p.CpuPercent:0.#}%　·　内存 {MemoryText(p.WorkingSetMb)}" +
                         (p.Description.Length > 0
                             ? "　·　" + p.Description
                             : p.Company.Length > 0 ? "　·　" + p.Company : ""),
                Basis = $"PID {p.Pid}　·　线程 {p.Threads}　·　句柄 {p.Handles}" +
                        (p.StartTime != null ? $"　·　启动 {p.StartTime:MM-dd HH:mm}" : "") +
                        "　·　" + (p.Path.Length > 0 ? Shorten(p.Path, 70) : "路径不可读（受保护进程）"),
                Tag = p.Company.Length > 0 ? Shorten(p.Company, 22) : (p.Critical ? "系统进程" : "未知来源"),
                Level = p.Level,
                LevelText = p.LevelText,
                MetricText = p.CpuPercent > 0 ? $"{p.CpuPercent:0.#}%" : "—",
                Payload = p
            })
            .ToList();

        page.Kpis = () =>
        {
            var list = page.Rows.Select(r => (ProcessInventoryService.ProcessEntry)r.Payload).ToList();
            var (count, memoryMb, cpu) = ProcessInventoryService.Summary(list);

            return new List<ToolKpi>
            {
                new(count.ToString(), "进程数", Palette.KnownHex),
                new(cpu.ToString("0.#") + "%", "CPU 合计", cpu >= 60 ? Palette.WarnHex : Palette.OkHex),
                new(memoryMb >= 1024 ? $"{memoryMb / 1024.0:0.#} GB" : memoryMb + " MB", "内存合计", Palette.HintHex),
                new(list.Count(x => x.Critical).ToString(), "系统进程", Palette.KnownHex)
            };
        };

        page.Actions.Add(new ToolAction
        {
            Label = "结束任务",
            Run = ctx =>
            {
                if (ctx.First?.Payload is not ProcessInventoryService.ProcessEntry p)
                    return ToolActionResult.Fail("请先选中一个进程。");

                var confirm = UiDialog.Show(ctx.Owner,
                    $"结束「{p.Name}（PID {p.Pid}）」？\n\n" +
                    (p.Path.Length > 0 ? $"路径：{p.Path}\n\n" : "") +
                    "未保存的数据会丢失。如果是游戏或反作弊进程，可能导致对局判负或需要重新登录。",
                    "确认结束进程", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);

                if (confirm != System.Windows.MessageBoxResult.OK)
                    return new ToolActionResult(true, "已取消。", false);

                var (ok, message) = ProcessInventoryService.Kill(p);
                return new ToolActionResult(ok, message, false);
            }
        });

        page.Actions.Add(new ToolAction
        {
            Label = "优先级与亲和性…",
            Tip = "改这个进程的优先级，或把它限制在部分 CPU 上。只对当前实例有效。",
            Run = ctx =>
            {
                if (ctx.First?.Payload is not ProcessInventoryService.ProcessEntry p)
                    return ToolActionResult.Fail("请先选中一个进程。");

                if (p.Priority.Length == 0 && p.AffinityMask == 0)
                    return ToolActionResult.Fail("这个进程的优先级与亲和性都读不到——被反作弊或系统保护，改不了。");

                var dialog = new SysDiag.ProcessTuneWindow(p) { Owner = ctx.Owner };

                return dialog.ShowDialog() == true
                    ? new ToolActionResult(true, dialog.ApplyMessage, true)
                    : new ToolActionResult(true, "已取消。", false);
            }
        });

        page.RowActions.Add(new ToolAction
        {
            Label = "打开文件位置",
            Run = ctx =>
            {
                if (ctx.First?.Payload is not ProcessInventoryService.ProcessEntry p) return ToolActionResult.Fail("请先选中一个进程。");

                if (p.Path.Length == 0) return ToolActionResult.Fail("这个进程的路径读不到（受保护进程或权限不足）。");

                LauncherService.RevealInExplorer(p.Path);
                return new ToolActionResult(true, "已定位到可执行文件。", false);
            }
        });

        return page;
    }

    // ---------------- 网络连接 ----------------

    public static ToolPage Adapters()
    {
        var page = new ToolPage
        {
            Key = "adapters",
            Title = "网络连接",
            Badge = "软件内查看",
            Subtitle = "网卡的连接状态、IP、网关、DNS、链路速度与累计流量，全部在软件内读取。可以直接启用或禁用网卡。",
            LaunchArg = "--open-tool=adapters",
            Note = "只列物理网卡（虚拟网卡、VPN 适配器已过滤）。改 IP / DNS 见「DNS 优化」页；高级属性（巨帧、唤醒、802.1X）需要网卡厂商的驱动面板。",
            EmptyText = "没有读到物理网卡。",
            NativeFallbackLabel = "打开系统网络连接",
            NativeFallback = _ => OpenNative("control.exe", "ncpa.cpl")
        };

        page.Options = new LogViewOptions
        {
            DefaultSort = "level",
            ShowTimeline = false,
            LevelLabels = new Dictionary<string, string>
            {
                ["ok"] = "已连接", ["hint"] = "未连接", ["none"] = "已禁用"
            }
        };

        page.Load = () => AdapterInventoryService.Scan()
            .Select(a => new LogRow
            {
                Title = a.Name,
                Detail = $"IPv4 {(a.Ipv4.Length > 0 ? a.Ipv4 : "无")}　·　网关 {(a.Gateway.Length > 0 ? a.Gateway : "无")}　·　DNS {(a.Dns.Length > 0 ? a.Dns : "无")}",
                Basis = $"MAC {a.Mac}　·　{a.Speed}　·　{a.Traffic}" + (a.Dhcp ? "　·　DHCP" : "　·　静态"),
                Tag = a.Type,
                Level = a.Level,
                LevelText = a.LevelText,
                MetricText = a.Ipv4,
                Payload = a
            })
            .ToList();

        page.Kpis = () =>
        {
            var list = page.Rows.Select(r => (AdapterInventoryService.AdapterEntry)r.Payload).ToList();

            return new List<ToolKpi>
            {
                new(list.Count.ToString(), "物理网卡", Palette.KnownHex),
                new(list.Count(x => x.Up).ToString(), "已连接", Palette.OkHex),
                new(list.Count(x => !x.Enabled).ToString(), "已禁用", Palette.HintHex),
                new(list.Count(x => x.Type == "无线").ToString(), "无线", Palette.KnownHex)
            };
        };

        page.Actions.Add(new ToolAction
        {
            Label = "启用",
            NeedsAdmin = true,
            Run = ctx => ToggleAdapter(ctx, true)
        });

        page.Actions.Add(new ToolAction
        {
            Label = "禁用",
            NeedsAdmin = true,
            Run = ctx =>
            {
                if (ctx.First?.Payload is not AdapterInventoryService.AdapterEntry a)
                    return ToolActionResult.Fail("请先选中一块网卡。");

                var confirm = UiDialog.Show(ctx.Owner,
                    $"禁用「{a.Name}」？\n\n" +
                    "如果这块网卡正在承载你的远程连接（远程桌面 / 游戏加速器），禁用后连接会立即断开。\n" +
                    "禁用后仍可在本页重新启用。",
                    "确认禁用网卡", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);

                if (confirm != System.Windows.MessageBoxResult.OK)
                    return new ToolActionResult(true, "已取消。", false);

                return ToggleAdapter(ctx, false);
            }
        });

        page.RowActions.Add(new ToolAction
        {
            Label = "复制网络信息",
            Run = ctx =>
            {
                if (ctx.First?.Payload is not AdapterInventoryService.AdapterEntry a) return ToolActionResult.Fail("请先选中一块网卡。");

                Clipboard.SetText(
                    $"{a.Name}（{a.Description}）\n状态：{a.LevelText}\nMAC：{a.Mac}\nIPv4：{a.Ipv4}\nIPv6：{a.Ipv6}\n" +
                    $"网关：{a.Gateway}\nDNS：{a.Dns}\n链路：{a.Speed}\n流量：{a.Traffic}");

                return new ToolActionResult(true, "网络信息已复制。", false);
            }
        });

        return page;
    }

    private static ToolActionResult ToggleAdapter(ToolActionContext ctx, bool enable)
    {
        if (ctx.First?.Payload is not AdapterInventoryService.AdapterEntry a)
            return ToolActionResult.Fail("请先选中一块网卡。");

        var (ok, message) = AdapterInventoryService.SetEnabled(a, enable);
        return new ToolActionResult(ok, message);
    }

    // ---------------- 防火墙 ----------------

    public static ToolPage Firewall()
    {
        var page = new ToolPage
        {
            Key = "firewall",
            Title = "防火墙",
            Badge = "软件内查看",
            Subtitle = "解析注册表里的防火墙规则：方向、动作、协议、端口、程序与配置文件。三个配置文件（域 / 专用 / 公用）的开关状态也一并读出。",
            LaunchArg = "--open-tool=firewall",
            Note = "本页只读：新增/修改规则要写策略并校验规则语义，仍请用系统防火墙面板（右下角可打开）。排查「某个程序连不上/被拦了」时，先在这里按程序名搜。",
            EmptyText = "没有读到防火墙规则（需要管理员权限，或防火墙服务未运行）。",
            NativeFallbackLabel = "打开系统防火墙设置",
            NativeFallback = _ => OpenNative("control.exe", "firewall.cpl")
        };

        page.Options = new LogViewOptions
        {
            DefaultSort = "level",
            ShowTimeline = false,
            LevelLabels = new Dictionary<string, string>
            {
                ["hint"] = "阻止规则", ["ok"] = "允许规则", ["none"] = "已停用"
            }
        };

        page.Load = () => FirewallRuleService.Scan()
            .Select(r => new LogRow
            {
                Title = r.Name,
                Detail = $"{r.Action}　{r.Ports}" +
                         (r.Program.Length > 0 ? $"　·　{Shorten(r.Program, 80)}" : "　·　任意程序"),
                Basis = $"{(r.Group.Length > 0 ? r.Group + "　·　" : "")}{r.Direction}{r.Protocol}　·　配置文件 {r.Profile}",
                Tag = r.Direction,
                Level = r.Level,
                LevelText = r.LevelText,
                MetricText = r.Protocol,
                Payload = r
            })
            .ToList();

        page.Kpis = () =>
        {
            var list = page.Rows.Select(r => (FirewallRuleService.RuleEntry)r.Payload).ToList();
            var (total, enabled, block, inbound) = FirewallRuleService.Count(list);

            return new List<ToolKpi>
            {
                new(total.ToString(), "规则总数", Palette.KnownHex),
                new(enabled.ToString(), "已启用", Palette.OkHex),
                new(block.ToString(), "阻止规则", block > 0 ? Palette.HintHex : Palette.OkHex),
                new(inbound.ToString(), "入站启用", Palette.KnownHex)
            };
        };

        page.NoteProvider = () =>
        {
            var profiles = FirewallRuleService.Profiles();

            if (profiles.Count == 0)
                return page.Note;

            var text = string.Join("　·　", profiles.Select(p =>
                $"{p.Profile}：{(p.Enabled ? "已开启" : "已关闭")}（入站默认{p.DefaultInbound}）"));

            return "配置文件状态 —— " + text + "　|　本页只读，改规则请用系统防火墙面板。";
        };

        page.RowActions.Add(new ToolAction
        {
            Label = "复制规则",
            Run = ctx =>
            {
                if (ctx.First?.Payload is not FirewallRuleService.RuleEntry r) return ToolActionResult.Fail("请先选中一条规则。");

                Clipboard.SetText(
                    $"{r.Name}\n组：{r.Group}\n动作：{r.Action}　方向：{r.Direction}　协议：{r.Protocol}\n" +
                    $"端口：{r.Ports}\n程序：{(r.Program.Length > 0 ? r.Program : "任意")}\n配置文件：{r.Profile}\n状态：{r.LevelText}");

                return new ToolActionResult(true, "规则已复制。", false);
            }
        });

        return page;
    }

    // ---------------- 磁盘管理 ----------------

    public static ToolPage Disks()
    {
        var page = new ToolPage
        {
            Key = "disks",
            Title = "磁盘管理",
            Badge = "软件内查看",
            Subtitle = "物理磁盘与卷的容量、接口、序列号与占用情况。分区、格式化、改盘符这类操作仍保留在系统磁盘管理里。",
            LaunchArg = "--open-tool=disks",
            Note = "只读视图：分区表与卷影的写入风险太高，创建/删除分区请用 diskmgmt.msc。判断「C 盘还剩多少、是不是要清理」看这一页就够。",
            EmptyText = "没有读到磁盘信息。",
            NativeFallbackLabel = "打开系统磁盘管理",
            NativeFallback = _ => OpenNative("diskmgmt.msc")
        };

        page.Options = new LogViewOptions
        {
            DefaultSort = "title",
            ShowTimeline = false,
            LevelLabels = new Dictionary<string, string>
            {
                ["warn"] = "空间告急", ["hint"] = "占用偏高", ["ok"] = "正常"
            }
        };

        page.SelectionKey = r => r.Tag + "|" + r.Title;

        page.Load = () =>
        {
            var (disks, volumes) = DiskInventoryService.Scan();
            var rows = new List<LogRow>();

            foreach (var d in disks)
            {
                rows.Add(new LogRow
                {
                    Title = $"磁盘 {d.Index}　{d.Model}",
                    Detail = $"{d.SizeText}　·　{d.Interface}　·　{d.Partitions} 个分区" +
                             (d.Serial.Length > 0 ? $"　·　SN {d.Serial}" : "") +
                             (d.MediaType.Length > 0 ? $"　·　{d.MediaType}" : ""),
                    Basis = d.DeviceId,
                    Tag = "物理磁盘",
                    Level = d.Status.Equals("OK", StringComparison.OrdinalIgnoreCase) ? "ok" : "warn",
                    LevelText = d.Status.Equals("OK", StringComparison.OrdinalIgnoreCase) ? "正常" : d.Status,
                    MetricText = d.SizeText,
                    Payload = d
                });
            }

            foreach (var v in volumes)
            {
                rows.Add(new LogRow
                {
                    Title = $"{v.Letter}　{(v.Label.Length > 0 ? v.Label : "本地磁盘")}",
                    Detail = $"已用 {v.UsedPercent:0.#}%　·　{v.FreeText} 可用 / 共 {v.SizeText}　·　{v.FileSystem}　·　{v.DriveType}",
                    Basis = $"{v.Letter}\\　·　{(v.SizeBytes > 0 ? "容量已识别" : "容量未知")}",
                    Tag = "卷",
                    Level = v.Level,
                    LevelText = v.LevelText,
                    MetricText = v.FreeText,
                    Payload = v
                });
            }

            return rows;
        };

        page.Kpis = () =>
        {
            var disks = page.Rows.Where(r => r.Payload is DiskInventoryService.DiskEntry)
                .Select(r => (DiskInventoryService.DiskEntry)r.Payload).ToList();

            var volumes = page.Rows.Where(r => r.Payload is DiskInventoryService.VolumeEntry)
                .Select(r => (DiskInventoryService.VolumeEntry)r.Payload).ToList();

            var total = volumes.Sum(v => v.SizeBytes);
            var free = volumes.Sum(v => v.FreeBytes);

            return new List<ToolKpi>
            {
                new(disks.Count.ToString(), "物理磁盘", Palette.KnownHex),
                new(volumes.Count.ToString(), "卷", Palette.KnownHex),
                new(total >= 1L << 40 ? $"{total / 1024.0 / 1024 / 1024 / 1024:0.#} TB" : $"{total / 1024.0 / 1024 / 1024:0} GB", "总容量", Palette.KnownHex),
                new(free >= 1L << 40 ? $"{free / 1024.0 / 1024 / 1024 / 1024:0.#} TB" : $"{free / 1024.0 / 1024 / 1024:0} GB", "可用", Palette.OkHex)
            };
        };

        page.RowActions.Add(new ToolAction
        {
            Label = "在资源管理器中打开",
            Run = ctx =>
            {
                if (ctx.First?.Payload is DiskInventoryService.VolumeEntry v)
                {
                    LauncherService.RevealInExplorer(v.Letter + "\\");
                    return new ToolActionResult(true, $"已打开 {v.Letter}\\", false);
                }

                return ToolActionResult.Fail("只有「卷」能在资源管理器里打开；物理磁盘请用系统磁盘管理。");
            }
        });

        return page;
    }

    // ---------------- 公共 ----------------

    /// <summary>打开系统原生工具。缺失时给出「可能被精简镜像删除」的明确提示。</summary>
    internal static void OpenNative(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = true,
                Arguments = args is { Length: > 0 } ? string.Join(" ", args) : ""
            };

            Process.Start(psi);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"打不开 {file}。精简镜像经常删掉这类系统工具，可以用本页的信息代替。\n\n{ex.Message}");
        }
    }

    private static string MemoryText(long mb)
        => mb >= 1024 ? $"{mb / 1024.0:0.##} GB" : mb + " MB";

    private static string Shorten(string text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var clean = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return clean.Length <= max ? clean : clean[..max] + "…";
    }
}
