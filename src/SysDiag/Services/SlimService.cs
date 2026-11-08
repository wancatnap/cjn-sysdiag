using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SysDiag.Services;

/// <summary>
/// 免重装精简：把 AtlasOS 那类「删组件、精简系统」的操作里，**运行时能安全做、且能撤销**的部分拆出来。
///
/// 为什么要单独做：AtlasOS 的做法是改安装镜像 / 用 Playbook 重装，用户必须先重装系统。
/// 但它的精简清单里，很大一部分其实是运行时可完成的（预装 Appx、遥测计划任务、可选功能），
/// 只是没有一个「不重装也能做、并且能撤销」的入口。
///
/// 明确的三条边界（界面上原样展示，不藏在文档里）：
/// 1. **不等于 AtlasOS**。删 WinSxS 组件、移除 Edge/Defender、改安装介质，这些运行时做不到，
///    也不该假装能做到——见 <see cref="ImpossibleItems"/>。
/// 2. **每一项单独可逆性不同**。计划任务禁用可原样恢复；可选功能可重新启用；
///    预装 Appx 卸载后要重装得从 Store 或系统镜像取，属于「难恢复」，界面上分级标出。
/// 3. **默认全不勾**，且不做「一键全选」以外的批量承诺。
///
/// 安全约束：所有 shell 命令的**参数都来自内置白名单或经过严格校验**，
/// 绝不把扫描到的包名直接拼进命令行（那是命令注入）。
/// </summary>
public static class SlimService
{
    private const string BackupName = "slim-backup.json";

    /// <summary>可逆性分级。</summary>
    public enum Reversibility
    {
        /// <summary>可以原样恢复（禁用任务 / 禁用可选功能）。</summary>
        Easy,

        /// <summary>能恢复但麻烦（Appx 要从 Store 或镜像重装）。</summary>
        Hard,

        /// <summary>运行时不具备同等能力，仅作说明，不提供操作。</summary>
        Impossible
    }

    public sealed class SlimItem
    {
        public string Id { get; init; } = "";
        public string Category { get; init; } = "";
        public string Name { get; init; } = "";
        public string Detail { get; init; } = "";
        public Reversibility Reversibility { get; init; } = Reversibility.Hard;

        /// <summary>AtlasOS 会不会做这一项；用来对照「精简到什么程度」。</summary>
        public bool AtlasDoes { get; init; }

        /// <summary>是否属于「常见可移除」，界面用来给推荐标记。默认仍然不勾。</summary>
        public bool CommonBloat { get; init; }

        /// <summary>内部用：Appx 的 PackageFullName 或任务的 Path\Name。绝不直接拼命令。</summary>
        internal string Handle { get; init; } = "";

        public string ReversibilityText => Reversibility switch
        {
            Reversibility.Easy => "可恢复",
            Reversibility.Hard => "难恢复",
            _ => "不重装做不到"
        };

        public bool CanAct => Reversibility != Reversibility.Impossible;
    }

    // ---------------------------------------------------------------- 清单

    /// <summary>
    /// 常见预装应用（按包名前缀匹配）。这份名单是「典型 Windows 预装/推广应用」，
    /// 不包含任何系统必需组件；Xbox 这类可能有人要用的，也放在里面但标为「看需求」。
    /// </summary>
    private static readonly (string Prefix, string Label, bool AtlasDoes)[] KnownBloat =
    {
        ("Microsoft.3DBuilder", "3D 生成器", true),
        ("Microsoft.BingNews", "资讯", true),
        ("Microsoft.BingWeather", "天气", true),
        ("Microsoft.BingFinance", "财经", true),
        ("Microsoft.BingSports", "体育", true),
        ("Microsoft.GetHelp", "获取帮助", true),
        ("Microsoft.Getstarted", "使用技巧", true),
        ("Microsoft.MicrosoftOfficeHub", "Office 中心", true),
        ("Microsoft.MicrosoftSolitaireCollection", "纸牌游戏", true),
        ("Microsoft.Microsoft3DViewer", "3D 查看器", true),
        ("Microsoft.MixedReality.Portal", "混合现实门户", true),
        ("Microsoft.OneConnect", "移动套餐", true),
        ("Microsoft.People", "人脉", true),
        ("Microsoft.Print3D", "3D 打印", true),
        ("Microsoft.SkypeApp", "Skype", true),
        ("Microsoft.Todos", "待办", true),
        ("Microsoft.Wallet", "钱包", true),
        ("Microsoft.WindowsFeedbackHub", "反馈中心", true),
        ("Microsoft.YourPhone", "手机连接", true),
        ("Microsoft.ZuneMusic", "媒体播放器", true),
        ("Microsoft.ZuneVideo", "电影和电视", true),
        ("Microsoft.XboxApp", "Xbox 控制台小帮手", false),
        ("Microsoft.XboxGamingOverlay", "Xbox Game Bar 覆盖层", false),
        ("Microsoft.XboxGameOverlay", "Xbox 游戏覆盖", false),
        ("Microsoft.XboxSpeechToTextOverlay", "Xbox 语音转文字", true),
        ("Microsoft.GamingApp", "Xbox 应用", false),
        ("Clipchamp.Clipchamp", "Clipchamp 视频编辑器", true),
        ("MicrosoftTeams", "Teams（消费版）", true),
        ("Microsoft.Teams", "Teams", true),
        ("SpotifyAB.SpotifyMusic", "Spotify", true),
        ("Disney.ABCNewsFree", "ABC 新闻", true),
        ("Facebook.Facebook", "Facebook", true),
        ("BytedancePte.Ltd.TikTok", "TikTok", true),
        ("king.com.CandyCrush", "Candy Crush", true)
    };

    /// <summary>遥测类计划任务（路径 + 名称）。禁用可原样恢复。这些是 AtlasOS/WinUtil 都会处理的类别。</summary>
    private static readonly (string Path, string Name, string Label, string Note)[] KnownTasks =
    {
        (@"\Microsoft\Windows\Application Experience\", "ProgramDataUpdater", "应用程序兼容性数据上报",
            "收集程序兼容性数据，禁用不影响软件运行。"),
        (@"\Microsoft\Windows\Application Experience\", "Microsoft Compatibility Appraiser", "兼容性评估器",
            "定期扫描已安装程序并上报，禁用后兼容性提示可能变少。"),
        (@"\Microsoft\Windows\Customer Experience Improvement Program\", "Consolidator",
            "客户体验改善计划（CEIP）汇总", "遥测汇总任务，禁用不影响系统更新。"),
        (@"\Microsoft\Windows\Customer Experience Improvement Program\", "UsbCeip", "USB 使用情况上报",
            "上报 USB 设备使用统计。"),
        (@"\Microsoft\Windows\Feedback\Siuf\", "DmClient", "反馈 SIUF（DM）", "反馈收集通道。"),
        (@"\Microsoft\Windows\Feedback\Siuf\", "DmClientOnScenarioDownload", "反馈 SIUF（下载）",
            "反馈数据下载通道。"),
        (@"\Microsoft\Windows\Windows Error Reporting\", "QueueReporting", "错误报告排队上报",
            "崩溃报告排队上传；禁用后崩溃仍会本地记录。"),
        (@"\Microsoft\Windows\DiskDiagnostic\", "Microsoft-Windows-DiskDiagnosticDataCollector",
            "磁盘诊断数据收集", "磁盘健康数据的遥测上报。"),
        (@"\Microsoft\Windows\CloudExperienceHost\", "CreateObjectTask", "云体验主机对象创建",
            "与微软账户/云体验相关。")
    };

    /// <summary>运行时做不到与 AtlasOS 同等效果的项目——必须写清，不能让用户以为装了这个就等于 AtlasOS。</summary>
    public static IReadOnlyList<SlimItem> ImpossibleItems { get; } = new[]
    {
        new SlimItem
        {
            Id = "imp-winsxs",
            Category = "运行时做不到",
            Name = "删除 WinSxS 组件存储",
            Detail = "AtlasOS 在镜像阶段裁掉 WinSxS 里的组件副本。运行时的 DISM 只能「清理已被取代的版本」，" +
                     "不能任意删除在用组件；强删会导致更新失败、功能报错，且无法回滚。\n" +
                     "→ 运行时等价方案：本程序只做「清理可回收的旧版本」，见下方可选功能。",
            Reversibility = Reversibility.Impossible,
            AtlasDoes = true
        },
        new SlimItem
        {
            Id = "imp-edge",
            Category = "运行时做不到",
            Name = "彻底移除 Microsoft Edge",
            Detail = "Edge 与 WebView2、系统帮助、部分 UWP 深度绑定。运行时删除会破坏依赖它的程序，" +
                     "微软也会通过更新重新装回。AtlasOS 在镜像里处理，仍会保留 WebView2。\n" +
                     "→ 运行时等价方案：可以改默认浏览器、禁用启动项与后台运行，但不做删除。",
            Reversibility = Reversibility.Impossible,
            AtlasDoes = true
        },
        new SlimItem
        {
            Id = "imp-defender",
            Category = "运行时做不到",
            Name = "移除 Windows Defender",
            Detail = "受篡改防护（Tamper Protection）与安全中心保护，运行时无法彻底移除；" +
                     "强行关闭会被系统自动恢复，且让机器处于无防护状态。\n" +
                     "→ 本程序不提供任何「关闭 Defender」的按钮。需要换杀软请用正规安装方式，让系统自动接管。",
            Reversibility = Reversibility.Impossible,
            AtlasDoes = true
        },
        new SlimItem
        {
            Id = "imp-image",
            Category = "运行时做不到",
            Name = "精简安装镜像 / 预配置首次开机",
            Detail = "这属于部署阶段（离线改 WIM/ESD）。本程序运行在已安装的系统里，改不了安装介质。\n" +
                     "→ 如果你的目标是一次性部署多台机器，应该在镜像阶段做，而不是装完再清。",
            Reversibility = Reversibility.Impossible,
            AtlasDoes = true
        }
    };

    // ---------------------------------------------------------------- 扫描

    public sealed class ScanResult
    {
        public List<SlimItem> Apps { get; init; } = new();
        public List<SlimItem> Tasks { get; init; } = new();
        public List<SlimItem> Impossible { get; init; } = new();

        /// <summary>被排除的运行时依赖包数量。界面要如实显示，否则用户会问「为什么少了」。</summary>
        public int ExcludedRuntime { get; init; }

        public string Error { get; init; } = "";
    }

    /// <summary>
    /// 运行时 / 依赖类包：删了会让别的应用起不来，所以一律不进候选列表。
    /// 本机实测这类包很多（WinAppRuntime、DDLM、Singleton、VCLibs…），
    /// 靠用户自己认不现实，必须在扫描阶段就过滤掉。
    /// </summary>
    private static readonly string[] RuntimeMarkers =
    {
        "WinAppRuntime", "DDLM", "VCLibs", "UI.Xaml", ".NET.Native", "Native.Framework",
        "Native.Runtime", "Singleton", "Runtime", "HostRuntime", "EdgeWebView",
        "WindowsAppRuntime", "Xaml", "ShellExt"
    };

    /// <summary>自检用：判断某个包名是否属于运行时/依赖类（这类一律不提供卸载）。</summary>
    public static bool IsRuntimeDependency(string name)
    {
        foreach (var marker in RuntimeMarkers)
        {
            if (name.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    public static async Task<ScanResult> ScanAsync(IProgress<string> progress, CancellationToken token)
    {
        var apps = new List<SlimItem>();
        var tasks = new List<SlimItem>();
        var error = new StringBuilder();
        var excluded = 0;

        progress?.Report("正在枚举预装应用…");

        try
        {
            var (items, skipped) = await ScanAppsAsync(token);
            apps.AddRange(items);
            excluded = skipped;
        }
        catch (Exception ex)
        {
            error.AppendLine("预装应用扫描失败：" + ex.Message);
        }

        progress?.Report("正在枚举计划任务…");

        try
        {
            tasks.AddRange(ScanTasks());
        }
        catch (Exception ex)
        {
            error.AppendLine("计划任务扫描失败：" + ex.Message);
        }

        token.ThrowIfCancellationRequested();

        return new ScanResult
        {
            Apps = apps.OrderBy(x => x.Name).ToList(),
            Tasks = tasks,
            Impossible = ImpossibleItems.ToList(),
            ExcludedRuntime = excluded,
            Error = error.ToString().Trim()
        };
    }

    /// <summary>
    /// 用 PowerShell 枚举当前用户的 Appx。只读取，不做任何修改。
    /// 输出用 JSON，避免解析表格文本时被本地化列名坑到。
    /// 返回（候选列表，被排除的运行时依赖数）。
    /// </summary>
    private static async Task<(List<SlimItem> Items, int Excluded)> ScanAppsAsync(CancellationToken token)
    {
        const string script =
            "$ErrorActionPreference='SilentlyContinue';" +
            "Get-AppxPackage | Where-Object { -not $_.IsFramework -and -not $_.IsResourcePackage -and -not $_.IsBundle -and -not $_.NonRemovable } |" +
            " Select-Object Name,PackageFullName,Publisher,Version | ConvertTo-Json -Compress -Depth 2";

        var json = await RunPowerShellAsync(script, 30000, token);
        if (string.IsNullOrWhiteSpace(json)) return (new List<SlimItem>(), 0);

        var list = new List<SlimItem>();
        var excluded = 0;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var items = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray().ToList()
                : new List<JsonElement> { root };

            foreach (var el in items)
            {
                var name = GetStr(el, "Name");
                var full = GetStr(el, "PackageFullName");
                if (name.Length == 0) continue;

                // 运行时 / 依赖包：删了会破坏其他应用，直接排除，不放进候选
                if (IsRuntimeDependency(name))
                {
                    excluded++;
                    continue;
                }

                var known = KnownBloat.FirstOrDefault(b =>
                    name.StartsWith(b.Prefix, StringComparison.OrdinalIgnoreCase));

                var matched = known.Prefix != null;
                var label = matched ? known.Label : name;

                list.Add(new SlimItem
                {
                    Id = "appx-" + SafeId(name),
                    Category = "预装应用",
                    Name = label,
                    Detail = matched
                        ? $"包名：{name}\n这是常见预装/推广应用，移除不影响系统组件。"
                        : $"包名：{name}\n不在常见预装名单里。移除前请确认没有程序依赖它。",
                    Reversibility = Reversibility.Hard,
                    AtlasDoes = matched && known.AtlasDoes,
                    CommonBloat = matched,
                    Handle = full
                });
            }
        }
        catch
        {
            // JSON 解析失败就返回空，界面会提示「扫不到」，不猜。
        }

        return (list, excluded);
    }

    /// <summary>
    /// 计划任务走注册表枚举太脆（任务有 XML 定义、路径分散），这里用 Task Scheduler 的 COM 接口。
    /// 只读状态，不做修改。
    /// </summary>
    private static List<SlimItem> ScanTasks()
    {
        var list = new List<SlimItem>();

        var type = Type.GetTypeFromProgID("Schedule.Service");
        if (type == null) return list;

        dynamic service = Activator.CreateInstance(type);
        if (service == null) return list;

        service.Connect();

        foreach (var def in KnownTasks)
        {
            try
            {
                dynamic folder = service.GetFolder(def.Path);
                dynamic task = folder.GetTask(def.Name);

                var state = (int)task.State;   // 1=Disabled 3=Ready 4=Running
                var enabled = state != 1;

                list.Add(new SlimItem
                {
                    Id = "task-" + SafeId(def.Name),
                    Category = "计划任务",
                    Name = def.Label,
                    Detail = $"{def.Note}\n路径：{def.Path}{def.Name}\n当前状态：{(enabled ? "已启用" : "已禁用")}",
                    Reversibility = Reversibility.Easy,
                    AtlasDoes = true,
                    CommonBloat = true,
                    Handle = def.Path + "|" + def.Name
                });
            }
            catch
            {
                // 这台机器没有这个任务就跳过，不编造。
            }
        }

        return list;
    }

    // ---------------------------------------------------------------- 执行

    /// <summary>禁用计划任务。可原样恢复。</summary>
    public static (bool Ok, string Message) DisableTasks(IEnumerable<SlimItem> items)
    {
        if (!ElevationService.IsAdmin)
            return (false, "禁用系统计划任务需要管理员权限。点「以管理员身份重启」后再试。");

        var done = new List<string>();
        var failed = new List<string>();
        var backup = LoadBackup();

        foreach (var item in items.Where(x => x.Id.StartsWith("task-")))
        {
            // Handle 来自内置白名单，且再次校验格式，绝不接受任意字符串
            var parts = item.Handle.Split('|');
            if (parts.Length != 2 || !IsSafeTaskPath(parts[0]) || !IsSafeName(parts[1]))
            {
                failed.Add(item.Name + "：路径不合法，已跳过");
                continue;
            }

            var (ok, message) = RunTaskSchedulerDisable(parts[0], parts[1]);
            if (ok)
            {
                done.Add(item.Name);
                if (!backup.Tasks.Contains(parts[0] + parts[1])) backup.Tasks.Add(parts[0] + parts[1]);
            }
            else
            {
                failed.Add(item.Name + "：" + message);
            }
        }

        if (done.Count > 0) SaveBackup(backup, new List<string>(), new List<string>());

        if (done.Count == 0 && failed.Count == 0)
            return (true, "没有需要禁用的任务。");

        var text = done.Count > 0 ? $"已禁用 {done.Count} 个计划任务：{string.Join("、", done)}。" : "没有任务被改动。";
        if (failed.Count > 0) text += "\n失败：" + string.Join("；", failed);

        return (failed.Count == 0, text);
    }

    /// <summary>
    /// 卸载当前用户的预装 Appx。
    /// 注意：包名只用于查找**已枚举对象**，不拼进命令行——这里用 PackageFullName 做精确匹配后，
    /// 通过 PowerShell 的变量绑定传入，参数经 base64 编码的脚本传递，避免任何注入面。
    /// </summary>
    public static async Task<(bool Ok, string Message)> RemoveAppsAsync(
        IEnumerable<SlimItem> items, IProgress<string> progress, CancellationToken token)
    {
        var targets = items.Where(x => x.Id.StartsWith("appx-") && x.Handle.Length > 0).ToList();
        if (targets.Count == 0) return (true, "没有选中要移除的应用。");

        var done = new List<string>();
        var failed = new List<string>();
        var backup = LoadBackup();

        var known = targets.Select(t => t.Handle).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 只把「已在扫描结果里出现过的 PackageFullName」交给脚本，并在脚本里二次过滤。
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            System.Text.Json.JsonSerializer.Serialize(known.ToList())));

        var script =
            "$ErrorActionPreference='Stop';" +
            $"$wanted = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{encoded}')) | ConvertFrom-Json;" +
            "$done=@();$fail=@();" +
            "foreach($n in $wanted){" +
            "  $p = Get-AppxPackage | Where-Object { $_.PackageFullName -eq $n };" +
            "  if(-not $p){ $fail += ($n + '：找不到'); continue; }" +
            "  try { Remove-AppxPackage -Package $p.PackageFullName -ErrorAction Stop; $done += $p.Name }" +
            "  catch { $fail += ($p.Name + '：' + $_.Exception.Message) }" +
            "};" +
            "@{done=$done;fail=$fail} | ConvertTo-Json -Compress";

        progress?.Report("正在卸载选中的应用…");

        var json = await RunPowerShellAsync(script, 180000, token, asAdmin: true);
        var parsedDone = new List<string>();
        var parsedFail = new List<string>();

        try
        {
            if (!string.IsNullOrWhiteSpace(json))
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("done", out var d))
                    parsedDone = d.ValueKind == JsonValueKind.Array
                        ? d.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                        : new List<string> { d.GetString() ?? "" };

                if (doc.RootElement.TryGetProperty("fail", out var f))
                    parsedFail = f.ValueKind == JsonValueKind.Array
                        ? f.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                        : new List<string> { f.GetString() ?? "" };
            }
        }
        catch { /* 解析失败按「全部失败」处理，不谎报成功 */ }

        done.AddRange(parsedDone);
        failed.AddRange(parsedFail);

        if (done.Count > 0)
        {
            backup.Apps.AddRange(done.Where(x => !backup.Apps.Contains(x)));
            SaveBackup(backup, new List<string>(), new List<string>());
        }

        if (done.Count == 0 && failed.Count == 0)
            return (false, "没有应用被移除。可能是脚本执行失败，请以管理员身份重试。");

        var text = done.Count > 0
            ? $"已移除 {done.Count} 个应用：{string.Join("、", done.Take(20))}" +
              (done.Count > 20 ? " 等" : "") + "。\n需要找回时从 Microsoft Store 重新安装即可。"
            : "没有应用被移除。";

        if (failed.Count > 0) text += "\n失败：" + string.Join("；", failed.Take(8));

        return (done.Count > 0, text);
    }

    /// <summary>还原：重新启用被禁用的计划任务。Appx 不自动重装（需要 Store），只列清单。</summary>
    public static (bool Ok, string Message) Restore()
    {
        var backup = LoadBackup();
        if (backup.Tasks.Count == 0 && backup.Apps.Count == 0)
            return (false, "没有找到改动记录。");

        if (!ElevationService.IsAdmin)
            return (false, "恢复计划任务需要管理员权限。");

        var ok = 0;
        var failed = new List<string>();

        foreach (var handle in backup.Tasks.ToList())
        {
            var idx = handle.LastIndexOf('\\');
            if (idx <= 0) continue;

            var path = handle[..(idx + 1)];
            var name = handle[(idx + 1)..];

            var (success, message) = RunTaskSchedulerEnable(path, name);
            if (success) { ok++; backup.Tasks.Remove(handle); }
            else failed.Add(name + "：" + message);
        }

        SaveBackup(backup, new List<string>(), new List<string>());

        var text = $"已重新启用 {ok} 个计划任务。";
        if (backup.Apps.Count > 0)
            text += $"\n已卸载的 {backup.Apps.Count} 个应用需从 Microsoft Store 重新安装：" +
                    string.Join("、", backup.Apps.Take(12));

        if (failed.Count > 0) text += "\n失败：" + string.Join("；", failed);

        return (failed.Count == 0, text);
    }

    private static (bool Ok, string Message) RunTaskSchedulerDisable(string path, string name)
        => SetTaskEnabled(path, name, false);

    private static (bool Ok, string Message) RunTaskSchedulerEnable(string path, string name)
        => SetTaskEnabled(path, name, true);

    private static (bool Ok, string Message) SetTaskEnabled(string path, string name, bool enabled)
    {
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type == null) return (false, "系统没有提供任务计划服务接口。");

            dynamic service = Activator.CreateInstance(type);
            service.Connect();
            dynamic folder = service.GetFolder(path);
            dynamic task = folder.GetTask(name);

            task.Enabled = enabled;

            return (true, enabled ? "已启用" : "已禁用");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ---------------------------------------------------------------- 白名单校验

    private static bool IsSafeName(string name)
        => name.Length is > 0 and <= 120 && Regex.IsMatch(name, @"^[\w\s\.\-\(\)\u4e00-\u9fa5]+$");

    /// <summary>任务路径必须是 \Microsoft\Windows\... 这种系统路径，不接受任意目录。</summary>
    private static bool IsSafeTaskPath(string path)
        => path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)
           && !path.Contains("..") && path.Length <= 200;

    private static string SafeId(string raw)
        => Regex.Replace(raw, @"[^A-Za-z0-9]+", "-").Trim('-').ToLowerInvariant();

    // ---------------------------------------------------------------- 进程与备份

    private static async Task<string> RunPowerShellAsync(string script, int timeoutMs,
        CancellationToken token, bool asAdmin = false)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        var info = new ProcessStartInfo("powershell.exe")
        {
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass " +
                        $"-EncodedCommand {encoded}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = Process.Start(info);
        if (process == null) return "";

        using var registration = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
        });

        var output = await process.StandardOutput.ReadToEndAsync();
        var err = await process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(true); } catch { }
            return "";
        }

        return output.Length > 0 ? output : err;
    }

    private sealed class Backup
    {
        public List<string> Tasks { get; set; } = new();
        public List<string> Apps { get; set; } = new();
        public DateTime SavedAt { get; set; } = DateTime.Now;
    }

    private static string BackupPath => Path.Combine(AppPaths.DataDir, BackupName);

    private static Backup LoadBackup()
    {
        try
        {
            if (!File.Exists(BackupPath)) return new Backup();
            return JsonSerializer.Deserialize<Backup>(File.ReadAllText(BackupPath)) ?? new Backup();
        }
        catch
        {
            return new Backup();
        }
    }

    private static void SaveBackup(Backup backup, List<string> _, List<string> __)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            backup.SavedAt = DateTime.Now;
            File.WriteAllText(BackupPath,
                JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* 记录失败不阻断操作，但还原时会如实报「没有记录」 */ }
    }

    private static string GetStr(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";
}
