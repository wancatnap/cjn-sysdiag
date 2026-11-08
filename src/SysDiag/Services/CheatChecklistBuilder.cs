using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 把体检的十一个采集结果整理成「查一个人有没有开挂要经过哪些检查」的清单。
/// 四层证据链：硬件（有没有作弊硬件）→ 系统（系统有没有被改）→ 运行（现在在做什么）→ 历史（做过什么）。
/// 判定规则统一：数据源不可用 = 未检查（不算通过）；有线索 = 异常；只有提示 = 关注。
/// </summary>
public static class CheatChecklistBuilder
{
    public static CheatChecklist Build(CheatScanResult r)
    {
        var byGroup = r.Findings
            .GroupBy(f => f.Group)
            .ToDictionary(g => g.Key, g => g.ToList());

        var stages = r.Stages
            .GroupBy(s => s.Name)
            .ToDictionary(g => g.Key, g => g.First());

        var items = new List<CheatCheck>
        {
            // ---------------- 硬件层 ----------------
            Make("硬件层", "作弊硬件（DMA 卡 / 采集卡 / 输入模拟）", "深度硬件痕迹", "深度硬件痕迹", byGroup, stages,
                "外挂分软件与硬件两类。DMA 卡、采集卡、键鼠模拟在进程和日志里都看不到，只能从设备与驱动层找。"),

            Make("硬件层", "可疑 USB 设备", "USB 与输入设备", "USB 与输入设备", byGroup, stages,
                "伪装成键鼠的硬件宏、带固件的输入模拟设备会以 USB 设备身份出现。"),

            Make("硬件层", "驱动来源与签名", "驱动与设备", "驱动与设备", byGroup, stages,
                "外挂常带内核驱动；看驱动来源、签名与安装路径是否异常。"),

            // ---------------- 系统层 ----------------
            MakeAntiCheat(byGroup),

            Make("系统层", "日志记录是否被关闭", "审计日志", "审计日志", byGroup, stages,
                "关闭审核策略、清空事件日志是常见的反取证手段，本身就可疑。"),

            Make("系统层", "事件日志规则命中（Sigma 子集）", "日志规则匹配", "日志规则匹配", byGroup, stages,
                "系统日志能查出服务安装、驱动加载、可疑命令执行等痕迹；命中已排除反作弊组件的正常事件。"),

            Make("系统层", "服务与驱动的来源（路径 / 签名）", "服务与驱动来源", "服务与驱动来源", byGroup, stages,
                "内核驱动能直接读写游戏内存。正常驱动在 System32\\drivers 且有微软签名；装在用户目录、文件已丢失或未签名才叫异常。"),

            Make("系统层", "无文件持久化（WMI 订阅 / AppInit_DLLs / IFEO）", "无文件持久化", "无文件持久化", byGroup, stages,
                "这三处能让外挂在重启后继续生效，却不出现在启动项里。正常系统里通常为空。"),

            // ---------------- 运行层 ----------------
            Make("运行层", "隐藏进程（多套 API 枚举差异）", "隐藏进程比对", "隐藏进程比对", byGroup, stages,
                "外挂会 hook 进程枚举让自己从任务管理器消失。用三套互不依赖的机制比对，差集就是「有一方看不到它」。"),

            Make("运行层", "可执行文件的行为特征（导入表）", "文件行为特征", "文件行为特征", byGroup, stages,
                "读写他人内存、远程线程注入、全局键盘钩子——这些能力必须导入对应系统函数。看导入表比看文件名可靠。"),

            Make("运行层", "网络过滤层（WFP / NDIS 轻量过滤）", "网络过滤层", "网络过滤层", byGroup, stages,
                "挂在内核网络栈上的驱动能看、也能改进出本机的数据包。VPN、杀软、虚拟机都会装，所以只清点不下结论。"),

            Make("系统层", "系统完整性（是否精简 / 魔改镜像）", "系统完整性", "系统完整性", byGroup, stages,
                "精简系统会删掉 Defender 平台、部分 WMI 提供程序与 WinRE。不先确认这一点，"
                + "后面那些「读不到」会被误当成「没问题」——这是最容易发生的误判。"),

            Make("系统层", "hosts / 系统代理 / 抓包驱动 / DNS 指向", "网络隐蔽手法", "网络过滤层", byGroup, stages,
                "这四样改一行配置就能生效，不装驱动、不碰游戏进程：hosts 能把域名指到别处，"
                + "代理能让流量走中转，npcap 能读到全部数据包，自建 DNS 效果同 hosts 但本机看不出痕迹。"),

            Make("运行层", "进程注入（来路不明的模块）", "模块完整性", "模块完整性", byGroup, stages,
                "外挂必须把代码塞进游戏进程，必然出现临时目录 DLL 或磁盘上已不存在的模块。"),

            MakeHandles(byGroup, stages),

            Make("运行层", "组合判定（多项线索同时出现）", "组合判定", "", byGroup, stages,
                "单项都能解释的线索同时出现时，可信度明显更高。"),

            // ---------------- 历史层 ----------------
            Make("历史层", "程序执行记录（BAM / AmCache）", "程序执行记录", "程序执行记录", byGroup, stages,
                "能证明某个程序在这台机器上跑过、什么时候跑的。"),

            Make("历史层", "本地执行痕迹（Prefetch / ShimCache）", "本地执行痕迹", "本地执行痕迹", byGroup, stages,
                "Prefetch 记运行次数与路径，ShimCache 记文件是否出现过，用来补执行证据。"),

            Make("历史层", "文件删除痕迹（USN 日志）", "文件删除痕迹", "文件删除痕迹", byGroup, stages,
                "删除记录能看出是否在清理外挂文件；大量集中在短时间内删除更值得追问。")
        };

        return new CheatChecklist { Items = items };
    }

    /// <summary>通用检查项：阶段状态优先（未检查 / 读取失败），再看发现等级。</summary>
    private static CheatCheck Make(string layer, string name, string group, string stageName,
        Dictionary<string, List<CheatFinding>> byGroup, Dictionary<string, CheatStage> stages, string why)
    {
        var stageSkip = StageStatus(stageName, stages);
        if (stageSkip != null) return With(layer, name, "skip", stageSkip, why);

        byGroup.TryGetValue(group, out var found);
        found ??= new List<CheatFinding>();

        var warns = found.Where(f => f.Level == "warn").ToList();
        var hints = found.Where(f => f.Level == "hint").ToList();

        if (warns.Count > 0)
            return With(layer, name, "fail", $"{warns.Count} 条异常：{warns[0].Title}", why);

        if (hints.Count > 0)
            return With(layer, name, "watch", $"{hints.Count} 条提示：{hints[0].Title}", why);

        var stage = stages.TryGetValue(stageName, out var st) ? st : null;
        var scope = stage != null && stage.Items > 0 ? $"（检查范围 {stage.Items:N0}）" : "";

        return With(layer, name, "pass", "未发现异常" + scope, why);
    }

    /// <summary>反作弊组件是正面信号：在跑说明环境未被绕过，但不能反过来当「没开挂」的证据。</summary>
    private static CheatCheck MakeAntiCheat(Dictionary<string, List<CheatFinding>> byGroup)
    {
        const string why = "反作弊在运行说明游戏环境未被绕过，属于正面信号；但它只说明「有防护」，不能证明没开挂。";

        if (!byGroup.TryGetValue("反作弊组件", out var found) || found.Count == 0)
            return With("系统层", "反作弊组件是否在位", "watch",
                "未检测到常见反作弊组件（可能没装游戏、或组件被移除）", why);

        var titles = found.Select(f => f.Title).Distinct().Take(3).ToList();
        return With("系统层", "反作弊组件是否在位", "pass", string.Join("；", titles), why);
    }

    /// <summary>句柄检查单独处理：没游戏进程时是「未检查」，不是「通过」。</summary>
    private static CheatCheck MakeHandles(Dictionary<string, List<CheatFinding>> byGroup,
        Dictionary<string, CheatStage> stages)
    {
        const string why = "能写游戏内存是注入类外挂的必要条件。只能反映扫描那一刻谁开着游戏进程，不等于一直开着。";

        var stageSkip = StageStatus("游戏进程句柄", stages);
        if (stageSkip != null) return With("运行层", "谁在读游戏进程（句柄）", "skip", stageSkip, why);

        byGroup.TryGetValue("游戏进程句柄", out var found);
        found ??= new List<CheatFinding>();

        var warns = found.Where(f => f.Level == "warn").ToList();
        var hints = found.Where(f => f.Level == "hint").ToList();

        if (warns.Count > 0)
            return With("运行层", "谁在读游戏进程（句柄）", "fail", $"{warns.Count} 个未知进程可写内存：{warns[0].Title}", why);

        if (hints.Count > 0)
            return With("运行层", "谁在读游戏进程（句柄）", "watch", $"{hints.Count} 个未知进程可读内存：{hints[0].Title}", why);

        var stage = stages.TryGetValue("游戏进程句柄", out var st) ? st : null;
        return With("运行层", "谁在读游戏进程（句柄）", "pass",
            stage?.Note.Length > 0 ? stage.Note : "只有系统组件与反作弊持有句柄", why);
    }

    /// <summary>阶段不可用时返回原因（skip），可用时返回 null。</summary>
    private static string? StageStatus(string stageName, Dictionary<string, CheatStage> stages)
    {
        if (stageName.Length == 0 || !stages.TryGetValue(stageName, out var st)) return null;

        if (st.State == "failed")
            return "读取失败" + (st.Note.Length > 0 ? "：" + st.Note : "（权限不足或数据源不可用）");

        if (st.State == "skipped")
            return st.Note.Length > 0 ? st.Note : "本次未执行";

        return null;
    }

    private static CheatCheck With(string layer, string name, string result, string evidence, string why) =>
        new()
        {
            Layer = layer,
            Name = name,
            Result = result,
            Evidence = evidence,
            Why = why
        };
}
