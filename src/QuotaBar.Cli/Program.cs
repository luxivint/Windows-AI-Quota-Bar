using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QuotaBar.Core;
using QuotaBar.Core.Providers;

Console.OutputEncoding = Encoding.UTF8;
var argList = args.ToList();
bool Flag(string f) { var i = argList.IndexOf(f); if (i < 0) return false; argList.RemoveAt(i); return true; }
var json = Flag("--json");
var cached = Flag("--cached");
var cmd = argList.Count > 0 ? argList[0].ToLowerInvariant() : "status";

try
{
    switch (cmd)
    {
        case "status":
        case "usage":
        {
            var provider = argList.Count > 1 ? Normalize(argList[1]) : null;
            var report = await Reports.Get(provider, cached);
            if (json) Console.WriteLine(JsonSerializer.Serialize(report, Json.Options));
            else Console.Write(Text.Render(report));
            return 0;
        }
        case "accounts":
            foreach (var a in AntigravityAccounts.List())
                Console.WriteLine($"{(a.Active ? "*" : " ")} {a.Email}");
            return 0;
        case "switch":
            if (argList.Count < 2) { Console.Error.WriteLine("usage: qb switch <email>"); return 2; }
            AntigravityAccounts.Switch(argList[1]);
            Console.WriteLine($"Antigravity active account → {argList[1]}. Restart running agy sessions to pick it up.");
            return 0;
        case "mcp":
            await Mcp.Run();
            return 0;
        case "help":
        case "--help":
        case "-h":
            Console.WriteLine("""
                QuotaBar CLI — AI subscription usage on Windows

                  qb [status] [provider] [--json] [--cached]   Show usage (claude, codex, antigravity, opencode)
                  qb accounts                                  List Antigravity accounts (* = active)
                  qb switch <email>                            Switch the active Antigravity account
                  qb mcp                                       Run as an MCP server (stdio) for Claude Code etc.
                """);
            return 0;
        default:
            Console.Error.WriteLine($"Unknown command '{cmd}'. Try `qb help`.");
            return 2;
    }
}
catch (Exception e)
{
    Console.Error.WriteLine("error: " + e.Message);
    return 1;
}

static string? Normalize(string p) => p.ToLowerInvariant() switch
{
    "claude" or "anthropic" => "claude",
    "codex" or "chatgpt" or "openai" or "gpt" => "codex",
    "antigravity" or "agy" or "gemini" or "google" => "antigravity",
    "opencode" or "opencode-go" or "go" => "opencode-go",
    _ => p.ToLowerInvariant(),
};

static class Reports
{
    public static async Task<UsageReport> Get(string? provider, bool cached)
    {
        if (cached && UsageService.LoadCached() is { } c)
        {
            if (provider is not null) c.Providers = c.Providers.Where(p => p.ProviderKind == provider).ToList();
            return c;
        }
        var report = await new UsageService().RefreshAsync(AppSettings.Load(), provider);
        if (provider is not null) report.Providers = report.Providers.Where(p => p.ProviderKind == provider).ToList();
        return report;
    }
}

static class Text
{
    public static string Render(UsageReport r)
    {
        var sb = new StringBuilder();
        var now = DateTimeOffset.Now;
        foreach (var p in r.Providers)
        {
            var head = p.DisplayName + (p.Plan is null ? "" : $" [{p.Plan}]") + (p.Account is null ? "" : $"  {p.Account}") + (p.ProviderKind == "antigravity" && p.IsActiveAccount ? "  (active)" : "");
            sb.AppendLine(head);
            if (p.Status != ProviderStatus.Ok) { sb.AppendLine($"  {p.Status}: {p.Message}"); sb.AppendLine(); continue; }
            foreach (var w in p.Windows)
            {
                var bar = Bar(w.LeftPercent);
                var reset = w.ResetsAt is { } t ? "resets in " + Fmt.Until(t, now) : "";
                sb.AppendLine($"  {w.Label,-16} {bar} {w.LeftPercent,5:0}% left  {reset}");
                if (w.PaceDelta(now) is { } d && Math.Abs(d) >= 1)
                    sb.AppendLine($"  {"",-16} {(d > 0 ? $"{d:0}% ahead of pace" : $"{-d:0}% under pace")}{(w.ProjectedRunOut(now) is { } ro ? ", runs out in " + Fmt.Until(ro, now) : "")}");
            }
            foreach (var i in p.Info) sb.AppendLine($"  {i.Label}: {i.Value}");
            if (p.Message is not null) sb.AppendLine($"  ! {p.Message}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    static string Bar(double leftPct)
    {
        const int width = 20;
        var filled = (int)Math.Round(leftPct / 100 * width);
        return "[" + new string('#', filled) + new string('.', width - filled) + "]";
    }
}

static class Mcp
{
    static readonly JsonSerializerOptions Opts = Json.Options;

    public static async Task Run()
    {
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        string? line;
        while ((line = await stdin.ReadLineAsync()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? msg;
            try { msg = JsonNode.Parse(line); } catch { continue; }
            var id = msg?["id"]?.DeepClone();
            var method = msg?["method"]?.GetValue<string>();
            if (id is null) continue; // notification
            JsonNode result;
            try { result = await Handle(method, msg?["params"]); }
            catch (Exception e)
            {
                await stdout.WriteLineAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = -32603, ["message"] = e.Message } }.ToJsonString());
                continue;
            }
            await stdout.WriteLineAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString());
        }
    }

    static JsonObject Schema(JsonObject props, params string[] required) => new()
    {
        ["type"] = "object", ["properties"] = props, ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()),
    };

    static async Task<JsonNode> Handle(string? method, JsonNode? p)
    {
        switch (method)
        {
            case "initialize":
                return new JsonObject
                {
                    ["protocolVersion"] = p?["protocolVersion"]?.GetValue<string>() ?? "2025-06-18",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "quotabar", ["version"] = Updates.Current.ToString(3) },
                    ["instructions"] = "Windows AI Quota Bar reports remaining usage of the user's AI subscriptions (Claude, Codex/ChatGPT, Google Antigravity accounts, OpenCode Go). Use get_usage when the user asks how much quota/limit/usage is left.",
                };
            case "ping":
                return new JsonObject();
            case "tools/list":
            {
                var tools = new JsonArray
                    {
                        new JsonObject
                        {
                            ["name"] = "get_usage",
                            ["description"] = "Live remaining usage for the user's AI subscriptions: percent left per window (5-hour session, weekly, monthly, per-model), reset times and pace. Optional provider filter: claude, codex, antigravity, opencode-go.",
                            ["inputSchema"] = Schema(new JsonObject
                            {
                                ["provider"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("claude", "codex", "antigravity", "opencode-go") },
                                ["cached"] = new JsonObject { ["type"] = "boolean", ["description"] = "Use the last snapshot from the Windows AI Quota Bar app instead of fetching live." },
                            }),
                            ["annotations"] = ReadOnly(),
                        },
                        new JsonObject
                        {
                            ["name"] = "list_antigravity_accounts",
                            ["description"] = "List Google Antigravity accounts known to Windows AI Quota Bar and which one is active in the agy CLI.",
                            ["inputSchema"] = Schema(new JsonObject()),
                            ["annotations"] = ReadOnly(),
                        },
                    };
                // Changing the active account is a write, so it is only offered when the user turned it on in Settings.
                if (AppSettings.Load().AllowAssistantAccountSwitch)
                    tools.Add(new JsonObject
                    {
                        ["name"] = "switch_antigravity_account",
                        ["description"] = "Make another stored Google account the active Antigravity (agy) account. Only call when the user asks to switch.",
                        ["inputSchema"] = Schema(new JsonObject { ["email"] = new JsonObject { ["type"] = "string" } }, "email"),
                        ["annotations"] = new JsonObject { ["readOnlyHint"] = false, ["destructiveHint"] = false, ["openWorldHint"] = false },
                    });
                return new JsonObject { ["tools"] = tools };
            }
            case "tools/call":
            {
                var name = p?["name"]?.GetValue<string>();
                var a = p?["arguments"];
                string text = name switch
                {
                    "get_usage" => Text.Render(await Reports.Get(a?["provider"]?.GetValue<string>(), a?["cached"]?.GetValue<bool>() ?? false)),
                    "list_antigravity_accounts" => string.Join("\n", AntigravityAccounts.List().Select(x => $"{(x.Active ? "* " : "  ")}{x.Email}")) is { Length: > 0 } s ? s : "No accounts stored yet.",
                    "switch_antigravity_account" => Switch(a?["email"]?.GetValue<string>()),
                    _ => throw new InvalidOperationException($"Unknown tool {name}"),
                };
                return new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) };
            }
            default:
                throw new InvalidOperationException($"Method not found: {method}");
        }
    }

    static JsonObject ReadOnly() => new() { ["readOnlyHint"] = true, ["openWorldHint"] = false };

    static string Switch(string? email)
    {
        if (!AppSettings.Load().AllowAssistantAccountSwitch)
            throw new InvalidOperationException("Account switching by AI assistants is turned off. The user can enable it in Windows AI Quota Bar → Settings → Ask your AI assistant.");
        if (string.IsNullOrEmpty(email)) throw new InvalidOperationException("email is required");
        AntigravityAccounts.Switch(email);
        return $"Active Antigravity account is now {email}. Running agy sessions must be restarted.";
    }

}
