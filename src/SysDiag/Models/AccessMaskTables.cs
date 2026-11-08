namespace SysDiag.Models;

/// <summary>
/// 句柄访问掩码解码表。
///
/// 借鉴 System Informer（原 Process Hacker）的 <c>phnt</c> 头文件做法：
/// 它把「标准权限 + 对象类型专属权限 + 泛用权限映射」分成三张表，
/// 解码时先把 GENERIC_* 展开成具体位，再按对象类型查表。
/// 我们原来的实现只认进程专属的那十来个位，有两个真实问题：
/// <list type="bullet">
/// <item>只带 <c>GENERIC_WRITE</c>（0x40000000）的句柄会被解成「—」，看起来人畜无害，
///       实际它有创建线程 + 写内存的权限；</item>
/// <item><c>DELETE</c> / <c>WRITE_DAC</c> / <c>WRITE_OWNER</c> 这类标准权限完全没提，
///       而 WRITE_DAC 意味着可以改对象安全描述符（提权链路上的关键一步）。</item>
/// </list>
/// 这里按 ntddk.h 的通用映射表展开，进程句柄用 PROCESS_GENERIC_READ / WRITE / ALL。
/// </summary>
public static class AccessMaskTables
{
    // ---------------- 标准权限（所有对象通用） ----------------

    public const uint DELETE = 0x00010000;
    public const uint READ_CONTROL = 0x00020000;
    public const uint WRITE_DAC = 0x00040000;
    public const uint WRITE_OWNER = 0x00080000;
    public const uint SYNCHRONIZE = 0x00100000;
    public const uint ACCESS_SYSTEM_SECURITY = 0x01000000;
    public const uint MAXIMUM_ALLOWED = 0x02000000;

    // ---------------- 泛用权限 ----------------

    public const uint GENERIC_ALL = 0x10000000;
    public const uint GENERIC_EXECUTE = 0x20000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint GENERIC_READ = 0x80000000;

    // ---------------- 进程专属 ----------------

    public const uint PROCESS_TERMINATE = 0x0001;
    public const uint PROCESS_CREATE_THREAD = 0x0002;
    public const uint PROCESS_SET_SESSIONID = 0x0004;
    public const uint PROCESS_VM_OPERATION = 0x0008;
    public const uint PROCESS_VM_READ = 0x0010;
    public const uint PROCESS_VM_WRITE = 0x0020;
    public const uint PROCESS_DUP_HANDLE = 0x0040;
    public const uint PROCESS_CREATE_PROCESS = 0x0080;
    public const uint PROCESS_SET_QUOTA = 0x0100;
    public const uint PROCESS_SET_INFORMATION = 0x0200;
    public const uint PROCESS_QUERY_INFORMATION = 0x0400;
    public const uint PROCESS_SUSPEND_RESUME = 0x0800;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>进程的全部权限（不含标准权限位）。</summary>
    public const uint PROCESS_ALL_ACCESS = 0x001FFFFF;

    /// <summary>ntddk 里的 PROCESS_GENERIC_READ：标准读 + 查询信息 + 读内存 + 等待。</summary>
    private const uint ProcessGenericRead = READ_CONTROL | PROCESS_QUERY_INFORMATION | PROCESS_VM_READ | SYNCHRONIZE;

    /// <summary>ntddk 里的 PROCESS_GENERIC_WRITE：标准写 + 建线程 + 写内存 + 改配额/信息。</summary>
    private const uint ProcessGenericWrite = READ_CONTROL | PROCESS_CREATE_THREAD | PROCESS_SET_SESSIONID
        | PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_SET_QUOTA | PROCESS_SET_INFORMATION;

    /// <summary>ntddk 里的 PROCESS_GENERIC_EXECUTE：标准执行 + 同步 + 读配额/信息。</summary>
    private const uint ProcessGenericExecute = READ_CONTROL | SYNCHRONIZE | PROCESS_QUERY_INFORMATION;

    /// <summary>
    /// 把泛用权限位展开成进程专属的具体位。没有泛用位时原样返回。
    /// 注意：内核在打开句柄时通常已经做过一次映射，但通过 <c>DuplicateHandle</c> 复制出来的
    /// 掩码可能是原始泛用值，所以这里必须再展开一次。
    /// </summary>
    public static uint ExpandGeneric(uint mask)
    {
        if ((mask & (GENERIC_ALL | GENERIC_READ | GENERIC_WRITE | GENERIC_EXECUTE)) == 0) return mask;

        var expanded = mask;

        if ((mask & GENERIC_ALL) != 0) expanded |= PROCESS_ALL_ACCESS | SYNCHRONIZE;
        if ((mask & GENERIC_READ) != 0) expanded |= ProcessGenericRead;
        if ((mask & GENERIC_WRITE) != 0) expanded |= ProcessGenericWrite;
        if ((mask & GENERIC_EXECUTE) != 0) expanded |= ProcessGenericExecute;

        return expanded;
    }

    /// <summary>是否有泛用权限位（报告里要单独提示，因为原始掩码看不出具体能力）。</summary>
    public static bool HasGeneric(uint mask)
        => (mask & (GENERIC_ALL | GENERIC_READ | GENERIC_WRITE | GENERIC_EXECUTE)) != 0;

    /// <summary>
    /// 权限位 → 中文说明（按危险度排序，最重要的放前面）。
    /// 返回空列表表示掩码里一个已知位都没有。
    /// </summary>
    public static List<string> DescribeProcess(uint mask)
    {
        var expanded = ExpandGeneric(mask);
        var flags = new List<string>();

        if ((expanded & PROCESS_VM_WRITE) != 0) flags.Add("可写入内存");
        if ((expanded & PROCESS_CREATE_THREAD) != 0) flags.Add("可创建线程");
        if ((expanded & PROCESS_VM_READ) != 0) flags.Add("可读取内存");
        if ((expanded & PROCESS_VM_OPERATION) != 0 && (expanded & (PROCESS_VM_READ | PROCESS_VM_WRITE)) == 0)
            flags.Add("可操作内存");
        if ((expanded & PROCESS_DUP_HANDLE) != 0) flags.Add("可复制句柄");
        if ((expanded & PROCESS_CREATE_PROCESS) != 0) flags.Add("可创建子进程");
        if ((expanded & PROCESS_SUSPEND_RESUME) != 0) flags.Add("可挂起");
        if ((expanded & PROCESS_TERMINATE) != 0) flags.Add("可终止进程");
        if ((expanded & PROCESS_SET_INFORMATION) != 0) flags.Add("可改进程信息");
        if ((expanded & PROCESS_SET_QUOTA) != 0) flags.Add("可改内存配额");

        // 安全描述符相关的两位单独提示：正常监控软件几乎不需要
        if ((expanded & WRITE_DAC) != 0) flags.Add("可改安全描述符");
        if ((expanded & WRITE_OWNER) != 0) flags.Add("可改所有者");

        if (flags.Count == 0 && ((expanded & PROCESS_QUERY_INFORMATION) != 0 || (expanded & PROCESS_QUERY_LIMITED_INFORMATION) != 0))
            flags.Add((expanded & SYNCHRONIZE) != 0 ? "仅查询 · 可等待" : "仅查询");

        return flags;
    }

    /// <summary>掩码里是否带「能改内存或能建线程」的权限（注入的必要条件）。</summary>
    public static bool CanInject(uint mask)
    {
        var expanded = ExpandGeneric(mask);
        return (expanded & (PROCESS_VM_WRITE | PROCESS_CREATE_THREAD)) != 0;
    }

    /// <summary>掩码里是否带「读内存 / 复制句柄 / 挂起 / 建子进程」这类注入链路上的能力。</summary>
    public static bool CanProbe(uint mask)
    {
        var expanded = ExpandGeneric(mask);
        return (expanded & (PROCESS_VM_READ | PROCESS_DUP_HANDLE | PROCESS_SUSPEND_RESUME | PROCESS_CREATE_PROCESS)) != 0;
    }

    /// <summary>是否只有查询类权限。</summary>
    public static bool QueryOnly(uint mask)
    {
        var expanded = ExpandGeneric(mask);
        return !CanInject(expanded) && !CanProbe(expanded)
               && (expanded & (PROCESS_QUERY_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION)) != 0;
    }
}
