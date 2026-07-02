using Snapfield.Core.Model;
using Snapfield.Core.Net;
using Snapfield.Core.Persistence;
using Snapfield.Platform.Input;

namespace Snapfield.Platform.Net;

public enum PeerRole { None, Controller, Receiver }

/// <summary>
/// A cross-machine sharing session over one <see cref="PeerLink"/>.
///
/// The machine that calls <see cref="Connect"/> is the CONTROLLER (it has the
/// physical keyboard/mouse); the one that calls <see cref="Listen"/> is the
/// RECEIVER. On connect both send <see cref="MsgType.Hello"/> with their monitors;
/// the controller then builds a combined layout (local monitors + the receiver's
/// placed to the right) and runs the <see cref="InputEngine"/>. When the cursor
/// crosses onto a receiver monitor, the controller streams cursor/button/wheel
/// events; the receiver injects them.
/// </summary>
public sealed class NetworkSession : IDisposable
{
    private readonly string _localMachineId;
    private readonly IReadOnlyList<MonitorInfo> _localMonitors;
    private readonly PeerLink _link = new();
    private InputEngine? _engine;

    public PeerRole Role { get; private set; } = PeerRole.None;
    public string RemoteMachineId { get; private set; } = "";
    public int RemoteMonitorCount { get; private set; }

    private long _rxCount;

    public event Action<string>? Status;
    public event Action<EngineStatus>? EngineStatus;
    public event Action<int>? ControllerReady;          // remote monitor count (controller)
    public event Action<long, int, int>? ReceiverActivity; // (count, x, y) injected (receiver)

    public int LocalMonitorCount => _localMonitors.Count;

    public NetworkSession(string localMachineId, IReadOnlyList<MonitorInfo> localMonitors)
    {
        _localMachineId = localMachineId;
        _localMonitors = localMonitors;

        _link.Connected += OnConnected;
        _link.MessageReceived += OnMessage;
        _link.Disconnected += reason => Status?.Invoke($"Disconnected: {reason}");
    }

    public void Connect(string host, int port)
    {
        Role = PeerRole.Controller;
        Status?.Invoke($"Connecting to {host}:{port} …");
        _link.Connect(host, port);
    }

    public void Listen(int port)
    {
        Role = PeerRole.Receiver;
        Status?.Invoke($"Listening on port {port} — waiting for a controller to connect …");
        _link.Listen(port);
    }

    private void OnConnected()
    {
        var monitors = _localMonitors.Select(MonitorState.From).ToArray();
        _link.Send(NetMessage.Hello(_localMachineId, monitors));
        Status?.Invoke("Connected. Exchanging layouts …");
    }

    private void OnMessage(NetMessage msg)
    {
        switch (msg.Type)
        {
            case MsgType.Hello:
                RemoteMachineId = msg.MachineId ?? "remote";
                var remoteMonitors = (msg.Monitors ?? Array.Empty<MonitorState>())
                    .Select(s => s.ToMonitorInfo()).ToList();
                if (Role == PeerRole.Controller)
                    StartController(remoteMonitors);
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
            case MsgType.ControlEnter: Status?.Invoke("Controller took over this screen."); break;
            case MsgType.ControlLeave: Status?.Invoke("Controller left this screen."); break;
        }
    }

    private void StartController(IReadOnlyList<MonitorInfo> remoteMonitors)
    {
        var local = PhysicalLayoutBuilder.WindowsAligned(_localMonitors);
        var combined = new DesktopLayout(PhysicalLayoutBuilder.AppendToRight(local, remoteMonitors));

        _engine = new InputEngine(_localMachineId, combined);
        _engine.RemoteCursor += (_, x, y) => _link.Send(NetMessage.Cursor(x, y));
        _engine.RemoteButton += (b, down) => _link.Send(NetMessage.MouseBtn(b, down));
        _engine.RemoteWheel += (d, h) => _link.Send(NetMessage.Wheel(d, h));
        _engine.ControlEnteredRemote += _ => _link.Send(NetMessage.Enter());
        _engine.ControlReturnedLocal += () => _link.Send(NetMessage.Leave());
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
        _engine?.Dispose();
        _link.Dispose();
    }
}
