using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Snapfield.Core.Persistence;

namespace Snapfield.Core.Net;

public enum MsgType
{
    Hello,        // exchange machine id + monitors on connect
    CursorMove,   // warp receiver cursor to (X, Y) in its virtual-desktop pixels
    MouseButton,  // Button (0=L,1=R,2=M), Down
    MouseWheel,   // WheelDelta, Horizontal
    ControlEnter, // control just entered the receiver
    ControlLeave, // control just left the receiver
    Key,          // Vk, Scan, Down, Extended — keyboard transition
    Clipboard,      // Text — clipboard text changed on the sender
    AuthFail,       // receiver rejected the controller's pairing pin
    ClipboardImage, // Text carries base64 PNG — clipboard image changed on the sender
}

/// <summary>
/// One wire message. A single flat record keeps (de)serialisation trivial; unused
/// fields stay at their defaults per message type. Frames are length-prefixed
/// (4-byte little-endian length + UTF-8 JSON body).
/// </summary>
public sealed record NetMessage
{
    public MsgType Type { get; init; }
    public string? MachineId { get; init; }
    public MonitorState[]? Monitors { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Button { get; init; }
    public bool Down { get; init; }
    public int WheelDelta { get; init; }
    public bool Horizontal { get; init; }
    public int Vk { get; init; }
    public int Scan { get; init; }
    public bool Extended { get; init; }
    public string? Text { get; init; }
    public string? Pin { get; init; }

    private static readonly JsonSerializerOptions Json = new();

    public static NetMessage Hello(string machineId, MonitorState[] monitors, string? pin = null) =>
        new() { Type = MsgType.Hello, MachineId = machineId, Monitors = monitors, Pin = pin };

    public static NetMessage ClipboardText(string text) => new() { Type = MsgType.Clipboard, Text = text };
    public static NetMessage ClipboardPng(byte[] png) => new() { Type = MsgType.ClipboardImage, Text = Convert.ToBase64String(png) };
    public static NetMessage AuthFailed() => new() { Type = MsgType.AuthFail };

    public static NetMessage Cursor(int x, int y) => new() { Type = MsgType.CursorMove, X = x, Y = y };
    public static NetMessage MouseBtn(int button, bool down) => new() { Type = MsgType.MouseButton, Button = button, Down = down };
    public static NetMessage Wheel(int delta, bool horizontal) => new() { Type = MsgType.MouseWheel, WheelDelta = delta, Horizontal = horizontal };
    public static NetMessage KeyEvent(int vk, int scan, bool down, bool extended) =>
        new() { Type = MsgType.Key, Vk = vk, Scan = scan, Down = down, Extended = extended };
    public static NetMessage Enter() => new() { Type = MsgType.ControlEnter };
    public static NetMessage Leave() => new() { Type = MsgType.ControlLeave };

    /// <summary>Serialise to a length-prefixed frame ready to write to a stream.</summary>
    public byte[] ToFrame()
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(this, Json);
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }

    public static NetMessage? FromBody(ReadOnlySpan<byte> body) =>
        JsonSerializer.Deserialize<NetMessage>(body, Json);
}
