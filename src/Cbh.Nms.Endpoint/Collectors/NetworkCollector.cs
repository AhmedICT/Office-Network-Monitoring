using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Cbh.Nms.Shared;

namespace Cbh.Nms.Endpoint.Collectors;

/// <summary>Adapters, default gateway and HO reachability (latency / loss), and total throughput since the last call.</summary>
public sealed class NetworkCollector
{
    private const int Pings = 4;
    private (long Rx, long Tx, DateTime At)? _last;

    public async Task<EndpointNetwork> CollectAsync(string? hoTarget, CancellationToken ct)
    {
        var nics = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .ToList();

        var infos = new List<NicInfo>();
        NetworkInterface? primary = null;
        IPAddress? gateway = null;
        long rx = 0, tx = 0;
        foreach (var n in nics)
        {
            var props = n.GetIPProperties();
            var ip = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
            var up = n.OperationalStatus == OperationalStatus.Up;
            if (up && ip is not null && !IPAddress.IsLoopback(ip) && !ip.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            {
                var gw = props.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                                                                     && !g.Address.Equals(IPAddress.Any))?.Address;
                if (gw is not null && primary is null) { primary = n; gateway = gw; }
                infos.Add(new NicInfo(n.Name, Kind(n), ip.ToString(), Mac(n), n.Speed > 0 ? n.Speed / 1_000_000 : 0, up));
            }
            else if (n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 && IsPhysical(n))
                infos.Add(new NicInfo(n.Name, Kind(n), null, Mac(n), 0, up));

            if (up)
            {
                try { var s = n.GetIPStatistics(); rx += s.BytesReceived; tx += s.BytesSent; }
                catch (NetworkInformationException) { }
            }
        }

        double rxKbps = 0, txKbps = 0;
        var now = DateTime.UtcNow;
        if (_last is { } l && now > l.At && rx >= l.Rx && tx >= l.Tx)
        {
            var secs = (now - l.At).TotalSeconds;
            rxKbps = Math.Round((rx - l.Rx) * 8 / 1000.0 / secs, 1);
            txKbps = Math.Round((tx - l.Tx) * 8 / 1000.0 / secs, 1);
        }
        _last = (rx, tx, now);

        var gwTask = gateway is null ? Task.FromResult<(double?, double?)>((null, null)) : PingAsync(gateway, ct);
        var hoTask = await ResolveAsync(hoTarget, ct) is { } ho ? PingAsync(ho, ct) : Task.FromResult<(double?, double?)>((null, null));
        var (gwRtt, gwLoss) = await gwTask;
        var (hoRtt, hoLoss) = await hoTask;

        var primaryInfo = primary is null ? null : infos.FirstOrDefault(i => i.Name == primary.Name);
        return new EndpointNetwork(primaryInfo?.Ip, primaryInfo?.Mac, gateway?.ToString(), gwRtt, gwLoss, hoRtt, hoLoss, rxKbps, txKbps, infos);
    }

    private static async Task<IPAddress?> ResolveAsync(string? host, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        if (IPAddress.TryParse(host, out var ip)) return ip;
        try { return (await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct)).FirstOrDefault(); }
        catch (SocketException) { return null; }
    }

    /// <summary>Average RTT of the replies and % lost, over <see cref="Pings"/> echoes.</summary>
    private static async Task<(double? Rtt, double? Loss)> PingAsync(IPAddress ip, CancellationToken ct)
    {
        using var ping = new Ping();
        var rtts = new List<long>();
        for (var i = 0; i < Pings; i++)
        {
            try
            {
                var r = await ping.SendPingAsync(ip, TimeSpan.FromSeconds(1), cancellationToken: ct);
                if (r.Status == IPStatus.Success) rtts.Add(r.RoundtripTime);
            }
            catch (PingException) { }
        }
        return (rtts.Count > 0 ? Math.Round(rtts.Average(), 1) : null, Math.Round(100.0 * (Pings - rtts.Count) / Pings, 1));
    }

    /// <summary>Hides Windows' filter / scheduler bindings ("Ethernet-WFP …-0000"), Wi-Fi Direct virtual
    /// adapters ("Local Area Connection* 9"), Bluetooth PAN and the kernel debugger NIC, which have no address.</summary>
    private static bool IsPhysical(NetworkInterface n) =>
        !n.Name.Contains("-0000", StringComparison.Ordinal)
        && !n.Name.StartsWith("Local Area Connection*", StringComparison.Ordinal)
        && !n.Description.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
        && !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
        && !n.Description.Contains("Kernel Debug", StringComparison.OrdinalIgnoreCase)
        && !n.Name.Contains("Kernel Debugger", StringComparison.OrdinalIgnoreCase)
        && n.GetPhysicalAddress().GetAddressBytes().Length == 6;

    private static string Kind(NetworkInterface n) => n.NetworkInterfaceType switch
    {
        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => "Ethernet",
        NetworkInterfaceType.Ppp => "PPP/VPN",
        var t => t.ToString(),
    };

    private static string? Mac(NetworkInterface n)
    {
        var b = n.GetPhysicalAddress().GetAddressBytes();
        return b.Length == 6 ? string.Join(":", b.Select(x => x.ToString("X2"))) : null;
    }
}
