using SysDiag.Models;
using System.IO;

namespace SysDiag.Services;

public static class CardCatalog
{
    private const string Blue = "#4C6EF5", BlueTint = "#EDF2FF";
    private const string Orange = "#F08C00", OrangeTint = "#FFF4E6";
    private const string Pink = "#D6336C", PinkTint = "#FFF0F6";
    private const string Purple = "#7048E8", PurpleTint = "#F3F0FF";
    private const string Teal = "#0C8599", TealTint = "#E3FAFC";
    private const string Green = "#2F9E44", GreenTint = "#EBFBEE";
    private const string Red = "#C92A2A", RedTint = "#FFF5F5";
    private const string Gray = "#868E96", GrayTint = "#F1F3F5";
    private const string Indigo = "#364FC7", IndigoTint = "#EDF2FF";

    /// <summary>
    /// 卡片目录。分组按「用户想干什么」排，不按技术模块排——
    /// 同一组里的卡片效果相近，找东西时不用先想「这功能属于哪个子系统」。
    ///
    /// 顺序也是刻意的：先看（体检）、再清（腾空间）、再调（提速），
    /// 然后是玩（游戏）、查（日志）、修（网络）、验（取证），最后是目录快捷方式。
    /// </summary>
    public static List<CardGroup> Build()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(userProfile, "Downloads");
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var groups = new List<CardGroup>();

        // ---------------------------------------------------------------- 看一眼
        groups.Add(Group("体检与硬件", Blue, BlueTint,
            Card("系统体检", "硬件总览", "diag", "system-checkup").WithIcon("shield"),
            Card("磁盘健康", "SMART 状态", "diag", "disk-health").WithIcon("disk"),
            Card("系统信息", "软件内查看·含精简判定", "diag", "system-info").WithIcon("cpu"),
            Card("已装更新", "补丁清单", "diag", "update-list").WithIcon("cloud-down"),
            Card("设备管理器", "设备·驱动·问题代码", "diag", "devices").WithIcon("devices"),
            Card("显卡信息", "型号·驱动·显存", "diag", "gpu-info"),
            Card("磁盘管理", "磁盘·分区·占用", "diag", "disks").WithIcon("disk"),
            Card("设备安装记录", "驱动安装/卸载", "diag", "driver-history"),
            Card("USB 设备清单", "型号·序列号·端口", "diag", "usb-history"),
            Card("PnP 事件分析", "设备启动失败", "diag", "pnp-report")));

        // ---------------------------------------------------------------- 腾空间
        groups.Add(Group("清理与空间", Orange, OrangeTint,
            Card("C 盘清理", "自动扫描·自选释放", "diag", "disk-clean").WithIcon("sparkle"),
            Card("大文件管理", "按大小找·删进回收站", "diag", "big-files").WithIcon("folder"),
            Card("重复文件", "按内容比对·删到回收站", "diag", "duplicates").WithIcon("compare"),
            Card("日志清理", "扫描并回收空间", "diag", "cleanup-logs"),
            Card("注册表清理", "只删孤儿项·先备份", "diag", "registry-clean").WithIcon("key")));

        // ---------------------------------------------------------------- 调设置
        groups.Add(Group("系统优化", Indigo, IndigoTint,
            Card("系统优化", "82 项开关·逐条可还原", "diag", "system-optimize").WithIcon("sliders"),
            Card("系统调优", "Game Bar·GPU·CPU", "diag", "system-tune").WithIcon("gear"),
            Card("服务", "启停·改启动类型", "diag", "services").WithIcon("gear"),
            Card("服务预设", "批量禁用·可还原", "diag", "service-preset").WithIcon("sliders"),
            Card("开机自启动", "当前用户 / 计划任务", "diag", "autostart").WithIcon("rocket"),
            Card("启动项", "开机自启", "shell", "shell:startup").WithIcon("rocket"),
            Card("电源选项", "切换高性能", "diag", "power-plans").WithIcon("power"),
            Card("程序和功能", "已装软件·可卸载", "diag", "installed-apps").WithIcon("apps"),
            Card("免重装精简", "预装应用·遥测任务", "diag", "slim").WithIcon("broom"),
            Card("优化推论", "结论·前提·依据·限制", "diag", "notes").WithIcon("book")));

        // ---------------------------------------------------------------- 玩与看
        groups.Add(Group("游戏与显示", Pink, PinkTint,
            Card("游戏优化", "缓存·着色器·一键调优", "diag", "delta-cache").WithIcon("bolt"),
            Card("延迟优化", "时钟·MSI·外设节能", "diag", "latency").WithIcon("bolt"),
            Card("CPU 停核", "关闭停核·禁用空闲", "diag", "core-parking").WithIcon("cpu"),
            Card("延迟实测", "改前改后对照", "diag", "latency-lab").WithIcon("chart"),
            Card("DLSS 模型", "预设 K·画质档位", "diag", "dlss").WithIcon("sparkle"),
            Card("直播伴侣优化", "降优先级·限核心", "diag", "live-optimizer"),
            Card("显示器配色", "ICC·导入导出", "diag", "icc").WithIcon("eye")));

        // ---------------------------------------------------------------- 查日志
        groups.Add(Group("日志与报告", Green, GreenTint,
            Card("事件查看器", "内置·全部事件", "diag", "eventviewer").WithIcon("scroll"),
            Card("可靠性监视器", "稳定性指数·崩溃史", "diag", "reliability").WithIcon("chart"),
            Card("错误与警告", "近 3 天报告", "diag", "error-report").WithIcon("alert"),
            Card("查找游戏日志", "全盘扫描 Saved\\Logs", "diag", "find-logs"),
            Card("分析日志包", "选 zip 分析", "diag", "analyze-zip"),
            Card("分析日志目录", "选文件夹→自选文件", "diag", "analyze-folder"),
            Card("分析日志文件", "多选 .log / .evtx", "diag", "analyze-files"),
            Card("报告对比", "导入·双份对比", "diag", "report-compare")));

        // ---------------------------------------------------------------- 修网络
        groups.Add(Group("网络", Teal, TealTint,
            Card("网络连接", "IP·网关·DNS", "diag", "adapters").WithIcon("wifi"),
            Card("网络诊断", "丢包与延时", "diag", "network-report"),
            Card("断网急救", "体检·重置·修复", "diag", "net-repair"),
            Card("DNS 优化", "测速·应用·还原", "diag", "dns"),
            Card("修改网卡 MAC", "换网卡地址", "diag", "mac-set"),
            Card("恢复网卡 MAC", "还原硬件地址", "diag", "mac-restore"),
            Card("防火墙", "规则清单·只读", "diag", "firewall").WithIcon("shield")));

        // ---------------------------------------------------------------- 看进程
        groups.Add(Group("性能与进程", Purple, PurpleTint,
            Card("内存", "占用排行·释放缓存", "diag", "memory").WithIcon("box"),
            Card("任务管理器", "实时进程·可结束", "diag", "processes").WithIcon("activity"),
            Card("资源监视器", "CPU·内存·句柄", "diag", "processes").WithIcon("line-chart"),
            Card("性能监视器", "实时曲线·来源进程", "diag", "processes").WithIcon("chart"),
            Card("进程句柄排查", "谁在读游戏进程", "diag", "handle").WithIcon("search")));

        // ---------------------------------------------------------------- 做取证
        groups.Add(Group("安全与取证", Red, RedTint,
            Card("外挂痕迹体检", "八项合一·统一报告", "diag", "cheat-scan").WithIcon("shield"),
            Card("挂机监控", "自己打·自动出报告", "diag", "monitor"),
            Card("日志规则匹配", "Sigma 子集·事件日志", "diag", "sigma"),
            Card("本地执行痕迹", "Prefetch·ShimCache", "diag", "prefetch"),
            Card("深度硬件痕迹", "DMA·采集卡·输入模拟", "diag", "deep-trace"),
            Card("程序执行记录", "AmCache·BAM", "diag", "exec-trace"),
            Card("文件删除记录", "USN 删除日志", "diag", "usn"),
            Card("机器码", "出厂校验", "diag", "machine"),
            Card("机器码伪造检测", "不用登记·查改机痕迹", "diag", "hwid-integrity").WithIcon("shield"),
            Card("设备比对", "存基线·查变化", "diag", "baseline"),
            Card("防护状态审查", "谁关了我的杀软", "diag", "defender-review").WithIcon("shield"),
            Card("日志记录开关", "打开系统/运行日志", "diag", "logging-switch"),
            Card("一键证据包", "打包 + SHA256", "diag", "evidence-pack").WithIcon("archive")));

        // ---------------------------------------------------------------- 跳目录
        groups.Add(Group("常用目录", Gray, GrayTint,
            Card("报告目录", "outputs", "folder", AppPaths.OutputDir).WithIcon("folder"),
            Card("最近使用", "Recent", "shell", "shell:recent").WithIcon("clock"),
            Card("下载", "Downloads", "folder", downloads).WithIcon("cloud-down"),
            Card("AppData", "程序数据", "folder", appData).WithIcon("folder"),
            Card("回收站", "已删除", "shell", "shell:RecycleBinFolder").WithIcon("trash"),
            Card("Prefetch", "启动痕迹", "folder", Path.Combine(win, "Prefetch")).WithIcon("clock"),
            Card("事件日志文件", "EVTX 目录", "folder", Path.Combine(win, "System32", "winevt", "Logs")).WithIcon("scroll"),
            Card("更新日志", "WindowsUpdate", "folder", Path.Combine(win, "Logs", "WindowsUpdate")).WithIcon("cloud-down"),
            Card("蓝屏转储", "Minidump", "folder", Path.Combine(win, "Minidump")).WithIcon("alert")));

        return groups;
    }

    private static CardGroup Group(string name, string accent, string tint, params CardItem[] items)
    {
        foreach (var it in items)
        {
            it.AccentHex = accent;
            it.TintHex = tint;
        }
        return new CardGroup { Name = name, AccentHex = accent, TintHex = tint, Items = items.ToList() };
    }

    private static CardItem Card(string name, string desc, string kind, string target, params string[] args)
        => new() { Name = name, Desc = desc, Kind = kind, Target = target, Args = args ?? Array.Empty<string>() };
}
