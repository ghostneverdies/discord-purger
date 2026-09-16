using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace DiscordPurger.Core;

public class DiscordApiClient
{
    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };
    private string? _token;

    private const string ApiBase = "https://discord.com/api/v9";
    private const string CdnBase = "https://cdn.discordapp.com";

    public string? Token => _token;

    public void SetToken(string? token) => _token = token;

    private HttpRequestMessage MakeRequest(HttpMethod method, string path, string? tokenOverride = null)
    {
        var req = new HttpRequestMessage(method, $"{ApiBase}{path}");
        var tok = tokenOverride ?? _token;
        if (!string.IsNullOrEmpty(tok))
            req.Headers.Add("Authorization", tok);
        req.Headers.UserAgent.ParseAdd("DiscordPurger/1.0");
        return req;
    }

    public async Task<JsonElement> SendAsync(HttpMethod method, string path, string? tokenOverride = null, HttpContent? content = null)
    {
        using var req = MakeRequest(method, path, tokenOverride);
        if (content != null) req.Content = content;

        using var resp = await _http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();

        if (resp.StatusCode == (HttpStatusCode)429)
            throw new RateLimitException(ReadRetryAfter(body));

        if (!resp.IsSuccessStatusCode)
        {
            var detail = "";
            try
            {
                if (!string.IsNullOrWhiteSpace(body))
                {
                    var err = JsonSerializer.Deserialize<JsonElement>(body);
                    if (err.TryGetProperty("code", out var c))
                        detail += $" code {c.GetInt32()}";
                    var msg = Str(err, "message");
                    if (!string.IsNullOrEmpty(msg))
                        detail += $" {msg}";
                }
            }
            catch { }
            throw new BridgeException("HTTP_ERROR", $"{(int)resp.StatusCode}{detail}".Trim());
        }

        if (string.IsNullOrWhiteSpace(body))
            return default;

        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    public async Task<DiscordAccount?> FetchAccountAsync(string token)
    {
        try
        {
            var json = await SendAsync(HttpMethod.Get, "/users/@me", tokenOverride: token);
            if (json.ValueKind == JsonValueKind.Undefined) return null;

            var id = Str(json, "id");
            var username = Str(json, "username");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(username)) return null;

            var discriminator = Str(json, "discriminator");
            var globalName = json.TryGetProperty("global_name", out var gn) ? gn.GetString() : null;
            var avatarUrl = BuildAvatarUrl(id, json);

            return new DiscordAccount(id, username, discriminator, globalName, avatarUrl, token);
        }
        catch
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<PrivateChannel>> GetDmsAsync(string? tokenOverride = null)
    {
        var json = await SendAsync(HttpMethod.Get, "/users/@me/channels", tokenOverride: tokenOverride);
        var list = new List<PrivateChannel>();
        if (json.ValueKind != JsonValueKind.Array) return list;

        foreach (var ch in json.EnumerateArray())
        {
            var id = Str(ch, "id");
            var type = ch.TryGetProperty("type", out var t) ? t.GetInt32() : 0;
            if (type != 1 && type != 3) continue;

            var kind = type == 1 ? "dm" : "group";
            var name = ResolveDmName(ch, kind);
            var avatarUrl = kind == "dm" ? BuildDmAvatarUrl(ch) : BuildGroupAvatarUrl(ch);
            int? memberCount = kind == "group" && ch.TryGetProperty("recipients", out var rc)
                ? rc.GetArrayLength()
                : null;

            list.Add(new PrivateChannel(id, name, kind, avatarUrl, memberCount));
        }
        return list;
    }

    public async Task<IReadOnlyList<DiscordGuild>> GetGuildsAsync(string? tokenOverride = null)
    {
        var json = await SendAsync(HttpMethod.Get, "/users/@me/guilds?with_counts=false", tokenOverride: tokenOverride);
        var list = new List<DiscordGuild>();
        if (json.ValueKind != JsonValueKind.Array) return list;

        foreach (var g in json.EnumerateArray())
        {
            var id = Str(g, "id");
            var name = Str(g, "name");
            var iconUrl = BuildGuildIconUrl(id, g);
            list.Add(new DiscordGuild(id, name, iconUrl));
        }
        return list;
    }

    public async Task<IReadOnlyList<GuildChannel>> GetGuildChannelsAsync(string guildId, string? tokenOverride = null)
    {
        var json = await SendAsync(HttpMethod.Get, $"/guilds/{guildId}/channels", tokenOverride: tokenOverride);
        var list = new List<GuildChannel>();
        if (json.ValueKind != JsonValueKind.Array) return list;

        foreach (var c in json.EnumerateArray())
        {
            var id = Str(c, "id");
            var name = Str(c, "name");
            var type = c.TryGetProperty("type", out var t) ? t.GetInt32() : 0;
            var purgeable = type == 0 || type == 5;
            list.Add(new GuildChannel(id, name, type, purgeable));
        }
        return list;
    }

    public async Task<FetchResult> FetchMessagesAsync(string channelId, string? before = null, string? tokenOverride = null)
    {
        var path = $"/channels/{channelId}/messages?limit=100";
        if (!string.IsNullOrEmpty(before)) path += $"&before={before}";

        try
        {
            var json = await SendAsync(HttpMethod.Get, path, tokenOverride: tokenOverride);
            var messages = new List<MessageInfo>();
            if (json.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in json.EnumerateArray())
                {
                    var mid = Str(m, "id");
                    var authorId = "";
                    if (m.TryGetProperty("author", out var author) && author.TryGetProperty("id", out var aid))
                        authorId = aid.GetString() ?? "";
                    var type = m.TryGetProperty("type", out var mt) ? mt.GetInt32() : 0;
                    var content = Str(m, "content");
                    messages.Add(new MessageInfo(mid, authorId, type, content));
                }
            }
            var hitEnd = messages.Count < 100;
            return new FetchResult(messages, false, null, hitEnd);
        }
        catch (RateLimitException ex)
        {
            return new FetchResult([], true, ex.RetryAfterSeconds, false);
        }
    }

    public async Task<DeleteResult> DeleteMessageAsync(string channelId, string messageId, string? tokenOverride = null)
    {
        try
        {
            await SendAsync(HttpMethod.Delete, $"/channels/{channelId}/messages/{messageId}", tokenOverride: tokenOverride);
            return new DeleteResult(DeleteStatus.Ok, null, null);
        }
        catch (RateLimitException ex)
        {
            return new DeleteResult(DeleteStatus.RateLimited, ex.RetryAfterSeconds, null);
        }
        catch (BridgeException ex)
        {
            return new DeleteResult(DeleteStatus.Failed, null, ex.Message);
        }
    }

    public static double ReadRetryAfter(string body)
    {
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(body);
            if (json.TryGetProperty("retry_after", out var ra) && ra.ValueKind == JsonValueKind.Number)
                return ra.GetDouble();
        }
        catch { }
        return 5.0;
    }

    public static string Str(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public static bool TryGetFields(JsonElement el, string prop, out JsonElement value)
        => el.TryGetProperty(prop, out value);

    private static bool HeaderIsAttachment(JsonElement msg)
        => msg.TryGetProperty("attachments", out var a) && a.GetArrayLength() > 0;

    private static string ResolveDmName(JsonElement ch, string kind)
    {
        if (ch.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
        {
            var name = n.GetString();
            if (!string.IsNullOrEmpty(name)) return name;
        }
        if (kind == "dm" && ch.TryGetProperty("recipients", out var recipients) && recipients.GetArrayLength() > 0)
        {
            var first = recipients[0];
            var gn = first.TryGetProperty("global_name", out var g) ? g.GetString() : null;
            var un = Str(first, "username");
            return !string.IsNullOrEmpty(gn) ? gn : un;
        }
        return "Group DM";
    }

    private static string? BuildDmAvatarUrl(JsonElement ch)
    {
        if (!ch.TryGetProperty("recipients", out var r) || r.GetArrayLength() == 0) return null;
        var first = r[0];
        var userId = Str(first, "id");
        var avatar = Str(first, "avatar");
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(avatar)) return null;
        return $"{CdnBase}/avatars/{userId}/{avatar}.png?size=128";
    }

    private static string? BuildGroupAvatarUrl(JsonElement ch)
    {
        var icon = Str(ch, "icon");
        var id = Str(ch, "id");
        if (string.IsNullOrEmpty(icon)) return null;
        return $"{CdnBase}/channel-icons/{id}/{icon}.png?size=128";
    }

    private static string? BuildGuildIconUrl(string guildId, JsonElement g)
    {
        var icon = Str(g, "icon");
        if (string.IsNullOrEmpty(icon)) return null;
        return $"{CdnBase}/icons/{guildId}/{icon}.png?size=128";
    }

    private static string? BuildAvatarUrl(string userId, JsonElement user)
    {
        var avatar = Str(user, "avatar");
        if (string.IsNullOrEmpty(avatar)) return null;
        return $"{CdnBase}/avatars/{userId}/{avatar}.png?size=128";
    }
}
