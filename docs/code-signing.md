# Code signing

Windows AI Quota Bar has two separate signatures. They protect against different things.

| Signature | What it is for | Status |
|---|---|---|
| **Update signature** (`.sig`, ECDSA P-256) | The in-app updater installs a file only if this signature matches the public key built into the app. | Every release since 0.1.2. See [SECURITY.md](../SECURITY.md). |
| **Authenticode** (signed `.exe`) | Windows shows the publisher instead of "Unknown publisher", and SmartScreen warns less. | Not yet. Applied for free signing for open-source projects (SignPath Foundation). |

## How a release is built

1. A version tag (`v0.5.0`) starts [`.github/workflows/release.yml`](../.github/workflows/release.yml) on GitHub's runners.
2. It runs the tests, builds the installer and portable zip, and records **build provenance**: a signed statement that these exact files came from this repository's workflow at this commit.
3. It creates a **draft** release. Nothing is public yet.
4. The maintainer runs [`tools/sign-release.ps1`](../tools/sign-release.ps1). It checks the provenance and the checksum, adds the update signature (the key stays on the maintainer's PC), and publishes.

Check a download yourself with the GitHub CLI:

```powershell
gh attestation verify .\WindowsAIQuotaBar-Setup-0.5.0.exe --repo valuex-creative/Windows-AI-Quota-Bar
```

Releases up to 0.4.0 were built on the maintainer's PC and have no provenance record.

## Code signing policy

This section becomes active once the SignPath Foundation certificate is issued.

- Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).
- Only files built by this repository's release workflow from a tagged commit are submitted for signing.
- **Committers and reviewers:** [valuex-creative](https://github.com/valuex-creative).
- **Approver** (approves every signing request): [valuex-creative](https://github.com/valuex-creative).
- **Privacy:** the program does not send any information to other networked systems unless the user asks it to. The exceptions it needs to work are listed in [SECURITY.md](../SECURITY.md): each provider's own API for the usage numbers, GitHub for update checks, the providers' public status pages. Each one except the usage APIs can be turned off.
