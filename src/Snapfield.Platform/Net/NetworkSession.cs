using Snapfield.Core.Model;
using Snapfield.Core.Net;
using Snapfield.Core.Persistence;
using Snapfield.Platform.Input;

namespace Snapfield.Platform.Net;

public enum PeerRole { None, Controller, Receiver }

/// <summary>
/// A sharing session.
///
/// A RECEIVER (<see cref="Listen"/>) accepts a single controller and injects its
/// input. A CONTROLLER (<see cref="Connect"/>) is a hub: it may connect to many
/// receivers at once, places them all on one global plane, runs a single
/// <see cref="InputEngine"/>, and routes each cursor/button/key event to the
/// receiver that owns the monitor the cursor is currently on. Clipboard and the
/// combined layout are broadcast to every connected receiver.
///
/// Each connection encrypts + PIN-authenticates independently and reconnects on
/// its own if it drops.
/// </summary>
public sealed class NetworkSession : IDisposable
{
    private const int ReconnectDelayMs = 3000;
    private const int RelistenDelayMs = 500;
    private const long FileTotalCap = 32L * 1024 * 1024;

    private readonly string _localMachineId;
    private readonly IReadOnlyList<MonitorInfo> _localMonitors;
    private readonly object _lock = new();       // guards _conns
    private readonly object _engineLock = new(); // serializes engine + clipboard creation
    private readonly List<Conn> _conns = new();
    private volatile bool _disposed;

    private InputEngine? _engine;          // controller only, shared across peers
    private string? _activeRemote;         // machine the cursor is currently on
    private ClipboardMonitor? _clipboard;
    private string _lastAppliedClipboard = "";
    private Beacon? _beacon;               // receiver only
    private long _rxCount;
    private int _lastRxTick;               // throttles ReceiverActivity UI updates
    private double _sensitivity = 1.0;

    public PeerRole Role { get; private set; } = PeerRole.None;
    /// <summary>Mutable: a regenerated code applies from the next handshake on
    /// (the receiver conn re-reads it on every listen cycle).</summary>
    public string ReceiverPin { get; set; } = "";
    public string ControllerPin { get; init; } = "";
    public int LocalMonitorCount => _localMonitors.Count;

    public double Sensitivity
    {
        get => _sensitivity;
        set { _sensitivity = value; if (_engine is not null) _engine.Sensitivity = value; }
    }

    public event Action<string>? Status;
    public event Action<EngineStatus>? EngineStatus;
    public event Action<long, int, int>? ReceiverActivity;             // (count, x, y) — receiver
    public event Action<IReadOnlyList<string>>? PeersChanged;          // connected receiver names — controller
    public event Action<string, int, string>? PeerIdentified;          // (host, port, machineId) post-Hello — controller
    public event Action<string>? AuthFailed;                           // wrong pin, conn dropped — controller

    // Machine-switch UX, raised on whichever machine must draw it: the landing
    // pulse at local pixels, and the switcher strip (machine ids + selection).
    public event Action<int, int>? CursorPing;
    public event Action<string[], int>? SwitcherChanged;
    public event Action? SwitcherClosed;

    public NetworkSession(string localMachineId, IReadOnlyList<MonitorInfo> localMonitors)
    {
        _localMachineId = localMachineId;
        _localMonitors = localMonitors;
        LayoutStore.Saved += OnLayoutSaved;
    }

    // ── entry points ──────────────────────────────────────────────────────────
    public void Listen(int port)
    {
        Role = PeerRole.Receiver;
        var conn = new Conn(this, initiator: false, host: "", port, ReceiverPin);
        lock (_lock) _conns.Add(conn);
        conn.Start();
        _beacon = new Beacon();
        _beacon.Start(_localMachineId, port);
        Status?.Invoke($"포트 {port}에서 대기 중 — 조작 기기의 연결을 기다립니다.");
    }

    public void Connect(string host, int port, string pin)
    {
        Role = PeerRole.Controller;
        lock (_lock)
        {
            if (_conns.Any(c => c.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && c.Port == port)) return; // already connected/ing
        }
        var conn = new Conn(this, initiator: true, host, port, pin);
        lock (_lock) _conns.Add(conn);
        conn.Start();
        Status?.Invoke($"{host} 연결 중 …");
    }

    // ── connection callbacks (from Conn) ───────────────────────────────────────
    private void OnConnected(Conn c)
    {
        if (Role == PeerRole.Controller)
            c.Link!.Send(NetMessage.Hello(_localMachineId, _localMonitors.Select(MonitorState.From).ToArray()));
        else
            Status?.Invoke("암호화 연결됨 — 컨트롤러의 hello 대기 중 …");
    }

    private void OnMessage(Conn c, NetMessage msg)
    {
        switch (msg.Type)
        {
            case MsgType.Hello:
                c.MachineId = msg.MachineId ?? "remote";
                c.IsUp = true;
                c.Monitors = (msg.Monitors ?? Array.Empty<MonitorState>()).Select(s => s.ToMonitorInfo()).ToList();
                if (Role == PeerRole.Controller)
                {
                    EnsureClipboard();
                    RebuildEngine();
                    RaisePeers();
                    PeerIdentified?.Invoke(c.Host, c.Port, c.MachineId); // lets the UI name its recents
                    Status?.Invoke($"'{c.MachineId}' 연결됨 — 총 {ConnectedCount}대 조작 중.");
                }
                else
                {
                    c.Link!.Send(NetMessage.Hello(_localMachineId, _localMonitors.Select(MonitorState.From).ToArray()));
                    EnsureClipboard();
                    RaisePeers();
                    Status?.Invoke($"'{c.MachineId}'에 연결됨 (모니터 {c.Monitors.Count}대). 제어 대기 중.");
                }
                break;

            // Receiver-side injection. Role-gated: a receiver must never be able
            // to drive the controller's own input over the same channel.
            case MsgType.CursorMove:
                if (Role != PeerRole.Receiver) break;
                CursorInjector.WarpTo(msg.X, msg.Y);
                _rxCount++;
                // Throttle to ~33 Hz: moves arrive at mouse polling rate, and
                // dispatching every one to the UI thread starves rendering.
                var tick = Environment.TickCount;
                if (_rxCount == 1 || tick - _lastRxTick >= 30)
                {
                    _lastRxTick = tick;
                    ReceiverActivity?.Invoke(_rxCount, msg.X, msg.Y);
                }
                break;
            case MsgType.MouseButton:
                if (Role == PeerRole.Receiver) CursorInjector.MouseButton(msg.Button, msg.Down);
                break;
            case MsgType.MouseWheel:
                if (Role == PeerRole.Receiver) CursorInjector.Wheel(msg.WheelDelta, msg.Horizontal);
                break;
            case MsgType.Key:
                if (Role == PeerRole.Receiver) CursorInjector.KeyEvent(msg.Vk, msg.Scan, msg.Down, msg.Extended);
                break;
            case MsgType.ControlEnter: Status?.Invoke("조작 기기가 이 화면을 넘겨받았습니다."); break;
            case MsgType.ControlLeave: Status?.Invoke("조작 기기가 이 화면을 떠났습니다."); break;
            case MsgType.Clipboard:
                if (msg.Text is { Length: > 0 } t) { _lastAppliedClipboard = t; ClipboardIO.TrySetText(t); _clipboard?.NoteSelfChange(); }
                break;
            case MsgType.ClipboardImage:
                if (msg.Text is { Length: > 0 } b64)
                    try { ClipboardIO.TrySetImagePng(Convert.FromBase64String(b64)); _clipboard?.NoteSelfChange(); } catch { }
                break;
            case MsgType.ClipboardFiles:
                if (msg.Files is { Length: > 0 } files) ReceiveFiles(files);
                break;
            case MsgType.Layout:
                if (Role == PeerRole.Receiver && msg.Monitors is { Length: > 0 } plane)
                    LayoutStore.Save(LayoutStore.DefaultPath, new DesktopLayout(plane.Select(s => s.ToMonitorInfo())));
                break;
            case MsgType.CursorPing:
                if (Role == PeerRole.Receiver) CursorPing?.Invoke(msg.X, msg.Y);
                break;
            case MsgType.SwitcherShow:
                if (Role == PeerRole.Receiver && msg.Text is { Length: > 0 } ids)
                    SwitcherChanged?.Invoke(ids.Split('\n'), msg.X);
                break;
            case MsgType.SwitcherHide:
                if (Role == PeerRole.Receiver) SwitcherClosed?.Invoke();
                break;
        }
    }

    private void OnDown(Conn c, string reason)
    {
        c.IsUp = false;

        if (Role == PeerRole.Controller && c.MachineId.Length > 0)
        {
            c.Monitors = new List<MonitorInfo>(); // drop its monitors from the plane
            RebuildEngine();
            ReleaseIfActive(c.MachineId); // cursor was ON that machine → yank it home NOW
        }
        RaisePeers(); // reflect the drop (hides its monitors on both ends' canvases)

        if (reason.StartsWith("AUTH:", StringComparison.Ordinal))
        {
            var text = reason["AUTH:".Length..].Trim();
            if (Role == PeerRole.Receiver)
            {
                // A stranger's (or mistyped) wrong code must not kill the
                // listener — keep waiting for the controller that knows it.
                Status?.Invoke(text + " 계속 대기합니다.");
                c.ScheduleReconnect(RelistenDelayMs);
            }
            else
            {
                Status?.Invoke(text + " 코드를 확인하고 다시 연결하세요.");
                lock (_lock) _conns.Remove(c);
                c.Stop(); // release the socket — a removed conn never retries
                RaisePeers();
                AuthFailed?.Invoke(text);
            }
            return;
        }

        var delay = Role == PeerRole.Receiver ? RelistenDelayMs : ReconnectDelayMs;
        Status?.Invoke($"'{(c.MachineId.Length > 0 ? c.MachineId : c.Host)}' 연결 끊김: {reason} — {delay / 1000.0:0.#}초 후 재시도 …");
        c.ScheduleReconnect(delay);
    }

    /// <summary>Controller hub: drops ONE receiver, keeping the rest (no retry —
    /// this is an explicit user action, unlike a link failure).</summary>
    public void DisconnectPeer(string machineId)
    {
        Conn? victim;
        lock (_lock)
        {
            victim = _conns.FirstOrDefault(c => c.MachineId == machineId);
            if (victim is not null) _conns.Remove(victim);
        }
        if (victim is null) return;
        victim.Stop();
        RebuildEngine(); // plane loses its monitors
        ReleaseIfActive(machineId);
        RaisePeers();
        Status?.Invoke($"'{machineId}' 연결을 끊었습니다.");
    }

    /// <summary>If the cursor is currently ON the given (now-gone) machine, force
    /// it back to a local monitor. Without this, a dead peer leaves the local
    /// cursor parked AND transparent — invisible until the user blindly wiggles
    /// far enough to cross back.</summary>
    private void ReleaseIfActive(string machineId)
    {
        if (_activeRemote != machineId) return;
        _activeRemote = null;
        lock (_engineLock) _engine?.ForceReleaseToLocal();
    }

    /// <summary>Machine-switch hotkey target list is engine-side; expose the jump.</summary>
    public void JumpToMachine(string machineId)
    {
        lock (_engineLock) _engine?.JumpToMachine(machineId);
    }

    // ── controller: engine + routing ────────────────────────────────────────
    private void RebuildEngine()
    {
        List<MonitorInfo> remote;
        lock (_lock) remote = _conns.SelectMany(x => x.Monitors).ToList();

        var saved = LayoutStore.Load(LayoutStore.DefaultPath);
        var combined = new DesktopLayout(PhysicalLayoutBuilder.Calibrated(_localMonitors, remote, saved));

        // Serialize so two peers connecting at once can't each create an engine
        // (which would install duplicate global hooks and thrash all input).
        lock (_engineLock)
        {
            if (_engine is null)
            {
                _engine = new InputEngine(_localMachineId, combined) { Sensitivity = _sensitivity };
                _engine.RemoteCursor += (mid, x, y) => { _activeRemote = mid; LinkFor(mid)?.Send(NetMessage.Cursor(x, y)); };
                _engine.RemoteButton += (b, down) => LinkFor(_activeRemote)?.Send(NetMessage.MouseBtn(b, down));
                _engine.RemoteWheel += (d, h) => LinkFor(_activeRemote)?.Send(NetMessage.Wheel(d, h));
                _engine.RemoteKey += (vk, sc, down, ext) => LinkFor(_activeRemote)?.Send(NetMessage.KeyEvent(vk, sc, down, ext));
                _engine.ControlEnteredRemote += mid => { _activeRemote = mid; LinkFor(mid)?.Send(NetMessage.Enter()); };
                _engine.ControlReturnedLocal += () => { LinkFor(_activeRemote)?.Send(NetMessage.Leave()); _activeRemote = null; };
                _engine.JumpLanded += (mid, x, y) =>
                {
                    if (mid is null) CursorPing?.Invoke(x, y);
                    else LinkFor(mid)?.Send(NetMessage.Ping(x, y));
                };
                // The strip shows locally AND on the machine the user is looking
                // at (the active remote while captured). Engine raises Closed
                // BEFORE retargeting a jump, so _activeRemote is still the old one.
                _engine.SwitcherChanged += (ids, sel) =>
                {
                    SwitcherChanged?.Invoke(ids, sel);
                    LinkFor(_activeRemote)?.Send(NetMessage.SwitcherShowMsg(string.Join('\n', ids), sel));
                };
                _engine.SwitcherClosed += () =>
                {
                    SwitcherClosed?.Invoke();
                    LinkFor(_activeRemote)?.Send(NetMessage.SwitcherHideMsg());
                };
                _engine.StatusChanged += s => EngineStatus?.Invoke(s);
                _engine.Fault += m => Status?.Invoke(m);
                _engine.Start();
            }
            else
            {
                _engine.UpdateLayout(combined);
            }
        }

        PersistPlane(combined, saved);
        Broadcast(NetMessage.LayoutSync(combined.Monitors.Select(MonitorState.From).ToArray()));
    }

    // Called per mouse move while captured — plain loop, no closure allocation.
    private PeerLink? LinkFor(string? machineId)
    {
        if (machineId is null) return null;
        lock (_lock)
        {
            foreach (var c in _conns)
                if (c.MachineId == machineId) return c.Link;
        }
        return null;
    }

    private void Broadcast(NetMessage m)
    {
        List<Conn> snapshot;
        lock (_lock) snapshot = _conns.ToList();
        foreach (var c in snapshot) c.Link?.Send(m);
    }

    private int ConnectedCount { get { lock (_lock) return _conns.Count(c => c.IsUp && c.MachineId.Length > 0); } }
    private void RaisePeers()
    {
        List<string> names;
        lock (_lock) names = _conns.Where(c => c.IsUp && c.MachineId.Length > 0).Select(c => c.MachineId).ToList();
        PeersChanged?.Invoke(names);
    }

    private void OnLayoutSaved()
    {
        if (_disposed || _engine is null || Role != PeerRole.Controller) return;
        List<MonitorInfo> remote;
        lock (_lock) remote = _conns.SelectMany(x => x.Monitors).ToList();
        var combined = PhysicalLayoutBuilder.Calibrated(_localMonitors, remote, LayoutStore.Load(LayoutStore.DefaultPath));
        lock (_engineLock) _engine?.UpdateLayout(new DesktopLayout(combined));
        Broadcast(NetMessage.LayoutSync(combined.Select(MonitorState.From).ToArray()));
    }

    // ── clipboard (both roles) ────────────────────────────────────────────────
    private void EnsureClipboard()
    {
        lock (_engineLock)
        {
            if (_clipboard is not null) return;
            _clipboard = new ClipboardMonitor();
        _clipboard.TextChanged += text => { if (text != _lastAppliedClipboard) Broadcast(NetMessage.ClipboardText(text)); };
        _clipboard.ImageChanged += png => Broadcast(NetMessage.ClipboardPng(png));
        _clipboard.FilesChanged += paths => SendFiles(paths);
        _clipboard.Start();
        }
    }

    private void SendFiles(string[] paths)
    {
        try
        {
            long total = 0;
            var items = new List<SharedFile>();
            foreach (var p in paths)
            {
                if (!File.Exists(p)) continue;
                total += new FileInfo(p).Length;
                if (total > FileTotalCap) { Status?.Invoke($"파일이 너무 큽니다(>{FileTotalCap / 1024 / 1024}MB) — 전송 생략."); return; }
                items.Add(new SharedFile { Name = Path.GetFileName(p), Data = Convert.ToBase64String(File.ReadAllBytes(p)) });
            }
            if (items.Count > 0) Broadcast(NetMessage.ClipboardFilesMsg(items.ToArray()));
        }
        catch { }
    }

    private static string ReceivedDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snapfield", "Received");

    private void ReceiveFiles(SharedFile[] files)
    {
        try
        {
            Directory.CreateDirectory(ReceivedDir);
            var written = new List<string>();
            foreach (var f in files)
            {
                var name = Path.GetFileName(f.Name);
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
        catch { }
    }

    private void PersistPlane(DesktopLayout combined, DesktopLayout? saved)
    {
        try
        {
            var sessionMachines = combined.Monitors.Select(m => m.MachineId).ToHashSet();
            var others = saved?.Monitors.Where(m => !sessionMachines.Contains(m.MachineId)) ?? Enumerable.Empty<MonitorInfo>();
            LayoutStore.Save(LayoutStore.DefaultPath, new DesktopLayout(combined.Monitors.Concat(others)));
        }
        catch { }
    }

    public void Dispose()
    {
        _disposed = true;
        LayoutStore.Saved -= OnLayoutSaved;
        List<Conn> snapshot;
        lock (_lock) { snapshot = _conns.ToList(); _conns.Clear(); }
        foreach (var c in snapshot) c.Stop();
        _beacon?.Dispose();
        _clipboard?.Dispose();
        _engine?.Dispose();
    }

    // ── one connection (link + reconnect) ─────────────────────────────────────
    private sealed class Conn
    {
        private readonly NetworkSession _s;
        private readonly string _pin;
        public readonly bool Initiator;
        public readonly string Host;
        public readonly int Port;
        public PeerLink? Link;
        public string MachineId = "";
        public bool IsUp;
        public List<MonitorInfo> Monitors = new();
        private int _gen;
        private volatile bool _stopped;

        public Conn(NetworkSession s, bool initiator, string host, int port, string pin)
        {
            _s = s; Initiator = initiator; Host = host; Port = port; _pin = pin;
        }

        public void Start()
        {
            var gen = Interlocked.Increment(ref _gen);
            var link = new PeerLink();
            Link = link;
            link.Connected += () => { if (Current(gen)) _s.OnConnected(this); };
            link.MessageReceived += m => { if (Current(gen)) _s.OnMessage(this, m); };
            link.Disconnected += r => { if (Current(gen)) _s.OnDown(this, r); };
            if (Initiator) link.Connect(Host, Port, _pin);
            else link.Listen(Port, _s.ReceiverPin); // re-read: a regenerated code applies on re-listen
        }

        public void ScheduleReconnect(int delayMs)
        {
            var gen = _gen;
            var t = new Thread(() =>
            {
                Thread.Sleep(delayMs);
                if (!Current(gen)) return;
                try { Link?.Dispose(); } catch { }
                Start();
            }) { IsBackground = true, Name = "Snapfield.Reconnect" };
            t.Start();
        }

        private bool Current(int gen) => !_stopped && !_s._disposed && gen == _gen;

        public void Stop()
        {
            _stopped = true;
            Interlocked.Increment(ref _gen);
            try { Link?.Dispose(); } catch { }
        }
    }
}
