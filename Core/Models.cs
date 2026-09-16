using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscordPurger.Core;

internal static class Json
{
    public static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public record DiscordAccount(
    string Id,
    string Username,
    string Discriminator,
    string? GlobalName,
    string? AvatarUrl,
    string Token
);

public record PrivateChannel(
    string Id,
    string Name,
    string Kind,
    string? AvatarUrl,
    int? MemberCount
);

public record DiscordGuild(
    string Id,
    string Name,
    string? IconUrl
);

public record GuildChannel(
    string Id,
    string Name,
    int Type,
    bool Purgeable
);

public record PurgeTarget(
    string Id,
    string Name,
    string Kind
);

public enum LogLevel { Inf, Ok, Err, Wrn, Dim }

public record LogEntry(string Level, string Text);

public record PurgeProgress(int Total);

public record RateLimitInfo(double Seconds);

public record PurgeResult(int TotalDeleted, bool StoppedByUser);

public record FetchStatus(string State, int Fetched, int? Total);

public record FetchResult(
    IReadOnlyList<MessageInfo> Messages,
    bool RateLimited,
    double? RetryAfter,
    bool HitEnd
);

public record MessageInfo(string Id, string AuthorId, int Type, string Content);

public enum DeleteStatus { Ok, RateLimited, Failed }

public record DeleteResult(
    DeleteStatus Status,
    double? RetryAfterSeconds,
    string? Error
);

public class BridgeException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public class RateLimitException(double retryAfterSeconds)
    : Exception($"Rate limited, retry after {retryAfterSeconds:F1}s")
{
    public double RetryAfterSeconds { get; } = retryAfterSeconds;
}

public record StorageScanResult(IReadOnlyList<string> Tokens, int FilesScanned);
public record MemoryScanResult(IReadOnlyList<string> Tokens, int ProcessCount);
public record ScanAccountsResult(IReadOnlyList<DiscordAccount> Accounts, bool DiscordRunning);
