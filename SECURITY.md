# Security

Windows AI Quota Bar reads the logins of your AI tools in order to show their usage. That makes it more sensitive than an ordinary tray app, so this page lists exactly what it touches, where data goes, and how to check a release yourself.

## Summary

- **No server of its own, no telemetry, no analytics.** The app only talks to the providers' own APIs and to GitHub for updates.
- **Read-mostly.** It reads the logins your tools already keep on this PC. It writes to another tool's file in only two cases, both listed below.
- **Encrypted storage.** Anything it keeps is encrypted with Windows DPAPI for your Windows user.
- **Signed updates.** Installers are signed. The app installs an update only if the signature matches a public key compiled into the app.
- **Your AI assistant can only read by default.** The MCP server is read-only unless you turn on account switching.

## What it reads and writes

| Provider | Reads | Writes | Sent to |
|---|---|---|---|
| Claude | The claude.ai session from the sign-in window inside the app, or the Claude Code CLI login (`~/.claude/.credentials.json`) | Claude Code's file, **only** when its token has expired and must be refreshed (see below) | `claude.ai`, `api.anthropic.com`, `console.anthropic.com` |
| Codex / ChatGPT | `~/.codex/auth.json` (from `codex login`) | Same file, **only** when its token has expired and must be refreshed (see below) | `chatgpt.com`, `auth.openai.com` |
| Google Antigravity | The `agy` login in Windows Credential Manager (`gemini:antigravity`) | That entry, **only** when *you* click **Switch** or **Add account** | `cloudcode-pa.googleapis.com`, `oauth2.googleapis.com` |
| OpenCode Go | Keys saved by OpenCode (`~/.local/share/opencode/opencode.db`), read-only | Nothing | `opencode.ai` |
| API-key providers | Keys you paste into Settings | Nothing outside the app | That provider's API only (`openrouter.ai`, `api.github.com`, `api.z.ai` / `open.bigmodel.cn`, `api.kimi.com`, `api.minimax.io` / `api.minimaxi.com`, `api.deepseek.com`, `api.moonshot.ai` / `api.moonshot.cn`) |
| Updates | — | — | `api.github.com` and `github.com` (this repository's releases) |

Other links in the app (dashboards, status pages) open in your browser. The app does not fetch them.

### Token refresh for Codex and Claude Code

Access tokens expire. When the Codex CLI or Claude Code token is rejected, the app refreshes it the same way the CLI would, and writes the result back so the CLI keeps working. It does this conservatively:

1. It takes a lock, so the app and `qb.exe` never refresh at the same time.
2. It re-reads the file. If the CLI has already refreshed the token, the app uses the CLI's token and refreshes nothing.
3. After the refresh it reads the file again. If the CLI changed the file in the meantime, the CLI's version wins and the app writes nothing.
4. The write is atomic (temporary file, then replace), so the file is never left half-written. All other fields in the file stay untouched.

The code is in [`TokenFile.cs`](src/QuotaBar.Core/TokenFile.cs).

### GitHub Copilot

**Use my GitHub CLI login** does not copy your `gh` token. The app asks `gh auth token` for it at each refresh, uses it for one request to `api.github.com/copilot_internal/user`, and stores nothing. If you paste a token yourself, it is stored encrypted like any other key.

### Google Antigravity

To refresh Antigravity tokens, the app uses the OAuth client of the Antigravity you have installed, read from the `agy` binary at runtime. No client secret is shipped in this repository.

## What it stores

Everything lives under `%LOCALAPPDATA%\QuotaBar`:

| File | Content | Protection |
|---|---|---|
| `secrets\*.bin` | claude.ai session, saved Antigravity accounts, API keys | DPAPI, current Windows user only |
| `settings.json` | Preferences and key labels (no keys) | Plain text |
| `snapshot.json` | Last usage numbers, plan names and account e-mails | Plain text |
| `quotabar.log` | Timings and errors (no tokens) | Plain text |
| `history\*.jsonl` | Usage percentages and reset times per limit, for the charts (kept 4 months) | Plain text |
| `alerts.json` | Which notifications were already shown in the current cycle | Plain text |
| `webview\` | The embedded browser profile used for claude.ai | Like a browser profile |

To erase everything, uninstall and answer **Yes** when the uninstaller asks whether to delete your data. You can also delete the folder yourself.

## Updates

- The app asks GitHub for the latest release at start and every 6 hours. You can turn this off in Settings → Updates.
- **Install automatically** is off by default.
- An update is installed only if the installer's **ECDSA P-256 signature** (the `.sig` file) verifies against the public key compiled into the app ([`Updates.cs`](src/QuotaBar.Core/Updates.cs)).
  - The private key exists only on the maintainer's PC, encrypted with DPAPI, and never in this repository or in CI.
  - So someone who could replace files on a GitHub release still could not get an installer accepted.
  - The `.sha256` file is an extra check for broken downloads.
- The installer is downloaded only from `github.com/valuex-creative/Windows-AI-Quota-Bar/releases/download/…`. The file stays locked from the moment it is verified until it starts, so it cannot be swapped in between.

Update signing key fingerprint (SHA-256 of the public key):

```
7d8b52a2253d4bbb99f303a4c94dc94f5e806827072e01ff5cd3a172edbaee13
```

## Verify a release yourself

**1. Checksum.** Compare the hash with the `.sha256` file on the release and with the SHA-256 that GitHub shows next to each asset:

```powershell
Get-FileHash .\WindowsAIQuotaBar-Setup-0.2.0.exe -Algorithm SHA256
```

**2. Signature** (PowerShell 7):

```powershell
$pub = 'MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEobHZOnIGIf4U9SkDrsULGKsKVxq/QPOm4doeHvk2qpWztdcbCyYM47SYqzw6p3ZTUjVQ71SE0qc8mhlKfhqk5w=='
$file = '.\WindowsAIQuotaBar-Setup-0.2.0.exe'
$k = [System.Security.Cryptography.ECDsa]::Create()
$read = 0; $k.ImportSubjectPublicKeyInfo([Convert]::FromBase64String($pub), [ref]$read)
$k.VerifyData([IO.File]::ReadAllBytes($file), [Convert]::FromBase64String((Get-Content "$file.sig" -Raw).Trim()), 'SHA256')   # True = genuine
```

**3. Scan it.** Upload the installer to [VirusTotal](https://www.virustotal.com/) or scan it with your antivirus.

**4. Or build it yourself.** Everything needed is in this repository (see the README). `build-release.ps1` produces the same installer layout.

### Known limitations

- **The installer is not Authenticode-signed yet,** so Windows SmartScreen shows a warning on first run. Update signing (above) protects the in-app updater. Authenticode would additionally let Windows itself vouch for the file.
- **The claude.ai session grants access to your Claude account,** just like the cookie in your browser. It is stored encrypted and never leaves your PC except in requests to claude.ai. **Sign out** in Settings deletes it.

## Your AI assistant (MCP)

`qb mcp` offers two read-only tools by default:

- `get_usage`
- `list_antigravity_accounts`

`switch_antigravity_account` is listed only after you enable **Let assistants switch Antigravity accounts** in Settings → Ask your AI assistant. Calls to it are refused while that setting is off.

## Reporting a vulnerability

Please report security problems privately through **[Security → Report a vulnerability](https://github.com/valuex-creative/Windows-AI-Quota-Bar/security/advisories/new)** on this repository, not in a public issue.
