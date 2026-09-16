# Discord DM Purger

A clean, privacy-focused tool to delete your own Discord DMs, group DMs, and guild-channel messages quickly and efficiently.

Runs as a WebView2 desktop app: a raw HTML/CSS/JS frontend talks to a C# backend over a native bridge.

---

## ⚠️ Disclaimer

Use this tool **at your own risk**.

**This tool automates Discord's API using your own user token — this is known as "self-botting" and is explicitly forbidden by [Discord's Terms of Service](https://discord.com/terms).** Self-botting can result in:

- **Permanent account termination** without warning
- **Data loss** — DMs, servers, and friends cannot be recovered after a ban
- **Rate-limiting or IP restrictions**

The author is **not responsible** for any account restrictions, bans, data loss, or other consequences resulting from the use of this project.

---

## 🔐 How Authentication Works

Two ways to sign in:

1. **Auto-detect (default)** — The app scans your *own* local Discord and browser storage
   (Discord / Canary / PTB LevelDB files, Chrome, Edge, Brave) plus the memory of any running
   Discord processes, validates the candidates against Discord's API in the background, and shows
   you a list of the accounts it found. You pick one by name and avatar — **the token is never
   displayed on screen**.

2. **Manual entry** — If auto-detect finds nothing (or you prefer it), switch to manual mode and
   paste a token into the password field.

In both cases the token:

- Lives only in process memory
- Is never stored, logged, or written to disk by this application
- Is never shown in plaintext in the UI
- Leaves the machine **only** when sent to Discord's own API (validation and API calls)

---

## ⬇️ Download the latest release

Prebuilt binaries are published as a **zip archive** in the
[Releases](https://github.com/ghostneverdies/discord-purger/releases) page.

1. Download the latest `DiscordPurger-<version>.zip`.
2. **Right-click → Properties → Unblock**, then extract it anywhere.
3. Run `DiscordPurger.exe`.

> No installation required. The app is **not persisted** anywhere by itself — delete the extracted
> folder and it's gone.

---

## 🚀 Building from Source

### Requirements (for building)

- **.NET 10 SDK** — [Download](https://dotnet.microsoft.com/download/dotnet/10.0)
- **WebView2 Runtime** — [Download](https://developer.microsoft.com/microsoft-edge/webview2/) (preinstalled on Windows 11)
- An internet connection

**End users of the prebuilt release** only need the **.NET 10 Desktop Runtime** (framework-dependent
build) plus WebView2 — the SDK is not required to run `DiscordPurger.exe`.

> The frontend lives in `web/` (plain HTML/CSS/JS — no libraries) and is served by the WPF host
> from a virtual host. Rebuild or edit the files in `web/` and re-run; the host copies them on build.

### 1. Clone the repository

```bash
git clone https://github.com/ghostneverdies/discord-purger.git
cd discord-purger
```

### 2. Restore & run

```bash
dotnet restore
dotnet run
```

### 3. Build a release zip

```bash
dotnet publish -c Release
```

Zip the `bin/Release/net10.0-windows/publish/` output and ship it — that is exactly what the GitHub
Releases zip contains.

---

## 🖥️ How to Use

1. **Start the app** — it scans for Discord accounts automatically.
2. **Pick an account** from the detected list (or choose *"Have a token already?"* in the top-right
   corner to paste a token manually).
3. Once signed in, your DMs, group DMs, and servers load automatically.
4. Use the hamburger menu (left rail) to switch between **Servers**, **DMs**, and **Groups**.
5. **Servers** — expand a server to see its text channels; select the channels you want to purge.
   **DMs / Groups** — select individual conversations.
6. Set the **delay** between deletions (default 0.8s — lower values increase rate-limit risk).
7. Click **▶ Start Purge** and monitor the live activity log. **⏹ Stop** aborts mid-run.

Selected conversations appear as removable chips above the log.

---

## ⚙️ Technical Notes

- **Stack:** WPF host + WebView2 + raw HTML/CSS/JS frontend; C# backend does all Discord API I/O.
- **Bridge:** the frontend calls `window.bridge.*` (promises). Events: `netStatus`, `log`,
  `purgeDeleted`, `rateLimited`, `purgeDone`.
- **Purge engine:** fetches message history page-by-page, deletes only your own messages, honors
  Discord rate-limit headers (`retry_after`) automatically, and aborts cleanly on Stop.
- **Group DMs:** detected and shown as a separate category with member counts and channel avatars.

---

## 📌 Notes

- Only your own messages are deleted — you cannot delete messages sent by others.
- Deleted messages cannot be recovered.
- Using very low delay values increases the chance of being rate-limited by Discord.
- Auto-detection reads files in your own user profile. Because it also scans process memory, some
  antivirus engines may flag the binary — see `security.md` for the full disclosure.
- **Only download the zip from the official Releases page** — never run builds from random mirrors.
- Windows SmartScreen may warn about the prebuilt exe because it's unsigned; it's from the official
  repo, so choose *More info → Run anyway* if you trust the source.

---

## 🛡️ False Positives

Discord API automation, `Authorization` headers, and reads of local browser/Discord storage +
process memory match the signature of credential-stealing malware, so antivirus engines and
Windows SmartScreen may flag the binary. This is a false positive — the app does nothing beyond
what's described here and in `security.md`:

- It only ever reads **your own** Discord/browser storage and Discord process memory
- Tokens are never exfiltrated, stored to disk, or sent anywhere except Discord's own API
- If you're unsure, build from source (instructions above) and review the code — it's fully public

---

## ⭐ Support

If you find this project useful, consider giving it a star on GitHub.