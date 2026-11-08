using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QuotaBar.Core;
using CodexAccountSwitcher.Core;

if (args.FirstOrDefault() == "app-server") { await FakeRpc(); return 0; }
var host = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/switcher-runtime/SwitcherHost.exe");
var root = Path.Combine(Path.GetTempPath(), "quotabar-switcher-check-" + Guid.NewGuid().ToString("N"));
var active = Path.Combine(root, "active");
var data = Path.Combine(root, "data");
var control = Path.Combine(root, "control");
Directory.CreateDirectory(active); Directory.CreateDirectory(control);
var oldCli = Environment.GetEnvironmentVariable("CODEX_CLI_PATH");
var oldControl = Environment.GetEnvironmentVariable("QUOTABAR_FIXTURE_CONTROL");
var events = new ConcurrentQueue<string>();
try
{
    Environment.SetEnvironmentVariable("CODEX_CLI_PATH", Path.ChangeExtension(typeof(Program).Assembly.Location, ".exe"));
    Environment.SetEnvironmentVariable("QUOTABAR_FIXTURE_CONTROL", control);
    var activeAuth = Path.Combine(active, "auth.json");
    var original = Credential("original", "original@example.test", "initial-fake-token");
    File.WriteAllText(activeAuth, original);
    Task Platform(string method, string? url)
    {
        Require(method is "openBrowser" or "closeDesktop" or "reopenDesktop", "Unexpected platform call.");
        if (method == "openBrowser") Require(url == "https://example.test/sign-in", "Unexpected login address.");
        events.Enqueue(method); // No browser or real Desktop operation is performed.
        return Task.CompletedTask;
    }
    await using (var core = new CodexSwitcherService(host, data, active, Platform))
    {
        await core.InitializeAsync();
        await Until(() => core.State.Accounts.Length == 1 && core.State.ActiveAccountID != null);
        var originalId = core.State.ActiveAccountID!.Value;
        Require(File.ReadAllText(activeAuth) == original, "Startup changed the active login.");
        Console.WriteLine("PASS: original engine automatically registers the current fake login.");

        await using (var competitor = new CodexSwitcherService(host, data, active, Platform))
        {
            var refused = false;
            try { await competitor.InitializeAsync(); } catch (IOException) { refused = true; }
            Require(refused, "A second engine must not own the same account store.");
        }
        Console.WriteLine("PASS: standalone-compatible ownership lock prevents concurrent engines.");

        await core.CommandAsync("add");
        await Until(() => core.State.Accounts.Length == 2 && !core.State.IsAddingAccount);
        var targetId = core.State.Accounts.Single(a => a.Profile.Id != originalId).Profile.Id;
        Require(events.Contains("openBrowser"), "Login did not use the platform browser interface.");
        Require(File.ReadAllText(activeAuth) == original, "Browser login changed the active account.");
        await core.RefreshAsync();
        await Until(() => core.State.Accounts.All(a => a.Usage != null));
        Require(core.State.Accounts.All(a => a.Usage!.RemainingPercent == 42 && a.Usage.FiveHourRemainingPercent == 67), "The engine used the wrong quota bucket/windows.");
        Require(core.Providers.Count == 2, "Both active and inactive accounts must reach Quota Bar.");
        Console.WriteLine("PASS: browser-login RPC, early completion notification and all-account quota mapping.");

        File.WriteAllText(Path.Combine(control, "usage-fail"), "fail");
        await core.RefreshAsync();
        await Until(() => core.State.Accounts.All(a => a.UsageStatus == "stale"));
        Require(core.State.Accounts.All(a => a.Usage!.RemainingPercent == 42), "Failed refresh discarded last successful usage.");
        File.Delete(Path.Combine(control, "usage-fail"));
        Console.WriteLine("PASS: original engine retains each account's cached quota on failure.");

        // Corrupt only the disposable target profile to exercise the real Swift restoration path.
        var targetAuth = Path.Combine(data, "accounts", targetId.ToString(), "auth.json");
        var validTarget = File.ReadAllText(targetAuth);
        File.WriteAllText(targetAuth, Credential("wrong-identity", "wrong@example.test", "fake"));
        while (events.TryDequeue(out _)) { }
        await core.CommandAsync("switch", targetId);
        await Until(() => core.State.Error != null && !core.State.IsMutating);
        Require(File.ReadAllText(activeAuth) == original, "Identity failure did not restore the original credential.");
        Require(core.State.ActiveAccountID == originalId, "Identity failure changed the committed account.");
        Require(events.Contains("closeDesktop") && !events.Contains("reopenDesktop"), "A failed switch must stop before reopening.");
        File.WriteAllText(targetAuth, validTarget);
        await core.CommandAsync("dismissError");
        Console.WriteLine("PASS: real Swift switch pipeline restores the original after target identity failure.");

        var rotated = Credential("original", "original@example.test", "fresh-fake-token");
        File.WriteAllText(activeAuth, rotated);
        while (events.TryDequeue(out _)) { }
        await core.CommandAsync("switch", targetId);
        await Until(() => core.State.ActiveAccountID == targetId && !core.State.IsMutating);
        Require(ReadIdentity(activeAuth).account == "target", "Selected account was not installed.");
        Require(File.ReadAllText(Path.Combine(data, "accounts", originalId.ToString(), "auth.json")) == rotated, "Switch did not preserve the latest original credentials.");
        Require(events.ToArray().SequenceEqual(new[] { "closeDesktop", "reopenDesktop" }), "Desktop handoff order changed.");
        Console.WriteLine("PASS: original close/save/activate/verify/commit/reopen flow with fake Desktop adapters.");

        await core.CommandAsync("remove", targetId);
        await Until(() => core.State.Error != null && !core.State.IsMutating);
        Require(core.State.Accounts.Length == 2, "The active account must be protected from removal.");
        await core.CommandAsync("dismissError");
        await core.CommandAsync("remove", originalId);
        await Until(() => core.State.Accounts.Length == 1 && !core.State.IsMutating);
        Require(!Directory.Exists(Path.Combine(data, "accounts", originalId.ToString())), "Removed profile still exists.");
        Console.WriteLine("PASS: active-profile protection and inactive-profile removal.");

        File.WriteAllText(Path.Combine(control, "login-wait"), "wait");
        await core.CommandAsync("add");
        await Until(() => core.State.IsAddingAccount);
        await core.CommandAsync("cancelAdd");
        await Until(() => !core.State.IsAddingAccount);
        Require(core.State.Accounts.Length == 1, "Canceled login created an account.");
        File.Delete(Path.Combine(control, "login-wait"));
        Console.WriteLine("PASS: sign-in cancellation leaves the active account and account list intact.");
    }
    await using (var reopened = new CodexSwitcherService(host, data, active, Platform))
    {
        await reopened.InitializeAsync();
        await Until(() => reopened.State.Accounts.Length == 1 && reopened.State.ActiveAccountID != null);
        Require(reopened.State.Accounts[0].Profile.Email == "target@example.test", "Registry did not survive restart.");
    }
    Console.WriteLine("PASS: persistence and ownership release across host restart. No real accounts, browsers or Desktop processes were used.");
    return 0;
}
catch (Exception e) { Console.Error.WriteLine(e); return 1; }
finally
{
    Environment.SetEnvironmentVariable("CODEX_CLI_PATH", oldCli);
    Environment.SetEnvironmentVariable("QUOTABAR_FIXTURE_CONTROL", oldControl);
    SecureFiles.DeleteOwnedDirectory(Path.GetTempPath(), root);
}

static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task Until(Func<bool> predicate)
{
    var deadline = DateTime.UtcNow.AddSeconds(20);
    while (!predicate()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("Expected shared-core state was not published."); await Task.Delay(30); }
}
static string Credential(string account, string email, string token)
{
    var claims = new JsonObject { ["email"] = email, ["https://api.openai.com/auth"] = new JsonObject { ["chatgpt_account_id"] = account } };
    var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(claims.ToJsonString())).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    return JsonSerializer.Serialize(new { tokens = new { account_id = account, access_token = token, refresh_token = "fake-refresh", id_token = "h." + payload + ".s" } });
}
static (string account, string email) ReadIdentity(string path)
{
    var auth = JsonNode.Parse(File.ReadAllText(path))!;
    var account = auth["tokens"]!["account_id"]!.GetValue<string>();
    return (account, account + "@example.test");
}
static async Task FakeRpc()
{
    var home = Environment.GetEnvironmentVariable("CODEX_HOME") ?? throw new Exception("Missing fake home.");
    var control = Environment.GetEnvironmentVariable("QUOTABAR_FIXTURE_CONTROL") ?? throw new Exception("Missing fixture controls.");
    while (await Console.In.ReadLineAsync() is { } line)
    {
        using var document = JsonDocument.Parse(line);
        var message = document.RootElement;
        if (!message.TryGetProperty("id", out var id)) continue;
        var method = message.GetProperty("method").GetString();
        if (method == "account/rateLimits/read" && File.Exists(Path.Combine(control, "usage-fail")))
        {
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { id = id.GetInt32(), error = new { code = -1, message = "fixture usage failure" } }));
            await Console.Out.FlushAsync(); continue;
        }
        if (method == "account/login/start")
        {
            if (!File.Exists(Path.Combine(control, "login-wait")))
            {
                File.WriteAllText(Path.Combine(home, "auth.json"), Credential("target", "target@example.test", "fake-target-token"));
                // Deliberately arrive before the start response, as the production transport must permit.
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { method = "account/login/completed", @params = new { success = true } }));
            }
        }
        var identity = method == "account/read" ? ReadIdentity(Path.Combine(home, "auth.json")) : default;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        object result = method switch
        {
            "account/read" => new { account = new { accountId = identity.account, email = identity.email } },
            "account/login/start" => new { authUrl = "https://example.test/sign-in" },
            "account/rateLimits/read" => new { rateLimitResetCredits = new { availableCount = 2 }, rateLimitsByLimitId = new {
                codex = new { primary = new { usedPercent = 33, windowDurationMins = 300, resetsAt = now + 7200 }, secondary = new { usedPercent = 58, windowDurationMins = 10080, resetsAt = now + 259200 } },
                other = new { primary = new { usedPercent = 99, windowDurationMins = 300, resetsAt = now + 7200 } } } },
            _ => new { },
        };
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { id = id.GetInt32(), result }));
        await Console.Out.FlushAsync();
    }
}
