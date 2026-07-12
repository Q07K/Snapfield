using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Snapfield.LinuxReceiver;

// Snapfield Linux receiver — a headless console daemon for Wayland (and X11)
// desktops. Run it inside the desktop session (it needs the session's
// WAYLAND_DISPLAY/DISPLAY for clipboard + screen detection; input injection is
// kernel-level uinput and works regardless).

const int DefaultPort = 45654;

string? argPin = null, argName = null, argSize = null, argMm = null;
var port = DefaultPort;
var clipboardOn = true;

for (var i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} 뒤에 값이 필요합니다");
    switch (args[i])
    {
        case "--port": port = int.Parse(Next()); break;
        case "--pin": argPin = Next(); break;
        case "--name": argName = Next(); break;
        case "--size": argSize = Next(); break;
        case "--mm": argMm = Next(); break;
        case "--no-clipboard": clipboardOn = false; break;
        case "--help" or "-h":
            Console.WriteLine("""
                snapfield-receiver — PC 커서가 이 리눅스 화면으로 넘어옵니다.

                옵션:
                  --port N          수신 포트 (기본 45654)
                  --pin XXXXXX      연결 코드 지정 (기본: 생성 후 ~/.config/snapfield 에 저장)
                  --name NAME       기기 이름 (기본: 호스트명)
                  --size WxH        화면 크기 수동 지정 (xrandr 감지 실패/오류 시)
                  --mm WxH          화면 물리 크기(mm) 수동 지정
                  --no-clipboard    클립보드 동기화 끄기
                """);
            return 0;
        default:
            Console.WriteLine($"알 수 없는 옵션: {args[i]} (--help 참고)");
            return 1;
    }
}

var name = argName ?? Environment.MachineName;
var pin = argPin ?? LoadOrCreatePin();

var screens = ScreenInfo.Detect(argSize, argMm);
var (ux, uy, uw, uh) = ScreenInfo.Union(screens);

UinputInjector injector;
try { injector = new UinputInjector(ux, uy, uw, uh); }
catch (IOException ex)
{
    Console.WriteLine(ex.Message);
    return 1;
}

var clipboard = new WaylandClipboard();
using var session = new ReceiverSession(
    name, port, () => pin, ScreenInfo.AsMonitors(screens, name), injector, clipboard);
session.Status += s => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {s}");

Console.WriteLine($"""
    Snapfield 수신기 — {name}
      화면      : {string.Join(", ", screens.Select(s => $"{s.Name} {s.W}x{s.H}+{s.X}+{s.Y}"))}
      이 기기 IP: {string.Join("  ", LocalIps())}
      연결 코드 : {pin}   (PC의 '기기 추가'에서 입력)
      포트      : {port}
    """);

if (clipboardOn) clipboard.Start();
session.Start();

// Run until Ctrl+C / SIGTERM, then release any held keys before exiting.
var done = new ManualResetEventSlim();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => done.Set();
done.Wait();

clipboard.Dispose();
injector.Dispose();
return 0;

static string LoadOrCreatePin()
{
    var dir = Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "snapfield");
    var file = Path.Combine(dir, "receiver.json");
    try
    {
        if (File.Exists(file))
        {
            var saved = JsonSerializer.Deserialize<Config>(File.ReadAllText(file));
            if (saved?.Pin is { Length: > 0 } p) return p;
        }
    }
    catch { /* corrupt config — regenerate */ }

    var pin = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
    try
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(file, JsonSerializer.Serialize(new Config { Pin = pin }));
    }
    catch { /* unwritable config dir — pin just won't persist */ }
    return pin;
}

static IEnumerable<string> LocalIps()
{
    var ips = new List<string>();
    try
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up ||
                ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                    ips.Add(ua.Address.ToString());
        }
    }
    catch { }
    return ips.Count > 0 ? ips : new List<string> { "?" };
}

internal sealed class Config
{
    public string? Pin { get; set; }
}
