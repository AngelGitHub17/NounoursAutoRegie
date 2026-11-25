using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace NAR.Server.Services.Network;

public sealed record NetworkHost(string Address, string? Name);

/// <summary>
/// Cherche sur le réseau local les machines qui partagent des fichiers (port 445 ouvert),
/// et leur nom (DNS de la box, sinon nom NetBIOS de Windows).
/// </summary>
public sealed class NetworkScanner(ILogger<NetworkScanner> logger)
{
    private const int MaxHosts = 1024;

    public async Task<List<NetworkHost>> ScanAsync(CancellationToken ct)
    {
        var targets = LocalSubnets().SelectMany(Hosts).Distinct().Take(MaxHosts * 2).ToList();
        logger.LogInformation("Recherche de partages Windows sur {Count} adresse(s)", targets.Count);

        var found = new List<IPAddress>();
        await Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = 96, CancellationToken = ct }, async (ip, token) =>
        {
            if (await IsOpenAsync(ip, 445, TimeSpan.FromMilliseconds(600), token))
                lock (found) found.Add(ip);
        });

        var hosts = await Task.WhenAll(found.Select(async ip => new NetworkHost(ip.ToString(), await NameOfAsync(ip, ct))));
        return hosts.OrderBy(h => h.Name is null).ThenBy(h => h.Name).ThenBy(h => ip4Key(h.Address)).ToList();

        static uint ip4Key(string a) => IPAddress.TryParse(a, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork
            ? (uint)IPAddress.NetworkToHostOrder(BitConverter.ToInt32(ip.GetAddressBytes(), 0)) : 0;
    }

    /// <summary>Réseaux IPv4 des interfaces actives (limités à /22 pour rester rapide).</summary>
    private static IEnumerable<(IPAddress Address, int Prefix)> LocalSubnets()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var u in nic.GetIPProperties().UnicastAddresses)
            {
                if (u.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(u.Address)) continue;
                var bytes = u.Address.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254) continue; // auto-configuration
                yield return (u.Address, Math.Max(22, u.PrefixLength));
            }
        }
    }

    private static IEnumerable<IPAddress> Hosts((IPAddress Address, int Prefix) net)
    {
        var self = (uint)IPAddress.NetworkToHostOrder(BitConverter.ToInt32(net.Address.GetAddressBytes(), 0));
        var mask = net.Prefix == 0 ? 0u : uint.MaxValue << (32 - net.Prefix);
        var first = (self & mask) + 1;
        var last = (self | ~mask) - 1;
        for (var a = first; a <= last && a - first < MaxHosts; a++)
        {
            if (a == self) continue;
            yield return new IPAddress(BitConverter.GetBytes(IPAddress.HostToNetworkOrder((int)a)));
        }
    }

    private static async Task<bool> IsOpenAsync(IPAddress ip, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(ip, port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string?> NameOfAsync(IPAddress ip, CancellationToken ct)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(ip.ToString(), ct).WaitAsync(TimeSpan.FromSeconds(1.5), ct);
            if (!string.IsNullOrWhiteSpace(entry.HostName) && entry.HostName != ip.ToString())
                return entry.HostName.Split('.')[0];
        }
        catch { /* pas de nom DNS */ }
        return await NetBiosNameAsync(ip, ct);
    }

    /// <summary>Requête NetBIOS « node status » (UDP 137) : renvoie le nom de machine Windows.</summary>
    private static async Task<string?> NetBiosNameAsync(IPAddress ip, CancellationToken ct)
    {
        byte[] request =
        [
            0x13, 0x37, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x20, 0x43, 0x4B, .. Enumerable.Repeat((byte)0x41, 30), 0x00,
            0x00, 0x21, 0x00, 0x01,
        ];
        try
        {
            using var udp = new UdpClient();
            await udp.SendAsync(request, new IPEndPoint(ip, 137), ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(1));
            var result = await udp.ReceiveAsync(cts.Token);
            var data = result.Buffer;
            // En-tête (12) + nom (34) + type/classe/TTL/longueur (10), puis nombre de noms.
            const int countOffset = 56;
            if (data.Length <= countOffset) return null;
            int count = data[countOffset];
            for (var i = 0; i < count; i++)
            {
                var offset = countOffset + 1 + i * 18;
                if (offset + 18 > data.Length) break;
                var suffix = data[offset + 15];
                var flags = data[offset + 16];
                var isGroup = (flags & 0x80) != 0;
                if (suffix == 0x00 && !isGroup)
                    return Encoding.ASCII.GetString(data, offset, 15).Trim();
            }
        }
        catch { /* pas de réponse NetBIOS */ }
        return null;
    }
}
