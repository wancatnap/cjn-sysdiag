using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 报告包（.sdreport）：zip 里放 report.json（唯一可信源）+ report.html（人类可读）+ manifest.json（SHA256）。
/// <para>
/// 导入时**只信 report.json**，不渲染包里的 HTML —— 避免别人发来的报告带脚本。
/// 界面展示一律由 JSON 重建，样式与本地报告一致。
/// </para>
/// </summary>
public static class ReportPackage
{
    public const string Extension = ".sdreport";

    private const long MaxTotalBytes = 200L * 1024 * 1024;
    private const long MaxEntryBytes = 50L * 1024 * 1024;
    private const int MaxEntries = 200;

    public sealed class ImportResult
    {
        public ReportData Data { get; set; }

        public string SourcePath { get; set; } = "";

        public string SourceName => Path.GetFileName(SourcePath);

        /// <summary>完整性：报告里声明的哈希与实际是否一致。</summary>
        public bool IntegrityOk { get; set; }

        public string Note { get; set; } = "";

        /// <summary>由 JSON 重建的 HTML 临时文件，供 ReportWindow 展示。</summary>
        public string RenderedHtmlPath { get; set; } = "";
    }

    // ---------------- 导出 ----------------

    public static string Export(ReportPayload payload, string targetPath)
    {
        var data = ReportData.From(payload)
                   ?? throw new InvalidOperationException("这份报告没有结构化数据，无法导出报告包。");

        if (File.Exists(targetPath)) File.Delete(targetPath);

        var json = data.ToJson();
        var jsonBytes = new UTF8Encoding(false).GetBytes(json);

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["report.json"] = Sha256(jsonBytes)
        };

        using (var zip = ZipFile.Open(targetPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("report.json", CompressionLevel.Optimal);
            using (var stream = entry.Open())
                stream.Write(jsonBytes, 0, jsonBytes.Length);

            if (!string.IsNullOrEmpty(payload.HtmlPath) && File.Exists(payload.HtmlPath))
            {
                zip.CreateEntryFromFile(payload.HtmlPath, "report.html", CompressionLevel.Optimal);
                files["report.html"] = Sha256File(payload.HtmlPath);
            }

            var manifest = new
            {
                schema = data.Schema,
                generatedAt = data.GeneratedAt,
                tool = data.Tool,
                machine = data.Machine.Name,
                files
            };

            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });

            var mEntry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
            using var mStream = mEntry.Open();
            mStream.Write(manifestBytes, 0, manifestBytes.Length);
        }

        return targetPath;
    }

    /// <summary>默认导出路径：outputs\报告标题-时间戳.sdreport</summary>
    public static string SuggestPath(ReportPayload payload)
    {
        AppPaths.Ensure();

        var name = Sanitize(payload?.Title ?? "报告");
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmm");

        return Path.Combine(AppPaths.OutputDir, $"{name}-{stamp}{Extension}");
    }

    // ---------------- 导入 ----------------

    public static ImportResult Import(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("找不到报告文件", path);

        var info = new FileInfo(path);
        if (info.Length > MaxTotalBytes * 2) throw new InvalidDataException("文件过大，不像是报告包。");

        var result = new ImportResult { SourcePath = path };

        using var zip = ZipFile.OpenRead(path);

        if (zip.Entries.Count > MaxEntries)
            throw new InvalidDataException($"包内文件过多（{zip.Entries.Count}），已拒绝。");

        var root = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\";
        long total = 0;

        foreach (var entry in zip.Entries)
        {
            // 路径穿越防护：规范化后必须仍在根目录下
            var full = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"包内含非法路径：{entry.FullName}");

            if (entry.Length > MaxEntryBytes)
                throw new InvalidDataException($"包内单个文件过大：{entry.FullName}");

            total += entry.Length;
            if (total > MaxTotalBytes)
                throw new InvalidDataException("解压后体积超过上限，已拒绝。");
        }

        var jsonEntry = zip.GetEntry("report.json")
                        ?? throw new InvalidDataException("不是有效的报告包：缺少 report.json。");

        string json;
        using (var reader = new StreamReader(jsonEntry.Open(), Encoding.UTF8))
            json = reader.ReadToEnd();

        var data = ReportData.FromJson(json)
                   ?? throw new InvalidDataException("report.json 解析失败。");

        if (string.IsNullOrEmpty(data.Schema))
            throw new InvalidDataException("report.json 缺少 schema 字段。");

        result.Data = data;

        // 完整性校验：manifest 里声明的哈希与实际比对（不一致只提示，不阻断）
        try
        {
            var manifestEntry = zip.GetEntry("manifest.json");
            if (manifestEntry != null)
            {
                using var reader = new StreamReader(manifestEntry.Open(), Encoding.UTF8);
                using var doc = JsonDocument.Parse(reader.ReadToEnd());

                if (doc.RootElement.TryGetProperty("files", out var files) &&
                    files.TryGetProperty("report.json", out var expected))
                {
                    result.IntegrityOk = string.Equals(expected.GetString(), Sha256(Encoding.UTF8.GetBytes(json)),
                        StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        catch
        {
            result.IntegrityOk = false;
        }

        if (!data.IsCompatible())
            result.Note = $"报告版本（{data.Schema}）与当前程序不兼容，只能查看，无法对比。";
        else if (!result.IntegrityOk)
            result.Note = "完整性校验未通过：内容可能被修改过。";

        result.RenderedHtmlPath = RenderHtmlToFile(data);
        return result;
    }

    // ---------------- 由 JSON 重建 HTML ----------------

    public static string RenderHtmlToFile(ReportData data)
    {
        // 放系统临时目录：导入别人的报告不该往用户 outputs 里塞文件
        var dir = Path.Combine(Path.GetTempPath(), "SysDiag-import");

        try { Directory.CreateDirectory(dir); }
        catch { dir = Path.GetTempPath(); }

        var path = Path.Combine(dir, "_imported_" + Guid.NewGuid().ToString("N")[..8] + ".html");
        File.WriteAllText(path, RenderHtml(data), new UTF8Encoding(false));

        return path;
    }

    public static string RenderHtml(ReportData d)
    {
        var subtitle = string.Join("　·　", new[]
        {
            d.Machine.Name, d.GeneratedText, $"导入自 {d.Schema}"
        }.Where(x => !string.IsNullOrWhiteSpace(x)));

        var doc = new HtmlDoc(string.IsNullOrEmpty(d.Title) ? "报告" : d.Title, subtitle);

        if (d.Verdict.Score >= 0)
        {
            var color = d.Verdict.Score >= 50 ? "#C92A2A" : d.Verdict.Score >= 20 ? "#E8590C" : "#2F9E44";

            doc.Kpis(
                (d.Verdict.Score.ToString(), $"风险分（{d.Verdict.Level}）", color),
                (d.Verdict.Counts.Warn.ToString(), "线索", "#C92A2A"),
                (d.Verdict.Counts.Hint.ToString(), "提示", "#E8590C"),
                (d.Verdict.Counts.Ok.ToString(), "正常", "#2F9E44"));
        }

        if (!string.IsNullOrEmpty(d.Verdict.Text))
            doc.Note($"<b>{HtmlDoc.E(d.Verdict.Text)}</b>");

        doc.Note($"机器：{HtmlDoc.E(d.Machine.Name)}　·　用户：{HtmlDoc.E(d.Machine.User)}　·　" +
                 $"{(d.Machine.Admin ? "管理员权限" : "普通权限")}　·　工具 {HtmlDoc.E(d.Tool.Name)} {HtmlDoc.E(d.Tool.Version)}");

        foreach (var section in d.Sections)
        {
            doc.H2($"{section.Group}（{section.Items.Count} 项）");

            doc.Table(
                new[] { "级别", "结论", "依据" },
                section.Items.Select(i => new[]
                {
                    $"<span class=\"tag {(i.Level == "warn" ? "t-bad" : i.Level == "hint" ? "t-warn" : "t-ok")}\">{LevelText(i.Level)}</span>",
                    $"<b>{HtmlDoc.E(i.Title)}</b>" +
                    (i.Detail.Length > 0 ? $"<br/><span class=\"muted\">{HtmlDoc.E(i.Detail)}</span>" : ""),
                    i.Basis.Length > 0 ? $"<span class=\"mono\">{HtmlDoc.E(i.Basis)}</span>" : "<span class=\"muted\">—</span>"
                }),
                new[] { "70", "44%", "" });
        }

        if (d.Stages.Count > 0)
        {
            doc.H2("采集覆盖");
            doc.Table(
                new[] { "采集项", "状态", "条数", "说明" },
                d.Stages.Select(s => new[]
                {
                    HtmlDoc.E(s.Name),
                    $"<span class=\"tag {(s.State == "done" ? "t-ok" : s.State == "limited" ? "t-warn" : "t-bin")}\">{StateText(s.State)}</span>",
                    s.Items.ToString(),
                    HtmlDoc.E(s.Note)
                }),
                new[] { "150", "78", "66", "" });
        }

        if (d.Timeline.Count > 0)
        {
            doc.H2($"时间线（{d.Timeline.Count} 条）");
            doc.Table(
                new[] { "时间", "来源", "事件" },
                d.Timeline.Select(e => new[]
                {
                    $"<span class=\"mono\">{HtmlDoc.E(e.Time)}</span>",
                    HtmlDoc.E(e.Source),
                    $"<span class=\"{(e.Level == "hint" ? "t-hint" : e.Level == "warn" ? "t-bad" : "")}\">{HtmlDoc.E(e.Text)}</span>"
                }),
                new[] { "160", "80", "" });
        }

        doc.Note("这份 HTML 由 .sdreport 里的 report.json 重建，不执行任何脚本。" +
                 "如需原始排版，请解压报告包查看 report.html。");

        return doc.Build();
    }

    private static string LevelText(string level) => level switch
    {
        "warn" => "线索",
        "hint" => "提示",
        _ => "正常"
    };

    private static string StateText(string state) => state switch
    {
        "limited" => "受限",
        "failed" => "失败",
        _ => "完成"
    };

    // ---------------- 辅助 ----------------

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '-');

        return name.Trim().Length == 0 ? "报告" : name.Trim();
    }

    public static string Sha256(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return "sha256:" + Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();

        return "sha256:" + Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }
}
