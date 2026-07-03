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

    // Clipboard state
    private ClipboardMonitor? _clipboard;
    private Beacon? _beacon;
    private string _lastAppliedClipboard = "";

    /// <summary>Pin this machine requires from controllers (receiver role). Empty = accept anyone.</summary>
    public string ReceiverPin { get; init; } = "";

    /// <summary>Pin sent when connecting to a receiver (controller role).</summary>
    public string ControllerPin { get; init; } = "";

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

    private IReadOnlyList<MonitorInfo> _lastRemoteMonitors = Array.Empty<MonitorInfo>();

    public NetworkSession(string localMachineId, IReadOnlyList<MonitorInfo> localMonitors)
    {
        _localMachineId = localMachineId;
        _localMonitors = localMonitors;
        LayoutStore.Saved += OnLayoutSaved;
    }

    /// <summary>Re-route against the newly calibrated plane without reconnecting.</summary>
    private void OnLayoutSaved()
    {
        if (_disposed || _engine is null || Role != PeerRole.Controller) return;
        var combined = PhysicalLayoutBuilder.Calibrated(
            _localMonitors, _lastRemoteMonitors, LayoutStore.Load(LayoutStore.DefaultPath));
        _engine.UpdateLayout(new DesktopLayout(combined));
        _link?.Send(NetMessage.LayoutSync(combined.Select(MonitorState.From).ToArray()));
        Status?.Invoke("Calibration applied to the live session.");
    }

    public void Connect(string host, int port)
    {
        Role = PeerRole.Controller;
        _host = host;
        _port = port;
        Status?.Invoke($"Connecting to {host}:{port} …");
        StartLink(l => l.Connect(host, port, ControllerPin));
    }

    public void Listen(int port)
    {
        Role = PeerRole.Receiver;
        _port = port;
        Status?.Invoke($"Listening on port {port} — waiting for a controller to connect …");
        StartLink(l => l.Listen(port, ReceiverPin));

        // Advertise on the LAN so controllers can find us without typing an IP.
        _beacon = new Beacon();
        _beacon.Start(_localMachineId, port);
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
        _clipboard?.Dispose();
        _clipboard = null;
        try { _link?.Dispose(); } catch { }
        _link = null;

        if (reason.StartsWith("AUTH:", StringComparison.Ordinal))
        {
            // Wrong pairing code — the encrypted handshake rejected it. Don't spam retries.
            Status?.Invoke(reason["AUTH:".Length..].Trim() + " 코드를 확인하고 다시 연결하세요.");
            return;
        }

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
                StartLink(l => l.Connect(_host, _port, ControllerPin));
            }
            else
            {
                Status?.Invoke($"Listening again on port {_port} (attempt {_reconnectAttempt}) …");
                StartLink(l => l.Listen(_port, ReceiverPin));
            }
        }) { IsBackground = true, Name = "Snapfield.Reconnect" };
        t.Start();
    }

    // ── Handshake + messages ─────────────────────────────────────────────────

    private void OnConnected(PeerLink link)
    {
        _reconnectAttempt = 0;
        if (Role == PeerRole.Controller)
        {
            // Channel is already encrypted and PIN-authenticated by the handshake;
            // Hello now just carries the monitor layout.
            var monitors = _localMonitors.Select(MonitorState.From).ToArray();
            link.Send(NetMessage.Hello(_localMachineId, monitors));
            Status?.Invoke("Connected — 레이아웃 교환 중 …");
        }
        else
        {
            Status?.Invoke("암호화 연결됨 — 컨트롤러의 hello 대기 중 …");
        }
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
                {
                    StartController(link, remoteMonitors);
                    StartClipboardSync(link);
                }
                else
                {
                    var mine = _localMonitors.Select(MonitorState.From).ToArray();
                    link.Send(NetMessage.Hello(_localMachineId, mine));
                    StartClipboardSync(link);
                    Status?.Invoke($"'{RemoteMachineId}'에 연결됨 (모니터 {remoteMonitors.Count}대). 제어 대기 중.");
                }
                break;

            case MsgType.Clipboard:
                if (msg.Text is { Length: > 0 } incoming)
                {
                    _lastAppliedClipboard = incoming;
                    ClipboardIO.TrySetText(incoming);
                    _clipboard?.NoteSelfChange();
                }
                break;

            case MsgType.ClipboardImage:
                if (msg.Text is { Length: > 0 } b64)
                {
                    try
                    {
                        ClipboardIO.TrySetImagePng(Convert.FromBase64String(b64));
                        _clipboard?.NoteSelfChange();
                    }
                    catch { /* malformed payload — ignore */ }
                }
                break;

            case MsgType.ClipboardFiles:
                if (msg.Files is { Length: > 0 } files) ReceiveFiles(files);
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

            // Receiver: mirror the controller's combined plane into our layout file
            // so the 모니터 배치 tab shows the same global arrangement (LayoutStore.Saved
            // then refreshes the calibration canvas).
            case MsgType.Layout:
                if (Role == PeerRole.Receiver && msg.Monitors is { Length: > 0 })
                    LayoutStore.Save(LayoutStore.DefaultPath,
                        new DesktopLayout(msg.Monitors.Select(s => s.ToMonitorInfo())));
                break;
        }
    }

    private void StartController(PeerLink link, IReadOnlyList<MonitorInfo> remoteMonitors)
    {
        _engine?.Dispose(); // a reconnect replaces any previous engine
        _lastRemoteMonitors = remoteMonitors;

        // Route the way the user calibrated it; un-calibrated monitors fall back
        // to the automatic arrangement (locals Windows-aligned, remotes appended
        // right). Then persist the combined plane so the calibration canvas shows
        // the remote monitors and the user can drag them into place.
        var saved = LayoutStore.Load(LayoutStore.DefaultPath);
        var combined = new DesktopLayout(PhysicalLayoutBuilder.Calibrated(_localMonitors, remoteMonitors, saved));
        PersistPlane(combined, saved);

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

        // Send the combined plane so the receiver's calibration canvas mirrors it.
        link.Send(NetMessage.LayoutSync(combined.Monitors.Select(MonitorState.From).ToArray()));

        Status?.Invoke($"Controlling '{RemoteMachineId}' ({remoteMonitors.Count} remote monitor(s)). " +
                       "Push the cursor off the right edge to cross over.");
    }

    /// <summary>
    /// Mirrors local clipboard text to the peer. The peer records what it applied,
    /// so its own monitor skips the echo and the pair can't ping-pong.
    /// </summary>
    private void StartClipboardSync(PeerLink link)
    {
        _clipboard?.Dispose();
        _clipboard = new ClipboardMonitor();
        _clipboard.TextChanged += text =>
        {
            if (text == _lastAppliedClipboard) return; // our own application of the peer's copy
            link.Send(NetMessage.ClipboardText(text));
        };
        _clipboard.ImageChanged += png => link.Send(NetMessage.ClipboardPng(png));
        _clipboard.FilesChanged += paths => SendFiles(paths, link);
        _clipboard.Start();
    }

    private const long FileTotalCap = 32L * 1024 * 1024; // 32 MB across all copied files
    private static string ReceivedDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snapfield", "Received");

    /// <summary>Reads copied files and sends them to the peer, within a size cap.</summary>
    private void SendFiles(string[] paths, PeerLink link)
    {
        try
        {
            long total = 0;
            var items = new List<SharedFile>();
            foreach (var p in paths)
            {
                if (!File.Exists(p)) continue;         // folders/unsupported — skip
                var info = new FileInfo(p);
                total += info.Length;
                if (total > FileTotalCap) { Status?.Invoke($"파일이 너무 큽니다(>{FileTotalCap / 1024 / 1024}MB) — 전송 생략."); return; }
                items.Add(new SharedFile { Name = Path.GetFileName(p), Data = Convert.ToBase64String(File.ReadAllBytes(p)) });
            }
            if (items.Count > 0) link.Send(NetMessage.ClipboardFilesMsg(items.ToArray()));
        }
        catch { /* unreadable file — skip silently */ }
    }

    /// <summary>Writes received files locally and puts them on the clipboard, paste-ready.</summary>
    private void ReceiveFiles(SharedFile[] files)
    {
        try
        {
            Directory.CreateDirectory(ReceivedDir);
            var written = new List<string>();
            foreach (var f in files)
            {
                var name = Path.GetFileName(f.Name); // strip any path components
                if (string.IsNullOrWhiteSpace(name)) continue;
                var dest = Path.Combine(ReceivedDir, name);
                File.WriteAllBytes(dest, Convert.FromBase64String(f.Data));
                written.Add(dest);
            }
            if (written.Count > 0)
            {
                ClipboardIO.TrySetFiles(written.ToArray());
                _clipboard?.NoteSelfChange();
                Status?.Invoke($"파일 {written.Count}개 수신 — 붙여넣기(Ctrl+V) 가능.");
            }
        }
        catch { /* write/clipboard failure — ignore */ }
    }

    /// <summary>
    /// Saves the combined plane while preserving calibrated monitors of machines
    /// that are not part of this session (a third PC calibrated earlier must not
    /// be dropped by a two-machine connection).
    /// </summary>
    private void PersistPlane(DesktopLayout combined, DesktopLayout? saved)
    {
        try
        {
            var sessionMachines = combined.Monitors.Select(m => m.MachineId).ToHashSet();
            var others = saved?.Monitors.Where(m => !sessionMachines.Contains(m.MachineId))
                         ?? Enumerable.Empty<MonitorInfo>();
            LayoutStore.Save(LayoutStore.DefaultPath, new DesktopLayout(combined.Monitors.Concat(others)));
        }
        catch { /* persistence is best-effort; routing already has the layout */ }
    }

    public void Dispose()
    {
        _disposed = true;
        _linkGen++; // invalidate any pending reconnect
        LayoutStore.Saved -= OnLayoutSaved;
        _beacon?.Dispose();
        _clipboard?.Dispose();
        _engine?.Dispose();
        _link?.Dispose();
    }
}
