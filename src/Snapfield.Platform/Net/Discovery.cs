using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Snapfield.Platform.Net;

/// <summary>A receiver seen on the local network via its UDP beacon.</summary>
public readonly record struct DiscoveredPeer(string Name, string Host, int Port);

/// <summary>
/// LAN auto-discovery over a UDP broadcast beacon (no mDNS dependency). A
/// listening receiver advertises "SNAPFIELD1\tname\tport" every couple seconds;
/// a controller listens and surfaces the senders so the user can pick one
/// instead of typing an IP. The pairing code is never broadcast — discovery only
/// fills in the address; the code still gates the (encrypted) connection.
/// </summary>
public static class Discovery
{
    public const int Port = 45655;
}

/// <summary>Broadcasts this machine's presence while it is acting as a receiver.</summary>
public sealed class Beacon : IDisposable
{
    private const string Magic = "SNAPFIELD1";
    private Thread? _thread;
    private volatile bool _stop;

    public void Start(string name, int tcpPort)
    {
        if (_thread is not null) return;
        _thread = new Thread(() => Run(name, tcpPort)) { IsBackground = true, Name = "Snapfield.Beacon" };
        _thread.Start();
    }

    private void Run(string name, int tcpPort)
    {
        var payload = Encoding.UTF8.GetBytes($"{Magic}\t{name}\t{tcpPort}");
        try
        {
            using var udp = new UdpClient { EnableBroadcast = true };
            var targets = BroadcastTargets();
            var iteration = 0;
            while (!_stop)
            {
                // Interfaces come and go (Wi-Fi switch, VPN) — refresh every ~30s.
                if (iteration++ % 15 == 0) targets = BroadcastTargets();
                foreach (var t in targets)
                {
                    try { udp.Send(payload, payload.Length, t); } catch { }
                }
                for (var i = 0; i < 20 && !_stop; i++) Thread.Sleep(100); // ~2s, responsive stop
            }
        }
        catch { /* broadcast unavailable — discovery just won't show us */ }
    }

    /// <summary>
    /// 255.255.255.255 only leaves on the default interface, so a machine with a
    /// VPN or virtual adapter can beacon into the wrong network. Also send the
    /// subnet-directed broadcast (e.g. 192.168.0.255) of every up IPv4 interface.
    /// </summary>
    private static List<IPEndPoint> BroadcastTargets()
    {
        var targets = new List<IPEndPoint> { new(IPAddress.Broadcast, Discovery.Port) };
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var mask = ua.IPv4Mask;
                    if (mask is null || mask.Equals(IPAddress.Any)) continue;
                    var a = ua.Address.GetAddressBytes();
                    var m = mask.GetAddressBytes();
                    var b = new byte[4];
                    for (var i = 0; i < 4; i++) b[i] = (byte)(a[i] | ~m[i]);
                    var bcast = new IPAddress(b);
                    if (!targets.Any(t => t.Address.Equals(bcast)))
                        targets.Add(new IPEndPoint(bcast, Discovery.Port));
                }
            }
        }
        catch { /* interface enumeration failed — the global broadcast still goes out */ }
        return targets;
    }

    public void Dispose() { _stop = true; _thread = null; }
}

/// <summary>Listens for receiver beacons and reports each one seen.</summary>
public sealed class DiscoveryListener : IDisposable
{
    private const string Magic = "SNAPFIELD1";
    private UdpClient? _udp;
    private Thread? _thread;
    private volatile bool _stop;

    public event Action<DiscoveredPeer>? Found;

    public void Start()
    {
        if (_thread is not null) return;
        try
        {
            _udp = new UdpClient();
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, Discovery.Port));
        }
        catch { _udp?.Dispose(); _udp = null; return; }

        _thread = new Thread(Run) { IsBackground = true, Name = "Snapfield.Discover" };
        _thread.Start();
    }

    private void Run()
    {
        var from = new IPEndPoint(IPAddress.Any, 0);
        while (!_stop)
        {
            byte[] data;
            try { data = _udp!.Receive(ref from); }
            catch { break; } // closed

            try
            {
                var text = Encoding.UTF8.GetString(data);
                var parts = text.Split('\t');
                if (parts.Length != 3 || parts[0] != Magic) continue;
                if (!int.TryParse(parts[2], out var port)) continue;
                Found?.Invoke(new DiscoveredPeer(parts[1], from.Address.ToString(), port));
            }
            catch { /* malformed packet — ignore */ }
        }
    }

    public void Dispose()
    {
        _stop = true;
        try { _udp?.Close(); } catch { }
        _udp = null;
        _thread = null;
    }
}
