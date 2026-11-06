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
        case "config":
            return await Config.Run(argList.Skip(1).ToList(), json);
        case "cost":
        {
            var c = await CostScan.RunAsync();
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    currency = "USD", basis = "Anthropic API list prices, from local Claude Code logs",
                    today = Math.Round(c.Today, 2), last7Days = Math.Round(c.Last7, 2), thisMonth = Math.Round(c.ThisMonth, 2), last30Days = Math.Round(c.Last30, 2),
                    tokensLast30Days = c.TokensLast30, requestsLast30Days = c.RequestsLast30,
                    byModelLast30Days = c.ByModelLast30.Select(m => new { model = m.Model, cost = Math.Round(m.Cost, 2) }),
                    daily = c.Daily.Select(d => new { day = d.Day.ToString("yyyy-MM-dd"), cost = Math.Round(d.Cost, 2) }),
                    unpricedModels = c.UnpricedModels,
                }, Json.Options));
                return 0;
            }
            Console.WriteLine("Claude Code at API prices (estimate from local logs)");
            Console.WriteLine($"  Today        {CostScan.Money(c.Today)}");
            Console.WriteLine($"  Last 7 days  {CostScan.Money(c.Last7)}");
            Console.WriteLine($"  This month   {CostScan.Money(c.ThisMonth)}");
            Console.WriteLine($"  Last 30 days {CostScan.Money(c.Last30)}  (" + c.RequestsLast30.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + $" requests, {CostScan.Tokens(c.TokensLast30)} tokens)");
            foreach (var m in c.ByModelLast30.Take(5)) Console.WriteLine($"    {m.Model,-12} {CostScan.Money(m.Cost)}");
            if (c.UnpricedModels.Count > 0) Console.WriteLine("  No price for: " + string.Join(", ", c.UnpricedModels));
            return 0;
        }
        case "help":
        case "--help":
        case "-h":
            Console.WriteLine("""
                QuotaBar CLI — AI subscription usage on Windows

                  qb [status] [provider] [--json] [--cached]   Show usage (claude, codex, antigravity, opencode)
                  qb accounts                                  List Antigravity accounts (* = active)
                  qb switch <email>                            Switch the active Antigravity account
                  qb mcp                                       Run as an MCP server (stdio) for Claude Code etc.
                  qb cost [--json]                             Claude Code usage priced at API rates (from local logs)

                  qb config providers [--json]                 List providers, whether they are shown, and added keys
                  qb config enable|disable <provider>          Show or hide a provider
                  qb config set-key <provider> --stdin [--label NAME]
                                                               Test an API key read from stdin, then store it encrypted
                  qb config remove-key <provider> [label]      Remove an added key
                  qb config get [setting]                      Show settings (refresh, hotkey, widget-style, alerts, status)
                  qb config set <setting> <value>              Change a setting; the running app picks it up
                  qb config export [file]                      Write a settings backup (no API keys)
                  qb config import <file|->                    Restore a settings backup; keeps the keys on this PC
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

static class Config
{
    static readonly string[] BuiltIn = UsageService.BuiltIn;

    static string Canon(string p)
    {
        var k = p.ToLowerInvariant() switch
        {
            "claude" or "anthropic" => "claude",
            "codex" or "chatgpt" or "openai" => "codex",
            "antigravity" or "agy" => "antigravity",
            "opencode" or "opencode-go" or "go" => "opencode-go",
            "github" or "copilot" => "copilot",
            var x => x,
        };
        if (!BuiltIn.Contains(k) && KeyProviders.Find(k) is null)
            throw new ArgumentException($"Unknown provider '{p}'. Known: {string.Join(", ", BuiltIn.Concat(KeyProviders.Catalog.Select(d => d.Kind)))}");
        return k;
    }

    public static async Task<int> Run(List<string> a, bool json)
    {
        string? Opt(string name) { var i = a.IndexOf(name); if (i < 0 || i + 1 >= a.Count) return null; var v = a[i + 1]; a.RemoveRange(i, 2); return v; }
        bool Has(string name) { var i = a.IndexOf(name); if (i < 0) return false; a.RemoveAt(i); return true; }
        var s = AppSettings.Load();
        var sub = a.Count > 0 ? a[0].ToLowerInvariant() : "providers";
        switch (sub)
        {
            case "providers":
            {
                var rows = BuiltIn.Select(k => new { kind = k, name = k, shown = s.IsEnabled(k), keys = Array.Empty<string>() })
                    .Concat(KeyProviders.Catalog.Select(d => new
                    {
                        kind = d.Kind, name = d.Name, shown = s.IsEnabled(d.Kind),
                        keys = s.KeyAccounts.Where(x => x.Kind == d.Kind).Select(x => x.Label).ToArray(),
                    })).ToList();
                if (json) { Console.WriteLine(JsonSerializer.Serialize(rows, Json.Options)); return 0; }
                foreach (var r in rows)
                    Console.WriteLine($"{(r.shown ? "on " : "off")}  {r.kind,-12} {(r.keys.Length > 0 ? string.Join(", ", r.keys) : BuiltIn.Contains(r.kind) ? "(automatic)" : "")}");
                return 0;
            }
            case "enable":
            case "disable":
            {
                if (a.Count < 2) { Console.Error.WriteLine($"usage: qb config {sub} <provider>"); return 2; }
                var k = Canon(a[1]);
                s.EnabledProviders[k] = sub == "enable";
                s.Save();
                Console.WriteLine($"{k}: {(sub == "enable" ? "shown" : "hidden")}");
                return 0;
            }
            case "set-key":
            {
                var label = Opt("--label");
                var stdin = Has("--stdin");
                var inline = Opt("--key");
                if (a.Count < 2 || (!stdin && inline is null)) { Console.Error.WriteLine("usage: qb config set-key <provider> --stdin [--label NAME]"); return 2; }
                var k = Canon(a[1]);
                if (KeyProviders.Find(k) is not { } def) { Console.Error.WriteLine($"{k} does not use API keys."); return 2; }
                var key = (stdin ? await Console.In.ReadToEndAsync() : inline!).Trim();
                if (key.Length == 0) { Console.Error.WriteLine("No key on stdin."); return 2; }
                var snap = await KeyProviders.Test(k, key, CancellationToken.None);
                if (snap.Status != ProviderStatus.Ok) { Console.Error.WriteLine($"Key rejected: {snap.Message}"); return 1; }
                var acc = KeyProviders.Add(s, k, label ?? def.Name, key);
                s.EnabledProviders[k] = true;
                s.Save();
                Console.WriteLine($"Added {def.Name} \"{acc.Label}\" (stored encrypted for this Windows user).");
                return 0;
            }
            case "remove-key":
            {
                if (a.Count < 2) { Console.Error.WriteLine("usage: qb config remove-key <provider> [label]"); return 2; }
                var k = Canon(a[1]);
                var matches = s.KeyAccounts.Where(x => x.Kind == k && (a.Count < 3 || x.Label.Equals(a[2], StringComparison.OrdinalIgnoreCase))).ToList();
                if (matches.Count == 0) { Console.Error.WriteLine("No matching key."); return 1; }
                if (matches.Count > 1) { Console.Error.WriteLine("Several keys match; add the label: " + string.Join(", ", matches.Select(m => m.Label))); return 2; }
                KeyProviders.Remove(s, matches[0].Id);
                Console.WriteLine($"Removed {matches[0].Label}.");
                return 0;
            }
            case "export":
            {
                var text = SettingsTransfer.Export(s);
                if (a.Count > 1) { File.WriteAllText(a[1], text); Console.WriteLine($"Settings written to {a[1]} (no API keys)."); }
                else Console.WriteLine(text);
                return 0;
            }
            case "import":
            {
                if (a.Count < 2) { Console.Error.WriteLine("usage: qb config import <file>   (use - for stdin)"); return 2; }
                var text = a[1] == "-" ? await Console.In.ReadToEndAsync() : File.ReadAllText(a[1]);
                SettingsTransfer.Import(text, s).Save();
                Console.WriteLine("Settings imported. API keys on this PC were kept.");
                return 0;
            }
            case "get":
            {
                var all = new Dictionary<string, string>
                {
                    ["refresh"] = s.RefreshMinutes.ToString(),
                    ["hotkey"] = s.Hotkey.Length == 0 ? "off" : s.Hotkey,
                    ["widget"] = s.ShowTaskbarWidget ? "on" : "off",
                    ["widget-style"] = s.WidgetStyle,
                    ["widget-auto"] = s.WidgetAuto ? "on" : "off",
                    ["all-monitors"] = s.WidgetAllMonitors ? "on" : "off",
                    ["alerts"] = s.Alerts.Enabled ? "on" : "off",
                    ["alert-thresholds"] = string.Join(",", s.Alerts.Thresholds),
                    ["status"] = s.CheckServiceStatus ? "on" : "off",
                    ["cost-scan"] = s.ScanLocalCost ? "on" : "off",
                    ["language"] = s.Language,
                    ["widget-color"] = s.WidgetColor,
                    ["widget-size"] = s.WidgetSize,
                    ["start-with-windows"] = s.StartWithWindows ? "on" : "off",
                };
                if (a.Count > 1)
                {
                    if (!all.TryGetValue(a[1], out var v)) { Console.Error.WriteLine("Unknown setting. Known: " + string.Join(", ", all.Keys)); return 2; }
                    Console.WriteLine(v);
                }
                else if (json) Console.WriteLine(JsonSerializer.Serialize(all, Json.Options));
                else foreach (var kv in all) Console.WriteLine($"{kv.Key,-20} {kv.Value}");
                return 0;
            }
            case "set":
            {
                if (a.Count < 3) { Console.Error.WriteLine("usage: qb config set <setting> <value>"); return 2; }
                var v = a[2];
                bool On() => v.ToLowerInvariant() switch { "on" or "true" or "1" or "yes" => true, "off" or "false" or "0" or "no" => false, _ => throw new ArgumentException("Use on or off.") };
                switch (a[1])
                {
                    case "refresh": s.RefreshMinutes = Math.Clamp(int.Parse(v), 1, 120); break;
                    case "hotkey": s.Hotkey = v.Equals("off", StringComparison.OrdinalIgnoreCase) ? "" : v; break;
                    case "widget": s.ShowTaskbarWidget = On(); break;
                    case "widget-style": s.WidgetStyle = v is "rings" or "bars" ? v : throw new ArgumentException("Use rings or bars."); break;
                    case "widget-auto": s.WidgetAuto = On(); break;
                    case "all-monitors": s.WidgetAllMonitors = On(); break;
                    case "alerts": s.Alerts.Enabled = On(); break;
                    case "alert-thresholds": s.Alerts.Thresholds = v.Split(',').Select(x => Math.Clamp(int.Parse(x.Trim()), 1, 100)).Distinct().OrderBy(x => x).ToList(); break;
                    case "status": s.CheckServiceStatus = On(); break;
                    case "cost-scan": s.ScanLocalCost = On(); break;
                    case "language": s.Language = v is "auto" or "en" or "tr" or "de" or "es" or "fr" or "pt" or "ja" or "zh" ? v : throw new ArgumentException("Use auto, en, tr, de, es, fr, pt, ja or zh."); break;
                    case "widget-color": s.WidgetColor = v is "brand" or "status" or "mono" or "accent" ? v : throw new ArgumentException("Use brand, status, mono or accent."); break;
                    case "widget-size": s.WidgetSize = v is "compact" or "normal" or "large" ? v : throw new ArgumentException("Use compact, normal or large."); break;
                    case "start-with-windows": s.StartWithWindows = On(); break;
                    default: Console.Error.WriteLine("Unknown setting. See `qb config get`."); return 2;
                }
                s.Save();
                Console.WriteLine($"{a[1]} = {v}");
                return 0;
            }
            default:
                Console.Error.WriteLine("Unknown config command. Try `qb help`.");
                return 2;
        }
    }
}
