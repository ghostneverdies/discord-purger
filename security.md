# Security Policy

## Overview

This application is an open-source Discord utility for deleting your own direct messages and guild
messages. The source code is fully public and intended to be audited. Users are encouraged to review
the implementation, dependencies, and network activity before use.

---

## Token Handling

The application offers **two** authentication paths:

1. **Auto-detection (default):** local detection of Discord tokens from your own user profile —
   Discord / Canary / PTB LevelDB storage files, Chromium-based browser local storage
   (Chrome, Edge, Brave), and the memory of running Discord processes. Candidate tokens are
   validated against Discord's API in the background and presented as a list of accounts; the raw
   token is **never displayed on screen**.

2. **Manual entry:** you paste a token (masked, paste/copy blocked) on the login screen.

In **both** paths, tokens that you sign in with are:

- Held only in process memory
- Never written to disk, logged, or persisted by this application
- Never shown in plaintext in the UI (auto-detection does not display tokens at all)
- Sent only to Discord's official API (`discord.com/api/v9`) — never to any third party
- Discarded when the application is closed

**Tokens are never exfiltrated, uploaded, or written to any external server or file under any
circumstance.** The only outbound HTTP requests are Discord API calls (`GET /users/@me` for
validation, `GET` for listing DMs/channels, and `DELETE` for purging messages) — all strictly
to `discord.com/api/v9`.

---

## Local File System Behavior

The application reads, from your own user profile:

- `%APPDATA%\discord(,canary,ptb)\Local Storage\leveldb` — LevelDB `.ldb` / `.log` files
- profile-local storage under `Chrome`, `Edge`, and `Brave` user-data folders

**Temporary files:** LevelDB files and crash-reported files are copied to uniquely-named temporary
files (via `Path.GetTempFileName()` in the system temp dir) so they can be read without lock
conflicts while Discord is running. Each temporary copy is deleted immediately after it is read —
nothing persists on disk after the scan finishes.

**Avatar cache:** profile/avatar images fetched from Discord's CDN (`cdn.discordapp.com`) are cached
under `%TEMP%\DiscordPurger\avatars\<accountId>\` so the UI renders fast. These are just the same
public images Discord serves in your client, keyed by account ID; the cache is deleted whenever you
re-check the account, and contains zero token or message data.

The application does **not**:

- Write tokens, credentials, or session data to disk
- Drop files outside its working directory or the temp folder
- Install services, drivers, or persistence mechanisms
- Modify system-level configuration or registry entries
- Create hidden background processes

---

## Memory Scanning Details

The application scans the virtual memory of running Discord processes to detect active session
tokens. To minimize exposure, the scan is restricted:

**Processes scanned:** only `Discord.exe`, `DiscordCanary.exe`, and `DiscordPTB.exe` — and only
processes whose command line indicates a **main** process (no `--type=` flag) or a **renderer**
process (`--type=renderer`). GPU, utility, and crashpad sub-processes are skipped entirely since
they never hold session tokens.

**Regions scanned:** only `MEM_COMMIT`, readable memory regions. `MEM_IMAGE` (mapped DLLs / EXEs)
and regions larger than 256 MB are skipped. A dot-byte pre-filter skips regions that contain no
floating-point-like token structure before any decoding is attempted.

**Scan strategy:** a **fast targeted pass** runs first, searching only for two byte patterns
observed in Chromium's memory:

- The ASCII string `Authorization` (the header name in the HTTP cache)
- The V8 / LevelDB record key prefix `6D 00 00 00 05 token 6D 00 00 00 03`

Only regions containing one of these anchors are decoded and regex-matched. If the fast pass finds
candidates, the scan stops there (~4 seconds). If the fast pass finds nothing (e.g. Discord
changed its internal layout in an update), a **full fallback scan** runs the token regex over all
readable regions of the remaining processes (~2 minutes).

All decoded text is treated as **UTF-8 only** — no UTF-16 decoding is performed, as Discord's
memory consistently uses single-byte encoding for token strings.

---

## Network Activity

This application communicates exclusively with official Discord API endpoints
(`discord.com/api/v9` and `cdn.discordapp.com`).

- No third-party APIs are used
- No telemetry or analytics of any kind
- No external servers are contacted beyond Discord's own CDN and API
- No background data collection occurs

All outbound traffic is strictly limited to:
- Token validation on login / account detection
- Loading your DM, group DM, and guild data
- Fetching user avatars from Discord's CDN
- Deleting your own messages via the Discord API

---

## Data Handling

This application does not:

- Exfiltrate user data of any kind
- Send personal information to external servers
- Store authentication data persistently between sessions
- Log private message content externally

All processing is performed locally on the user's device. Message content appears only in the
in-app activity log during a purge session and is not saved anywhere.

---

## What This Application Does NOT Do

- Steal or transmit Discord tokens or any other credentials to third parties
- Harvest passwords or session cookies from any source
- Access unrelated system data or processes
- Download or execute remote code
- Perform unauthorized data collection
- Extract tokens belonging to any account other than those logged in on your own machine

---

## False Positives

Security software may flag this application because it:

- Automates Discord API requests
- Uses authentication headers in HTTP requests
- Sends DELETE requests to Discord's API
- Reads local browser/Discord storage and process memory for account detection

Reading local storage and process memory for token detection is the same signature used by
credential-stealing malware, so antivirus engines may produce false positives. This application
does nothing beyond what is described in this document — once the scan is complete, tokens are
handled only in memory and never transmitted anywhere but Discord's own API.

---

## You can build on top of this, but here's how to obtain binaries safely

The project ships **prebuilt Windows binaries** as a zip archive in the GitHub
[Releases](https://github.com/ghostneverdies/discord-purger/releases) section. Because binaries
cannot be cryptographically verified unless signed (and this project is not code-signed), the
releases are designed so you can verify their integrity:

- **Download only from the official Releases page** on the GitHub repository — never from mirrors,
  random blogs, or file-sharing sites.
- **Watch for zip tampering:** the zip itself is a passive archive; the moment you extract and run
  `DiscordPurger.exe` you are executing whatever is inside. If a malicious copy of this repo had
  been republished elsewhere, that exe could do anything.
- **Verify the checksum** of the zip against the one posted in the release notes.
- **Or don't trust binaries at all:** build from source with `dotnet publish -c Release` (see the
  README) and compare the behavior — the source is fully public and auditable.

---

## Security Transparency

Users are encouraged to:

- Inspect the full source code before running it
- Monitor outbound network traffic during use (e.g. with Wireshark)
- Run the application from the official release or build it from source rather than unofficial builds
- Verify all dependencies independently

---

## Reporting Issues

If you discover unexpected behavior or a potential security issue:

- Open an issue on the repository
- Provide reproduction steps where possible
- Do not publicly share your token or any credentials

---

## Responsible Use

Users are responsible for ensuring compliance with:

- Discord's Terms of Service
- Local laws and regulations
- Platform usage policies

This project is provided for personal utility and educational purposes only.