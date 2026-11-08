using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 读取 NTFS USN 日志里的「文件删除」记录。
/// 需要管理员权限（打开卷句柄），非管理员时返回 NeedAdmin，由界面提示提权。
/// </summary>
public static class UsnService
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;

    private const uint FsctlQueryUsnJournal = 0x000900F4;
    private const uint FsctlReadUsnJournal = 0x000900BB;
    private const uint FsctlEnumUsnData = 0x000900B3;

    private const uint ReasonFileDelete = 0x00000200;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, int nInBufferSize, IntPtr lpOutBuffer, int nOutBufferSize,
        out int lpBytesReturned, IntPtr lpOverlapped);

    [StructLayout(LayoutKind.Sequential)]
    private struct UsnJournalData
    {
        public ulong UsnJournalId;
        public long FirstUsn;
        public long NextUsn;
        public long LowestValidUsn;
        public long MaxUsn;
        public ulong MaximumSize;
        public ulong AllocationDelta;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReadUsnJournalData
    {
        public long StartUsn;
        public uint ReasonMask;
        public uint ReturnOnlyOnClose;
        public ulong Timeout;
        public ulong BytesToWaitFor;
        public ulong UsnJournalId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MftEnumData
    {
        public ulong StartFileReferenceNumber;
        public long LowUsn;
        public long HighUsn;
    }

    // ==================== 读取 ====================

    public static UsnResult Read(string drive, DateTime since, int maxRecords = 3000, DateTime? until = null)
    {
        var res = new UsnResult { Drive = drive, Since = since, Until = until };

        if (!LoggingService.IsAdmin())
        {
            res.NeedAdmin = true;
            res.Error = "读取 USN 日志需要管理员权限（要打开磁盘卷句柄）";
            return res;
        }

        SafeFileHandle handle = null;
        try
        {
            handle = CreateFileW($@"\\.\{drive}:", GenericRead, FileShareReadWrite,
                IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

            if (handle.IsInvalid)
            {
                res.Error = "打开卷失败：" + LastError();
                return res;
            }

            var journal = QueryJournal(handle);
            if (journal.NextUsn <= 0)
            {
                res.Error = "这个卷上没有可用的 USN 日志（可能不是 NTFS）";
                return res;
            }

            res.JournalFirstUsn = journal.FirstUsn;
            res.JournalNextUsn = journal.NextUsn;

            var map = BuildNameMap(handle, journal, 250_000, TimeSpan.FromSeconds(6));
            res.IndexCount = map.Count;

            ReadDeleteRecords(handle, journal, since, maxRecords, map, res, until);
            return res;
        }
        catch (Exception ex)
        {
            res.Error = ex.Message;
            return res;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private static UsnJournalData QueryJournal(SafeFileHandle handle)
    {
        var size = Marshal.SizeOf<UsnJournalData>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!DeviceIoControl(handle, FsctlQueryUsnJournal, IntPtr.Zero, 0,
                    buffer, size, out _, IntPtr.Zero))
            {
                return default;
            }
            return Marshal.PtrToStructure<UsnJournalData>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>枚举 MFT，建立 FRN → (名字, 父 FRN) 的索引，用来把删除记录还原成路径。</summary>
    private static Dictionary<ulong, (string Name, ulong Parent)> BuildNameMap(
        SafeFileHandle handle, UsnJournalData journal, int maxEntries, TimeSpan budget)
    {
        var map = new Dictionary<ulong, (string, ulong)>();
        var input = new MftEnumData { StartFileReferenceNumber = 0, LowUsn = 0, HighUsn = journal.NextUsn };

        var inSize = Marshal.SizeOf<MftEnumData>();
        var inBuf = Marshal.AllocHGlobal(inSize);
        const int outSize = 1 << 20;
        var outBuf = Marshal.AllocHGlobal(outSize);
        var sw = Stopwatch.StartNew();

        try
        {
            while (map.Count < maxEntries && sw.Elapsed < budget)
            {
                Marshal.StructureToPtr(input, inBuf, false);

                if (!DeviceIoControl(handle, FsctlEnumUsnData, inBuf, inSize,
                        outBuf, outSize, out var bytes, IntPtr.Zero) || bytes <= 8)
                {
                    break;
                }

                var next = (ulong)Marshal.ReadInt64(outBuf);
                var offset = 8;
                while (offset + 60 <= bytes)
                {
                    var len = Marshal.ReadInt32(outBuf, offset);
                    if (len <= 0 || offset + len > bytes) break;

                    var frn = (ulong)Marshal.ReadInt64(outBuf, offset + 8);
                    var parent = (ulong)Marshal.ReadInt64(outBuf, offset + 16);
                    var nameLen = (int)Marshal.ReadInt16(outBuf, offset + 56);
                    var nameOff = (int)Marshal.ReadInt16(outBuf, offset + 58);

                    if (nameLen > 0 && offset + nameOff + nameLen <= bytes)
                    {
                        var name = Marshal.PtrToStringUni(IntPtr.Add(outBuf, offset + nameOff), nameLen / 2) ?? "";
                        if (name.Length > 0) map[frn] = (name, parent);
                    }

                    offset += len;
                }

                if (next == 0 || next == input.StartFileReferenceNumber) break;
                input.StartFileReferenceNumber = next;
            }
        }
        catch
        {
            // 索引建不全时仍可返回文件名，路径退化为「…\文件名」
        }
        finally
        {
            Marshal.FreeHGlobal(inBuf);
            Marshal.FreeHGlobal(outBuf);
        }

        return map;
    }

    private static void ReadDeleteRecords(SafeFileHandle handle, UsnJournalData journal,
        DateTime since, int maxRecords, Dictionary<ulong, (string Name, ulong Parent)> map, UsnResult res,
        DateTime? until = null)
    {
        var input = new ReadUsnJournalData
        {
            StartUsn = journal.FirstUsn,
            ReasonMask = ReasonFileDelete,
            ReturnOnlyOnClose = 0,
            Timeout = 0,
            BytesToWaitFor = 0,
            UsnJournalId = journal.UsnJournalId
        };

        var inSize = Marshal.SizeOf<ReadUsnJournalData>();
        var inBuf = Marshal.AllocHGlobal(inSize);
        const int outSize = 1 << 20;
        var outBuf = Marshal.AllocHGlobal(outSize);

        string drivePrefix = res.Drive + ":\\";
        var next = journal.FirstUsn;

        try
        {
            while (next < journal.NextUsn && res.Records.Count < maxRecords)
            {
                input.StartUsn = next;
                Marshal.StructureToPtr(input, inBuf, false);

                var ok = DeviceIoControl(handle, FsctlReadUsnJournal, inBuf, inSize,
                    outBuf, outSize, out var bytes, IntPtr.Zero);

                if (!ok)
                {
                    // 一条都没读到就报错；读了一部分再失败，当作结束处理
                    if (res.ScannedRecords == 0) res.Error = "读取 USN 失败：" + LastError();
                    break;
                }

                if (bytes <= 8) break;

                var following = Marshal.ReadInt64(outBuf);
                var offset = 8;
                while (offset + 60 <= bytes && res.Records.Count < maxRecords)
                {
                    var recordStart = offset;
                    var len = Marshal.ReadInt32(outBuf, offset);
                    if (len <= 0 || offset + len > bytes) break;

                    var frn = (ulong)Marshal.ReadInt64(outBuf, offset + 8);
                    var parent = (ulong)Marshal.ReadInt64(outBuf, offset + 16);
                    var stamp = Marshal.ReadInt64(outBuf, offset + 32);
                    var reason = (uint)Marshal.ReadInt32(outBuf, offset + 40);
                    var nameLen = (int)Marshal.ReadInt16(outBuf, offset + 56);
                    var nameOff = (int)Marshal.ReadInt16(outBuf, offset + 58);

                    offset += len;
                    res.ScannedRecords++;

                    if ((reason & ReasonFileDelete) == 0) continue;
                    if (nameLen <= 0) continue;

                    var name = Marshal.PtrToStringUni(IntPtr.Add(outBuf, recordStart + nameOff), nameLen / 2) ?? "";
                    var time = SafeTime(stamp);
                    if (time < since) continue;
                    if (until.HasValue && time > until.Value) continue;

                    var parentPath = ResolvePath(parent, map, 8);
                    res.Records.Add(new UsnRecord
                    {
                        Time = time,
                        Name = name,
                        Path = parentPath.Length > 0 ? $"{drivePrefix}{parentPath}\\{name}" : name,
                        Reason = "删除",
                        FileId = frn
                    });
                }

                if (following <= 0 || following <= next) break;
                next = following;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inBuf);
            Marshal.FreeHGlobal(outBuf);
        }

        res.Records = res.Records.OrderByDescending(r => r.Time).ToList();
    }

    private static string ResolvePath(ulong frn, Dictionary<ulong, (string Name, ulong Parent)> map, int depth)
    {
        var parts = new List<string>();
        var current = frn;

        while (depth-- > 0 && map.TryGetValue(current, out var entry))
        {
            parts.Add(entry.Name);
            current = entry.Parent;
            if (current == 0) break;
        }

        parts.Reverse();
        return string.Join("\\", parts);
    }

    private static DateTime SafeTime(long fileTime)
    {
        try { return DateTime.FromFileTimeUtc(fileTime).ToLocalTime(); }
        catch { return DateTime.MinValue; }
    }

    private static string LastError() => new Win32Exception(Marshal.GetLastWin32Error()).Message;

    /// <summary>供自检使用：把一段 USC 记录缓冲解析成删除记录（不访问磁盘）。</summary>
    public static List<UsnRecord> ParseForTest(byte[] buffer)
    {
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var ptr = handle.AddrOfPinnedObject();
            var list = new List<UsnRecord>();
            var offset = 8;
            while (offset + 60 <= buffer.Length)
            {
                var len = Marshal.ReadInt32(ptr, offset);
                if (len <= 0 || offset + len > buffer.Length) break;

                var stamp = Marshal.ReadInt64(ptr, offset + 32);
                var reason = (uint)Marshal.ReadInt32(ptr, offset + 40);
                var nameLen = (int)Marshal.ReadInt16(ptr, offset + 56);
                var nameOff = (int)Marshal.ReadInt16(ptr, offset + 58);

                if ((reason & ReasonFileDelete) != 0 && nameLen > 0)
                {
                    list.Add(new UsnRecord
                    {
                        Time = SafeTime(stamp),
                        Name = Marshal.PtrToStringUni(IntPtr.Add(ptr, offset + nameOff), nameLen / 2) ?? "",
                        Reason = "删除"
                    });
                }

                offset += len;
            }
            return list;
        }
        finally
        {
            handle.Free();
        }
    }

    // ==================== 报告 ====================

    public static ReportPayload BuildReport(UsnResult result)
    {
        var html = HtmlReportBuilder.BuildUsn(result);
        var path = HtmlReportBuilder.Save(html, "文件删除记录");

        var sb = new StringBuilder();
        sb.AppendLine($"【文件删除记录】{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"磁盘 {result.Drive}:　时间范围：{result.Since:yyyy-MM-dd HH:mm} 之后");

        if (result.NeedAdmin)
        {
            sb.AppendLine("读取失败：需要管理员权限。");
        }
        else if (result.Error.Length > 0)
        {
            sb.AppendLine("读取失败：" + result.Error);
        }
        else
        {
            sb.AppendLine($"扫描 {result.ScannedRecords:N0} 条 USN 记录，其中删除 {result.Records.Count:N0} 条。");
            sb.AppendLine();
            foreach (var r in result.Records.Take(40))
                sb.AppendLine($"  {r.TimeText}  {r.Path}");
            if (result.Records.Count > 40) sb.AppendLine($"  …… 共 {result.Records.Count} 条，完整列表见报告");
            sb.AppendLine();
            sb.AppendLine("说明：USN 日志按容量滚动覆盖，更早的记录已经不存在，查不到不代表没删过。");
        }

        return new ReportPayload
        {
            Title = "文件删除记录",
            Subtitle = result.NeedAdmin
                ? "需要管理员权限"
                : result.Error.Length > 0
                    ? "读取失败"
                    : $"{result.Drive}: · 删除 {result.Records.Count:N0} 条 · 扫描 {result.ScannedRecords:N0} 条",
            HtmlPath = path,
            PlainSummary = sb.ToString()
        };
    }

    /// <summary>命令行入口（由界面提权后调用）：--usn C 24。</summary>
    public static string RunFromArgs(string drive, int hours)
    {
        var since = hours <= 0 ? DateTime.Now.AddYears(-20) : DateTime.Now.AddHours(-hours);
        var result = Read(drive, since);

        var text = new StringBuilder();
        text.AppendLine($"读取 USN 日志 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine($"管理员权限：{(LoggingService.IsAdmin() ? "是" : "否")}");
        text.AppendLine($"磁盘：{drive}:　范围：{(hours <= 0 ? "全部" : $"最近 {hours} 小时")}");

        if (result.NeedAdmin || (result.Error.Length > 0 && result.Records.Count == 0))
        {
            text.AppendLine("结果：失败 - " + result.Error);
        }
        else
        {
            var payload = BuildReport(result);
            if (result.Error.Length > 0) text.AppendLine("注意：" + result.Error + "（已读到部分记录）");
            text.AppendLine($"USN 范围：{result.JournalFirstUsn} → {result.JournalNextUsn}　MFT 索引：{result.IndexCount:N0} 项");
            text.AppendLine($"结果：删除 {result.Records.Count:N0} 条 / 扫描 {result.ScannedRecords:N0} 条");
            text.AppendLine("报告：" + payload.HtmlPath);
        }

        try
        {
            AppPaths.Ensure();
            File.WriteAllText(Path.Combine(AppPaths.OutputDir, "usn-result.txt"), text.ToString(), new UTF8Encoding(true));
        }
        catch { }

        return text.ToString();
    }
}
