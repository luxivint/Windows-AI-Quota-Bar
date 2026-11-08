# Upstream source and runtime provenance

- Project: https://github.com/liuzhao1225/codex-account-switcher
- Author: Zhao Liu (liuzhao1225), MIT; original license retained in `LICENSE` and shipped with Quota Bar.
- Snapshot: v0.1.27, commit `1f2deac486d857d1c4f5db4373c0c12dfa30307d`.
- Retained source: `SwitcherCore`, `SwitcherHost`, Windows C/ACL helpers, native C# transport/Desktop adapters, original Windows integration/close fixtures and shared Swift tests. This is the Windows integration subset, not a full macOS app checkout.
- Swift account-policy source is unchanged. `CoreClient.cs` has two transport-only adaptations: an `IsConnected` property for reconnecting a stopped host, and an optional platform callback for isolated tests. Production uses the original Windows Desktop/browser adapters. No account, credential, login or switch policy is reimplemented in those adaptations.

Official runtime package:

`https://github.com/liuzhao1225/codex-account-switcher/releases/download/v0.1.27/Codex-Account-Switcher-windows-x64.exe`

EXE SHA-256: `1fc3e5636b903377f4b87e7ca90ae88db3bf8e2355709e25b45259e5d47dab06`

Embedded runtime archive/cache digest: `484056741F47AA25DBA69D39E1C6B8BC5D62DF8026BB84596547A3D4125FB5C4`

Extracted `SwitcherHost.exe` SHA-256: `818ca6743261fd3d33d17411e9c954b7bebc543df584f5035d4543ccdf9e572f`

`tools/prepare-switcher-runtime.ps1` verifies the release, executes only its isolated self-test and stages the host, dependencies and license notices. The runtime binaries and download are local build artifacts, not checked-in source. The app build fails if the runtime has not been prepared. No floating latest-release lookup is used for the engine.
