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
    Layout,         // Monitors — the controller's combined global plane, for the receiver to display
    ClipboardFiles, // Files — copied files (name + base64 bytes) shared via the clipboard
}

/// <summary>One file shared over the clipboard (name + base64-encoded content).</summary>
public sealed record SharedFile
{
    public string Name { get; init; } = "";
    public string Data { get; init; } = ""; // base64
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
    public SharedFile[]? Files { get; init; }

    // Omit default-valued fields: a CursorMove then carries only {X, Y} instead
    // of all 15 fields (~5× smaller frames at mouse-polling rate). Deserialising
    // fills missing fields with the same defaults, so the round-trip is lossless.
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault,
    };

    public static NetMessage Hello(string machineId, MonitorState[] monitors, string? pin = null) =>
        new() { Type = MsgType.Hello, MachineId = machineId, Monitors = monitors, Pin = pin };

    public static NetMessage ClipboardText(string text) => new() { Type = MsgType.Clipboard, Text = text };
    public static NetMessage ClipboardPng(byte[] png) => new() { Type = MsgType.ClipboardImage, Text = Convert.ToBase64String(png) };
    public static NetMessage AuthFailed() => new() { Type = MsgType.AuthFail };
    public static NetMessage LayoutSync(MonitorState[] monitors) => new() { Type = MsgType.Layout, Monitors = monitors };
    public static NetMessage ClipboardFilesMsg(SharedFile[] files) => new() { Type = MsgType.ClipboardFiles, Files = files };

    public static NetMessage Cursor(int x, int y) => new() { Type = MsgType.CursorMove, X = x, Y = y };
    public static NetMessage MouseBtn(int button, bool down) => new() { Type = MsgType.MouseButton, Button = button, Down = down };
    public static NetMessage Wheel(int delta, bool horizontal) => new() { Type = MsgType.MouseWheel, WheelDelta = delta, Horizontal = horizontal };
    public static NetMessage KeyEvent(int vk, int scan, bool down, bool extended) =>
        new() { Type = MsgType.Key, Vk = vk, Scan = scan, Down = down, Extended = extended };
    public static NetMessage Enter() => new() { Type = MsgType.ControlEnter };
    public static NetMessage Leave() => new() { Type = MsgType.ControlLeave };

    /// <summary>Serialise the message body to UTF-8 JSON (no length prefix).</summary>
    public byte[] ToJson() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    /// <summary>Serialise into a caller-owned writer — lets the send loop reuse
    /// its buffers instead of allocating per message.</summary>
    public void WriteTo(Utf8JsonWriter writer) => JsonSerializer.Serialize(writer, this, Json);

    public static NetMessage? FromBody(ReadOnlySpan<byte> body) =>
        JsonSerializer.Deserialize<NetMessage>(body, Json);
}
