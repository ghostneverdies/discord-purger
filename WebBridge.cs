using System.Collections.Concurrent;
using System.Text.Json;
using DiscordPurger.Core;
using Microsoft.Web.WebView2.Core;

namespace DiscordPurger;

public class WebBridge
{
    private readonly CoreWebView2 _wv;
    private readonly DiscordApiClient _api;
    private readonly Dictionary<string, DiscordAccount> _foundAccounts = new();
    private readonly ConcurrentDictionary<string, CachedAccountData> _accountData = new();
    private CancellationTokenSource? _purgeCts;
    private CancellationTokenSource? _netCheckCts;
    private DiscordAccount? _activeAccount;
    private PurgeEngine? _engine;

    private sealed class CachedAccountData
    {
        public List<object>? Dms;
        public List<object>? Guilds;
    }

    private static readonly JsonSerializerOptions SJO = Json.Opts;

    private const string BridgeScript = """
        (() => {
            const _h = {};
            const _p = {};
            let _id = 0;

            window.chrome.webview.addEventListener('message', (e) => {
                const m = e.data;
                if (m.type === 'event') {
                    const fns = _h[m.event];
                    if (fns) fns.forEach(fn => fn(m.data));
                } else if (m.type === 'response') {
                    const p = _p[m.id];
                    if (p) {
                        delete _p[m.id];
                        if (m.error) {
                            const er = new Error(m.error);
                            er.code = m.code || 'ERROR';
                            p.reject(er);
                        } else p.resolve(m.result);
                    }
                }
            });

            function call(method, args) {
                return new Promise((resolve, reject) => {
                    const id = ++_id;
                    _p[id] = { resolve, reject };
                    window.chrome.webview.postMessage({ type: 'call', id, method, args });
                });
            }

            window.bridge = {
                on(event, fn) {
                    if (!_h[event]) _h[event] = [];
                    _h[event].push(fn);
                },
                getState:        ()       => call('getState', {}),
                validateToken:   (args)   => call('validateToken', args),
                selectAccount:   (args)   => call('selectAccount', args),
                scanAccounts:    ()       => call('scanAccounts', {}),
                preloadAccounts: (args)   => call('preloadAccounts', args),
                getDms:          ()       => call('getDms', {}),
                getGuilds:       ()       => call('getGuilds', {}),
                getGuildChannels:(args)   => call('getGuildChannels', args),
                startPurge:      (args)   => call('startPurge', args),
                stopPurge:       ()       => call('stopPurge', {}),
            };
        })();
        """;

    public WebBridge(CoreWebView2 wv)
    {
        _wv = wv;
        _api = new DiscordApiClient();
        wv.WebMessageReceived += OnMessage;
        _ = wv.AddScriptToExecuteOnDocumentCreatedAsync(BridgeScript);
    }

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.WebMessageAsJson;
            var msg = JsonSerializer.Deserialize<JsonElement>(json);
            var type = msg.TryGetProperty("type", out var tp) ? tp.GetString() : "";
            if (type != "call") return;

            var method = msg.TryGetProperty("method", out var mt) ? mt.GetString() : "";
            var id = msg.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0;
            var args = msg.TryGetProperty("args", out var a) ? a : default;

            switch (method)
            {
                case "getState":
                    await HandleGetState(id);
                    break;
                case "validateToken":
                    await HandleValidateToken(id, args);
                    break;
                case "selectAccount":
                    await HandleSelectAccount(id, args);
                    break;
                case "scanAccounts":
                    await HandleScanAccounts(id);
                    break;
                case "preloadAccounts":
                    HandlePreloadAccounts(id, args);
                    break;
                case "getDms":
                    await HandleGetDms(id);
                    break;
                case "getGuilds":
                    await HandleGetGuilds(id);
                    break;
                case "getGuildChannels":
                    await HandleGetGuildChannels(id, args);
                    break;
                case "startPurge":
                    await HandleStartPurge(id, args);
                    break;
                case "stopPurge":
                    HandleStopPurge(id);
                    break;
                default:
                    await SendResponse(id, null, $"Unknown method: {method}");
                    break;
            }
        }
        catch (Exception ex)
        {
            try
            {
                var msg2 = JsonSerializer.Deserialize<JsonElement>(e.WebMessageAsJson);
                var id2 = msg2.TryGetProperty("id", out var idEl2) ? idEl2.GetInt32() : 0;
                var code = ex is BridgeException be ? be.Code : "ERROR";
                await SendResponse(id2, null, ex.Message, code);
            }
            catch { }
        }
    }

    private async Task HandleGetState(int id)
    {
        if (_activeAccount != null)
        {
            StartNetCheck();
            await SendResponse(id, new { account = StripToken(_activeAccount) });
        }
        else
            await SendResponse(id, new { });
    }

    private async Task HandleValidateToken(int id, JsonElement args)
    {
        var token = args.TryGetProperty("token", out var t) ? t.GetString()?.Trim() : null;
        if (string.IsNullOrEmpty(token))
            throw new BridgeException("NO_TOKEN", "Please paste your token first.");

        var account = await _api.FetchAccountAsync(token);
        if (account == null)
            throw new BridgeException("INVALID_TOKEN", "Could not validate this token with Discord.");

        await AuthenticateAsync(account);
        await SendResponse(id, StripToken(account));
    }

    private async Task HandleSelectAccount(int id, JsonElement args)
    {
        var accountId = args.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrEmpty(accountId))
            throw new BridgeException("NO_ID", "No account ID provided.");

        if (!_foundAccounts.TryGetValue(accountId, out var account))
            throw new BridgeException("NOT_FOUND", "Account not found. Please scan again.");

        await AuthenticateAsync(account);
        await SendResponse(id, StripToken(account));
    }

    private async Task HandleScanAccounts(int id)
    {
        var result = await ScanAccountsAsync();
        await SendResponse(id, new
        {
            accounts = result.Accounts.Select(a => StripToken(a)).ToList(),
            discordRunning = result.DiscordRunning
        });
    }

    private async Task HandleGetDms(int id)
    {
        if (_activeAccount == null)
            throw new BridgeException("NO_ACCOUNT", "Not authenticated.");

        var data = _accountData.GetOrAdd(_activeAccount.Id, _ => new CachedAccountData());
        data.Dms ??= await BuildDmsPayloadAsync(_activeAccount, _activeAccount.Token);
        await SendResponse(id, data.Dms);
    }

    private async Task HandleGetGuilds(int id)
    {
        if (_activeAccount == null)
            throw new BridgeException("NO_ACCOUNT", "Not authenticated.");

        var data = _accountData.GetOrAdd(_activeAccount.Id, _ => new CachedAccountData());
        data.Guilds ??= await BuildGuildsPayloadAsync(_activeAccount, _activeAccount.Token);
        await SendResponse(id, data.Guilds);
    }

    private async Task<List<object>> BuildDmsPayloadAsync(DiscordAccount account, string? token)
    {
        var dms = await _api.GetDmsAsync(tokenOverride: token);
        var result = new List<object>();
        foreach (var dm in dms)
        {
            var localized = await AvatarCache.LocalizeAsync(account.Id, dm.AvatarUrl);
            result.Add(new
            {
                dm.Id,
                dm.Name,
                dm.Kind,
                AvatarUrl = localized,
                dm.MemberCount
            });
        }
        return result;
    }

    private async Task<List<object>> BuildGuildsPayloadAsync(DiscordAccount account, string? token)
    {
        var guilds = await _api.GetGuildsAsync(tokenOverride: token);
        var result = new List<object>();
        foreach (var g in guilds)
        {
            var localized = await AvatarCache.LocalizeAsync(account.Id, g.IconUrl);
            result.Add(new
            {
                g.Id,
                g.Name,
                IconUrl = localized
            });
        }
        return result;
    }

    private void HandlePreloadAccounts(int id, JsonElement args)
    {
        var ids = new List<string>();
        if (args.ValueKind == JsonValueKind.Object &&
            args.TryGetProperty("ids", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in arr.EnumerateArray())
            {
                var s = e.GetString();
                if (!string.IsNullOrEmpty(s)) ids.Add(s);
            }
        }

        if (ids.Count > 0)
            _ = Task.Run(() => PreloadAccountsAsync(ids));

        SendResponse(id, "ok");
    }

    private async Task PreloadAccountsAsync(List<string> ids)
    {
        foreach (var accountId in ids)
        {
            try
            {
                if (!_foundAccounts.TryGetValue(accountId, out var account)) continue;
                _ = await AvatarCache.LocalizeAsync(account.Id, account.AvatarUrl);

                var data = _accountData.GetOrAdd(accountId, _ => new CachedAccountData());
                data.Guilds ??= await BuildGuildsPayloadAsync(account, account.Token);
                data.Dms ??= await BuildDmsPayloadAsync(account, account.Token);
            }
            catch (Exception ex)
            {
                Emit("log", new LogEntry("dim", $"Preload {accountId} failed: {ex.Message}"));
            }
        }
    }

    private async Task HandleGetGuildChannels(int id, JsonElement args)
    {
        var guildId = args.TryGetProperty("guildId", out var g) ? g.GetString() : null;
        if (string.IsNullOrEmpty(guildId))
            throw new BridgeException("NO_GUILD", "No guild ID provided.");

        var channels = await _api.GetGuildChannelsAsync(guildId);
        await SendResponse(id, channels);
    }

    private async Task HandleStartPurge(int id, JsonElement args)
    {
        if (_activeAccount == null)
            throw new BridgeException("NO_ACCOUNT", "Not authenticated.");

        _purgeCts?.Cancel();
        _purgeCts = new CancellationTokenSource();
        var ct = _purgeCts.Token;

        var targets = JsonSerializer.Deserialize<List<PurgeTarget>>(args.GetProperty("targets").GetRawText(), SJO);
        var delayMs = args.TryGetProperty("delayMs", out var d) ? d.GetDouble() : 800;

        if (targets == null || targets.Count == 0)
            throw new BridgeException("NO_TARGETS", "No targets selected.");

        _engine ??= new PurgeEngine(_api);
        _ = RunPurgeAsync(targets, delayMs / 1000.0, ct);

        await SendResponse(id, "ok");
    }

    private void HandleStopPurge(int id)
    {
        _purgeCts?.Cancel();
        SendResponse(id, "ok").ConfigureAwait(false);
    }

    private async Task RunPurgeAsync(List<PurgeTarget> targets, double delaySeconds, CancellationToken ct)
    {
        try
        {
            var result = await _engine!.PurgeAsync(
                _activeAccount!.Id,
                targets,
                delaySeconds,
                entry => Emit("log", entry),
                progress => Emit("purgeDeleted", progress),
                seconds => Emit("rateLimited", new { seconds }),
                ct);

            Emit("log", new LogEntry("ok", $"✅  Done — {result.TotalDeleted} message(s) deleted."));
            Emit("purgeDone", new { total = result.TotalDeleted });
        }
        catch (OperationCanceledException)
        {
            Emit("log", new LogEntry("wrn", "⏹️  Stop requested…"));
            Emit("purgeDone", new { total = 0 });
        }
        catch (Exception ex)
        {
            Emit("log", new LogEntry("err", $"✗  {ex.Message}"));
            Emit("purgeDone", new { total = 0 });
        }
    }

    private async Task AuthenticateAsync(DiscordAccount account)
    {
        _activeAccount = account;
        _api.SetToken(account.Token);

        AvatarCache.ResetFor(account.Id, account.AvatarUrl);
        StartNetCheck();
    }

    private async Task<ScanAccountsResult> ScanAccountsAsync()
    {
        var discordRunning = TokenScanner.IsDiscordRunning();

        Emit("scanPhase", new { text = "Reading Discord & browser storage…" });
        var storageTask = Task.Run(TokenScanner.ScanStorage);

        MemoryScanResult memResult = new([], 0);
        if (discordRunning)
        {
            Emit("scanPhase", new { text = "Scanning memory…" });
            memResult = await Task.Run(() => TokenScanner.ScanMemory(
                phase => Emit("scanPhase", new { text = phase })));
        }

        var storageResult = await storageTask;

        Emit("log", new LogEntry("inf", $"Storage scan: {storageResult.FilesScanned} files, {storageResult.Tokens.Count} tokens"));
        if (discordRunning)
            Emit("log", new LogEntry("inf", $"Memory scan: {memResult.ProcessCount} processes, {memResult.Tokens.Count} tokens"));

        Emit("scanPhase", new { text = "Validating tokens…" });

        var allTokens = storageResult.Tokens.Concat(memResult.Tokens).Distinct().ToList();
        var validAccounts = new List<DiscordAccount>();

        foreach (var token in allTokens)
        {
            try
            {
                var account = await _api.FetchAccountAsync(token);
                if (account != null)
                {
                    _foundAccounts[account.Id] = account;
                    validAccounts.Add(account);
                    Emit("log", new LogEntry("ok", $"Found: {account.Username}#{account.Discriminator}"));
                }
            }
            catch
            {
                Emit("log", new LogEntry("dim", "Invalid token skipped"));
            }
        }

        Emit("scanPhase", new { text = "Done" });
        return new ScanAccountsResult(validAccounts, discordRunning);
    }

    private void StartNetCheck()
    {
        _netCheckCts?.Cancel();
        _netCheckCts = new CancellationTokenSource();
        _ = RunNetCheckLoopAsync(_netCheckCts.Token);
    }

    private async Task RunNetCheckLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var connected = await NetCheck.CheckAsync(ct);
            Emit("netStatus", new { connected });
            try { await Task.Delay(5000, ct); }
            catch (TaskCanceledException) { break; }
        }
    }

    public void Emit(string eventName, object data)
    {
        try
        {
            var msg = JsonSerializer.Serialize(new { type = "event", @event = eventName, data }, SJO);
            _wv.PostWebMessageAsJson(msg);
        }
        catch { }
    }

    private Task SendResponse(int id, object? result = null, string? error = null, string? code = null)
    {
        try
        {
            var msg = JsonSerializer.Serialize(new { type = "response", id, result, error, code }, SJO);
            _wv.PostWebMessageAsJson(msg);
        }
        catch { }
        return Task.CompletedTask;
    }

    private static object StripToken(DiscordAccount a) => new
    {
        a.Id,
        a.Username,
        a.Discriminator,
        a.GlobalName,
        a.AvatarUrl
    };
}
