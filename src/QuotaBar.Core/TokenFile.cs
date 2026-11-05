using System.Security.Cryptography;
using System.Text;

namespace QuotaBar.Core;

/// <summary>
/// Safe refresh of token files that belong to another tool (Codex CLI's auth.json, Claude Code's .credentials.json).
/// <list type="bullet">
/// <item>Our own processes (the app and qb.exe) take a lock first, so they never refresh at the same time.</item>
/// <item>The file is re-read under the lock: if the CLI already refreshed it, its new token is used and nothing is refreshed.</item>
/// <item>After the network call the file is read again: if the CLI changed it meanwhile, its version wins and ours is not written.</item>
/// <item>Writes are atomic (temp file + replace), so a crash can never leave a half-written file.</item>
/// </list>
/// </summary>
public static class TokenFile
{
    /// <param name="path">The token file.</param>
    /// <param name="seenText">The file content the caller used when its token was rejected.</param>
    /// <param name="refresh">Gets new tokens for the given current content; returns the new file content.</param>
    /// <returns>The content to use from now on (ours, or the CLI's if it refreshed first).</returns>
    public static async Task<string> RefreshAsync(string path, string seenText, Func<string, Task<string>> refresh, CancellationToken ct)
    {
        await using var _ = await AcquireLock(path, ct);

        var current = await File.ReadAllTextAsync(path, ct);
        if (current != seenText) return current;   // someone (the CLI) refreshed already

        var updated = await refresh(current);

        var after = await File.ReadAllTextAsync(path, ct);
        if (after != current) return after;        // the CLI refreshed while we were waiting: keep its file

        WriteAtomic(path, updated);
        return updated;
    }

    static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".quotabar.tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        try { File.Replace(tmp, path, null, ignoreMetadataErrors: true); }   // keeps the original file's ACL
        catch (FileNotFoundException) { File.Move(tmp, path, overwrite: true); }
    }

    /// <summary>Cross-process lock between our own processes (lock file in our data folder, not next to the CLI's file).</summary>
    static async Task<FileStream> AcquireLock(string path, CancellationToken ct)
    {
        var dir = Path.Combine(AppPaths.Data, "locks");
        Directory.CreateDirectory(dir);
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToLowerInvariant())))[..16];
        var lockPath = Path.Combine(dir, name + ".lock");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(200, ct); }
        }
    }
}
