using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SysDiag.Services;

/// <summary>一条远程公告。</summary>
public sealed class RemoteNotice
{
    // 字段名必须和远端 JSON 对齐：JSON 用 camelCase 短名（v / enabled / ...），
    // C# 属性是 PascalCase，不加映射的话反序列化出来全是默认值（Signature 为空 → 验签必挂）。
    [JsonPropertyName("v")] public int Version { get; set; }

    [JsonPropertyName("enabled")] public bool Enabled { get; set; }

    /// <summary>info = 普通提示；warn = 需要留意的提醒（配色不同）。</summary>
    [JsonPropertyName("level")] public string Level { get; set; } = "info";

    [JsonPropertyName("title")] public string Title { get; set; } = "";

    [JsonPropertyName("message")] public string Message { get; set; } = "";

    [JsonPropertyName("url")] public string Url { get; set; } = "";

    [JsonPropertyName("urlText")] public string UrlText { get; set; } = "";

    [JsonPropertyName("expires")] public DateTimeOffset? Expires { get; set; }

    [JsonPropertyName("signedAt")] public string SignedAt { get; set; } = "";

    [JsonPropertyName("signature")] public string Signature { get; set; } = "";

    // ---- 运行时状态（不参与签名，也不写进缓存文件） ----

    /// <summary>签名是否通过。**只有 true 的内容才允许展示。**</summary>
    [JsonIgnore] public bool Verified { get; set; }

    /// <summary>remote = 刚从网络拿到；cache = 用的本地缓存；none = 没有可用内容。</summary>
    [JsonIgnore] public string Source { get; set; } = "none";

    [JsonIgnore] public string Error { get; set; } = "";

    /// <summary>该不该显示：启用 + 有内容 + 没过期 + 验签通过。</summary>
    [JsonIgnore]
    public bool Showable =>
        Verified && Enabled && Message.Length > 0 && (Expires == null || Expires > DateTimeOffset.Now);
}

/// <summary>
/// 远程公告：从 GitHub 上的静态 JSON 拉一条通告，**验签后才显示**。
///
/// 设计上有三条硬约束，都是刻意的：
///
/// 1. **只做公告，不做授权**。这里没有任何「禁用软件」的逻辑——远程开关一旦出错，
///    用户会直接以为软件坏了。真要控制可用性，得走服务端签发 token，不是读一个 JSON。
/// 2. **必须验签**。JSON 放在公开仓库里，谁都能看到；没有签名的话，
///    中间人（或被劫持的 DNS）可以返回任意内容，等于给了一个伪造公告的口子。
/// 3. **失败一律降级，绝不打扰**。网络失败用本地缓存（7 天内），
///    缓存也没有就安静地不显示——不能因为拉不到公告就让启动变慢或弹错误。
/// </summary>
public static class RemoteNoticeService
{
    /// <summary>公告地址。换仓库只改这里（或 AppSettings 里的覆盖值）。</summary>
    public const string DefaultUrl =
        "https://raw.githubusercontent.com/wancatnap/sysdiag-notice/main/notice.json";

    /// <summary>
    /// 内置公钥（ECDSA P-256，SubjectPublicKeyInfo 的 Base64）。
    /// 私钥不进仓库、不打包，只留在这台开发机上用于签名。
    /// </summary>
    private const string PublicKeyBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEkCHrgY+O+2iFPgW4M7f7aaaoYuaSpuFxqMO+jVEpPBpaL16Rm0EShnaSw31psLHOe6swMK+B21YCLCrRZTmKdg==";

    /// <summary>缓存最多用 7 天，超过就当成没有——避免长期显示一条过期公告。</summary>
    private static readonly TimeSpan CacheWindow = TimeSpan.FromDays(7);

    public static string CacheFile => Path.Combine(AppPaths.DataDir, "notice-cache.json");

    public static string DismissFile => Path.Combine(AppPaths.DataDir, "notice-dismissed.txt");

    /// <summary>拉取并验证公告。任何异常都会降级，不抛给调用方。</summary>
    public static async Task<RemoteNotice> LoadAsync(string url = null, int timeoutSeconds = 8)
    {
        url = string.IsNullOrWhiteSpace(url) ? DefaultUrl : url;

        var note = await FetchAsync(url, timeoutSeconds);

        if (note != null && note.Verified)
        {
            note.Source = "remote";
            SaveCache(note);
            return note;
        }

        // 网络或验签失败 → 退回缓存
        var cached = LoadCache();

        if (cached != null)
        {
            cached.Source = "cache";

            if (note != null && note.Error.Length > 0) cached.Error = note.Error;

            return cached;
        }

        return note ?? new RemoteNotice { Source = "none", Error = "没有可用公告。" };
    }

    private static async Task<RemoteNotice> FetchAsync(string url, int timeoutSeconds)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 3, 30)) };

            // User-Agent 必须是 ASCII：AppInfo.BuildText 里有「·」和中文，直接塞进去会被
            // HttpClient 判为非法头值（实测报 "The format of value ... is invalid"）。
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SysDiag/4.9");

            var text = await http.GetStringAsync(url).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(text))
                return new RemoteNotice { Error = "返回内容为空。" };

            return Parse(text);
        }
        catch (Exception ex)
        {
            return new RemoteNotice { Error = "拉取失败：" + ex.Message };
        }
    }

    /// <summary>解析并验签。JSON 结构不对或签名不符，都返回 Verified = false。</summary>
    public static RemoteNotice Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var note = new RemoteNotice
            {
                Version = Int(root, "v"),
                Enabled = Bool(root, "enabled"),
                Level = Str(root, "level"),
                Title = Str(root, "title"),
                Message = Str(root, "message"),
                Url = Str(root, "url"),
                UrlText = Str(root, "urlText"),
                SignedAt = Str(root, "signedAt"),
                Signature = Str(root, "signature")
            };

            var expires = Str(root, "expires");

            if (expires.Length > 0 && DateTimeOffset.TryParse(expires, out var parsed))
                note.Expires = parsed;

            if (note.Signature.Length == 0)
            {
                note.Error = "缺少签名。";
                return note;
            }

            note.Verified = Verify(note, note.Signature);

            if (!note.Verified) note.Error = "签名不匹配——内容可能被改过。";

            return note;
        }
        catch (Exception ex)
        {
            return new RemoteNotice { Error = "格式错误：" + ex.Message };
        }
    }

    // ---------------------------------------------------------------- 签名

    /// <summary>
    /// 参与签名的规范串。**签名端与验证端必须逐字节一致**，
    /// 所以这里用固定顺序 + 竖线分隔，字段里出现竖线直接判不合法。
    /// </summary>
    public static string Canonical(RemoteNotice note) =>
        string.Join("|",
            note.Version.ToString(),
            note.Enabled ? "1" : "0",
            note.Level,
            note.Title,
            note.Message,
            note.Url,
            note.UrlText,
            note.Expires?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ") ?? "",
            note.SignedAt);

    private static bool Verify(RemoteNotice note, string signatureBase64)
    {
        try
        {
            var canonical = Canonical(note);

            // 字段里出现分隔符会导致签名串有歧义，直接拒绝
            if (canonical.Count(c => c == '|') != 8) return false;

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKeyBase64), out _);

            return ecdsa.VerifyData(
                Encoding.UTF8.GetBytes(canonical),
                Convert.FromBase64String(signatureBase64),
                HashAlgorithmName.SHA256);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>生成一对密钥：私钥写盘，公钥打印出来贴进 <see cref="PublicKeyBase64"/>。</summary>
    public static (bool Ok, string Message) GenerateKeyPair(string privateKeyPath)
    {
        try
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            var publicBase64 = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
            var privatePem = ecdsa.ExportPkcs8PrivateKeyPem();

            var dir = Path.GetDirectoryName(privateKeyPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.WriteAllText(privateKeyPath, privatePem, new UTF8Encoding(false));

            return (true,
                "私钥已写入：" + privateKeyPath + "\n\n" +
                "把下面这行填到 RemoteNoticeService.PublicKeyBase64：\n" + publicBase64 + "\n\n" +
                "私钥不要提交到仓库、不要打进 exe。");
        }
        catch (Exception ex)
        {
            return (false, "生成密钥失败：" + ex.Message);
        }
    }

    /// <summary>给一份明文 JSON 签名，输出可直接上传的成文件。</summary>
    public static (bool Ok, string Message) SignFile(string inputPath, string outputPath, string privateKeyPath)
    {
        try
        {
            if (!File.Exists(inputPath)) return (false, "找不到输入文件：" + inputPath);
            if (!File.Exists(privateKeyPath)) return (false, "找不到私钥：" + privateKeyPath);

            var json = File.ReadAllText(inputPath);
            var note = Parse(json);

            // Parse 会因为没有签名字段而失败，这里只要字段值，不看 Verified
            if (note.SignedAt.Length == 0)
                note.SignedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

            var canonical = Canonical(note);

            if (canonical.Count(c => c == '|') != 8)
                return (false, "字段里不能出现竖线 |（会和签名串的分隔符冲突）。");

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(File.ReadAllText(privateKeyPath));

            var signature = Convert.ToBase64String(
                ecdsa.SignData(Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256));

            var payload = new
            {
                v = note.Version,
                enabled = note.Enabled,
                level = note.Level,
                title = note.Title,
                message = note.Message,
                url = note.Url,
                urlText = note.UrlText,
                expires = note.Expires?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ") ?? "",
                signedAt = note.SignedAt,
                signature
            };

            var text = JsonSerializer.Serialize(payload,
                new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

            File.WriteAllText(outputPath, text, new UTF8Encoding(false));

            // 自检一遍：签完立刻用公钥验一次，避免传上去才发现对不上
            var check = Parse(text);

            return check.Verified
                ? (true, "已签名并验证通过：\n" + outputPath + "\n\n把这个文件上传到仓库即可。")
                : (false, "签名后自验失败（" + check.Error + "）。请确认代码里的公钥和这把私钥是一对。");
        }
        catch (Exception ex)
        {
            return (false, "签名失败：" + ex.Message);
        }
    }

    // ---------------------------------------------------------------- 缓存

    private static void SaveCache(RemoteNotice note)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(note,
                new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }
        catch { /* 存不下不影响本次显示 */ }
    }

    private static RemoteNotice LoadCache()
    {
        try
        {
            if (!File.Exists(CacheFile)) return null;
            if (DateTime.Now - File.GetLastWriteTime(CacheFile) > CacheWindow) return null;

            var note = JsonSerializer.Deserialize<RemoteNotice>(File.ReadAllText(CacheFile));

            // 缓存也要重新验签：本地文件同样可能被改。
            // 注意不能信任文件里的 Verified 字段——那是上次运行写的状态，不构成证据。
            if (note == null || note.Signature.Length == 0) return null;

            note.Verified = Verify(note, note.Signature);

            return note.Verified ? note : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>记住已关闭的签名，同一条公告不再重复弹。</summary>
    public static void Dismiss(RemoteNotice note)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(DismissFile, note.Signature, new UTF8Encoding(false));
        }
        catch { /* 记不住就下次再显示一次，不是大问题 */ }
    }

    public static bool IsDismissed(RemoteNotice note)
    {
        try
        {
            return File.Exists(DismissFile)
                   && File.ReadAllText(DismissFile).Trim() == note.Signature
                   && note.Signature.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()?.Trim() ?? ""
            : "";

    private static int Int(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;

    private static bool Bool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
