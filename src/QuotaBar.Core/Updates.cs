using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace QuotaBar.Core;

public sealed record UpdateInfo(Version Version, string Tag, string PageUrl, string? SetupUrl, string? ShaUrl, string? Notes, DateTimeOffset? PublishedAt);

/// <summary>
/// Self-update through GitHub Releases: no server of our own. A release is expected to carry
/// <c>WindowsAIQuotaBar-Setup-X.Y.Z.exe</c> and <c>WindowsAIQuotaBar-Setup-X.Y.Z.exe.sha256</c>.
/// </summary>
public static class Updates
{
    public const string Repo = "valuex-creative/Windows-AI-Quota-Bar";
    public const string ReleasesUrl = "https://github.com/" + Repo + "/releases";

    // The shared client has a 25 s timeout; a 55 MB download needs longer.
    static readonly HttpClient Download = CreateDownloadClient();
    static HttpClient CreateDownloadClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("WindowsAIQuotaBar-Updater");
        return h;
    }

    /// <summary>Version of the running app (from the assembly informational version, without the +commit suffix).</summary>
    public static Version Current
    {
        get
        {
            var asm = System.Reflection.Assembly.GetEntryAssembly() ?? typeof(Updates).Assembly;
            var info = (Attribute.GetCustomAttribute(asm, typeof(System.Reflection.AssemblyInformationalVersionAttribute)) as System.Reflection.AssemblyInformationalVersionAttribute)?.InformationalVersion;
            return Parse(info) ?? asm.GetName().Version ?? new Version(0, 0, 0);
        }
    }

    public static Version? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().TrimStart('v', 'V');
        var cut = s.IndexOfAny(new[] { '+', '-', ' ' });
        if (cut > 0) s = s[..cut];
        return Version.TryParse(s, out var v) ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : null;
    }

    /// <summary>Latest published (non-draft, non-prerelease) release, or null when it is not newer than the running app.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        var r = await Http.SendJson(req, ct);
        var tag = r.Str("tag_name");
        var v = Parse(tag);
        if (v is null || v <= Current) return null;
        string? setup = null, sha = null;
        if (r.Prop("assets") is { ValueKind: JsonValueKind.Array } assets)
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.Str("name") ?? "";
                var url = a.Str("browser_download_url");
                if (name.EndsWith(".exe.sha256", StringComparison.OrdinalIgnoreCase)) sha = url;
                else if (name.Contains("Setup", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) setup = url;
            }
        return new UpdateInfo(v, tag!, r.Str("html_url") ?? ReleasesUrl, setup, sha, r.Str("body"), r.Date("published_at"));
    }

    /// <summary>True when this copy was installed by the Setup (it can update itself); false for portable / dev copies.</summary>
    public static bool CanSelfUpdate =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    /// <summary>
    /// Downloads the installer, checks it against the published SHA256 and starts it silently.
    /// The installer closes this app, upgrades in place and starts the new version.
    /// </summary>
    public static async Task InstallAsync(UpdateInfo u, IProgress<double>? progress, CancellationToken ct)
    {
        if (u.SetupUrl is null) throw new InvalidOperationException("This release has no installer.");
        if (u.ShaUrl is null) throw new InvalidOperationException("This release has no checksum, so it was not installed automatically.");
        if (!IsTrusted(u.SetupUrl) || !IsTrusted(u.ShaUrl)) throw new InvalidOperationException("Unexpected download location.");

        var expected = (await Retry(() => Download.GetStringAsync(u.ShaUrl, ct))).Trim().Split(' ', '\t', '\n', '\r')[0].ToLowerInvariant();
        if (expected.Length != 64) throw new InvalidOperationException("Invalid checksum file.");

        var dir = Path.Combine(Path.GetTempPath(), "WindowsAIQuotaBar-update");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"WindowsAIQuotaBar-Setup-{u.Version}.exe");

        await Retry(() => DownloadFile(u.SetupUrl, file, progress, ct));

        string actual;
        await using (var fs = File.OpenRead(file)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
        if (actual != expected)
        {
            try { File.Delete(file); } catch { }
            throw new InvalidOperationException("Checksum mismatch: the download was discarded.");
        }

        Process.Start(new ProcessStartInfo(file, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS") { UseShellExecute = true });
    }

    /// <summary>Retries network failures (DNS hiccups, dropped connections) up to 3 times.</summary>
    static async Task<T> Retry<T>(Func<Task<T>> op)
    {
        for (int i = 0; ; i++)
        {
            try { return await op(); }
            catch (Exception e) when (i < 2 && e is HttpRequestException or IOException or TaskCanceledException { InnerException: TimeoutException })
            { await Task.Delay(TimeSpan.FromSeconds(2 * (i + 1))); }
        }
    }

    static async Task<bool> DownloadFile(string url, string file, IProgress<double>? progress, CancellationToken ct)
    {
        using (var res = await Download.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            res.EnsureSuccessStatusCode();
            var total = res.Content.Headers.ContentLength ?? 0;
            await using var src = await res.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(file);
            var buf = new byte[81920];
            long done = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }
        return true;
    }

    /// <summary>Only GitHub release downloads of this repository (which redirect to GitHub's own CDN).</summary>
    static bool IsTrusted(string url) =>
        url.StartsWith($"https://github.com/{Repo}/releases/download/", StringComparison.OrdinalIgnoreCase);
}
