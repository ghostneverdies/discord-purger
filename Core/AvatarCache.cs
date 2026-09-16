using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace DiscordPurger.Core;

public static class AvatarCache
{
    public const string VirtualHost = "cache.local";

    public static readonly string Root;

    private static readonly ConcurrentDictionary<string, Lazy<Task<string?>>> InFlight = new();
    private static readonly SemaphoreSlim Semaphore = new(6);

    static AvatarCache()
    {
        Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DiscordPurger", "avatars");
        System.IO.Directory.CreateDirectory(Root);
    }

    public static void ResetFor(string accountId, string? avatarUrl = null)
    {
        if (!string.IsNullOrEmpty(avatarUrl) && FileIsCached(accountId, avatarUrl))
        {
            foreach (var kv in InFlight)
            {
                if (kv.Key.StartsWith(accountId + ":"))
                    InFlight.TryRemove(kv.Key, out _);
            }
            return;
        }

        var dir = System.IO.Path.Combine(Root, accountId);
        try
        {
            if (System.IO.Directory.Exists(dir))
                System.IO.Directory.Delete(dir, true);
            System.IO.Directory.CreateDirectory(dir);
        }
        catch { }
        foreach (var kv in InFlight)
        {
            if (kv.Key.StartsWith(accountId + ":"))
                InFlight.TryRemove(kv.Key, out _);
        }
    }

    private static bool FileIsCached(string accountId, string url)
    {
        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
            var ext = GetExtension(url);
            var path = System.IO.Path.Combine(Root, accountId, hash + ext);
            return System.IO.File.Exists(path);
        }
        catch { return false; }
    }

    public static async Task<string?> LocalizeAsync(string accountId, string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (url.Contains(VirtualHost)) return url;

        var key = $"{accountId}:{url}";
        var lazy = InFlight.GetOrAdd(key, _ => new Lazy<Task<string?>>(() => DownloadAsync(accountId, url)));
        return await lazy.Value;
    }

    private static async Task<string?> DownloadAsync(string accountId, string url)
    {
        await Semaphore.WaitAsync();
        try
        {
            var dir = System.IO.Path.Combine(Root, accountId);
            System.IO.Directory.CreateDirectory(dir);

            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));

            var ext = GetExtension(url);
            var fileName = hash + ext;
            var path = System.IO.Path.Combine(dir, fileName);

            if (System.IO.File.Exists(path))
                return BuildLocalUrl(accountId, fileName);

            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            var data = await http.GetByteArrayAsync(url);
            await System.IO.File.WriteAllBytesAsync(path, data);
            return BuildLocalUrl(accountId, fileName);
        }
        catch
        {
            return url;
        }
        finally
        {
            Semaphore.Release();
        }
    }

    private static string GetExtension(string url)
    {
        try
        {
            var uri = new Uri(url);
            var path = uri.AbsolutePath;
            var dot = path.LastIndexOf('.');
            if (dot >= 0)
            {
                var ext = path[dot..];
                if (ext.Length <= 6) return ext;
            }
        }
        catch { }
        return ".png";
    }

    private static string BuildLocalUrl(string accountId, string fileName)
        => $"https://{VirtualHost}/{accountId}/{fileName}";

    public static string StableName(string url)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        return hash;
    }
}
