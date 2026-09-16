namespace DiscordPurger.Core;

public class PurgeEngine
{
    private readonly DiscordApiClient _api;

    public PurgeEngine(DiscordApiClient api) => _api = api;

    public async Task<PurgeResult> PurgeAsync(
        string accountId,
        IReadOnlyList<PurgeTarget> targets,
        double delaySeconds,
        Action<LogEntry>? onLog,
        Action<PurgeProgress>? onProgress,
        Action<double>? onRateLimited,
        CancellationToken ct)
    {
        var total = 0;

        foreach (var target in targets)
        {
            if (ct.IsCancellationRequested) break;

            onLog?.Invoke(new LogEntry("inf", $"━━  Purging {target.Kind}: {target.Name}…"));

            var deleteCount = await PurgeChannel(target.Id, target.Name, accountId, delaySeconds, onLog, onProgress, onRateLimited, ct);
            total += deleteCount;
        }

        return new PurgeResult(total, ct.IsCancellationRequested);
    }

    private async Task<int> PurgeChannel(
        string channelId,
        string channelName,
        string accountId,
        double delaySeconds,
        Action<LogEntry>? onLog,
        Action<PurgeProgress>? onProgress,
        Action<double>? onRateLimited,
        CancellationToken ct)
    {
        var deleted = 0;
        string? before = null;

        while (!ct.IsCancellationRequested)
        {
            FetchResult fetch;
            try
            {
                fetch = await _api.FetchMessagesAsync(channelId, before);
            }
            catch (BridgeException ex)
            {
                onLog?.Invoke(new LogEntry("err", $"✗  Fetch failed ({ex.Message})"));
                break;
            }

            if (fetch.RateLimited)
            {
                var wait = fetch.RetryAfter ?? 5.0;
                onRateLimited?.Invoke(wait);
                onLog?.Invoke(new LogEntry("wrn", $"⚠️  Rate limited {wait:F1}s"));
                await SafeDelay(wait, ct);
                continue;
            }

            if (fetch.Messages.Count == 0)
            {
                onLog?.Invoke(new LogEntry("ok", $"✓  {channelName} — done."));
                break;
            }

            var ownMessages = fetch.Messages
                .Where(m => m.AuthorId == accountId && (m.Type == 0 || m.Type == 19))
                .ToList();

            if (ownMessages.Count == 0)
            {
                if (fetch.HitEnd)
                {
                    onLog?.Invoke(new LogEntry("ok", $"✓  {channelName} — start."));
                    break;
                }
                before = fetch.Messages[^1].Id;
                continue;
            }

            foreach (var msg in ownMessages)
            {
                if (ct.IsCancellationRequested) break;

                while (!ct.IsCancellationRequested)
                {
                    var result = await _api.DeleteMessageAsync(channelId, msg.Id);

                    if (result.Status == DeleteStatus.Ok)
                    {
                        deleted++;
                        onProgress?.Invoke(new PurgeProgress(deleted));
                        var text = string.IsNullOrWhiteSpace(msg.Content)
                            ? "[attachment]"
                            : msg.Content.Replace('\n', ' ').Trim();
                        if (text.Length > 60) text = text[..60];
                        onLog?.Invoke(new LogEntry("ok", $"🗑️  [{deleted}]  {text}"));
                        break;
                    }

                    if (result.Status == DeleteStatus.RateLimited)
                    {
                        var wait = result.RetryAfterSeconds ?? 5.0;
                        onRateLimited?.Invoke(wait);
                        onLog?.Invoke(new LogEntry("wrn", $"⚠️  Rate limited {wait:F1}s"));
                        await SafeDelay(wait, ct);
                        continue;
                    }

                    if (result.Error != null && result.Error.StartsWith("404"))
                    {
                        onLog?.Invoke(new LogEntry("dim", "⚠️  Already gone."));
                        break;
                    }

                    onLog?.Invoke(new LogEntry("err", $"✗  Failed to delete ({result.Error})"));
                    break;
                }

                if (delaySeconds > 0 && !ct.IsCancellationRequested)
                    await SafeDelay(delaySeconds, ct);
            }

            if (fetch.HitEnd) break;

            before = fetch.Messages[^1].Id;
        }

        return deleted;
    }

    private static async Task SafeDelay(double seconds, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
        }
        catch (TaskCanceledException) { }
    }
}
