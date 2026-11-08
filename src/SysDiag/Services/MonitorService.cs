using System.Diagnostics;
using System.IO;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>监控期间发生的一件事。</summary>
public sealed class MonitorEvent
{
    public DateTime Time { get; init; } = DateTime.Now;

    /// <summary>来源：监控 / 游戏 / 句柄 / 模块 / 进程。</summary>
    public string Source { get; init; } = "";

    /// <summary>warn / hint / ok。</summary>
    public string Level { get; init; } = "hint";

    public string Text { get; init; } = "";

    public string Detail { get; init; } = "";

    public string TimeText => Time.ToString("HH:mm:ss");

    public string LevelText => Level switch
    {
        "warn" => "高危",
        "hint" => "提示",
        _ => "正常"
    };
}

/// <summary>一次挂机监控会话。</summary>
public sealed class MonitorSession
{
    public int TargetPid { get; set; }
    public string TargetName { get; set; } = "";
    public string TargetPath { get; set; } = "";

    /// <summary>是否按名字自动锁定（游戏重启后跟随新进程）。</summary>
    public bool AutoFollow { get; set; } = true;

    public DateTime StartedAt { get; set; } = DateTime.Now;
    public DateTime? EndedAt { get; set; }
    public int IntervalSec { get; set; } = 5;
    public int Ticks { get; set; }

    /// <summary>监控期间是否看到目标进程在运行。</summary>
    public bool GameSeen { get; set; }

    /// <summary>监控期间目标是否中途退出过。</summary>
    public bool GameExited { get; set; }

    /// <summary>跟随重启的次数（目标 PID 变化）。</summary>
    public int TargetSwitches { get; set; }

    /// <summary>采样耗时统计（毫秒）。</summary>
    public double AvgSampleMs { get; set; }
    public double MaxSampleMs { get; set; }

    public List<MonitorEvent> Events { get; } = new();

    /// <summary>事件上限：长时间挂机也不会无限涨。</summary>
    public const int MaxEvents = 5000;

    public int WarnCount => Events.Count(e => e.Level == "warn");
    public int HintCount => Events.Count(e => e.Level == "hint");

    public string DurationText
    {
        get
        {
            var span = (EndedAt ?? DateTime.Now) - StartedAt;
            return span.TotalHours >= 1
                ? $"{(int)span.TotalHours} 小时 {span.Minutes} 分"
                : $"{(int)span.TotalMinutes} 分 {span.Seconds} 秒";
        }
    }

    /// <summary>锁定目标的一句话描述。</summary>
    public string TargetText => TargetPid > 0
        ? $"{TargetName}（PID {TargetPid}）"
        : string.IsNullOrEmpty(TargetName) ? "未锁定目标" : $"等待 {TargetName} 启动";

    public string Verdict => WarnCount > 0
        ? $"监控期间发现 {WarnCount} 条高危迹象"
        : HintCount > 0
            ? $"监控期间有 {HintCount} 条提示，未见高危"
            : "监控期间未见异常";

    public string VerdictLevel => WarnCount > 0 ? "高" : HintCount > 0 ? "中" : "低";

    public string VerdictColor => WarnCount > 0 ? Palette.WarnHex : HintCount > 0 ? Palette.HintHex : Palette.OkHex;

    public string PerfText => Ticks == 0
        ? "—"
        : $"采样 {Ticks} 次 · 平均 {AvgSampleMs:0} ms · 峰值 {MaxSampleMs:0} ms";
}

/// <summary>
/// 挂机监控：让被查的人自己开监控、自己打游戏，程序按间隔采样。
/// 资源占用控制：
/// 1) 句柄全表枚举每 3 次采样做一次，模块每 2 次一次，进程快照每次只取 pid + 名字；
/// 2) 目标没在运行时把间隔放大到 3 倍（最多 30 秒），不做无意义高频空转；
/// 3) 句柄扫描复用同一块缓冲，稳态下没有大对象分配。
/// </summary>
public sealed class MonitorRunner : IDisposable
{
    private CancellationTokenSource? _cts;

    public MonitorSession Session { get; private set; } = new();

    public bool IsRunning => _cts is { IsCancellationRequested: false };

    /// <summary>每产生一条事件回调一次（后台线程，界面需自行切回 UI 线程）。</summary>
    public event Action<MonitorEvent>? EventRaised;

    /// <summary>每采样一次回调一次（可用于刷新时长/采样数）。</summary>
    public event Action<MonitorSession>? Ticked;

    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string> _processes = new();
    private readonly HashSet<string> _modules = new(StringComparer.OrdinalIgnoreCase);

    private double _totalMs;

    /// <summary>
    /// 开始监控。targetPid 传 0 表示「等这个游戏启动」——按名字找，找到就锁定。
    /// </summary>
    public void Start(string targetName, int targetPid, int intervalSec, bool autoFollow = true)
    {
        Stop();

        Session = new MonitorSession
        {
            TargetName = targetName,
            TargetPid = Math.Max(0, targetPid),
            IntervalSec = Math.Max(3, intervalSec),
            AutoFollow = autoFollow,
            StartedAt = DateTime.Now
        };

        _seen.Clear();
        _modules.Clear();
        _processes.Clear();
        _totalMs = 0;

        if (Session.TargetPid <= 0 && autoFollow)
            Session.TargetPid = FindByName(targetName);

        if (Session.TargetPid > 0)
            Session.TargetPath = PathOf(Session.TargetPid);

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _ = Task.Run(() => LoopAsync(token), token);
    }

    public MonitorSession Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;

        if (Session.EndedAt == null)
        {
            Session.EndedAt = DateTime.Now;
            Add(new MonitorEvent
            {
                Source = "监控",
                Level = "ok",
                Text = "监控结束",
                Detail = $"{Session.Ticks} 次采样 · {Session.Events.Count} 条事件 · 平均 {Session.AvgSampleMs:0} ms"
            });
        }

        return Session;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        // 初始快照：先记「本来就有的」，之后只报增量
        SnapshotProcesses(first: true);
        if (Session.TargetPid > 0) SnapshotModules();

        while (!ct.IsCancellationRequested)
        {
            var latency = Session.IntervalSec;

            try
            {
                var elapsed = Sample();
                Session.Ticks++;

                _totalMs += elapsed;
                Session.AvgSampleMs = _totalMs / Session.Ticks;
                Session.MaxSampleMs = Math.Max(Session.MaxSampleMs, elapsed);

                Ticked?.Invoke(Session);

                // 目标没在运行：放慢到 3 倍间隔（最多 30 秒）
                if (Session.TargetPid <= 0)
                    latency = Math.Min(30, Math.Max(5, Session.IntervalSec * 3));
            }
            catch (Exception ex)
            {
                Add(new MonitorEvent
                {
                    Source = "监控",
                    Level = "hint",
                    Text = "本次采样失败",
                    Detail = ex.Message
                });
            }

            try { await Task.Delay(TimeSpan.FromSeconds(latency), ct); }
            catch { break; }
        }
    }

    /// <summary>一次采样，返回耗时（毫秒）。</summary>
    private double Sample()
    {
        var sw = Stopwatch.StartNew();

        if (Session.Ticks == 0)
        {
            Add(new MonitorEvent
            {
                Source = "监控",
                Level = "ok",
                Text = $"开始监控 {Session.TargetName}",
                Detail = Session.TargetPid > 0
                    ? $"已锁定 {Session.TargetText} · 每 {Session.IntervalSec} 秒采样"
                    : $"目标未启动，等待 {Session.TargetName} 出现后自动锁定"
            });
        }

        // 1) 解析 / 跟随目标
        var alive = Session.TargetPid > 0 && IsAlive(Session.TargetPid);

        if (!alive && Session.AutoFollow)
        {
            var pid = FindByName(Session.TargetName);

            if (pid > 0)
            {
                var switched = Session.TargetPid > 0;
                Session.TargetPid = pid;
                if (switched) Session.TargetSwitches++;

                Session.TargetPath = PathOf(pid);
                RebindModules();

                Add(new MonitorEvent
                {
                    Source = "游戏",
                    Level = "ok",
                    Text = switched
                        ? $"已跟随新进程：{Session.TargetName}（PID {pid}）"
                        : $"已锁定 {Session.TargetName}（PID {pid}）",
                    Detail = switched ? "游戏重启或换了客户端，监控已接到新进程上" : "监控开始前就已在运行"
                });

                alive = true;
            }
            else if (!Session.GameExited && Session.TargetPid > 0)
            {
                Session.GameExited = true;
                Add(new MonitorEvent
                {
                    Source = "游戏",
                    Level = "hint",
                    Text = $"{Session.TargetName} 已退出",
                    Detail = "继续等待，游戏重新启动后会自动接上"
                });
            }
        }

        if (alive)
        {
            if (!Session.GameSeen)
            {
                Session.GameSeen = true;
                Add(new MonitorEvent { Source = "游戏", Level = "ok", Text = $"{Session.TargetName} 正在运行" });
            }

            // 2) 分级采样：模块每 2 次、句柄每 3 次（这两项最贵）
            if (Session.Ticks % 2 == 0) SnapshotModules(diff: true);
            if (Session.Ticks % 3 == 0) CheckHandles();
        }

        // 3) 系统新进程（每次都做，但路径只对新出现的进程读）
        SnapshotProcesses();

        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    /// <summary>跟随到新进程：模块基线要重建，否则旧进程的模块会被当成「新加载」刷屏。</summary>
    private void RebindModules()
    {
        _modules.Clear();
        _seen.Clear();
        SnapshotModules();
    }

    /// <summary>游戏进程模块：新出现的模块通常意味着 DLL 注入。</summary>
    private void SnapshotModules(bool diff = false)
    {
        var fresh = new List<(string Name, string File)>();

        try
        {
            using var p = Process.GetProcessById(Session.TargetPid);
            foreach (ProcessModule m in p.Modules)
            {
                var file = m.FileName;
                if (string.IsNullOrEmpty(file)) continue;

                if (!_modules.Add(file)) continue;

                if (diff) fresh.Add((m.ModuleName ?? Path.GetFileName(file), file));
            }
        }
        catch { return; }

        if (!diff) return;

        foreach (var (name, file) in fresh.Take(8))
        {
            var temp = file.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase)
                       || file.Contains(@"\Downloads\", StringComparison.OrdinalIgnoreCase);

            Add(new MonitorEvent
            {
                Source = "模块",
                Level = temp ? "warn" : "hint",
                Text = $"游戏进程新加载模块：{name}",
                Detail = file
            });
        }
    }

    /// <summary>句柄：谁新打开了游戏进程。</summary>
    private void CheckHandles()
    {
        var hs = HandleScanService.Scan(Session.TargetPid, Session.TargetName);

        foreach (var f in hs.Findings.Where(x => !x.Known))
        {
            var key = $"handle:{f.Pid}:{(f.Risk == "warn" ? "w" : "r")}";
            if (!_seen.Add(key)) continue;

            Add(new MonitorEvent
            {
                Source = "句柄",
                Level = f.Risk == "warn" ? "warn" : "hint",
                Text = $"{f.Name}（{f.Pid}）打开了游戏进程",
                Detail = f.FlagsText
            });
        }
    }

    /// <summary>
    /// 系统进程快照：只取 pid + 名字；**路径只对新出现的进程读**
    /// （读路径要 OpenProcess，几百个进程每次都读很贵）。
    /// </summary>
    private void SnapshotProcesses(bool first = false)
    {
        var current = new List<(int Pid, string Name)>();

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id <= 4 || p.Id == Environment.ProcessId) continue;
                current.Add((p.Id, p.ProcessName));
            }
            catch { }
            finally { p.Dispose(); }
        }

        if (first)
        {
            foreach (var (pid, name) in current) _processes[pid] = name;
            return;
        }

        foreach (var (pid, name) in current)
        {
            if (_processes.ContainsKey(pid)) continue;
            _processes[pid] = name;

            // 系统组件 / 反作弊不算新进程噪音
            if (HandleScanService.IdentifyOwner(name).Length > 0) continue;

            var path = PathOf(pid);
            var temp = path.Length > 0
                       && (path.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase)
                           || path.Contains(@"\Downloads\", StringComparison.OrdinalIgnoreCase));

            Add(new MonitorEvent
            {
                Source = "进程",
                Level = temp ? "warn" : "hint",
                Text = $"新进程：{name}（{pid}）",
                Detail = path
            });
        }

        var alive = current.Select(c => c.Pid).ToHashSet();
        foreach (var pid in _processes.Keys.Where(k => !alive.Contains(k)).ToList())
            _processes.Remove(pid);
    }

    // ---------------- 目标发现 ----------------

    /// <summary>按名字找进程：先精确名，再用游戏识别目录兜底。</summary>
    private static int FindByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0;

        try
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                var id = p.Id;
                p.Dispose();
                return id;
            }
        }
        catch { }

        try
        {
            var game = GameProcessCatalog.DetectRunningGames()
                .FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

            return game?.Pid ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsAlive(int pid)
    {
        if (pid <= 0) return false;

        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static string PathOf(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.MainModule?.FileName ?? "";
        }
        catch
        {
            return "";
        }
    }

    private void Add(MonitorEvent e)
    {
        Session.Events.Add(e);

        // 长时间挂机不让事件无限涨：超上限丢最早的 500 条
        if (Session.Events.Count > MonitorSession.MaxEvents)
            Session.Events.RemoveRange(0, 500);

        EventRaised?.Invoke(e);
    }

    public void Dispose() => Stop();
}
