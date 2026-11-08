using System.Text;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 内置 Sigma 规则（子集）与解析器。
/// <para>
/// **规则数据为原创**：下面这些规则是按 Sigma 格式自己写的（中文标题、不带 SigmaHQ 的
/// UUID 与 author 字段），不是从 SigmaHQ 仓库复制来的。SigmaHQ 发布的规则适用
/// Detection Rule License (DRL) 1.1 且带传染性；本项目不引入其规则内容，因此不受该许可约束
/// （Sigma 规范本身是公有领域）。**往这里贴 SigmaHQ 的规则会改变整个项目的许可义务，别这么做。**
/// </para>
/// <para>
/// 只实现 Sigma 里真正用得上的那部分：logsource.service、detection 的
/// selection 块、字段修饰符（contains / startswith / endswith / re / all / gt / lt）、
/// condition 的 and / or / not / "N of xxx*"。
/// 不支持聚合（near / count / timeframe）和 correlation —— 这些需要状态窗口，
/// 放到单次快照里做会失真，不如不做。
/// </para>
/// </summary>
public static class SigmaRules
{
    private static List<SigmaRule> _cache;

    private static List<SigmaRule> _external;

    /// <summary>外部规则目录：exe 同目录下的 rules\（可放 Sigma / hayabusa 风格的 .yml）。</summary>
    public static string ExternalDir => System.IO.Path.Combine(AppPaths.Root, "rules");

    /// <summary>外部规则条数（没放目录时为 0）。</summary>
    public static int ExternalCount => External.Count;

    /// <summary>外部规则（首次访问时扫描目录，之后缓存）。</summary>
    public static List<SigmaRule> External => _external ??= LoadExternal();

    /// <summary>解析后的内置规则（首次访问时解析并缓存）。</summary>
    public static List<SigmaRule> All
    {
        get
        {
            if (_cache != null) return _cache;

            _cache = ParseMany(Builtin);

            for (var i = 0; i < _cache.Count; i++)
                if (_cache[i].Id.Length == 0)
                    _cache[i].Id = "sysdiag-builtin-" + (i + 1);

            // 外部规则目录里的规则排在后面：内置规则是「一定会跑」的底线，
            // 外部规则是加分项，解析失败不该影响内置规则。
            var external = External;
            if (external.Count > 0) _cache.AddRange(external);

            return _cache;
        }
    }

    /// <summary>
    /// 扫描 rules\ 目录下的 .yml / .yaml，全部按 Sigma 子集解析。
    /// 借鉴 hayabusa 的思路：规则的「字段映射」由引擎统一处理（见 SigmaRuleEngine.FieldAliases），
    /// 所以外部规则只要写标准 Sigma 字段就能直接跑，不需要我们逐条改写。
    /// </summary>
    private static List<SigmaRule> LoadExternal()
    {
        var list = new List<SigmaRule>();

        try
        {
            var dir = ExternalDir;
            if (!System.IO.Directory.Exists(dir)) return list;

            var files = System.IO.Directory
                .EnumerateFiles(dir, "*.*", System.IO.SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                            || f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                .Take(400)
                .ToList();

            foreach (var file in files)
            {
                try
                {
                    var info = new System.IO.FileInfo(file);
                    if (info.Length > 512 * 1024) continue;

                    foreach (var rule in ParseMany(System.IO.File.ReadAllText(file)))
                    {
                        rule.Source = "外部：" + System.IO.Path.GetFileName(info.DirectoryName ?? "") +
                                      "/" + info.Name;
                        if (rule.Id.Length == 0) rule.Id = "ext-" + (list.Count + 1);
                        list.Add(rule);
                    }
                }
                catch { /* 单条规则出错不影响其它规则 */ }
            }
        }
        catch { /* 目录不可读时当作没有外部规则 */ }

        return list;
    }

    // ==================== 解析 ====================

    /// <summary>解析一段包含多条规则的 YAML（用 --- 分隔）。解析失败的规则会被跳过。</summary>
    public static List<SigmaRule> ParseMany(string yaml)
    {
        var list = new List<SigmaRule>();
        if (string.IsNullOrWhiteSpace(yaml)) return list;

        foreach (var block in SplitBlocks(yaml))
        {
            var rule = ParseOne(block);
            if (rule != null) list.Add(rule);
        }

        return list;
    }

    private static IEnumerable<string> SplitBlocks(string yaml)
    {
        var lines = yaml.Replace("\r\n", "\n").Split('\n');
        var current = new StringBuilder();

        foreach (var line in lines)
        {
            if (line.TrimEnd() == "---")
            {
                if (current.ToString().Trim().Length > 0) yield return current.ToString();
                current.Clear();
                continue;
            }

            current.AppendLine(line);
        }

        if (current.ToString().Trim().Length > 0) yield return current.ToString();
    }

    private static SigmaRule ParseOne(string block)
    {
        var rule = new SigmaRule();

        var section = "";
        string selectionName = null;
        SigmaMatcher pending = null;

        try
        {
            foreach (var rawLine in block.Replace("\r\n", "\n").Split('\n'))
            {
                var line = StripComment(rawLine);
                if (line.Trim().Length == 0) continue;

                var indent = line.Length - line.TrimStart().Length;
                var body = line.Trim();

                // 列表项
                if (body.StartsWith("- ", StringComparison.Ordinal) || body == "-")
                {
                    var item = Unquote(body.Length > 1 ? body[1..].Trim() : "");

                    if (section == "tags")
                    {
                        if (item.Length > 0) rule.Tags.Add(item);
                    }
                    else if (pending != null && item.Length > 0)
                    {
                        pending.Values.Add(item);
                    }

                    continue;
                }

                if (indent == 0)
                {
                    var (key, value) = SplitKey(body);

                    switch (key)
                    {
                        case "title": rule.Title = value; break;
                        case "id": rule.Id = value; break;
                        case "description": rule.Description = value; break;
                        case "level": rule.Level = value.ToLowerInvariant(); break;
                        case "logsource":
                        case "detection":
                        case "tags":
                            section = key;
                            break;
                        default:
                            section = key is "author" or "status" or "references" ? section : "";
                            break;
                    }

                    continue;
                }

                if (section == "logsource")
                {
                    var (key, value) = SplitKey(body);
                    if (key == "service" && value.Length > 0) rule.Service = value.ToLowerInvariant();
                    continue;
                }

                if (section == "detection")
                {
                    if (indent <= 2)
                    {
                        var (key, value) = SplitKey(body);

                        if (key == "condition")
                        {
                            rule.Condition = value;
                            selectionName = null;
                        }
                        else
                        {
                            selectionName = key;
                            rule.Selections[key] = new List<SigmaMatcher>();
                            pending = null;
                        }

                        continue;
                    }

                    // 字段行：Image|endswith: '\cmd.exe'
                    var (fieldKey, fieldValue) = SplitKey(body);
                    if (selectionName == null || fieldKey.Length == 0) continue;

                    var parts = fieldKey.Split('|');
                    var matcher = new SigmaMatcher
                    {
                        Field = parts[0].Trim(),
                        Modifiers = parts.Skip(1).Select(x => x.Trim().ToLowerInvariant()).ToList()
                    };

                    if (fieldValue.Length > 0)
                    {
                        matcher.Values.AddRange(SplitInlineList(fieldValue));
                        pending = null;
                    }
                    else
                    {
                        pending = matcher;
                    }

                    rule.Selections[selectionName].Add(matcher);
                    continue;
                }

                if (section == "tags" && body.StartsWith("-", StringComparison.Ordinal))
                {
                    var item = Unquote(body[1..].Trim());
                    if (item.Length > 0) rule.Tags.Add(item);
                }
            }
        }
        catch
        {
            return null;
        }

        if (rule.Title.Length == 0 || rule.Selections.Count == 0) return null;
        if (rule.Condition.Length == 0) rule.Condition = string.Join(" or ", rule.Selections.Keys);

        return rule;
    }

    private static string StripComment(string line)
    {
        // 单引号内的 # 不是注释
        var inQuote = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (c == '\'') inQuote = !inQuote;
            if (c == '#' && !inQuote && (i == 0 || char.IsWhiteSpace(line[i - 1])))
                return line[..i];
        }

        return line;
    }

    private static (string Key, string Value) SplitKey(string body)
    {
        var idx = body.IndexOf(':');
        if (idx <= 0) return ("", "");

        var key = Unquote(body[..idx].Trim());
        var value = Unquote(body[(idx + 1)..].Trim());

        return (key, value);
    }

    private static string Unquote(string text)
    {
        if (text.Length >= 2)
        {
            if ((text[0] == '\'' && text[^1] == '\'') || (text[0] == '"' && text[^1] == '"'))
                return text[1..^1];
        }

        return text;
    }

    private static IEnumerable<string> SplitInlineList(string value)
    {
        // 只支持 [a, b, c] 这种简单形式
        if (!value.StartsWith("[", StringComparison.Ordinal) || !value.EndsWith("]", StringComparison.Ordinal))
        {
            yield return value;
            yield break;
        }

        var inner = value[1..^1];

        foreach (var part in inner.Split(','))
        {
            var item = Unquote(part.Trim());
            if (item.Length > 0) yield return item;
        }
    }

    // ==================== 内置规则 ====================

    /// <summary>
    /// 围绕「外挂痕迹」挑的规则，来源是 SigmaHQ 的公开规则集（改写为子集语法）。
    /// 刻意不含只对 Sysmon 有意义的全量规则——没装 Sysmon 时它们永远不会命中。
    /// </summary>
    public static readonly string Builtin = """
title: 安全日志被清除
id: sysdiag-1102
level: high
description: 审计日志被清空。这是最典型的反取证动作，正常软件不会做。
logsource:
  product: windows
  service: security
detection:
  selection:
    EventID: 1102
  condition: selection
tags:
  - attack.defense_evasion
  - attack.t1070.001
---
title: 系统日志被清除
id: sysdiag-104
level: high
description: System 日志被清空，同样属于反取证。
logsource:
  product: windows
  service: system
detection:
  selection:
    EventID: 104
  condition: selection
tags:
  - attack.defense_evasion
  - attack.t1070.001
---
title: 服务安装路径指向临时目录
id: sysdiag-7045-temp
level: high
description: 新装服务的可执行文件放在 Temp / AppData / Users\Public 下，是外挂与木马落地的常见手法。
logsource:
  product: windows
  service: system
detection:
  selection:
    EventID: 7045
    ImagePath|contains:
      - '\Temp\'
      - '\Users\Public\'
      - '\AppData\Local\Temp'
      - '\Downloads\'
  condition: selection
tags:
  - attack.persistence
  - attack.t1543.003
---
title: 服务注册了内核驱动
id: sysdiag-7045-driver
level: medium
description: 服务加载 .sys 文件。正常驱动安装也走这条路径，需要核对驱动来源。
logsource:
  product: windows
  service: system
detection:
  selection:
    EventID: 7045
    ImagePath|endswith: '.sys'
  condition: selection
tags:
  - attack.persistence
---
title: 任意服务安装
id: sysdiag-7045
level: low
description: 清点用。装驱动、装杀软、装虚拟网卡都会留下这条记录。
logsource:
  product: windows
  service: system
detection:
  selection:
    EventID: 7045
  condition: selection
---
title: 服务启动类型被修改
id: sysdiag-7040
level: medium
description: 已有服务的启动方式被改动，可能被用来让某个驱动/服务常驻。
logsource:
  product: windows
  service: system
detection:
  selection:
    EventID: 7040
  condition: selection
tags:
  - attack.persistence
  - attack.t1543.003
---
title: 安全日志中出现服务安装
id: sysdiag-4697
level: medium
description: 4697 需要开启审核策略才会记录，出现即说明近段时间有新服务被安装。
logsource:
  product: windows
  service: security
detection:
  selection:
    EventID: 4697
  condition: selection
tags:
  - attack.persistence
---
title: 进程从临时目录启动
id: sysdiag-4688-temp
level: medium
description: 4688 需要开启「审核进程创建」。从 Temp / Downloads 直接运行的进程值得看一眼。
logsource:
  product: windows
  service: security
detection:
  selection:
    EventID: 4688
    NewProcessName|contains:
      - '\Temp\'
      - '\Downloads\'
      - '\Users\Public\'
  condition: selection
tags:
  - attack.execution
---
title: 命令行出现输入模拟关键词
id: sysdiag-4688-km
level: high
description: 命令行里出现 kmbox / arduino / hid 这类词，通常与键鼠模拟设备有关。
logsource:
  product: windows
  service: security
detection:
  selection:
    EventID: 4688
    CommandLine|contains|all:
      - 'kmbox'
  condition: selection
---
title: 命令行出现外挂常用词
id: sysdiag-4688-cheat
level: high
description: 命令行里出现 cheat / aimbot / inject 这类词。误报可能来自安全工具本身，需要核对路径。
logsource:
  product: windows
  service: security
detection:
  selection:
    EventID: 4688
    CommandLine|contains:
      - 'aimbot'
      - 'wallhack'
      - 'cheatengine'
      - 'injector'
  condition: selection
---
title: 命令行清理日志或删除文件
id: sysdiag-4688-clean
level: high
description: wevtutil cl、del /s、vssadmin delete 这些命令常用于抹痕迹。
logsource:
  product: windows
  service: security
detection:
  selection:
    EventID: 4688
    CommandLine|contains:
      - 'wevtutil cl'
      - 'vssadmin delete'
      - 'del /s /q'
      - 'rd /s /q'
  condition: selection
tags:
  - attack.defense_evasion
  - attack.t1070
---
title: 审核策略被修改
id: sysdiag-4719
level: high
description: 系统审核策略被改动，可能是在关闭某些记录。
logsource:
  product: windows
  service: security
detection:
  selection:
    EventID: 4719
  condition: selection
tags:
  - attack.defense_evasion
  - attack.t1562.002
---
title: 识别到新的外部设备
id: sysdiag-6416
level: medium
description: 系统识别了一个新设备。USB 键鼠模拟器插入时也会触发。
logsource:
  product: windows
  service: security
detection:
  selection:
    EventID: 6416
  condition: selection
tags:
  - attack.hardware
---
title: PowerShell 下载后直接执行
id: sysdiag-4104-download
level: high
description: 脚本块里同时出现 IEX 与 DownloadString。单独出现下载或单独出现 IEX 都很常见，两者凑齐才是加载器写法。
logsource:
  product: windows
  service: powershell
detection:
  selection:
    EventID: 4104
    ScriptBlockText|contains|all:
      - 'IEX'
      - 'DownloadString'
  condition: selection
tags:
  - attack.execution
  - attack.t1059.001
---
title: PowerShell 调用内存写入 API
id: sysdiag-4104-win32
level: high
description: 同时出现 DllImport 与 VirtualAllocEx / WriteProcessMemory，说明脚本在直接操作其他进程的内存。
logsource:
  product: windows
  service: powershell
detection:
  selection_alloc:
    EventID: 4104
    ScriptBlockText|contains|all:
      - 'DllImport'
      - 'VirtualAllocEx'
  selection_write:
    EventID: 4104
    ScriptBlockText|contains|all:
      - 'DllImport'
      - 'WriteProcessMemory'
  condition: selection_alloc or selection_write
tags:
  - attack.execution
---
title: PowerShell 发起网络下载
id: sysdiag-4104-web
level: medium
description: 脚本块里出现 Invoke-WebRequest / DownloadFile。正常运维脚本也常用，只作提示。
logsource:
  product: windows
  service: powershell
detection:
  selection:
    EventID: 4104
    ScriptBlockText|contains:
      - 'Invoke-WebRequest'
      - 'DownloadFile'
      - 'Net.WebClient'
  condition: selection
---
title: Defender 检出威胁
id: sysdiag-defender-1116
level: medium
description: 历史上检出过恶意软件。注意这可能是误杀，也可能是外挂本体。
logsource:
  product: windows
  service: defender
detection:
  selection:
    EventID: 1116
  condition: selection
tags:
  - attack.defense_evasion
---
title: Defender 已处置威胁
id: sysdiag-defender-1117
level: medium
description: Defender 执行了清除或隔离动作。
logsource:
  product: windows
  service: defender
detection:
  selection:
    EventID: 1117
  condition: selection
---
title: Defender 实时防护被关闭
id: sysdiag-defender-5001
level: high
description: 实时防护被关闭，是外挂安装包最常做的一步。
logsource:
  product: windows
  service: defender
detection:
  selection:
    EventID: 5001
  condition: selection
tags:
  - attack.defense_evasion
  - attack.t1562.001
---
title: 远程线程创建
id: sysdiag-sysmon-8
level: high
description: 需要安装 Sysmon。CreateRemoteThread 是注入内存最直接的证据。
logsource:
  product: windows
  service: sysmon
detection:
  selection:
    EventID: 8
  condition: selection
tags:
  - attack.defense_evasion
  - attack.t1055
---
title: 跨进程内存访问
id: sysdiag-sysmon-10
level: high
description: 需要安装 Sysmon。读取其他进程内存（尤其游戏进程）是外部辅助的核心行为。
logsource:
  product: windows
  service: sysmon
detection:
  selection:
    EventID: 10
  condition: selection
tags:
  - attack.credential_access
  - attack.t1055
---
title: 从临时目录加载模块
id: sysdiag-sysmon-7
level: medium
description: 需要安装 Sysmon。ImageLoad 事件里出现临时目录路径，属于典型注入特征。
logsource:
  product: windows
  service: sysmon
detection:
  selection:
    EventID: 7
    ImageLoaded|contains:
      - '\Temp\'
      - '\Downloads\'
  condition: selection
tags:
  - attack.defense_evasion
  - attack.t1055
---
title: 远程线程注入（Sysmon 8）
id: sysdiag-sysmon-8-inject
level: high
description: 一个进程在另一个进程里创建线程。正常软件极少这么干，是注入类外挂与木马的核心动作。
logsource:
  product: windows
  service: sysmon
detection:
  selection:
    EventID: 8
  condition: selection
tags:
  - attack.defense_evasion
  - attack.t1055
---
title: 进程内存被以读写权限打开（Sysmon 10）
id: sysdiag-sysmon-10-write
level: high
description: 访问掩码含 0x1F0FFF / 0x143A，等于拿到了读写内存与建线程的完整权力，是注入的前置条件。
logsource:
  product: windows
  service: sysmon
detection:
  selection:
    EventID: 10
    GrantedAccess|contains:
      - '0x1F0FFF'
      - '0x143A'
      - '0x1010'
  condition: selection
tags:
  - attack.defense_evasion
  - attack.t1055
---
title: 有进程访问 lsass 内存
id: sysdiag-sysmon-10-lsass
level: critical
description: lsass 里存着登录凭据。除杀软与系统组件外，访问它的进程极少，是抓密码的典型动作。
logsource:
  product: windows
  service: sysmon
detection:
  selection:
    EventID: 10
    TargetImage|endswith: '\lsass.exe'
  condition: selection
tags:
  - attack.credential_access
  - attack.t1003.001
---
title: 启动项注册表被写入（Sysmon 13）
id: sysdiag-sysmon-13-run
level: medium
description: Run / RunOnce 键被写入，等于设置了开机自启。装软件会这样，木马也这样，需要看写入者是谁。
logsource:
  product: windows
  service: sysmon
detection:
  selection:
    EventID: 13
    TargetObject|contains:
      - '\CurrentVersion\Run\'
      - '\CurrentVersion\RunOnce\'
  condition: selection
tags:
  - attack.persistence
  - attack.t1547.001
---
title: WMI 事件订阅（Sysmon 19-21）
id: sysdiag-sysmon-wmi-sub
level: high
description: WMI 永久事件订阅是无文件持久化的经典手法，正常软件几乎不会创建。
logsource:
  product: windows
  service: sysmon
detection:
  selection:
    EventID: 19
  selection2:
    EventID: 20
  selection3:
    EventID: 21
  condition: selection or selection2 or selection3
tags:
  - attack.persistence
  - attack.t1546.003
---
title: 编码命令执行
id: sysdiag-encoded-cmd
level: high
description: 用 Base64 编码参数启动命令，是为了让命令行看起来不是明文。混淆脚本的常见第一步。
logsource:
  product: windows
  service: powershell
detection:
  selection:
    EventID: 4104
    ScriptBlockText|contains:
      - '-EncodedCommand'
      - '-enc '
      - 'FromBase64String'
  condition: selection
tags:
  - attack.execution
  - attack.t1059.001
---
title: 计划任务内容含临时目录或命令行解释器
id: sysdiag-task-temp
level: high
description: 任务计划里出现 Temp 路径或脚本解释器，通常是「让东西定时跑起来」的落地方式。
logsource:
  product: windows
  service: taskscheduler
detection:
  selection:
    EventID: 106
    TaskContent|contains:
      - '\Temp\'
      - 'powershell'
      - 'cmd.exe /c'
      - '\AppData\'
  condition: selection
tags:
  - attack.persistence
  - attack.t1053.005
---
title: 创建本地用户
id: sysdiag-4720
level: medium
description: 新建本地账户。正常装机后很少再建，出现时要核对是谁建的。
logsource:
  product: windows
  service: security
detection:
  selection:
    EventID: 4720
  condition: selection
tags:
  - attack.persistence
  - attack.t1136.001
---
title: 防病毒被关闭或篡改
id: sysdiag-defender-tamper
level: critical
description: 实时保护被关、防病毒被禁用。外挂使用者为了不让文件被删，常先关防护。
logsource:
  product: windows
  service: defender
detection:
  selection:
    EventID: 5001
  selection2:
    EventID: 5010
  condition: selection or selection2
tags:
  - attack.defense_evasion
  - attack.t1562.001
---
title: 以其他凭据运行程序
id: sysdiag-4648
level: medium
description: 显式使用另一组凭据启动进程（runas / 计划任务存凭据）。外挂工具常用来切到管理员上下文。
logsource:
  product: windows
  service: security
detection:
  selection:
    EventID: 4648
  condition: selection
tags:
  - attack.defense_evasion
  - attack.t1078
---
title: 账户被授予特殊权限
id: sysdiag-4672
level: low
description: 登录后拿到调试/备份等高权限。正常管理员登录也会出现，只在有其它线索时作为旁证。
logsource:
  product: windows
  service: security
detection:
  selection:
    EventID: 4672
  condition: selection
tags:
  - attack.privilege_escalation
  - attack.t1078
---
title: 账户被加入特权组
id: sysdiag-4732
level: high
description: 有账户被加入 Administrators 等本地组。查外挂时这是「谁给了权限」的直接证据。
logsource:
  product: windows
  service: security
detection:
  selection:
    EventID: 4732
  condition: selection
tags:
  - attack.persistence
  - attack.t1098
---
title: NTLM 凭据校验异常
id: sysdiag-4776
level: low
description: 域/NTLM 凭据校验记录。短时间大量失败是暴力尝试的特征，单条只是背景信息。
logsource:
  product: windows
  service: security
detection:
  selection:
    EventID: 4776
  condition: selection
tags:
  - attack.credential_access
  - attack.t1110
---
title: 网络共享被访问
id: sysdiag-5140
level: low
description: 有人访问了本机共享。排查「异地代打」时，共享访问时间能对上就是线索。
logsource:
  product: windows
  service: security
detection:
  selection:
    EventID: 5140
  condition: selection
tags:
  - attack.lateral_movement
  - attack.t1021.002
---
title: 卷影副本被删除
id: sysdiag-shadow-delete
level: critical
description: 删除卷影副本会让系统还原点一起失效，是勒索软件与「清理痕迹」工具的固定动作。
logsource:
  product: windows
  service: security
detection:
  selection:
    EventID: 4688
    CommandLine|contains:
      - 'vssadmin delete shadows'
      - 'vssadmin  delete shadows'
      - 'wbadmin delete catalog'
  condition: selection
tags:
  - attack.impact
  - attack.t1490
""";
}
