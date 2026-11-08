using System.IO;
using System.Runtime.InteropServices;

namespace SysDiag.Services;

/// <summary>把文件/目录移入回收站（可还原），不做永久删除。</summary>
public static class RecycleBin
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    /// <summary>批量移入回收站。返回成功移走的路径；失败项写入 errors。</summary>
    public static List<string> MoveToRecycleBin(IEnumerable<string> paths, List<string> errors)
    {
        var done = new List<string>();

        // 单次最多 10 项，避免一次操作范围过大
        foreach (var chunk in Chunk(paths.Where(Exists).ToList(), 10))
        {
            var from = string.Join("\0", chunk) + "\0\0";
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = from,
                pTo = null,
                fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI)
            };

            var code = SHFileOperation(ref op);
            if (code == 0 && !op.fAnyOperationsAborted)
            {
                done.AddRange(chunk);
            }
            else
            {
                var reason = op.fAnyOperationsAborted ? "操作被中止" : $"Shell 错误码 {code}";
                foreach (var p in chunk) errors?.Add($"{p} → {reason}");
            }
        }

        return done;
    }

    private static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

    private static IEnumerable<List<string>> Chunk(List<string> src, int size)
    {
        for (var i = 0; i < src.Count; i += size)
            yield return src.GetRange(i, Math.Min(size, src.Count - i));
    }
}
