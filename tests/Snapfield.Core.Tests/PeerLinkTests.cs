using System.Net;
using System.Net.Sockets;
using Snapfield.Core.Net;

namespace Snapfield.Core.Tests;

public class PeerLinkTests
{
    /// <summary>
    /// The receiver accepts exactly one client per listener, so while it holds a
    /// link a second controller's socket completes at the TCP level (the OS takes
    /// it into the backlog) and then nobody ever speaks. That used to hang the
    /// handshake read forever: the connection sat in the session's list, never
    /// firing Disconnected, never retrying, never being removed — and every later
    /// 연결 click was silently dropped as "already connected/ing".
    /// </summary>
    [Fact]
    public void SilentPeerFailsTheHandshakeInsteadOfHangingForever()
    {
        // Listening but never accepting: our connect lands in the backlog.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var link = new PeerLink();
        var down = new ManualResetEventSlim(false);
        var reason = "";
        link.Disconnected += r => { reason = r; down.Set(); };

        link.Connect("127.0.0.1", port, "1234");

        // Handshake bound is 8s; allow slack for a loaded CI machine.
        Assert.True(down.Wait(TimeSpan.FromSeconds(20)), "Disconnected never fired — the handshake hung.");
        Assert.NotEqual("", reason);
        Assert.False(link.IsConnected);

        listener.Stop();
    }

    /// <summary>A refused dial must report quickly, not after the OS SYN retries.</summary>
    [Fact]
    public void RefusedDialReportsPromptly()
    {
        // Bind then close to get a port nothing is listening on.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        using var link = new PeerLink();
        var down = new ManualResetEventSlim(false);
        link.Disconnected += _ => down.Set();

        link.Connect("127.0.0.1", port, "1234");

        Assert.True(down.Wait(TimeSpan.FromSeconds(15)), "a refused dial never reported.");
        Assert.False(link.IsConnected);
    }
}
