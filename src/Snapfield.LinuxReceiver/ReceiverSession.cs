using Snapfield.Core.Net;
using Snapfield.Core.Persistence;

namespace Snapfield.LinuxReceiver;

/// <summary>
/// The receiver session: accepts one controller, answers Hello with this
/// machine's screens (so it lands on the PC's physical plane like any monitor),
/// and routes input messages into uinput. Mirrors the desktop/Android receiver
/// lifecycle: re-listen on drop, keep listening on a wrong-pin attempt.
/// </summary>
public sealed class ReceiverSession : IDisposable
{
    private const int RelistenDelayMs = 500;

    private readonly string _machineId;
    private readonly int _port;
    private readonly Func<string> _pin;
    private readonly MonitorState[] _monitors;
    private readonly UinputInjector _injector;
    private readonly WaylandClipboard _clipboard;
    private readonly Beacon _beacon = new();

    private PeerLink? _link;
    private int _gen;
    private volatile bool _disposed;

    public event Action<string>? Status;

    public ReceiverSession(string machineId, int port, Func<string> pin,
        MonitorState[] monitors, UinputInjector injector, WaylandClipboard clipboard)
    {
        _machineId = machineId;
        _port = port;
        _pin = pin;
        _monitors = monitors;
        _injector = injector;
        _clipboard = clipboard;
        _clipboard.TextChanged += text => _link?.Send(NetMessage.ClipboardText(text));
        _clipboard.ImageChanged += png => _link?.Send(NetMessage.ClipboardPng(png));
        _clipboard.Status += s => Status?.Invoke(s);
    }

    public void Start()
    {
        _beacon.Start(_machineId, _port);
        Listen();
        Status?.Invoke($"연결 대기 중 — 포트 {_port}");
    }

    private void Listen()
    {
        if (_disposed) return;
        var gen = Interlocked.Increment(ref _gen);
        try { _link?.Dispose(); } catch { }
        var link = new PeerLink();
        _link = link;
        link.Connected += () => { if (Current(gen)) Status?.Invoke("암호화 연결됨 — 컨트롤러의 hello 대기 중 …"); };
        link.MessageReceived += m => { if (Current(gen)) OnMessage(link, m); };
        link.Disconnected += reason => { if (Current(gen)) OnDown(reason); };
        link.Listen(_port, _pin());
    }

    private bool Current(int gen) => !_disposed && gen == _gen;

    private void OnDown(string reason)
    {
        _injector.ReleaseAll(); // never leave a key/button stuck down on a dead link
        Status?.Invoke(reason.StartsWith("AUTH:", StringComparison.Ordinal)
            ? "연결 코드가 일치하지 않습니다. 계속 대기합니다."
            : $"연결 끊김: {reason} — 다시 대기합니다.");
        var gen = _gen;
        new Thread(() =>
        {
            Thread.Sleep(RelistenDelayMs);
            if (Current(gen)) Listen();
        }) { IsBackground = true, Name = "Snapfield.Relisten" }.Start();
    }

    private void OnMessage(PeerLink link, NetMessage msg)
    {
        switch (msg.Type)
        {
            case MsgType.Hello:
                link.Send(NetMessage.Hello(_machineId, _monitors));
                Status?.Invoke($"'{msg.MachineId ?? "controller"}'에 연결됨. 제어 대기 중.");
                break;
            case MsgType.CursorMove: _injector.MoveTo(msg.X, msg.Y); break;
            case MsgType.MouseButton: _injector.Button(msg.Button, msg.Down); break;
            case MsgType.MouseWheel: _injector.Wheel(msg.WheelDelta, msg.Horizontal); break;
            case MsgType.Key: _injector.Key(msg.Vk, msg.Down); break;
            case MsgType.ControlEnter: Status?.Invoke("조작 기기가 이 화면을 넘겨받았습니다."); break;
            case MsgType.ControlLeave:
                _injector.ReleaseAll();
                Status?.Invoke("조작 기기가 이 화면을 떠났습니다.");
                break;
            case MsgType.Clipboard:
                if (msg.Text is { Length: > 0 } text) { _clipboard.Apply(text); Status?.Invoke($"클립보드 수신 ({text.Length}자)"); }
                break;
            case MsgType.ClipboardImage:
                if (msg.Text is { Length: > 0 } b64)
                    try
                    {
                        var png = Convert.FromBase64String(b64);
                        _clipboard.ApplyPng(png);
                        Status?.Invoke($"클립보드 이미지 수신 ({png.Length / 1024}KB)");
                    }
                    catch { /* malformed image — skip */ }
                break;
            // Layout / files / ping / switcher: visual or Windows-specific — not consumed here (yet).
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Interlocked.Increment(ref _gen);
        _beacon.Dispose();
        try { _link?.Dispose(); } catch { }
    }
}
