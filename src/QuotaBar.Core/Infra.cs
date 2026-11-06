using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuotaBar.Core;

public static class AppPaths
{
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string Data
    {
        get
        {
            // QUOTABAR_DATA: alternative data folder (tests, portable use).
            var dir = Environment.GetEnvironmentVariable("QUOTABAR_DATA") is { Length: > 0 } custom ? custom
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuotaBar");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
    public static string Snapshot => Path.Combine(Data, "snapshot.json");
    public static string Settings => Path.Combine(Data, "settings.json");
    public static string Secrets
    {
        get { var d = Path.Combine(Data, "secrets"); Directory.CreateDirectory(d); return d; }
    }
    public static string WebProfile => Path.Combine(Data, "webview");
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static void WriteAtomic<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, overwrite: true);
    }

    public static T? Read<T>(string path) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) : null; }
        catch { return null; }
    }
}

/// <summary>Small DPAPI-protected key/value store under %LOCALAPPDATA%\QuotaBar\secrets (current user only).</summary>
public static class SecretStore
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("QuotaBar.v1");

    static string PathFor(string name)
    {
        var safe = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..32];
        return Path.Combine(AppPaths.Secrets, safe + ".bin");
    }

    static readonly object Gate = new();

    /// <summary>Writes atomically (temp file + rename) and retries briefly, because the app, its refresh thread
    /// and qb.exe can all touch the same secret at once.</summary>
    public static void Set(string name, string? value)
    {
        var p = PathFor(name);
        lock (Gate)
            Retry(() =>
            {
                if (value is null) { if (File.Exists(p)) File.Delete(p); return; }
                var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
                var tmp = p + "." + Environment.ProcessId + "." + Environment.CurrentManagedThreadId + ".tmp";
                File.WriteAllBytes(tmp, blob);
                File.Move(tmp, p, overwrite: true);
            });
    }

    public static string? Get(string name)
    {
        var p = PathFor(name);
        if (!File.Exists(p)) return null;
        byte[]? raw = null;
        try { Retry(() => raw = File.ReadAllBytes(p)); } catch { return null; }
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(raw!, Entropy, DataProtectionScope.CurrentUser)); }
        catch { return null; }
    }

    static void Retry(Action a)
    {
        for (int i = 0; ; i++)
        {
            try { a(); return; }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && i < 8) { Thread.Sleep(25 * (i + 1)); }
        }
    }
}

/// <summary>Windows Credential Manager (generic credentials).</summary>
public static class WinCred
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CREDENTIAL
    {
        public uint Flags; public uint Type; public string TargetName; public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize; public IntPtr CredentialBlob; public uint Persist;
        public uint AttributeCount; public IntPtr Attributes; public string? TargetAlias; public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredReadW(string target, uint type, uint flags, out IntPtr cred);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredWriteW(ref CREDENTIAL cred, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredDeleteW(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] static extern void CredFree(IntPtr p);

    const uint GENERIC = 1;

    public static (string? user, byte[] blob)? Read(string target)
    {
        if (!CredReadW(target, GENERIC, 0, out var p)) return null;
        try
        {
            var c = Marshal.PtrToStructure<CREDENTIAL>(p);
            var bytes = new byte[c.CredentialBlobSize];
            if (c.CredentialBlobSize > 0) Marshal.Copy(c.CredentialBlob, bytes, 0, bytes.Length);
            return (c.UserName, bytes);
        }
        finally { CredFree(p); }
    }

    public static void Write(string target, string user, byte[] blob)
    {
        var mem = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, mem, blob.Length);
            var c = new CREDENTIAL
            {
                Type = GENERIC, TargetName = target, UserName = user,
                CredentialBlob = mem, CredentialBlobSize = (uint)blob.Length, Persist = 2 /* LOCAL_MACHINE */,
            };
            if (!CredWriteW(ref c, 0)) throw new InvalidOperationException($"CredWrite failed ({Marshal.GetLastWin32Error()})");
        }
        finally { Marshal.FreeHGlobal(mem); }
    }

    public static bool Delete(string target) => CredDeleteW(target, GENERIC, 0);
}

public static class Http
{
    public static readonly HttpClient Client = Create();

    static HttpClient Create()
    {
        var h = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All, UseCookies = false })
        { Timeout = TimeSpan.FromSeconds(25) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("WindowsAIQuotaBar/0.1 (+https://github.com/valuex-creative/Windows-AI-Quota-Bar)");
        h.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return h;
    }

    public sealed class HttpError(int code, string body) : Exception($"HTTP {code}: {Trim(body)}")
    {
        public int Code { get; } = code;
        static string Trim(string s) => s.Length > 200 ? s[..200] + "…" : s;
    }

    public static async Task<JsonElement> SendJson(HttpRequestMessage req, CancellationToken ct)
    {
        using var res = await Client.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new HttpError((int)res.StatusCode, body);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }
}

public static class JsonExt
{
    public static JsonElement? Prop(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;
    public static string? Str(this JsonElement e, string name) => e.Prop(name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : e.Prop(name)?.ToString();
    public static double? Num(this JsonElement e, string name)
    {
        var v = e.Prop(name);
        if (v is null) return null;
        if (v.Value.ValueKind == JsonValueKind.Number) return v.Value.GetDouble();
        if (v.Value.ValueKind == JsonValueKind.String && double.TryParse(v.Value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
        return null;
    }
    public static DateTimeOffset? Date(this JsonElement e, string name) =>
        e.Str(name) is { } s && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d.ToLocalTime() : null;
}

public static class Jwt
{
    public static JsonElement? Payload(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var parts = token.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var s = parts[1].Replace('-', '+').Replace('_', '/');
            s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(s));
            return doc.RootElement.Clone();
        }
        catch { return null; }
    }
}
