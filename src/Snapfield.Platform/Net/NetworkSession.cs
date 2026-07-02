using Snapfield.Core.Model;
using Snapfield.Core.Net;
using Snapfield.Core.Persistence;
using Snapfield.Platform.Input;

namespace Snapfield.Platform.Net;

public enum PeerRole { None, Controller, Receiver }

/// <summary>
/// A cross-machine sharing session.
///
/// The machine that calls <see cref="Connect"/> is the CONTROLLER (it has the
/// physical keyboard/mouse); the one that calls <see cref="Listen"/> is the
/// RECEIVER. On connect both send <see cref="MsgType.Hello"/> with their monitors;
/// the controller then builds a combined layout (local monitors + the receiver's
/// placed to the right) and runs the <see cref="InputEngine"/>. When the cursor
/// crosses onto a receiver monitor, the controller streams cursor/button/wheel/key
/// events; the receiver injects them.
///
/// The session outlives individual connections: when a link drops it releases
/// the engine (freeing captured input) and automatically retries — the
/// controller re-connects, the receiver re-listens — until <see cref="Dispose"/>.
/// </summary>
public sealed class NetworkSession : IDisposable
{
    private const int ReconnectDelayMs = 3000;
    private const int RelistenDelayMs = 500;

    private readonly string _localMachineId;
    private readonly IReadOnlyList<MonitorInfo> _localMonitors;

    private PeerLink? _link;
    private InputEngine? _engine;
    private int _linkGen;                 // stale-event guard: one generation per link
    private volatile bool _disposed;
    private int _reconnectAttempt;
    private string _host = "";
    private int _port;

    public PeerRole Role { get; private set; } = PeerRole.None;
    public string RemoteMachineId { get; private set; } = "";
    public int RemoteMonitorCount { get; private set; }

    private long _rxCount;
    private double _sensitivity = 1.0;

    public event Action<string>? Status;
    public event Action<EngineStatus>? EngineStatus;
    public event Action<int>? ControllerReady;             // remote monitor count (controller)
    public event Action<long, int, int>? ReceiverActivity; // (count, x, y) injected (receiver)

    public int LocalMonitorCount => _localMonitors.Count;

    /// <summary>Remote-cursor speed multiplier; applies live to a running engine.</summary>
    public double Sensitivity
    {
        get => _sensitivity;
        set
        {
            _sensitivity = value;
            if (_engine is not null) _engine.Sensitivity = value;
        }
    }

    public NetworkSession(string localMachineId, IReadOnlyList<MonitorInfo> localMonitors)
    {
        _localMachineId = localMachineId;
        _localMonitors = localMonitors;
    }

    public void Connect(string host, int port)
    {
        Role = PeerRole.Controller;
        _host = host;
        _port = port;
        Status?.Invoke($"Connecting to {host}:{port} …");
        StartLink(l => l.Connect(host, port));
    }

    public void Listen(int port)
    {
        Role = PeerRole.Receiver;
        _port = port;
        Status?.Invoke($"Listening on port {port} — waiting for a controller to connect …");
        StartLink(l => l.Listen(port));
    }

    // ── Link lifecycle ───────────────────────────────────────────────────────

    private void StartLink(Action<PeerLink> begin)
    {
        var gen = ++_linkGen;
        var link = new PeerLink();
        link.Connected += () => { if (IsCurrent(gen)) OnConnected(link); };
        link.MessageReceived += m => { if (IsCurrent(gen)) OnMessage(link, m); };
        link.Disconnected += reason => OnLinkDown(gen, reason);
        _link = link;
        begin(link);
    }

    private bool IsCurrent(int gen) => !_disposed && gen == _linkGen;

    private void OnLinkDown(int gen, string reason)
    {
        if (!IsCurrent(gen)) return;

        // Release the hooks/capture immediately: with the peer gone, swallowed
        // mouse AND keyboard input would otherwise leave this machine locked.
        _engine?.Dispose();
        _engine = null;
        try { _link?.Dispose(); } catch { }
        _link = null;

        var delay = Role == PeerRole.Receiver ? RelistenDelayMs : ReconnectDelayMs;
        Status?.Invoke($"Disconnected: {reason} — retrying in {delay / 1000.0:0.#}s …");

        var t = new Thread(() =>
        {
            Thread.Sleep(delay);
            if (!IsCurrent(gen)) return; // user stopped or a newer link exists
            _reconnectAttempt++;
            if (Role == PeerRole.Controller)
            {
                Status?.Invoke($"Reconnecting to {_host}:{_port} (attempt {_reconnectAttempt}) …");
                StartLink(l => l.Connect(_host, _port));
            }
            else
            {
                Status?.Invoke($"Listening again on port {_port} (attempt {_reconnectAttempt}) …");
                StartLink(l => l.Listen(_port));
            }
        }) { IsBackground = true, Name = "Snapfield.Reconnect" };
        t.Start();
    }

    // ── Handshake + messages ─────────────────────────────────────────────────

    private void OnConnected(PeerLink link)
    {
        _reconnectAttempt = 0;
        var monitors = _localMonitors.Select(MonitorState.From).ToArray();
        link.Send(NetMessage.Hello(_localMachineId, monitors));
        Status?.Invoke("Connected. Exchanging layouts …");
    }

    private void OnMessage(PeerLink link, NetMessage msg)
    {
        switch (msg.Type)
        {
            case MsgType.Hello:
                RemoteMachineId = msg.MachineId ?? "remote";
                var remoteMonitors = (msg.Monitors ?? Array.Empty<MonitorState>())
                    .Select(s => s.ToMonitorInfo()).ToList();
                if (Role == PeerRole.Controller)
                    StartController(link, remoteMonitors);
                else
                    Status?.Invoke($"Connected to controller '{RemoteMachineId}' " +
                                   $"(it advertised {remoteMonitors.Count} monitor(s)). Ready to be controlled.");
                break;

            // Receiver side: reproduce the controller's input locally.
            case MsgType.CursorMove:
                CursorInjector.WarpTo(msg.X, msg.Y);
                ReceiverActivity?.Invoke(++_rxCount, msg.X, msg.Y);
                break;
            case MsgType.MouseButton: CursorInjector.MouseButton(msg.Button, msg.Down); break;
            case MsgType.MouseWheel: CursorInjector.Wheel(msg.WheelDelta, msg.Horizontal); break;
            case MsgType.Key: CursorInjector.KeyEvent(msg.Vk, msg.Scan, msg.Down, msg.Extended); break;
            case MsgType.ControlEnter: Status?.Invoke("Controller took over this screen."); break;
            case MsgType.ControlLeave: Status?.Invoke("Controller left this screen."); break;
        }
    }

    private void StartController(PeerLink link, IReadOnlyList<MonitorInfo> remoteMonitors)
    {
        _engine?.Dispose(); // a reconnect replaces any previous engine

        var local = PhysicalLayoutBuilder.WindowsAligned(_localMonitors);
        var combined = new DesktopLayout(PhysicalLayoutBuilder.AppendToRight(local, remoteMonitors));

        _engine = new InputEngine(_localMachineId, combined) { Sensitivity = _sensitivity };
        _engine.RemoteCursor += (_, x, y) => link.Send(NetMessage.Cursor(x, y));
        _engine.RemoteButton += (b, down) => link.Send(NetMessage.MouseBtn(b, down));
        _engine.RemoteWheel += (d, h) => link.Send(NetMessage.Wheel(d, h));
        _engine.RemoteKey += (vk, scan, down, ext) => link.Send(NetMessage.KeyEvent(vk, scan, down, ext));
        _engine.ControlEnteredRemote += _ => link.Send(NetMessage.Enter());
        _engine.ControlReturnedLocal += () => link.Send(NetMessage.Leave());
        _engine.StatusChanged += s => EngineStatus?.Invoke(s);

        // Publish the remote count BEFORE the engine's first status event so the
        // UI never renders a stale "0 remote monitors".
        RemoteMonitorCount = remoteMonitors.Count;
        ControllerReady?.Invoke(remoteMonitors.Count);
        _engine.Start();
        Status?.Invoke($"Controlling '{RemoteMachineId}' ({remoteMonitors.Count} remote monitor(s)). " +
                       "Push the cursor off the right edge to cross over.");
    }

    public void Dispose()
    {
        _disposed = true;
        _linkGen++; // invalidate any pending reconnect
        _engine?.Dispose();
        _link?.Dispose();
    }
}
