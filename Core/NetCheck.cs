using System.Net.Sockets;

namespace DiscordPurger.Core;

public static class NetCheck
{
    private static readonly (string Host, int Port)[] Targets =
    [
        ("8.8.8.8", 53),
        ("1.1.1.1", 53),
        ("9.9.9.9", 53)
    ];

    public static async Task<bool> CheckAsync(CancellationToken ct = default)
    {
        foreach (var (host, port) in Targets)
        {
            try
            {
                using var client = new TcpClient();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(3000);
                await client.ConnectAsync(host, port, cts.Token);
                return true;
            }
            catch { }
        }
        return false;
    }
}
